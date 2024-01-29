using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using CommonLibrary;
using NpoComputer.DCX.Common;
using Sungero.Company;
using Sungero.Content;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow;
using Sungero.Domain.Shared;
using Sungero.Exchange.Structures.Module;
using Sungero.ExchangeCore;
using Sungero.Metadata;
using Sungero.Parties;
using Sungero.Workflow;
using Calendar = Sungero.Core.Calendar;
using DcxClient = NpoComputer.DCX.ClientApi.Client;
using DocumentExchState = Sungero.Docflow.OfficialDocument.ExchangeState;
using DocumentInfoExchState = Sungero.Exchange.ExchangeDocumentInfo.ExchangeState;
using ExchDocumentType = Sungero.Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType;
using FinancialFunction = Sungero.FinancialArchive.PublicFunctions;
using Signature = NpoComputer.DCX.Common.Signature;

namespace Sungero.Exchange.Server
{
  public class ModuleFunctions
  {
    #region Обработка сообщений из сервиса обмена

    #region Разбор сообщений из сервиса
    
    /// <summary>
    /// Обработка легких сообщений сервиса обмена.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    public virtual void SyncLiteMessages(ExchangeCore.IBusinessUnitBox businessUnitBox)
    {
      try
      {
        this.LogDebugFormat(businessUnitBox, "Execute SyncLiteMessages.");
        var currentIncomingId = Functions.Module.GetLastIncomingMessageId(businessUnitBox);
        var currentOutgoingId = Functions.Module.GetLastOutgoingMessageId(businessUnitBox);
        var lastIncomingId = string.Empty;
        var lastOutgoingId = string.Empty;
        
        // Если это первый запуск - явно вызываем синхронизацию контрагентов, у неё таймаут большой.
        if (string.IsNullOrWhiteSpace(currentIncomingId) && string.IsNullOrWhiteSpace(currentOutgoingId))
        {
          Exchange.PublicFunctions.Module.LogDebugFormat(businessUnitBox, "SyncLiteMessages. Is first execute job. Requeue counterparty sync.");
          ExchangeCore.PublicFunctions.Module.Remote.RequeueCounterpartySync();
        }
        
        var messages = this.GetLiteMessages(businessUnitBox, currentIncomingId, currentOutgoingId, out lastIncomingId, out lastOutgoingId);
        messages = this.FilterLiteMessages(businessUnitBox, messages);
        this.RunMessagesOnAsyncHandler(businessUnitBox, messages);
        this.UpdateLastMessageIds(businessUnitBox, currentIncomingId, currentOutgoingId, lastIncomingId, lastOutgoingId);
        this.RunOldMessagesOnAsyncHandler(businessUnitBox);
        this.LogDebugFormat(businessUnitBox, "Done SyncLiteMessages.");
      }
      catch (Exception ex)
      {
        this.LogErrorFormat(string.Format(ExchangeCore.BusinessUnitBoxes.Resources.BoxError,  businessUnitBox.Id), ex);
      }
    }
    
    /// <summary>
    /// Обработка легких исторических сообщений сервиса обмена.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    public virtual void SyncLiteHistoricalMessages(ExchangeCore.IBusinessUnitBox businessUnitBox)
    {
      try
      {
        this.LogDebugFormat(businessUnitBox, "Execute SyncLiteHistoricalMessages.");
        
        var downloadSession = ExchangeCore.PublicFunctions.BusinessUnitBox.GetHistoricalMessagesDownloadSessionInWork(businessUnitBox);
        if (downloadSession != null)
        {
          var messageQueueItemsCount = ExchangeCore.MessageQueueItems.GetAll(q => q.ProcessingStatus != ExchangeCore.MessageQueueItem.ProcessingStatus.Suspended &&
                                                                             Equals(q.RootBox, businessUnitBox) && q.DownloadSession.Id == downloadSession.Id)
            .Count();
          
          this.LogDebugFormat(businessUnitBox, "SyncLiteHistoricalMessages. Found download session. Queue items count: {0}, Session Id: {1}", messageQueueItemsCount, downloadSession.Id);
          
          var maxMessageQueueItemsCount = Sungero.Docflow.PublicFunctions.Module.Remote.GetDocflowParamsIntegerValue(Sungero.Docflow.PublicConstants.Module.HistoricalMessageQueueItemLimitParamName);
          if (maxMessageQueueItemsCount <= 0)
            maxMessageQueueItemsCount = Sungero.Docflow.PublicConstants.Module.HistoricalMessageQueueItemLimit;
          
          if (messageQueueItemsCount < maxMessageQueueItemsCount)
          {
            var lastIncomingId = downloadSession.LastIncomingMessageId;
            var lastOutgoingId = downloadSession.LastOutgoingMessageId;
            var messages = this.GetLiteMessages(businessUnitBox, lastIncomingId, lastOutgoingId, out lastIncomingId, out lastOutgoingId, downloadSession.PeriodBegin.ToUtcTime().Value);
            
            messages = messages.Where(m => m.TimeStamp.Date <= downloadSession.PeriodEnd.Value.Date).ToList();
            this.LogDebugFormat(businessUnitBox, "SyncLiteHistoricalMessages. Messages count: {0}, Session Id: {1}", messages.Count, downloadSession.Id);
            
            if (messages.Count == 0)
            {
              downloadSession = HistoricalMessagesDownloadSessions.Get(downloadSession.Id);
              downloadSession.DownloadingState = ExchangeCore.HistoricalMessagesDownloadSession.DownloadingState.Completed;
              downloadSession.LastMessageDate = downloadSession.PeriodEnd;
              downloadSession.LastIncomingMessageId = string.Empty;
              downloadSession.LastOutgoingMessageId = string.Empty;
              this.LogDebugFormat(businessUnitBox, "SyncLiteHistoricalMessages. Download complete. Session Id: {0}.", downloadSession.Id);
            }
            else
            {
              var lastMessageDate = messages.Last().TimeStamp;
              messages = this.FilterLiteMessages(businessUnitBox, messages);
              this.RunMessagesOnAsyncHandler(businessUnitBox, messages, downloadSession);
              
              downloadSession = HistoricalMessagesDownloadSessions.Get(downloadSession.Id);
              downloadSession.LastMessageDate = lastMessageDate;
              downloadSession.LastIncomingMessageId = lastIncomingId;
              downloadSession.LastOutgoingMessageId = lastOutgoingId;
              
              this.LogDebugFormat(businessUnitBox, "SyncLiteHistoricalMessages. Messages added to processing. Messages count: {0}. LastMessageDate: {1}. Session Id: {2}.",
                                  messages.Count(), downloadSession.LastMessageDate, downloadSession.Id);
            }
            
            downloadSession.Save();
          }
        }
        
        this.RunOldMessagesOnAsyncHandler(businessUnitBox, true);
        this.LogDebugFormat(businessUnitBox, "Done SyncLiteHistoricalMessages. Session Id: {0}.", downloadSession?.Id);
      }
      catch (Exception ex)
      {
        this.LogErrorFormat(string.Format(ExchangeCore.BusinessUnitBoxes.Resources.BoxError,  businessUnitBox.Id), ex);
      }
    }

    /// <summary>
    /// Обработка сообщений из сервиса обмена.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    /// <param name="lastIncomingId">Id входящего сообщения, с которого нужно начать обработку.</param>
    /// <param name="lastOutgoingId">Id исходящего сообщения, с которого нужно начать обработку.</param>
    [Obsolete("Используйте метод SyncLiteMessages.")]
    public virtual void SyncMessages(ExchangeCore.IBusinessUnitBox businessUnitBox, string lastIncomingId, string lastOutgoingId)
    {
      try
      {
        var client = GetClient(businessUnitBox);
        var lastIncomingEventId = string.Empty;
        var lastOutgoingEventId = string.Empty;
        
        var lastIncomingDefaultId = string.Empty;
        var lastOutgoingDefaultId = string.Empty;
        
        var existLastId = !string.IsNullOrWhiteSpace(lastIncomingId) || !string.IsNullOrWhiteSpace(lastOutgoingId);
        
        if (existLastId)
        {
          lastIncomingDefaultId = string.IsNullOrWhiteSpace(lastIncomingId) ? lastOutgoingId : lastIncomingId;
          lastOutgoingDefaultId = string.IsNullOrWhiteSpace(lastOutgoingId) ? lastIncomingId : lastOutgoingId;
        }
        
        this.LogDebugFormat(businessUnitBox, "Start receiving messages from the service.");
        var messages = (existLastId ? client.GetMessages(lastIncomingDefaultId, lastOutgoingDefaultId, out lastIncomingEventId, out lastOutgoingEventId) :
                        client.GetMessages(Calendar.Today.ToUtcTime().Value, out lastIncomingEventId, out lastOutgoingEventId)).ToList();
        this.LogDebugFormat(businessUnitBox, "Done receiving messages from the service. Count messages = '{0}'.", messages.Count);
        
        foreach (var message in messages)
        {
          // Добавление в очередь.
          this.CreateQueueItem(businessUnitBox, message);
        }
        
        if (!string.IsNullOrEmpty(lastIncomingEventId) && !Equals(lastIncomingEventId, lastIncomingId))
          this.UpdateLastIncomingMessageId(businessUnitBox, lastIncomingEventId);
        
        if (!string.IsNullOrEmpty(lastOutgoingEventId) && !Equals(lastOutgoingEventId, lastOutgoingId))
          this.UpdateLastOutgoingMessageId(businessUnitBox, lastOutgoingEventId);
        
        var queueItems = ExchangeCore.MessageQueueItems.GetAll(q => Equals(q.RootBox, businessUnitBox) &&
                                                               !Equals(q.ProcessingStatus, ExchangeCore.MessageQueueItem.ProcessingStatus.Suspended)).ToList();
        
        // Дозагрузка сообщений из очереди.
        this.LoadMessagesFromQueueItems(messages, queueItems, client);

        // Обработка сообщений из очереди.
        this.ProcessMessages(messages, queueItems, client);

        // Удаление из очереди обработанных сообщений.
        this.DeleteProcessedQueueItems(businessUnitBox, client);
      }
      catch (Exception ex)
      {
        Logger.ErrorFormat(ExchangeCore.BusinessUnitBoxes.Resources.BoxError, ex, businessUnitBox.Id);
      }
    }

    /// <summary>
    /// Получение легких сообщений с сервиса обмена.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    /// <param name="currentIncomingId">Id входящего сообщения (текущее значение в базе).</param>
    /// <param name="currentOutgoingId">Id исхожящего сообщения (текущее значение в базе).</param>
    /// <param name="lastIncomingId">Id входящего сообщения, на котором закончилась обработка.</param>
    /// <param name="lastOutgoingId">Id исходящего сообщения, на котором закончилась обработка.</param>
    /// <param name="startingDate">Дата начала загрузки сообщений.</param>
    /// <returns>Список легких сообщений DCX.</returns>
    private List<NpoComputer.DCX.Common.IMessage> GetLiteMessages(ExchangeCore.IBusinessUnitBox businessUnitBox,
                                                                  string currentIncomingId, string currentOutgoingId,
                                                                  out string lastIncomingId, out string lastOutgoingId,
                                                                  DateTime? startingDate = null)
    {
      this.LogDebugFormat(businessUnitBox, "Execute GetLiteMessages. Start receiving messages from the service.");
      
      var client = GetClient(businessUnitBox);
      var maxMessagesToLoading = this.GetMaxMessagesToLoading();
      lastIncomingId = string.Empty;
      lastOutgoingId = string.Empty;
      var currentIncomingDefaultId = string.Empty;
      var currentOutgoingDefaultId = string.Empty;
      var allMessages = new List<IMessage>();
      
      for (int i = 0; i < this.GetMaxAttemptsToReceiveMessages(); i++)
      {
        var existLastId = !string.IsNullOrWhiteSpace(currentIncomingId) || !string.IsNullOrWhiteSpace(currentOutgoingId);
        
        if (existLastId)
        {
          currentIncomingDefaultId = string.IsNullOrWhiteSpace(currentIncomingId) ? currentOutgoingId : currentIncomingId;
          currentOutgoingDefaultId = string.IsNullOrWhiteSpace(currentOutgoingId) ? currentIncomingId : currentOutgoingId;
        }

        var messages = existLastId ?
          client.GetLiteMessages(currentIncomingDefaultId, currentOutgoingDefaultId, out lastIncomingId, out lastOutgoingId).ToList() :
          client.GetLiteMessages(startingDate ?? Calendar.Today.ToUtcTime().Value, out lastIncomingId, out lastOutgoingId).ToList();
        
        allMessages.AddRange(messages);
        currentIncomingId = lastIncomingId;
        currentOutgoingId = lastOutgoingId;
        
        if (allMessages.Count >= maxMessagesToLoading || messages.Count == 0)
          break;
      }
      
      this.LogDebugFormat(businessUnitBox, "Done GetLiteMessages. Count messages: '{0}'.", allMessages.Count);

      return allMessages;
    }
    
    /// <summary>
    /// Отфильтровать легкие сообшения.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    /// <param name="messages">Сообщения.</param>
    /// <returns>Список отфильтрованных сообщений.</returns>
    public virtual List<NpoComputer.DCX.Common.IMessage> FilterLiteMessages(ExchangeCore.IBusinessUnitBox businessUnitBox, List<NpoComputer.DCX.Common.IMessage> messages)
    {
      return messages;
    }

    /// <summary>
    /// Запустить асинхронную обработку для сгруппированных элементов очереди сообщений.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    /// <param name="messages">Сообщения DCX.</param>
    /// <param name="downloadSession">Сессия исторической загрузки.</param>
    public void RunMessagesOnAsyncHandler(ExchangeCore.IBusinessUnitBox businessUnitBox, List<NpoComputer.DCX.Common.IMessage> messages,
                                          IHistoricalMessagesDownloadSession downloadSession = null)
    {
      this.LogDebugFormat(businessUnitBox, string.Format("Execute RunMessagesOnAsyncHandler. Messages count: '{0}'", messages.Count()));

      foreach (var messageGroup in messages.GroupBy(m => m.ParentServiceMessageId))
      {
        var asyncHandlerId = Guid.NewGuid().ToString();
        var notProcessedQueueItems = new List<ExchangeCore.IMessageQueueItem>();
        
        notProcessedQueueItems.AddRange(this.CreateLiteQueueItems(businessUnitBox, messageGroup.ToList(), asyncHandlerId, downloadSession));
        
        var rootMessageId = messageGroup.First().ParentServiceMessageId;
        notProcessedQueueItems.AddRange(this.FillExistingQueueItemsAsyncHandlerId(businessUnitBox, rootMessageId, asyncHandlerId));
        
        if (notProcessedQueueItems.Any())
        {
          var queueItemIds = string.Join(",", notProcessedQueueItems.Select(q => q.Id));
          this.ExecuteProcessMessagesAsyncHandler(asyncHandlerId, queueItemIds);
        }
      }
      
      this.LogDebugFormat(businessUnitBox, "Done RunMessagesOnAsyncHandler.");
    }
    
    /// <summary>
    /// Заполнить Ид асинхроного обработчика для существующих не обработанных элементов очереди сообщений.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    /// <param name="rootMessageId">Ид родительского сообщения.</param>
    /// <param name="asyncHandlerId">Ид асинхронного обработчика.</param>
    /// <returns>Список элементов очереди сообщений с обновленными Ид асинхронного обработчика.</returns>
    protected virtual List<IMessageQueueItem> FillExistingQueueItemsAsyncHandlerId(IBusinessUnitBox businessUnitBox, string rootMessageId, string asyncHandlerId)
    {
      this.LogDebugFormat(businessUnitBox, "Execute FillExistingQueueItemsAsyncHandlerId. RootMessageId: {0}, AsyncHandlerId: {1}", rootMessageId, asyncHandlerId);

      var notProcessedQueueItems = ExchangeCore.MessageQueueItems.GetAll(q => Equals(q.RootBox, businessUnitBox) &&
                                                                         string.Equals(q.RootMessageId, rootMessageId) &&
                                                                         q.ProcessingStatus != ExchangeCore.MessageQueueItem.ProcessingStatus.Suspended &&
                                                                         q.ProcessingStatus != ExchangeCore.MessageQueueItem.ProcessingStatus.Processed &&
                                                                         q.AsyncHandlerId == null).ToList();
      foreach (var queueItem in notProcessedQueueItems)
      {
        queueItem.AsyncHandlerId = asyncHandlerId;
        queueItem.Save();
        this.LogDebugFormat(businessUnitBox, "Done FillExistingQueueItemsAsyncHandlerId. Fill AsyncHandlerId {0} for old queue item with Id {1}", asyncHandlerId, queueItem.Id);
      }
      
      return notProcessedQueueItems;
    }
    
    /// <summary>
    /// Создать элементы очереди.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    /// <param name="messages">Сообщения DCX.</param>
    /// <param name="asyncHandlerId">Ид асинхронного обработчика.</param>
    /// <param name="downloadSession">Сессия исторической загрузки.</param>
    /// <returns>Список созданных элементов очереди сообщений.</returns>
    public List<IMessageQueueItem> CreateLiteQueueItems(ExchangeCore.IBusinessUnitBox businessUnitBox, List<NpoComputer.DCX.Common.IMessage> messages,
                                                        string asyncHandlerId, IHistoricalMessagesDownloadSession downloadSession = null)
    {
      this.LogDebugFormat(businessUnitBox, string.Format("Execute CreateLiteQueueItems. Messages count: '{0}'", messages.Count()));
      var createdQueueItems = new List<ExchangeCore.IMessageQueueItem>();
      foreach (var message in messages)
      {
        var queueItem = ExchangeCore.MessageQueueItems.GetAll(q => Equals(q.RootBox, businessUnitBox) && q.ExternalId == message.ServiceMessageId).FirstOrDefault();
        
        if (queueItem == null)
        {
          createdQueueItems.Add(this.CreateLiteQueueItem(businessUnitBox, message, asyncHandlerId, downloadSession));
        }
        else
        {
          if (downloadSession != null && queueItem.DownloadSession != null &&
              queueItem.ProcessingStatus != Sungero.ExchangeCore.MessageQueueItem.ProcessingStatus.Processed)
          {
            try
            {
              queueItem.DownloadSession = downloadSession;
              queueItem.IsManualRestart = false;
              queueItem.ProcessingStatus = Sungero.ExchangeCore.MessageQueueItem.ProcessingStatus.NotProcessed;
              queueItem.AsyncHandlerId = null;
              queueItem.Save();
              this.LogDebugFormat(queueItem, string.Format("CreateLiteQueueItems. Queue item download session was updated, message id {0}.", message.ServiceMessageId));
            }
            catch (Exception ex)
            {
              var logMessage = string.Format("CreateLiteQueueItems. Failed updating queue item download session, message id {0}. Error: {1}", message.ServiceMessageId, ex.Message);
              this.LogDebugFormat(queueItem, logMessage);
            }
          }
          else
            this.LogDebugFormat(businessUnitBox, string.Format("CreateLiteQueueItems. Message with id {0} already exists.", message.ServiceMessageId));
        }
      }
      this.LogDebugFormat(businessUnitBox, "Done CreateLiteQueueItems.");

      return createdQueueItems;
    }
    
    /// <summary>
    /// Обновить ид последних полученных сообщений.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    /// <param name="currentIncomingId">Id входящего сообщения (текущее значение в базе).</param>
    /// <param name="currentOutgoingId">Id исходящего сообщения (текущее значение в базе).</param>
    /// <param name="lastIncomingId">Id входящего сообщения, на котором закончилась обработка.</param>
    /// <param name="lastOutgoingId">Id исходящего сообщения, на котором закончилась обработка.</param>
    public void UpdateLastMessageIds(ExchangeCore.IBusinessUnitBox businessUnitBox,
                                     string currentIncomingId, string currentOutgoingId,
                                     string lastIncomingId, string lastOutgoingId)
    {
      this.LogDebugFormat(businessUnitBox, "Execute UpdateLastMessageIds.");
      
      if (!string.IsNullOrEmpty(lastIncomingId) && !Equals(lastIncomingId, currentIncomingId))
        this.UpdateLastIncomingMessageId(businessUnitBox, lastIncomingId);
      
      if (!string.IsNullOrEmpty(lastOutgoingId) && !Equals(lastOutgoingId, currentOutgoingId))
        this.UpdateLastOutgoingMessageId(businessUnitBox, lastOutgoingId);
      
      this.LogDebugFormat(businessUnitBox, "UpdateLastMessageIds. Done.");
    }
    
    /// <summary>
    /// Получить максимальное количество загружаемых сообщений за одно выполнение фонового процесса "Получение сообщений".
    /// </summary>
    /// <returns>Максимальное количество загружаемых сообщений.</returns>
    public virtual int GetMaxMessagesToLoading()
    {
      return Exchange.Constants.Module.MaxMessagesToLoading;
    }
    
    /// <summary>
    /// Получить максимальное количество попыток получения сообщений.
    /// </summary>
    /// <returns>Максимальное количество попыток получения сообщений.</returns>
    public virtual int GetMaxAttemptsToReceiveMessages()
    {
      return Exchange.Constants.Module.MaxAttemptsToReceiveMessages;
    }
    
    /// <summary>
    /// Запустить асинхронную обработку для созданных ранее элементов очереди сообщений.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    /// <param name="isHistoricalDownloading">Признак запуска асинхронного обработчика в контексте исторической загрузки.</param>
    public virtual void RunOldMessagesOnAsyncHandler(ExchangeCore.IBusinessUnitBox businessUnitBox, bool isHistoricalDownloading = false)
    {
      this.LogDebugFormat(businessUnitBox, "Execute RunOldMessagesOnAsyncHandler.");
      
      var queueItems = ExchangeCore.MessageQueueItems.GetAll(q => Equals(q.RootBox, businessUnitBox) && q.AsyncHandlerId == null &&
                                                             q.ProcessingStatus != ExchangeCore.MessageQueueItem.ProcessingStatus.Suspended &&
                                                             (q.DownloadSession != null) == isHistoricalDownloading);
      
      foreach (var queueItemGroup in queueItems.GroupBy(q => q.RootMessageId ?? q.ExternalId))
      {
        var orderedQueueItemIds = queueItemGroup.Select(q => q.Id).ToList();
        
        var queueItemIds = string.Join(",", orderedQueueItemIds);
        var asyncHandlerId = Guid.NewGuid().ToString();
        
        try
        {
          foreach (var queueItem in queueItemGroup)
          {
            queueItem.AsyncHandlerId = asyncHandlerId;
            queueItem.Save();
            this.LogDebugFormat(queueItem, "Save AsyncHandlerId to queueItem.");
          }
          
          this.ExecuteProcessMessagesAsyncHandler(asyncHandlerId, queueItemIds);
        }
        catch (Exception e)
        {
          this.LogDebugFormat(string.Format("RunOldMessagesOnAsyncHandler. AsyncHandlerId: '{0}', queueItemIds '{1}'. Error: {2}", asyncHandlerId, queueItemIds, e.Message));
        }
      }
      
      this.LogDebugFormat(businessUnitBox, "Done RunOldMessagesOnAsyncHandler.");
    }
    
    /// <summary>
    /// Обработка сообщений очереди.
    /// </summary>
    /// <param name="messages">Сообщения.</param>
    /// <param name="queueItems">Элементы очереди.</param>
    /// <param name="client">Клиент.</param>
    [Obsolete("Логика перенесена в асинхронный обработчик Sungero.Exchange.AsyncHandlers.ProcessMessages.")]
    protected virtual void ProcessMessages(List<IMessage> messages, List<IMessageQueueItem> queueItems, DcxClient client)
    {
      this.LogDebugFormat("Start process messages.");
      
      var queueItemsIds = queueItems.Select(q => q.Id).ToList();
      foreach (var message in messages)
      {
        if (message.HasErrors == true)
        {
          Logger.ErrorFormat("Message Id {0} not processed, service error {1}", message.ServiceMessageId, message.ErrorText);
          continue;
        }
        
        // Сообщение из сервиса.
        this.LogDebugFormat(message, "Start process message.");
        this.LogFullMessage(message);

        Transactions.Execute(
          () =>
          {
            var transactionQueueItems = ExchangeCore.MessageQueueItems.GetAll(q => queueItemsIds.Contains(q.Id)).ToList();
            if (this.ProcessMessage(message, transactionQueueItems, client))
            {
              var queueItem = transactionQueueItems.Single(x => x.ExternalId == message.ServiceMessageId);
              queueItem.ProcessingStatus = ExchangeCore.MessageQueueItem.ProcessingStatus.Processed;
              queueItem.Save();
              this.LogDebugFormat(message, "Message processed successfully.");
            }
            else
            {
              var queueItem = transactionQueueItems.SingleOrDefault(x => x.ExternalId == message.ServiceMessageId);
              if (queueItem != null)
              {
                queueItem.Retries += 1;
                queueItem.Save();
                this.LogDebugFormat(message, "Process message failed. Retries: '{0}'.", queueItem.Retries);
              }
            }
          });

        this.LogDebugFormat(message, "End process message.");
      }
      
      this.LogDebugFormat("End process messages.");
    }
    
    /// <summary>
    /// Добавление сообщений в очередь.
    /// </summary>
    /// <param name="businessUnitBoxId">ИД ящика НОР.</param>
    /// <param name="serviceMessageId">ИД Сообщение.</param>
    [Remote]
    public virtual void CreateQueueItem(int businessUnitBoxId, string serviceMessageId)
    {
      var businessUnitBox = BusinessUnitBoxes.Get(businessUnitBoxId);
      var client = GetClient(businessUnitBox);
      var serviceMessage = client.GetMessage(serviceMessageId);
      this.CreateQueueItem(businessUnitBox, serviceMessage);
      RequeueMessagesGet();
    }
    
    /// <summary>
    /// Получить ящик из сообщения.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик нашей организации.</param>
    /// <param name="message">Сообщение.</param>
    /// <returns>Ящик.</returns>
    protected virtual IBoxBase GetMessageBox(IBusinessUnitBox businessUnitBox, IMessage message)
    {
      if (message.Sender == null || message.Sender.Organization == null)
        return businessUnitBox;
      
      var organizationId = message.Sender.Organization.OrganizationId;
      bool isIncomingMessage = organizationId != businessUnitBox.OrganizationId;
      var box = ExchangeCore.BoxBases.Null;
      if (isIncomingMessage && message.ToDepartment != null && ExchangeCore.DepartmentBoxes
          .GetAll(x => x.ServiceId == message.ToDepartment.Id && x.Status == CoreEntities.DatabookEntry.Status.Active).Any())
        box = ExchangeCore.DepartmentBoxes.GetAll(x => x.ServiceId == message.ToDepartment.Id && x.Status == CoreEntities.DatabookEntry.Status.Active)
          .Single();
      else if (!isIncomingMessage && message.FromDepartment != null && ExchangeCore.DepartmentBoxes
               .GetAll(x => x.ServiceId == message.FromDepartment.Id && x.Status == CoreEntities.DatabookEntry.Status.Active).Any())
        box = ExchangeCore.DepartmentBoxes.GetAll(x => x.ServiceId == message.FromDepartment.Id && x.Status == CoreEntities.DatabookEntry.Status.Active)
          .Single();
      else
        box = businessUnitBox;
      
      return box;
    }
    
    /// <summary>
    /// Добавление сообщений в очередь.
    /// </summary>
    /// <param name="businessUnitBox">Ящик НОР.</param>
    /// <param name="message">Сообщение.</param>
    protected virtual void CreateQueueItem(IBusinessUnitBox businessUnitBox, IMessage message)
    {
      if (ExchangeCore.MessageQueueItems.GetAll(q => Equals(q.RootBox, businessUnitBox) && q.ExternalId == message.ServiceMessageId).Any())
        return;
      
      var queueItem = ExchangeCore.MessageQueueItems.Create();
      queueItem.ExternalId = message.ServiceMessageId;
      queueItem.Box = this.GetMessageBox(businessUnitBox, message);
      queueItem.RootBox = businessUnitBox;
      queueItem.ProcessingStatus = ExchangeCore.MessageQueueItem.ProcessingStatus.NotProcessed;
      queueItem.Created = Calendar.Now;
      queueItem.Name = message.ServiceMessageId;
      
      if (!message.HasErrors)
      {
        var organizationId = message.Sender.Organization.OrganizationId;
        bool isIncomingMessage = organizationId != businessUnitBox.OrganizationId;
        queueItem.CounterpartyExternalId = isIncomingMessage ? organizationId : message.Receiver.Organization.OrganizationId;
      }
      
      if (message.HasErrors)
      {
        var note = string.Format("{0}{1}{2}", queueItem.Note, Environment.NewLine, message.ErrorText);
        if (note.Length > 1000)
          note = note.Substring(0, 1000);
        queueItem.Note = note;
      }
      
      foreach (var primary in message.PrimaryDocuments)
      {
        var itemDocument = queueItem.Documents.AddNew();
        itemDocument.ExternalId = primary.ServiceEntityId;
        itemDocument.Type = ExchangeCore.MessageQueueItemDocuments.Type.Primary;
      }

      foreach (var reglament in message.ReglamentDocuments)
      {
        var itemDocument = queueItem.Documents.AddNew();
        itemDocument.ExternalId = reglament.ServiceEntityId;
        itemDocument.Type = ExchangeCore.MessageQueueItemDocuments.Type.Reglament;
      }

      queueItem.Save();
      this.LogDebugFormat(message, queueItem, businessUnitBox, "CreateQueueItem. Add message to queue item.");
    }
    
    /// <summary>
    /// Добавление легкого сообщения в очередь.
    /// </summary>
    /// <param name="businessUnitBox">Ящик НОР.</param>
    /// <param name="message">Сообщение.</param>
    /// <param name="asyncHanderId">Ид асинхронного обработчика.</param>
    /// <param name="downloadSession">Сессия исторической загрузки.</param>
    /// <returns>Созданный элемент очереди.</returns>
    protected virtual IMessageQueueItem CreateLiteQueueItem(IBusinessUnitBox businessUnitBox, IMessage message, string asyncHanderId,
                                                            IHistoricalMessagesDownloadSession downloadSession = null)
    {
      this.LogDebugFormat(message, businessUnitBox, "Execute CreateLiteQueueItem.");
      var queueItem = ExchangeCore.MessageQueueItems.Create();
      queueItem.ExternalId = message.ServiceMessageId;
      queueItem.RootMessageId = message.ParentServiceMessageId;
      queueItem.IsRootMessage = message.IsRoot;
      queueItem.Box = this.GetMessageBox(businessUnitBox, message);
      queueItem.RootBox = businessUnitBox;
      queueItem.ProcessingStatus = ExchangeCore.MessageQueueItem.ProcessingStatus.NotProcessed;
      queueItem.Created = Calendar.Now;
      queueItem.Name = message.ServiceMessageId;
      queueItem.AsyncHandlerId = asyncHanderId;
      queueItem.DownloadSession = downloadSession;
      
      if (message.HasErrors)
      {
        var note = string.Format("{0}{1}{2}", queueItem.Note, Environment.NewLine, message.ErrorText);
        if (note.Length > 1000)
          note = note.Substring(0, 1000);
        queueItem.Note = note;
      }

      queueItem.Save();
      this.LogDebugFormat(message, queueItem, businessUnitBox, "Done CreateLiteQueueItem. Queue item created successfully.");
      
      return queueItem;
    }
    
    /// <summary>
    /// Запустить асинхронную обработку элементов очереди сообщений.
    /// </summary>
    /// <param name="asyncHandlerId">Ид асинхронного обработчика.</param>
    /// <param name="queueItemIds">Ид элементов очереди сообщений.</param>
    protected virtual void ExecuteProcessMessagesAsyncHandler(string asyncHandlerId, string queueItemIds)
    {
      var processMessagesHandler = Sungero.Exchange.AsyncHandlers.ProcessMessages.Create();
      processMessagesHandler.QueueItemIds = queueItemIds;
      processMessagesHandler.AsyncHandlerId = asyncHandlerId;
      processMessagesHandler.ExecuteAsync();
      this.LogDebugFormat(string.Format("ExecuteProcessMessagesAsyncHandler. AsyncHandlerId: '{0}', queueItemIds '{1}'.", asyncHandlerId, queueItemIds));
    }

    /// <summary>
    /// Дозагрузка сообщений из очереди.
    /// </summary>
    /// <param name="messages">Сообщения.</param>
    /// <param name="queueItems">Элементы очереди.</param>
    /// <param name="client">Клиент.</param>
    [Obsolete("При рефакторинге отказались от этого метода.")]
    protected virtual void LoadMessagesFromQueueItems(List<IMessage> messages, List<IMessageQueueItem> queueItems, DcxClient client)
    {
      var addedItems = queueItems.Where(x => messages.All(c => c.ServiceMessageId != x.ExternalId) &&
                                        !Equals(x.ProcessingStatus, ExchangeCore.MessageQueueItem.ProcessingStatus.Processed))
        .ToList();
      
      this.LogDebugFormat(string.Format("LoadMessagesFromQueueItems. Added queue items count: {0}.", addedItems.Count));
      
      foreach (var queueItem in addedItems)
      {
        var added = Transactions.Execute(
          () =>
          {
            var message = client.GetMessage(queueItem.ExternalId);
            if (message != null)
              messages.Add(message);
            else
            {
              queueItem.ProcessingStatus = ExchangeCore.MessageQueueItem.ProcessingStatus.Processed;
              queueItem.Save();
              Logger.DebugFormat("Exchange. Not found service message with Id = '{0}'.", queueItem.ExternalId);
            }
          });
        if (!added)
        {
          Transactions.Execute(
            () => { ExchangeCore.PublicFunctions.QueueItemBase.QueueItemOnError(queueItem, Resources.GetMessageFailed); });
          this.LogDebugFormat(queueItem, Resources.GetMessageFailed);
        }
      }
    }
    
    /// <summary>
    /// Удалить обработанные элементы очереди.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    /// <param name="client">Dcx клиент.</param>
    [Obsolete("Используйте DeleteProcessedQueueItem для каждого элемента очереди.")]
    protected virtual void DeleteProcessedQueueItems(IBusinessUnitBox businessUnitBox, DcxClient client)
    {
      var processedQueueItems = ExchangeCore.MessageQueueItems.GetAll(q => Equals(q.RootBox, businessUnitBox))
        .Where(q => Equals(q.ProcessingStatus, ExchangeCore.MessageQueueItem.ProcessingStatus.Processed))
        .ToList();
      foreach (var queueItem in processedQueueItems)
      {
        Transactions.Execute(
          () =>
          {
            var useRetry = false;
            var itemDocuments = queueItem.Documents.Select(d => d.ExternalId).ToList();
            var documentInfos = ExchangeDocumentInfos
              .GetAll(i => Equals(i.RootBox, businessUnitBox)
                      && Equals(i.ServiceMessageId, queueItem.ExternalId)
                      && itemDocuments.Contains(i.ServiceDocumentId)
                      && i.Document != null)
              .ToList();

            foreach (var documentInfo in documentInfos)
            {
              var canSendDeliveryConfirmation = true;
              try
              {
                canSendDeliveryConfirmation = client.CanSendDeliveryConfirmation(documentInfo.ServiceDocumentId, documentInfo.ServiceMessageId);
              }
              catch (Exception ex)
              {
                useRetry = true;
                this.LogDebugFormat(documentInfo, "Error while getting document from the service to generate delivery confirmation: {0}.", ex.Message);
              }
              
              if (!canSendDeliveryConfirmation && !useRetry)
                FixReceiptNotification(documentInfo, string.Empty, false);
            }

            if (!useRetry)
              ExchangeCore.MessageQueueItems.Delete(queueItem);
          });
      }
    }
    
    /// <summary>
    /// Удалить обработанный элемент очереди.
    /// </summary>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="client">Dcx клиент.</param>
    public virtual void DeleteProcessedQueueItem(IMessageQueueItem queueItem, NpoComputer.DCX.ClientApi.Client client)
    {
      this.LogDebugFormat(queueItem, "Execute DeleteProcessedQueueItem");
      queueItem = ExchangeCore.MessageQueueItems.GetAll(q => q.Id == queueItem.Id).Single();
      if (queueItem.ProcessingStatus != ExchangeCore.MessageQueueItem.ProcessingStatus.Processed)
        return;
      
      Transactions.Execute(
        () =>
        {
          ExchangeCore.MessageQueueItems.Delete(queueItem);
          this.LogDebugFormat(queueItem, "Deleted processed queueItem");
        });
      this.LogDebugFormat(queueItem, "Done DeleteProcessedQueueItem.");
    }
    
    /// <summary>
    /// Получить id последнего входящего сообщения.
    /// </summary>
    /// <param name="box">Ящик.</param>
    /// <returns>Id сообщения.</returns>
    [Public, Remote]
    public virtual string GetLastIncomingMessageId(ExchangeCore.IBusinessUnitBox box)
    {
      var key = string.Format(Constants.Module.LastBoxIncomingMessageId, box.Id);
      var command = string.Format(Queries.Module.GetLastMessageId, key);
      try
      {
        var executionResult = Docflow.PublicFunctions.Module.ExecuteScalarSQLCommand(command);
        var result = string.Empty;
        if (!(executionResult is DBNull) && executionResult != null)
          result = executionResult.ToString();
        this.LogDebugFormat(box, "Get messages. Last incoming message id in DB is {0}", result);
        return result;
      }
      catch (Exception ex)
      {
        this.LogDebugFormat(box, "Error while getting incoming message id. No messages in box. {0}", ex);
        return string.Empty;
      }
    }
    
    /// <summary>
    /// Получить id последнего исходящего сообщения.
    /// </summary>
    /// <param name="box">Ящик.</param>
    /// <returns>Id сообщения.</returns>
    [Public, Remote]
    public virtual string GetLastOutgoingMessageId(ExchangeCore.IBusinessUnitBox box)
    {
      var key = string.Format(Constants.Module.LastBoxOutgoingMessageId, box.Id);
      var command = string.Format(Queries.Module.GetLastMessageId, key);
      try
      {
        var executionResult = Docflow.PublicFunctions.Module.ExecuteScalarSQLCommand(command);
        var result = string.Empty;
        if (!(executionResult is DBNull) && executionResult != null)
          result = executionResult.ToString();
        this.LogDebugFormat(box, "Get messages. Last outgoing message id in DB is {0}", result);
        return result;
      }
      catch (Exception ex)
      {
        this.LogDebugFormat(box, "Error while getting outgoing message id. No messages in box. {0}", ex);
        return string.Empty;
      }
    }
    
    /// <summary>
    /// Обновить id полученного входящего сообщения.
    /// </summary>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="incomingMessageId">Новый id.</param>
    [Public, Remote]
    public virtual void UpdateLastIncomingMessageId(ExchangeCore.IBusinessUnitBox box, string incomingMessageId)
    {
      var key = string.Format(Constants.Module.LastBoxIncomingMessageId, box.Id);
      
      Docflow.PublicFunctions.Module.ExecuteSQLCommandFormat(Queries.Module.UpdateLastMessageId, new[] { key, incomingMessageId });
      this.LogDebugFormat(box, "Last box incoming message id is set to {0}", incomingMessageId);
    }
    
    /// <summary>
    /// Обновить id полученного исходящего сообщения.
    /// </summary>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="outgoingMessageId">Новый id.</param>
    [Public, Remote]
    public virtual void UpdateLastOutgoingMessageId(ExchangeCore.IBusinessUnitBox box, string outgoingMessageId)
    {
      var key = string.Format(Constants.Module.LastBoxOutgoingMessageId, box.Id);
      
      Docflow.PublicFunctions.Module.ExecuteSQLCommandFormat(Queries.Module.UpdateLastMessageId, new[] { key, outgoingMessageId });
      this.LogDebugFormat(box, "Last box outgoing message id is set to {0}", outgoingMessageId);
    }

    #endregion

    #region Обработка легкого сообщения
    
    /// <summary>
    /// Обработать легкий элемент очереди сообщений.
    /// </summary>
    /// <param name="queueItem">Элемент очереди сообщений.</param>
    /// <param name="asyncHandlerId">Ид ассинхронного события.</param>
    /// <param name="client">Dcx клиент.</param>
    /// <returns>True - если элемент очереди обработался успешно или не требует обработки, иначе - false.</returns>
    public virtual bool ProcessMessageLiteQueueItem(IMessageQueueItem queueItem, string asyncHandlerId, NpoComputer.DCX.ClientApi.Client client)
    {
      this.LogDebugFormat(queueItem, "Execute ProcessMessageLiteQueueItem.");
      
      try
      {
        if (!this.NeedSkipMessageQueueItemProcessing(queueItem, client, asyncHandlerId))
        {
          var message = this.GetMessageFromQueueItem(queueItem, client);

          // Удаляем элемент очереди, когда на сервисе отсутствует сообщение.
          if (message == null)
          {
            Sungero.ExchangeCore.MessageQueueItems.Delete(queueItem);
            this.LogDebugFormat(queueItem, "Not found service message with Id = '{0}'.", queueItem.ExternalId);
            return false;
          }
          
          if (!this.IsMessageValid(message, queueItem))
            return false;
          
          this.LogFullMessage(message);
          this.FillQueueItem(queueItem, message);
          
          if (!this.ProcessMessageQueueItem(client, message, queueItem))
            return false;
        }
        
        this.DeleteProcessedQueueItem(queueItem, client);
        return true;
      }
      catch (Exception ex)
      {
        this.LogErrorFormat(queueItem, "Process message`s lite queue item has error.", ex);
        return false;
      }
      finally
      {
        this.LogDebugFormat(queueItem, "Done ProcessMessageLiteQueueItem.");
      }
    }

    /// <summary>
    /// Получить сообщение из сервиса обмена.
    /// </summary>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="client">Dcx клиент.</param>
    /// <returns>Сообщение.</returns>
    public virtual NpoComputer.DCX.Common.IMessage GetMessageFromQueueItem(IMessageQueueItem queueItem, NpoComputer.DCX.ClientApi.Client client)
    {
      NpoComputer.DCX.Common.IMessage message = null;
      try
      {
        message = client.GetMessage(queueItem.ExternalId);
      }
      catch (Exception ex)
      {
        Transactions.Execute(() =>
                             {
                               queueItem.ProcessingStatus = ExchangeCore.QueueItemBase.ProcessingStatus.Error;
                               queueItem.Note = Resources.GetMessageFailed;
                               queueItem.Save();
                             });
        this.LogErrorFormat(queueItem, "GetMessageFromQueueItem. An error occurred while getting the message from service.", ex);
        message = new NpoComputer.DCX.Common.Message();
        message.HasErrors = true;
        message.ErrorText = ex.Message;
      }
      return message;
    }

    /// <summary>
    /// Обработать элемент очереди сообщений.
    /// </summary>
    /// <param name="client">Клиент DCX.</param>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    /// <param name="queueItem">Элемент очереди сообщений.</param>
    /// <returns>True - если обработка успешна, иначе - false.</returns>
    public virtual bool ProcessMessageQueueItem(NpoComputer.DCX.ClientApi.Client client, NpoComputer.DCX.Common.IMessage message, IMessageQueueItem queueItem)
    {
      this.LogDebugFormat(queueItem, "Execute ProcessMessageQueueItem.");
      bool processMessageSuccess = true;

      var result = Transactions.Execute(
        () => {
          if (this.ProcessMessage(message, queueItem, client))
          {
            var transactionQueueItem = ExchangeCore.MessageQueueItems.GetAll(q => q.Id == queueItem.Id).Single();
            transactionQueueItem.ProcessingStatus = ExchangeCore.MessageQueueItem.ProcessingStatus.Processed;
            transactionQueueItem.Save();
            this.LogDebugFormat(transactionQueueItem, "Message processed successfully.");
          }
          else
            processMessageSuccess = false;
        });

      if (!result || !processMessageSuccess)
      {
        this.LogDebugFormat(queueItem, "Process message failed.");
        return false;
      }
      this.LogDebugFormat(queueItem, "Done ProcessMessageQueueItem.");
      return true;
    }

    /// <summary>
    /// Добавить документы из сообщения к элементу очереди синхронизации сообщений.
    /// </summary>
    /// <param name="queueItem">Элементу очереди синхронизации сообщений.</param>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    public virtual void FillQueueItem(IMessageQueueItem queueItem, NpoComputer.DCX.Common.IMessage message)
    {
      this.LogDebugFormat(queueItem, "Execute FillQueueItem.");

      if (!message.HasErrors)
      {
        var organizationId = message.Sender.Organization.OrganizationId;
        bool isIncomingMessage = organizationId != queueItem.RootBox.OrganizationId;
        queueItem.CounterpartyExternalId = isIncomingMessage ? organizationId : message.Receiver.Organization.OrganizationId;
      }
      
      foreach (var primary in message.PrimaryDocuments)
      {
        if (!queueItem.Documents.Any(d => d.ExternalId == primary.ServiceEntityId &&
                                     Equals(d.Type, ExchangeCore.MessageQueueItemDocuments.Type.Primary)))
        {
          var itemDocument = queueItem.Documents.AddNew();
          itemDocument.ExternalId = primary.ServiceEntityId;
          itemDocument.Type = ExchangeCore.MessageQueueItemDocuments.Type.Primary;
        }
      }

      foreach (var reglament in message.ReglamentDocuments)
      {
        if (!queueItem.Documents.Any(d => d.ExternalId == reglament.ServiceEntityId &&
                                     Equals(d.Type, ExchangeCore.MessageQueueItemDocuments.Type.Reglament)))
        {
          var itemDocument = queueItem.Documents.AddNew();
          itemDocument.ExternalId = reglament.ServiceEntityId;
          itemDocument.Type = ExchangeCore.MessageQueueItemDocuments.Type.Reglament;
        }
      }

      if (queueItem.State.IsChanged)
        queueItem.Save();

      this.LogDebugFormat(queueItem, "Done FillQueueItem.");
    }

    /// <summary>
    /// Проверить, нужно ли пропустить обработку элемента очереди сообщений.
    /// </summary>
    /// <param name="queueItem">Элемент очереди сообщений.</param>
    /// <param name="client">Клиент DCX.</param>
    /// <param name="asyncHandlerId">Ид асинхронного обработчика.</param>
    /// <returns>True - если требуется пропустить обработку элемента очереди сообщений, иначе - false.</returns>
    public virtual bool NeedSkipMessageQueueItemProcessing(IMessageQueueItem queueItem, NpoComputer.DCX.ClientApi.Client client, string asyncHandlerId)
    {
      if (queueItem.ProcessingStatus == ExchangeCore.MessageQueueItem.ProcessingStatus.Suspended ||
          queueItem.ProcessingStatus == ExchangeCore.MessageQueueItem.ProcessingStatus.Processed ||
          !string.Equals(queueItem.AsyncHandlerId, asyncHandlerId))
      {
        this.LogDebugFormat(queueItem, "Skip queueItem: AsyncHandlerId: '{0}', ProcessingStatus: '{1}'. AsyncHandlerId: {2}.",
                            queueItem.AsyncHandlerId, queueItem.ProcessingStatus, asyncHandlerId);
        
        return true;
      }
      
      return false;
    }

    /// <summary>
    /// Проверить сообщение из сервиса обмена на наличие ошибок.
    /// </summary>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    /// <param name="queueItem">Связанный элемент очереди сообщений.</param>
    /// <returns>True - если сообщение без ошибок, иначе - false.</returns>
    public virtual bool IsMessageValid(NpoComputer.DCX.Common.IMessage message, IMessageQueueItem queueItem)
    {
      if (message.HasErrors == true)
      {
        this.LogDebugFormat(queueItem, "Message Id {0} not processed, service error {1}", message.ServiceMessageId, message.ErrorText);
        return false;
      }

      return true;
    }
    
    /// <summary>
    /// Проверить, обработано ли корневое сообщение для элемента очереди сообщений.
    /// </summary>
    /// <param name="queueItem">Элемент очереди сообщений.</param>
    /// <returns>True - если корневое сообщение обработано, либо текущее является корневым, иначе - false.</returns>
    public virtual bool IsRootMessageQueueItemProcessed(IMessageQueueItem queueItem)
    {
      this.LogDebugFormat(queueItem, "Execute IsRootMessageQueueItemProcessed");
      var rootMessageNotProcessed = Sungero.ExchangeCore.MessageQueueItems.GetAll(q => Equals(q.RootBox, queueItem.RootBox) &&
                                                                                  string.Equals(q.RootMessageId, queueItem.RootMessageId) && q.IsRootMessage == true &&
                                                                                  q.ProcessingStatus != ExchangeCore.MessageQueueItem.ProcessingStatus.Processed).Any();
      
      if (!rootMessageNotProcessed || queueItem.IsRootMessage == true)
        return true;
      
      return false;
    }

    #endregion
    
    #region Обработка одного сообщения

    /// <summary>
    /// Обработать сообщение.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Обрабатываемый элемент очереди.</param>
    /// <param name="client">Клиент.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    public virtual bool ProcessMessage(NpoComputer.DCX.Common.IMessage message, IMessageQueueItem queueItem, NpoComputer.DCX.ClientApi.Client client)
    {
      var queueItems = ExchangeCore.MessageQueueItems.GetAll(q => q.Id == queueItem.Id).ToList();
      return this.ProcessMessage(message, queueItems, client);
    }
    
    /// <summary>
    /// Обработать сообщение.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItems">Обрабатываемые элементы очереди.</param>
    /// <param name="client">Клиент.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    public virtual bool ProcessMessage(NpoComputer.DCX.Common.IMessage message, List<IMessageQueueItem> queueItems, NpoComputer.DCX.ClientApi.Client client)
    {
      var queueItem = queueItems.Single(x => x.ExternalId == message.ServiceMessageId);
      var businessUnitBox = queueItem.RootBox;
      var box = this.GetMessageBox(businessUnitBox, message);
      var isSbis = businessUnitBox.ExchangeService.ExchangeProvider == Sungero.ExchangeCore.ExchangeService.ExchangeProvider.Sbis;
      
      this.LogDebugFormat(message, queueItem, businessUnitBox, "Execute ProcessMessage.");
      
      var organizationId = message.Sender.Organization.OrganizationId;
      var isIncomingMessage = true;
      var serviceCounterpartyDepartmentId = string.Empty;
      
      // Обрабатываем исходящие сообщения для поддержки параллельных действий.
      if (organizationId == businessUnitBox.OrganizationId)
      {
        organizationId = message.Receiver.Organization.OrganizationId;
        if (message.ToDepartment != null && !message.ToDepartment.IsHead && !string.IsNullOrEmpty(message.ToDepartment.Kpp))
          serviceCounterpartyDepartmentId = message.ToDepartment.Id;
        isIncomingMessage = false;
      }
      
      var sender = Parties.Counterparties.Null;
      var serviceDepartment = isIncomingMessage ? message.FromDepartment : message.ToDepartment;
      var isLegalEntity = isIncomingMessage ?
        message.Sender.Organization.OrganizationType == OrganizationType.LegalEntity :
        message.Receiver.Organization.OrganizationType == OrganizationType.LegalEntity;
      
      if (!isLegalEntity && isSbis)
        serviceCounterpartyDepartmentId = GetIndividualServiceCounterpartyBranchId(message, isIncomingMessage);
      else
        serviceCounterpartyDepartmentId = GetServiceCounterpartyBranchId(message, isIncomingMessage);
      
      // Ищем филиал, к которому относится подразделение.
      if (serviceDepartment != null && !serviceDepartment.IsHead && string.IsNullOrEmpty(serviceDepartment.Kpp))
        sender = CounterpartyDepartmentBoxes.GetAll()
          .Where(c => c.DepartmentId == serviceDepartment.Id && Equals(c.OrganizationId, organizationId) && Equals(businessUnitBox, c.Box) &&
                 c.Status == ExchangeCore.CounterpartyDepartmentBox.Status.Active)
          .Select(x => x.Counterparty)
          .FirstOrDefault();
      
      // Если отправлено с филиала, то ищем организацию-филиал.
      if (!string.IsNullOrEmpty(serviceCounterpartyDepartmentId))
      {
        var counterparties = Parties.Counterparties
          .GetAll(x => x.ExchangeBoxes.Any(e => Equals(e.OrganizationId, organizationId) &&
                                           Equals(businessUnitBox, e.Box) &&
                                           e.CounterpartyBranchId == serviceCounterpartyDepartmentId));
        sender = counterparties.Count() > 1 ? null : counterparties.SingleOrDefault();
      }
      
      // Если не нашли филиал, то ищем головную организацию.
      var nonsyncBranch = false;
      if (sender == null && (!isSbis || isSbis && string.IsNullOrEmpty(serviceCounterpartyDepartmentId)))
      {
        var counterparties = Parties.Counterparties
          .GetAll(x => x.ExchangeBoxes.Any(e => Equals(e.OrganizationId, organizationId) &&
                                           Equals(businessUnitBox, e.Box) &&
                                           (e.CounterpartyBranchId == null || e.CounterpartyBranchId == string.Empty)));
        sender = counterparties.Count() > 1 ? null : counterparties.SingleOrDefault();
        nonsyncBranch = sender != null;
      }
      
      if (sender == null)
      {
        /* Список контактов с сервиса для СБИСа всегда пустой, но для автоматического создания КА
         * необходимо сохранить сообщение в очереди, чтобы оно не потерялось, а также сохранить информацию
         * о КА для последующей синхронизации в RX.
         */
        if (!message.PrimaryDocuments.Any() && message.ReglamentDocuments.All(d => d.DocumentType == ReglamentDocumentType.InvoiceConfirmation ||
                                                                              d.DocumentType == ReglamentDocumentType.Receipt ||
                                                                              d.DocumentType == ReglamentDocumentType.InvoiceReceipt)
            && client.CanSynchronizeContacts)
        {
          this.ProcessInvoiceConfirmation(message, queueItem, organizationId, businessUnitBox);
          this.ProcessReceiptNotice(message, queueItem, null, isIncomingMessage, businessUnitBox);
          return true;
        }
        else if (!client.CanSynchronizeContacts)
        {
          this.AddCounterpartyQueueItem(businessUnitBox, organizationId, serviceCounterpartyDepartmentId);
        }
        
        this.LogDebugFormat(message, queueItem, businessUnitBox, "Unknown counterparty with OrganizationId: '{0}'. It is necessary to synchronize counterparties.", organizationId);
        return false;
      }
      
      // ProcessNewMessage - только первая обработка документа.
      // Все служебные документы обрабатываются в ProcessReplyMessage.
      // ИОП обрабатывается и в ProcessNewMessage, и в ProcessReplyMessage.
      // Обработка аннулирования - это всегда ProcessReplyMessage.
      var result = true;
      
      if (!message.IsReply)
        result = this.ProcessNewMessage(message, queueItem, box, businessUnitBox, sender, organizationId, isIncomingMessage);
      else
        result = this.ProcessReplyMessage(message, queueItem, queueItems, client, box, businessUnitBox, sender, organizationId, isIncomingMessage);

      // Если подразделение было не синхронизировано и документ пришел на голову, пишем примечание.
      if (nonsyncBranch && serviceDepartment != null && !serviceDepartment.IsHead)
      {
        // Если подразделение было удалено, то информацию о нем нужно запросить из сервиса.
        var departmentName = serviceDepartment.Name;
        var departmentKpp = serviceDepartment.Kpp;
        if (string.IsNullOrEmpty(departmentName))
        {
          var dcxDepartment = client.GetDepartment(organizationId, serviceDepartment.Id);
          if (dcxDepartment != null)
          {
            departmentName = dcxDepartment.Name;
            departmentKpp = dcxDepartment.Kpp;
          }
        }
        this.AddServiceCounterpartyDepartmentNote(message, isIncomingMessage, sender.TIN, departmentKpp, departmentName);
      }
      
      return result;
    }
    
    /// <summary>
    /// Добавить примечание о филиале/подразделении.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="branchInn">ИНН.</param>
    /// <param name="branchKpp">КПП.</param>
    /// <param name="branchName">Наименование филиала/подразделения.</param>
    public virtual void AddServiceCounterpartyDepartmentNote(NpoComputer.DCX.Common.IMessage message, bool isIncomingMessage, string branchInn, string branchKpp, string branchName)
    {
      this.LogDebugFormat(message, "Execute AddServiceCounterpartyDepartmentNote.");
      var note = string.Empty;
      if (isIncomingMessage && !string.IsNullOrEmpty(branchKpp))
        note = Sungero.Exchange.Resources.IncomingBranchDocumentNoteFormat(branchName, branchInn, branchKpp);
      if (!isIncomingMessage && !string.IsNullOrEmpty(branchKpp))
        note = Sungero.Exchange.Resources.OutgoingBranchDocumentNoteFormat(branchName, branchInn, branchKpp);
      if (isIncomingMessage && string.IsNullOrEmpty(branchKpp))
        note = Sungero.Exchange.Resources.IncomingDepartmentDocumentNoteFormat(branchName);
      if (!isIncomingMessage && string.IsNullOrEmpty(branchKpp))
        note = Sungero.Exchange.Resources.OutgoingDepartmentDocumentNoteFormat(branchName);
      
      var messageType = isIncomingMessage ? Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming : Sungero.Exchange.ExchangeDocumentInfo.MessageType.Outgoing;
      foreach (var primaryDocument in message.PrimaryDocuments.ToList())
      {
        var primaryDocumentInfo = ExchangeDocumentInfos.GetAll()
          .Where(i => i.ServiceDocumentId == primaryDocument.ServiceEntityId)
          .Where(i => i.MessageType == messageType)
          .FirstOrDefault();
        if (primaryDocumentInfo != null && primaryDocumentInfo.Document != null)
        {
          var exchangeDocument = primaryDocumentInfo.Document;
          exchangeDocument.Note = string.IsNullOrEmpty(exchangeDocument.Note) ? note : string.Join(Environment.NewLine, exchangeDocument.Note, note);
          var maxLength = exchangeDocument.Info.Properties.Note.Length;
          if (exchangeDocument.Note.Length > maxLength)
            exchangeDocument.Note = CutText(exchangeDocument.Note, maxLength);
          exchangeDocument.Save();
        }
      }
    }

    /// <summary>
    /// Добавить контрагента из сообщения в очередь синхронизации.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик НОР.</param>
    /// <param name="organizationId">ИД организации контрагента.</param>
    protected virtual void AddCounterpartyQueueItem(IBusinessUnitBox businessUnitBox, string organizationId)
    {
      this.AddCounterpartyQueueItem(businessUnitBox, organizationId, null);
    }
    
    /// <summary>
    /// Добавить контрагента из сообщения в очередь синхронизации.
    /// </summary>
    /// <param name="businessUnitBox">Абонентский ящик НОР.</param>
    /// <param name="organizationId">ИД организации контрагента.</param>
    /// <param name="departmentCode">Код филиала.</param>
    protected virtual void AddCounterpartyQueueItem(IBusinessUnitBox businessUnitBox, string organizationId, string departmentCode)
    {
      this.LogDebugFormat(businessUnitBox, "Execute AddCounterpartyQueueItem.");
      // ИД филиала может быть null или string.Empty, считаем что они равны.
      if (!CounterpartyQueueItems.GetAll(c => c.ExternalId == organizationId &&
                                         (c.CounterpartyBranchId ?? string.Empty) == (departmentCode ?? string.Empty) &&
                                         Equals(c.Box, businessUnitBox)).Any())
      {
        var counterpartyQueueItem = CounterpartyQueueItems.Create();
        counterpartyQueueItem.ExternalId = organizationId;
        counterpartyQueueItem.CounterpartyBranchId = departmentCode;
        counterpartyQueueItem.Box = businessUnitBox;
        counterpartyQueueItem.RootBox = businessUnitBox;
        counterpartyQueueItem.ProcessingStatus = ExchangeCore.CounterpartyQueueItem.ProcessingStatus.NotProcessed;
        counterpartyQueueItem.Save();
        this.LogDebugFormat(businessUnitBox, "Create queue item for counterparty OrganizationId {0}.", organizationId);
      }
    }
    
    /// <summary>
    /// Обработать новое сообщение.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="box">Ящик, на который получено сообщение.</param>
    /// <param name="businessUnitBox">Ящик нашей организации.</param>
    /// <param name="sender">Отправитель.</param>
    /// <param name="organizationId">Идентификатор отправителя в сервисе обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessNewMessage(IMessage message, IMessageQueueItem queueItem, IBoxBase box,
                                             IBusinessUnitBox businessUnitBox, ICounterparty sender,
                                             string organizationId, bool isIncomingMessage)
    {
      this.LogDebugFormat(message, queueItem, box, "Execute ProcessNewMessage.");
      if (isIncomingMessage && message.ReglamentDocuments.Any(x => x.DocumentType == NpoComputer.DCX.Common.ReglamentDocumentType.DeliveryFailureNotification))
      {
        return this.ProcessDeliveryFailureNotification(message, box);
      }

      if (message.PrimaryDocuments.Any())
      {
        if (Functions.Module.IsMessageWithUnsupportedDocuments(message))
        {
          this.LogDebugFormat(message, queueItem, box, "Message contains unsupported documents.");
          // Некоторые документы не поддерживаются в системе.
          return this.ProcessMessageWithUnsupportedDocuments(message, sender, isIncomingMessage, box);
        }
        else
        {
          // Создание новых документов.
          var processed = this.ProcessNewIncomingMessage(message, queueItem, sender, isIncomingMessage, box);
          
          // Загрузка сервисных документов.
          return processed && this.ProcessInvoiceConfirmation(message, queueItem, organizationId, businessUnitBox);
        }
      }
      else
      {
        // Обработка ИОП.
        return this.ProcessReceiptNotice(message, queueItem, sender, isIncomingMessage, businessUnitBox);
      }

      // Не смогли обработать - пропускаем и помечаем как обработанное.
      this.LogDebugFormat(message, queueItem, box, "Message processing was skipped. Message is marked as processed.");
      
      return true;
    }

    /// <summary>
    /// Обработать ответное сообщение.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="queueItems">Прочие необработанные элементы очереди.</param>
    /// <param name="client">Клиент.</param>
    /// <param name="box">Ящик, на который получено сообщение.</param>
    /// <param name="businessUnitBox">Ящик нашей организации.</param>
    /// <param name="sender">Отправитель.</param>
    /// <param name="organizationId">Идентификатор отправителя в сервисе обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessReplyMessage(IMessage message,
                                               IMessageQueueItem queueItem, List<IMessageQueueItem> queueItems, DcxClient client, IBoxBase box,
                                               IBusinessUnitBox businessUnitBox, ICounterparty sender, string organizationId, bool isIncomingMessage)
    {
      this.LogDebugFormat(message, queueItem, box, "Execute ProcessReplyMessage.");
      var senderName = sender.Name;
      var serviceId = message.PrimaryDocuments.Select(d => d.ServiceEntityId).FirstOrDefault() ?? string.Empty;
      var exchangeDocumentInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(businessUnitBox, serviceId);
      if (exchangeDocumentInfo != null && exchangeDocumentInfo.CounterpartyDepartmentBox != null)
        senderName = Exchange.PublicFunctions.ExchangeDocumentInfo.GetCompanyNameWithDepartment(exchangeDocumentInfo, senderName);
      var historyInfo = this.GetHistoryInfoAfterReject(sender, businessUnitBox, isIncomingMessage);
      
      var processResult = true;
      
      // см. новость https://n.sbis.ru/news/853d0065-ee05-453f-afe8-12970b29f053
      // Обработка УОП (необходимо для исторической загрузки).
      if (this.GetNotificationReceiptReglamentDocuments(message).Any())
        processResult = this.ProcessNoteReceipt(message, queueItem, sender, isIncomingMessage, businessUnitBox);
      
      // Подпись неформализованного документа.
      if (this.GetSignedNonformalizedPrimaryDocuments(message).Where(x => message.Signatures.Any(s => s.DocumentId == x.ServiceEntityId)).Any() && processResult)
        processResult = this.ProcessNonformalizedSign(message, queueItem, client, box, sender, isIncomingMessage, historyInfo.Operation, historyInfo.Comment) && processResult;

      // Отказ в подписании.
      if (this.GetRejectReglamentDocuments(message).Any() && !this.GetRevocationOfferPrimaryDocuments(message).Any() && processResult)
        processResult = this.ProcessReject(message, queueItem, isIncomingMessage, box, historyInfo.Operation, historyInfo.Comment);
      
      // Обработка аннулирования.
      if (this.GetRevocationOfferPrimaryDocuments(message).Any() && processResult)
        processResult = this.ProcessCancellationAgreement(message, queueItems, sender, isIncomingMessage, box);
      
      // Обработка ИОП.
      if (this.GetReceiptReglamentDocuments(message).Any() && processResult)
        processResult = this.ProcessReceiptNotice(message, queueItem, sender, isIncomingMessage, businessUnitBox) && processResult;

      // Обработка ИОП на УОП (необходимо для исторической загрузки).
      if (this.GetNotificationOnReceiptOfNotificationReceiptReglamentDocuments(message).Any() && processResult)
        processResult = this.ProcessReceiptOfNoteReceipt(message, queueItem, sender, isIncomingMessage, businessUnitBox) && processResult;

      // Обработка подтверждения доставки.
      if (this.GetInvoiceConfirmationReglamentDocuments(message).Any() && processResult)
        processResult = this.ProcessInvoiceConfirmation(message, queueItem, organizationId, businessUnitBox) && processResult;
      
      // Титулы формализованных документов и подпись на СЧФ СБИС.
      if ((message.ReglamentDocuments.Any(x => this.GetSupportedReglamentDocumentTypes().Contains(x.DocumentType)) ||
           (message.PrimaryDocuments.Any(d => d.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferSchfSeller) &&
            businessUnitBox.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis)) && processResult)
        processResult = this.ProcessFormalizedSign(message, queueItem, queueItems, isIncomingMessage, box, historyInfo.Operation, historyInfo.Comment) && processResult;
      
      // Если все регламентные документы в сообщении не поддерживаются - пропускаем и удаляем из очереди.
      if (message.ReglamentDocuments.All(x => !this.GetSupportedReglamentDocumentTypes().Contains(x.DocumentType)) &&
          !message.ReglamentDocuments.Any(x => this.GetSupportedServiceDocumentTypes().Contains(x.DocumentType)))
      {
        this.LogDebugFormat(message, queueItem, box, "Message processing was skipped. Message is marked as processed.");
        processResult = true;
      }
      
      return processResult;
    }
    
    #endregion

    #region Обработка ошибочных и непринимаемых сообщений

    /// <summary>
    /// Обработка ошибок подписания из диадока.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessDeliveryFailureNotification(IMessage message, IBoxBase box)
    {
      this.LogDebugFormat(message, box, "Execute ProcessDeliveryFailureNotification.");
      var parentMessageId = message.ReglamentDocuments.First(x => x.DocumentType == NpoComputer.DCX.Common.ReglamentDocumentType.DeliveryFailureNotification).ParentServiceEntityId;
      var rootBox = ExchangeCore.PublicFunctions.BoxBase.GetRootBox(box);
      var exchangeDocumentInfo = ExchangeDocumentInfos.GetAll().Where(x => Equals(x.RootBox, rootBox) &&
                                                                      x.ServiceMessageId == parentMessageId).FirstOrDefault();
      
      if (exchangeDocumentInfo != null && exchangeDocumentInfo.Document != null)
      {
        var tracking = exchangeDocumentInfo.Document.Tracking.Where(x => x.ExternalLinkId == exchangeDocumentInfo.Id).FirstOrDefault();
        
        var performer = tracking != null
          ? tracking.DeliveredTo
          : ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(box, exchangeDocumentInfo.Counterparty, new List<IExchangeDocumentInfo>() { exchangeDocumentInfo });
        
        this.SendCannotDeliveryDocumentTask(exchangeDocumentInfo, performer, box);

        exchangeDocumentInfo.Document.ExchangeState = null;
        exchangeDocumentInfo.ExchangeState = null;
        exchangeDocumentInfo.Document.ExternalApprovalState = null;
        
        if (tracking != null)
        {
          // HACK: нельзя удалять запись выдачи с действием "Согласование с контрагентом", но любую другую можно.
          tracking.Action = Docflow.OfficialDocumentTracking.Action.Delivery;
          exchangeDocumentInfo.Document.Tracking.Remove(tracking);
        }
        
        exchangeDocumentInfo.Document.Save();
        exchangeDocumentInfo.Save();
        ExchangeDocumentInfos.Delete(exchangeDocumentInfo);
      }

      return true;
    }

    /// <summary>
    /// Отправка задачи о том, что документ не был доставлен КА, т.к. подпись не прошла проверку.
    /// </summary>
    /// <param name="exchangeDocumentInfo">Информация о документе.</param>
    /// <param name="performer">Исполнитель задания.</param>
    /// <param name="box">Абонентский ящик, на который получено сообщение.</param>
    protected virtual void SendCannotDeliveryDocumentTask(IExchangeDocumentInfo exchangeDocumentInfo, IEmployee performer, IBoxBase box)
    {
      var needReceive = ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(box);
      if (needReceive)
      {
        var task = Workflow.SimpleTasks.Create();
        task.NeedsReview = false;
        var step = task.RouteSteps.AddNew();
        step.AssignmentType = Workflow.SimpleTask.AssignmentType.Notice;
        step.Performer = performer;

        this.GrantAccessRightsForUpperBoxResponsibles(exchangeDocumentInfo.Document, box);
        task.Attachments.Add(exchangeDocumentInfo.Document);

        var hyperlink = Hyperlinks.Get(exchangeDocumentInfo.Document);

        task.ActiveText =
          Resources.CannotDeliveryDocumentToCounterpartyFormat(hyperlink, ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box).Name);
        task.Subject = CutText(Resources.ErrorSendingDocumentToCounterpartyFormat(exchangeDocumentInfo.Document.Name),
                               task.Info.Properties.Subject.Length);
        task.Start();
      }
    }

    /// <summary>
    /// Обработать входящее сообщение, в котором содержатся только неподдерживаемые документы.
    /// </summary>
    /// <param name="message">Сообщение сервиса обмена.</param>
    /// <param name="sender">Контрагент-отправитель.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Ящик.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessMessageWithUnsupportedDocuments(IMessage message, ICounterparty sender, bool isIncomingMessage, IBoxBase box)
    {
      this.LogDebugFormat(message, box, "Execute ProcessMessageWithUnsupportedDocuments.");
      var queueItem = MessageQueueItems.GetAll(q => q.ExternalId == message.ServiceMessageId && q.Box.Id == box.Id).FirstOrDefault();
      
      var needReceive = ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(box);
      if (needReceive && isIncomingMessage && queueItem != null && queueItem.DownloadSession == null)
      {
        var simpleTask = Sungero.Workflow.SimpleTasks.Create();
        var dateWithUTC = Sungero.Docflow.PublicFunctions.Module.GetDateWithUTCLabel(message.TimeStamp);
        simpleTask.Subject = Resources.NoticeSubjectFormat(sender, ExchangeCore.PublicFunctions.BoxBase.GetBusinessUnit(box), dateWithUTC,
                                                           ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box));
        simpleTask.Subject = CutText(simpleTask.Subject, simpleTask.Info.Properties.Subject.Length);
        simpleTask.ThreadSubject = Sungero.Exchange.Resources.NoticeThreadSubject;
        simpleTask.ActiveText = this.GenerateActiveTextFromUnsupportedDocuments(message.PrimaryDocuments, sender, isIncomingMessage, box, message.TimeStamp, true);
        
        var step = simpleTask.RouteSteps.AddNew();
        step.AssignmentType = Workflow.SimpleTask.AssignmentType.Notice;
        step.Performer = ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(box, sender, null);
        
        simpleTask.Save();
        simpleTask.Start();
      }

      return true;
    }

    /// <summary>
    /// Сгенерировать текст по полученным формализованным документам, для заполнения задачи/задания.
    /// </summary>
    /// <param name="documents">Список формализованных документов.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Ящик.</param>
    /// <param name="messageDate">Время сообщения.</param>
    /// <param name="allUnsupported">Признак, что все документы не поддерживаемые.</param>
    /// <returns>Сгенерированный текст.</returns>
    protected virtual string GenerateActiveTextFromUnsupportedDocuments(IEnumerable<IDocument> documents, ICounterparty sender, bool isIncomingMessage,
                                                                        IBoxBase box, DateTime messageDate, bool allUnsupported)
    {
      var documentList = new System.Text.StringBuilder();
      var documentNames = string.Empty;
      var otherDocuments = false;
      foreach (var document in documents)
      {
        var isXml = System.IO.Path.GetExtension(document.FileName).TrimStart('.').ToLower() == "xml";
        var generatedName = isXml ? GenerateUnsupportedDocumentName(document) : string.Empty;
        // Если имя не сформировалось, значит, пришел необработанный нами вид документа.
        if (!string.IsNullOrEmpty(generatedName))
          documentList.AppendLine(Resources.FormalizedDocumentNameFormat(generatedName));
        else
          otherDocuments = true;
      }
      
      if (!string.IsNullOrEmpty(documentList.ToString()))
      {
        // Добавить информацию о том, что пришли и другие документы.
        if (otherDocuments)
          documentList.AppendLine(Resources.OtherDocuments);
        
        documentNames = Resources.DocumentsListFormat(documentList);
      }
      
      if (allUnsupported)
        documentNames += this.ProcessBoundedDocuments(documents, null, isIncomingMessage, box);

      documentNames += string.Format("{0}{0}", Environment.NewLine);
      
      var counterPartyLink = Hyperlinks.Get(sender);
      if (isIncomingMessage)
        documentNames += Resources.ReceiptNeededActiveTextFormat(counterPartyLink, messageDate.ToShortDateString(), messageDate.ToShortTimeString());
      else
        documentNames += Resources.ReceiptNeededOutgoingActiveTextFormat(counterPartyLink, messageDate.ToShortDateString(), messageDate.ToShortTimeString());
      documentNames += Environment.NewLine;
      
      var exchangeService = ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box);
      return Resources.NoticeActiveTextFormat(documentNames, Resources.LinkToPersonalDataFormat(exchangeService.Name, exchangeService.LogonUrl));
    }

    /// <summary>
    /// Получить наименование формализованного документа, полученного из сервиса обмена.
    /// </summary>
    /// <param name="document">Документ из сервиса обмена.</param>
    /// <returns>Наименование, если получилось сформировать, иначе - пустая строка.</returns>
    private static string GenerateUnsupportedDocumentName(IDocument document)
    {
      System.Xml.Linq.XDocument xdoc;
      try
      {
        xdoc = System.Xml.Linq.XDocument.Load(new System.IO.MemoryStream(document.Content));
      }
      catch (Exception e)
      {
        Logger.ErrorFormat("Exchange. Failed to load XML: {0}", e.Message);
        return string.Empty;
      }
      
      RemoveNamespaces(xdoc);
      var documentType = string.Empty;
      var documentNumber = string.Empty;
      var documentDate = string.Empty;
      
      var fileElement = xdoc.Element("Файл");
      if (fileElement == null)
        return string.Empty;
      
      var docElement = fileElement.Element("Документ");
      if (document.DocumentType == NpoComputer.DCX.Common.DocumentType.Act)
      {
        var actInfo = docElement.Element("СвАктИ");
        documentType = GetAttributeValueByName(actInfo, "НаимПервДок");
        documentNumber = GetAttributeValueByName(actInfo, "НомАкт");
        documentDate = GetAttributeValueByName(actInfo, "ДатаАкт");
      }
      else if (document.DocumentType == NpoComputer.DCX.Common.DocumentType.Waybill)
      {
        var waybillInfo = docElement.Element("СвТНО");
        documentType = GetAttributeValueByName(waybillInfo, "НаимПервДок");
        
        var waybill = waybillInfo.Element("ТН");
        documentNumber = GetAttributeValueByName(waybill, "НомТН");
        documentDate = GetAttributeValueByName(waybill, "ДатаТН");
      }
      else if (document.DocumentType == NpoComputer.DCX.Common.DocumentType.Invoice ||
               document.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferDopSeller ||
               document.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferSchfSeller ||
               document.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferSchfDopSeller)
      {
        var invoiceInfo = docElement.Element("СвСчФакт");
        documentNumber = GetAttributeValueByName(invoiceInfo, "НомерСчФ");
        documentDate = GetAttributeValueByName(invoiceInfo, "ДатаСчФ");
        documentType = GetAttributeValueByName(docElement, "НаимДокОпр");
        if (string.IsNullOrEmpty(documentType))
          documentType = "Счет-фактура";
      }
      else if (document.DocumentType == NpoComputer.DCX.Common.DocumentType.InvoiceCorrection ||
               document.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferDopCorrectionSeller ||
               document.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferSchfCorrectionSeller ||
               document.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferSchfDopCorrectionSeller)
      {
        var invoiceCorrectionInfo = docElement.Element("СвКСчФ");
        documentNumber = GetAttributeValueByName(invoiceCorrectionInfo, "НомерКСчФ");
        documentDate = GetAttributeValueByName(invoiceCorrectionInfo, "ДатаКСчФ");
        documentType = GetAttributeValueByName(docElement, "НаимДокОпр");
        if (string.IsNullOrEmpty(documentType))
          documentType = "Корректировочный счет-фактура";
      }
      else if (document.DocumentType == NpoComputer.DCX.Common.DocumentType.InvoiceCorrectionRevision ||
               document.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferDopCorrectionRevisionSeller ||
               document.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferSchfCorrectionRevisionSeller ||
               document.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferSchfDopCorrectionRevisionSeller)
      {
        var invoiceCorrectionRevisionInfo = docElement.Element("СвКСчФ").Element("ИспрКСчФ");
        documentNumber = GetAttributeValueByName(invoiceCorrectionRevisionInfo, "НомИспрКСчФ");
        documentDate = GetAttributeValueByName(invoiceCorrectionRevisionInfo, "ДатаИспрКСчФ");
        documentType = GetAttributeValueByName(docElement, "НаимДокОпр");
        if (string.IsNullOrEmpty(documentType))
          documentType = "Исправление корректировочного счета-фактуры";
      }
      else if (document.DocumentType == NpoComputer.DCX.Common.DocumentType.InvoiceRevision ||
               document.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferDopRevisionSeller ||
               document.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferSchfRevisionSeller ||
               document.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferSchfDopRevisionSeller)
      {
        var invoiceRevisionInfo = docElement.Element("СвСчФакт").Element("ИспрСчФ");
        documentNumber = GetAttributeValueByName(invoiceRevisionInfo, "НомИспрСчФ");
        documentDate = GetAttributeValueByName(invoiceRevisionInfo, "ДатаИспрСчФ");
        documentType = GetAttributeValueByName(docElement, "НаимДокОпр");
        if (string.IsNullOrEmpty(documentType))
          documentType = "Исправление счета-фактуры";
      }
      else if (document.DocumentType == NpoComputer.DCX.Common.DocumentType.GoodsTransferSeller)
      {
        var goodsTransferSellerInfo = docElement.Element("СвДокПТПрКроме").Element("СвДокПТПр");
        documentNumber = GetAttributeValueByName(goodsTransferSellerInfo.Element("ИдентДок"), "НомДокПТ");
        documentDate = GetAttributeValueByName(goodsTransferSellerInfo.Element("ИдентДок"), "ДатаДокПТ");
        documentType = GetAttributeValueByName(goodsTransferSellerInfo.Element("НаимДок"), "НаимДокОпр");
      }
      else if (document.DocumentType == NpoComputer.DCX.Common.DocumentType.GoodsTransferRevisionSeller)
      {
        var goodsTransferRevisionSellerInfo = docElement.Element("СвДокПТПрКроме").Element("СвДокПТПр");
        documentNumber = GetAttributeValueByName(goodsTransferRevisionSellerInfo.Element("ИспрДокПТ"), "НомДокПТ");
        documentDate = GetAttributeValueByName(goodsTransferRevisionSellerInfo.Element("ИспрДокПТ"), "ДатаДокПТ");
        documentType = GetAttributeValueByName(goodsTransferRevisionSellerInfo.Element("НаимДок"), "НаимДокОпр");
      }
      else if (document.DocumentType == NpoComputer.DCX.Common.DocumentType.WorksTransferSeller)
      {
        var worksTransferSellerInfo = docElement.Element("СвДокПРУ");
        documentNumber = GetAttributeValueByName(worksTransferSellerInfo.Element("ИдентДок"), "НомДокПРУ");
        documentDate = GetAttributeValueByName(worksTransferSellerInfo.Element("ИдентДок"), "ДатаДокПРУ");
        documentType = GetAttributeValueByName(worksTransferSellerInfo.Element("НаимДок"), "НаимДокОпр");
      }
      else if (document.DocumentType == NpoComputer.DCX.Common.DocumentType.WorksTransferRevisionSeller)
      {
        var generalTransferRevisionSellerInfo = docElement.Element("СвДокПРУ");
        documentNumber = GetAttributeValueByName(generalTransferRevisionSellerInfo.Element("ИспрДокПРУ"), "НомИспрДокПРУ");
        documentDate = GetAttributeValueByName(generalTransferRevisionSellerInfo.Element("ИспрДокПРУ"), "ДатаИспрДокПРУ");
        documentType = GetAttributeValueByName(generalTransferRevisionSellerInfo.Element("НаимДок"), "НаимДокОпр");
      }
      else
      {
        return string.Empty;
      }
      
      return Resources.FormalizedDocumentTemplateNameFormat(documentType, documentNumber, documentDate);
    }

    #endregion

    #region Обработка сообщения с новыми документами
    
    #region Процессинг сообщения с новыми документами

    /// <summary>
    /// Добавить в информацию о документе сессию исторической загрузки.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="session">Сессия исторической загрузки.</param>
    protected virtual void ExchangeDocumentInfoSetSession(IExchangeDocumentInfo documentInfo, Sungero.ExchangeCore.IHistoricalMessagesDownloadSession session)
    {
      this.LogDebugFormat(documentInfo, "Execute ExchangeDocumentInfoSetSession.");
      documentInfo = ExchangeDocumentInfos.Get(documentInfo.Id);
      documentInfo.DownloadSession = session;
      documentInfo.Save();
    }
    
    /// <summary>
    /// Обработать новое входящее сообщение.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="sender">Отправитель.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessNewIncomingMessage(IMessage message, IMessageQueueItem queueItem, ICounterparty sender, bool isIncomingMessage, IBoxBase box)
    {
      this.LogDebugFormat(message, queueItem, box, "Execute ProcessNewIncomingMessage.");
      var infos = new List<IExchangeDocumentInfo>();
      var processedDocumentTypes = this.GetSupportedPrimaryDocumentTypes();
      IExchangeDocumentInfo info = null;

      // Обработка документов в сообщении.
      var processingDocuments = message.PrimaryDocuments.Where(x => processedDocumentTypes.Contains(x.DocumentType.Value)).ToList();
      foreach (var processingDocument in processingDocuments)
      {
        if (this.NeedCreateDocumentFromNewIncomingMessage(message, processingDocument, sender, isIncomingMessage, box))
        {
          var serviceCounterpartyId = isIncomingMessage ? message.Sender.Organization.OrganizationId : message.Receiver.Organization.OrganizationId;
          var serviceCounterpartyDepartmentId = GetServiceCounterpartyDepartmentId(message, isIncomingMessage);
          var exchangeDocument = this.GetOrCreateNewExchangeDocument(processingDocument, sender, serviceCounterpartyId, isIncomingMessage, message.TimeStamp,
                                                                     box, serviceCounterpartyDepartmentId);
          this.SignDocumentFromNewIncomingMessage(message, exchangeDocument, processingDocument, box);
          info = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, processingDocument.ServiceEntityId);
          
          if (exchangeDocument.LastVersion.PublicBody.Size == 0)
            Docflow.PublicFunctions.Module.GeneratePublicBodyForExchangeDocument(exchangeDocument, info.VersionId.Value, exchangeDocument.ExchangeState);
          infos.Add(info);
        }
        
        if (queueItem.DownloadSession != null)
        {
          info = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, processingDocument.ServiceEntityId);
          if (info != null && info.DownloadSession == null)
            this.ExchangeDocumentInfoSetSession(info, queueItem.DownloadSession);
        }
        
      }
      
      return this.ProcessDocumentsFromNewIncomingMessage(message, queueItem, infos, processingDocuments, sender, isIncomingMessage, box);
    }

    /// <summary>
    /// Обработать документы, созданные из сообщения.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="infos">Информация по обработанным документам.</param>
    /// <param name="processingDocuments">Обрабатываемые документы.</param>
    /// <param name="sender">Отправитель.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessDocumentsFromNewIncomingMessage(IMessage message, IMessageQueueItem queueItem, List<IExchangeDocumentInfo> infos,
                                                                  List<IDocument> processingDocuments, ICounterparty sender, bool isIncomingMessage, IBoxBase box)
    {
      this.LogDebugFormat(message, queueItem, box, "Execute ProcessDocumentsFromNewIncomingMessage.");
      // Если не создано ни одного документа - завершаем обработку.
      var documents = infos.Where(i => i.Document != null).Select(i => i.Document).ToList();
      if (!documents.Any())
      {
        queueItem.ProcessingStatus = ExchangeCore.MessageQueueItem.ProcessingStatus.Processed;
        queueItem.Save();
        return true;
      }
      
      this.FillCounterpartyDataFromNewMessage(message, infos, documents, sender, isIncomingMessage);
      
      foreach (var doc in documents)
        this.GrantAccessRightsForUpperBoxResponsibles(doc, box);

      var exchangeTaskActiveTextBoundedDocuments = this.ProcessBoundedDocuments(processingDocuments, documents, isIncomingMessage, box);

      var needReceive = this.NeedReceiveDocumentProcessingTask(box, message);
      if (needReceive && queueItem.DownloadSession == null)
        return this.StartExchangeTask(message, infos, sender, isIncomingMessage, box, exchangeTaskActiveTextBoundedDocuments);

      return true;
    }

    /// <summary>
    /// Проверка, что нужно заносить документы в систему.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="document">Обрабатываемый документ.</param>
    /// <param name="sender">Контрагент по документу.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <returns>True - если нужно создавать документ, иначе - false.</returns>
    /// <remarks>Создает информацию по документу, если тот будет загружен позже.</remarks>
    protected virtual bool NeedCreateDocumentFromNewIncomingMessage(IMessage message, IDocument document, ICounterparty sender, bool isIncomingMessage, IBoxBase box)
    {
      if (document.RevocationStatus == NpoComputer.DCX.Common.RevocationStatus.RevocationAccepted)
        return false;
      
      // Если документ может быть подписан, то создаем инфо, чтобы потом найти сообщение и подпись.
      if (!isIncomingMessage && document.DocumentType == DocumentType.Nonformalized &&
          (document.SignStatus == NpoComputer.DCX.Common.SignStatus.Waiting ||
           document.SignStatus == NpoComputer.DCX.Common.SignStatus.None))
      {
        var serviceCounterpartyDepartmentId = GetServiceCounterpartyDepartmentId(message, isIncomingMessage);
        var withoutDoc = GetOrCreateExchangeInfoWithoutDocument(document, sender, message.Receiver.Organization.OrganizationId, isIncomingMessage, message.TimeStamp,
                                                                box, serviceCounterpartyDepartmentId);
        withoutDoc.Save();
        return false;
      }

      // Не грузим отправленные и не подписанные сообщения, кроме формализованных документов.
      if (!isIncomingMessage && document.SignStatus != NpoComputer.DCX.Common.SignStatus.Signed &&
          document.DocumentType == DocumentType.Nonformalized)
        return false;
      
      // Не грузим сообщения с отказом.
      if (document.SignStatus == NpoComputer.DCX.Common.SignStatus.Rejected)
        return false;

      // Если документ нами и отправлен - задачу отправлять уже не надо.
      var info = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, document.ServiceEntityId);
      if (!isIncomingMessage && info != null)
        return false;
      
      return true;
    }

    /// <summary>
    /// Подписать документ из сервиса обмена.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="signedDocument">Подписываемый документ.</param>
    /// <param name="serviceDocument">Документ в сервисе обмена.</param>
    /// <param name="box">Абонентский ящик.</param>
    protected virtual void SignDocumentFromNewIncomingMessage(IMessage message, IOfficialDocument signedDocument, IDocument serviceDocument, IBoxBase box)
    {
      var info = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, serviceDocument.ServiceEntityId);
      this.LogDebugFormat(info, "Execute SignDocumentFromNewIncomingMessage.");
      var signatures = message.Signatures.Where(x => x.DocumentId == serviceDocument.ServiceEntityId);
      var addedThumbprints = Signatures.Get(signedDocument.LastVersion)
        .Where(s => s.SignCertificate != null)
        .Select(x => x.SignCertificate.Thumbprint);
      var accountDocument = Docflow.AccountingDocumentBases.As(signedDocument);
      
      foreach (var signature in signatures)
      {
        var certificateInfo = Docflow.PublicFunctions.Module.GetSignatureCertificateInfo(signature.Content);
        var signatoryInfo = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(certificateInfo.SubjectInfo);
        
        var signatureIsAlreadyAdded = addedThumbprints.Any(x => x.Equals(certificateInfo.Thumbprint));
        if (!signatureIsAlreadyAdded)
        {
          this.SignDocument(info, signature, signedDocument.LastVersion, signatoryInfo, message.TimeStamp);

          if (accountDocument != null)
          {
            var lastSignature = this.GetLastDocumentSignature(accountDocument);
            accountDocument.SellerSignatureId = lastSignature.Id;
          }
        }
      }
    }
    
    /// <summary>
    /// Обработка связанных документов.
    /// </summary>
    /// <param name="documents">Документы сервиса обмена.</param>
    /// <param name="officialDocuments">Документы в RX.</param>
    /// <param name="fromCounterparty">True, если документы от контрагента.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <returns>Текст с информацией о связанных документах.</returns>
    protected virtual string ProcessBoundedDocuments(IEnumerable<IDocument> documents, IList<IOfficialDocument> officialDocuments,
                                                     bool fromCounterparty, IBoxBase box)
    {
      this.LogDebugFormat(box, "Execute ProcessBoundedDocuments.");
      officialDocuments = officialDocuments ?? new List<IOfficialDocument>();

      var bounds = new List<string>();
      foreach (var document in documents.Where(x => x.BoundDocuments != null))
        bounds.AddRange(document.BoundDocuments.Select(x => x.DocumentId));

      bounds = bounds.Distinct().ToList();

      var links = new List<string>();
      var hasBound = false;
      foreach (var bound in bounds)
      {
        var info = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, bound);
        if (info != null && info.Document != null)
        {
          var relatedDocs = info.Document.Relations.GetRelatedFrom(Constants.Module.SimpleRelationRelationName);
          
          if (officialDocuments.Any())
          {
            foreach (var offdoc in officialDocuments.Where(x => !Equals(x, info.Document)).Where(x => relatedDocs.All(d => !Equals(d, x))))
            {
              if (Docflow.AccountingDocumentBases.Is(offdoc) && Docflow.AccountingDocumentBases.As(offdoc).IsAdjustment == true)
                continue;
              
              this.AddRelations(offdoc, info);
            }
            info.Document.Save();
          }
          hasBound = true;
          var link = Hyperlinks.Get(info.Document);
          if (!officialDocuments.Contains(info.Document))
            links.Add(link);
        }
      }
      
      if (hasBound)
      {
        var text = fromCounterparty ?
          Resources.NoticeCounterpartyBoundDocument :
          Resources.NoticeOurBoundDocument;
        if (links.Any())
        {
          var separator = string.Format(", {0}", Environment.NewLine);
          var allLinks = Environment.NewLine + string.Join(separator, links);
          text = fromCounterparty ?
            Resources.NoticeCounterpartyBoundDocumentWithLinksFormat(allLinks) :
            Resources.NoticeOurBoundDocumentWithLinksFormat(allLinks);
        }
        return string.Format("{0}{0}{1}", Environment.NewLine, text);
      }

      return string.Empty;
    }
    
    /// <summary>
    /// Выдать права на документ ответственным за вышестоящие абонентские ящики.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="box">Абонентский ящик.</param>
    protected virtual void GrantAccessRightsForUpperBoxResponsibles(IOfficialDocument document, IBoxBase box)
    {
      var boxes = new List<IBoxBase>() { box };
      if (!ExchangeCore.BusinessUnitBoxes.Is(box))
      {
        var departmentBox = ExchangeCore.DepartmentBoxes.As(box);
        boxes.Add(departmentBox.RootBox);
        
        ExchangeCore.IBoxBase parentBox = departmentBox.ParentBox;
        while (!ExchangeCore.BusinessUnitBoxes.Is(parentBox))
        {
          departmentBox = ExchangeCore.DepartmentBoxes.As(parentBox);
          boxes.Add(departmentBox);
          parentBox = departmentBox.ParentBox;
        }
      }
      
      var responsibles = new List<Sungero.Company.IEmployee>();
      foreach (var currentBox in boxes)
      {
        if (currentBox.Responsible != null)
          responsibles.Add(currentBox.Responsible);
        
        var info = Functions.ExchangeDocumentInfo.GetLastDocumentInfo(document);
        var computedResponsible = ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(currentBox, info.Counterparty, new List<Exchange.IExchangeDocumentInfo>() { info });
        if (computedResponsible != null && !responsibles.Contains(computedResponsible))
          responsibles.Add(computedResponsible);
        
        var allCompanies = Docflow.PublicFunctions.OfficialDocument.GetCounterparties(document);
        if (allCompanies != null)
        {
          var companies = allCompanies.Where(c => Parties.CompanyBases.Is(c));
          responsibles.AddRange(companies.Where(c => Parties.CompanyBases.As(c).Responsible != null).Select(c => Parties.CompanyBases.As(c).Responsible));
        }
      }
      
      foreach (var responsible in responsibles)
      {
        if (!document.AccessRights.IsGrantedDirectly(DefaultAccessRightsTypes.FullAccess, responsible))
          document.AccessRights.Grant(responsible, DefaultAccessRightsTypes.FullAccess);
      }

      document.AccessRights.Save();
    }
    
    /// <summary>
    /// Заполнить подписывающего и основание со стороны контрагента.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="infos">Информация по обработанным документам.</param>
    /// <param name="documents">Документы сервиса обмена.</param>
    /// <param name="counterparty">Отправитель.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <remarks>Если сообщение исходящее, то подписывающий и основание со стороны контрагента не заполняются.</remarks>
    protected virtual void FillCounterpartyDataFromNewMessage(IMessage message, List<IExchangeDocumentInfo> infos,
                                                              List<IOfficialDocument> documents, ICounterparty counterparty, bool isIncomingMessage)
    {
      this.LogDebugFormat(message, "Execute FillCounterpartyDataFromNewMessage.");
      
      // Если сообщение исходящее, то заполнять подписывающего и основание со стороны контрагента не надо.
      if (!isIncomingMessage)
        return;
      
      foreach (var doc in documents)
      {
        var info = infos.FirstOrDefault(x => x.Document != null && Equals(doc, x.Document));
        this.FillCounterpartySignatoryAndSigningReason(message, info.ServiceDocumentId, doc, counterparty);
      }
    }
    
    /// <summary>
    /// Заполнить подписывающего и основание со стороны контрагента для ответа по документу.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="serviceDocumentId">ИД документа в сервисе обмена.</param>
    /// <param name="document">Документ сервиса обмена.</param>
    /// <param name="counterparty">Отправитель.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <remarks>Если сообщение исходящее и не является ответом, то подписывающий и основание со стороны контрагента не заполняются.</remarks>
    protected virtual void FillCounterpartyDataFromReplyMessage(IMessage message, string serviceDocumentId,
                                                                IOfficialDocument document, ICounterparty counterparty, bool isIncomingMessage)
    {
      this.LogDebugFormat(message, "Execute FillCounterpartyDataFromReplyMessage.");
      
      // Если сообщение исходящее, то заполнять подписывающего и основание со стороны контрагента не надо.
      if (!isIncomingMessage)
        return;
      
      this.FillCounterpartySignatoryAndSigningReason(message, serviceDocumentId, document, counterparty);
    }
    
    /// <summary>
    /// Заполнить подписывающего и основание со стороны контрагента в отдельном документе.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="serviceDocumentId">ИД документа в сервисе обмена.</param>
    /// <param name="doc">Документ сервиса обмена.</param>
    /// <param name="counterparty">Отправитель.</param>
    /// <remarks>Поля должны заполняться только при работе с входящими документами или с ответами на исходящие.</remarks>
    protected virtual void FillCounterpartySignatoryAndSigningReason(IMessage message, string serviceDocumentId,
                                                                     IOfficialDocument doc, ICounterparty counterparty)
    {
      this.LogDebugFormat(message, "Execute FillCounterpartySignatoryAndSigningReason.");
      
      var signature = message.Signatures.Where(x => x.DocumentId == serviceDocumentId).FirstOrDefault();
      if (signature != null)
      {
        var certificateInfo = Docflow.PublicFunctions.Module.GetSignatureCertificateInfo(signature.Content);
        var signatoryName = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(certificateInfo.SubjectInfo);
        var signatory = Parties.PublicFunctions.Contact.GetContactByName(signatoryName, counterparty);
        
        if (signatory != null)
          Sungero.Docflow.PublicFunctions.OfficialDocument.FillCounterpartySignatory(doc, signatory);
        
        // Заполнить основание со стороны контрагента по формату: "<Основание подписания> (<Подписант>)".
        var signingReason = string.Empty;
        var unifiedRegNumber = signature.FormalizedPoAUnifiedRegNumber;
        // Если подписали по МЧД, то взять её номер.
        if (!string.IsNullOrWhiteSpace(unifiedRegNumber))
          signingReason = string.Format(Exchange.Resources.CounterpartyPowerOfAttorney, unifiedRegNumber, signatoryName);
        
        // Если МЧД нет и документ формализованный, то получить информацию об основании из xml.
        if (string.IsNullOrWhiteSpace(signingReason))
        {
          if (message.IsReply)
            signingReason = this.GetSigningReasonFromReglamentDocumentXml(message, serviceDocumentId, doc, signatoryName);
          else
            signingReason = this.GetSigningReasonFromPrimaryDocumentXml(message, serviceDocumentId, doc, signatoryName);
        }
        
        // Если не удалось получить основание, то заполняем - "Должностные обязанности".
        if (string.IsNullOrWhiteSpace(signingReason))
          signingReason = SignatureSettings.Resources.DutiesDisplayNameFormat(signatoryName);
        
        Sungero.Docflow.PublicFunctions.OfficialDocument.FillCounterpartySigningReason(doc, signingReason);
        doc.Save();
      }
    }
    
    /// <summary>
    /// Получить основание подписания из XML основного документа.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="serviceDocumentId">ИД документа в сервисе обмена.</param>
    /// <param name="doc">Документ сервиса обмена.</param>
    /// <param name="signatoryName">Имя подписывающего.</param>
    /// <returns>Основание контрагента, если не получилось найти, то пустая строка.</returns>
    protected virtual string GetSigningReasonFromPrimaryDocumentXml(IMessage message, string serviceDocumentId, IOfficialDocument doc, string signatoryName)
    {
      var processingDocument = message.PrimaryDocuments.Where(x => x.ServiceEntityId == serviceDocumentId).FirstOrDefault();
      
      if (processingDocument == null || !AccountingDocumentBases.Is(doc) || AccountingDocumentBases.As(doc).IsFormalized != true)
        return string.Empty;
      
      var xdoc = System.Xml.Linq.XDocument.Load(new System.IO.MemoryStream(processingDocument.Content));
      var documentInfo = xdoc.Element("Файл").Element("Документ");
      var signingReason = this.GetSigningReasonFromXml(documentInfo);
      if (!string.IsNullOrWhiteSpace(signingReason))
        return Sungero.Exchange.Resources.SigningReasonDisplayValueFormat(signingReason, signatoryName);
      
      return string.Empty;
    }
    
    /// <summary>
    /// Получить основание подписания из XML регламентного документа.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="serviceDocumentId">ИД документа в сервисе обмена.</param>
    /// <param name="doc">Документ сервиса обмена.</param>
    /// <param name="signatoryName">Имя подписывающего.</param>
    /// <returns>Основание контрагента, если не получилось найти, то пустая строка.</returns>
    protected virtual string GetSigningReasonFromReglamentDocumentXml(IMessage message, string serviceDocumentId, IOfficialDocument doc, string signatoryName)
    {
      // Рассматриваем регламентные документы, т.к. работаем с титулами, которые есть только у формализованных документов.
      var processingDocument = message.ReglamentDocuments.Where(x => x.ServiceEntityId == serviceDocumentId).FirstOrDefault();
      
      if (processingDocument == null || !AccountingDocumentBases.Is(doc) || AccountingDocumentBases.As(doc).IsFormalized != true)
        return string.Empty;
      
      var xdoc = System.Xml.Linq.XDocument.Load(new System.IO.MemoryStream(processingDocument.Content));
      var documentInfo = xdoc.Element("Файл").Element("ИнфПок");
      // Для актов и накладных в старом формате (ДПТ, ДПРР) информация о документе находится в другом элементе.
      if (documentInfo == null)
        documentInfo = xdoc.Element("Файл").Element("Документ");
      var signingReason = this.GetSigningReasonFromXml(documentInfo);
      if (!string.IsNullOrWhiteSpace(signingReason))
        return Sungero.Exchange.Resources.SigningReasonDisplayValueFormat(signingReason, signatoryName);
      
      return string.Empty;
    }
    
    /// <summary>
    /// Получить основание подписания из XML-документа.
    /// </summary>
    /// <param name="documentInfo">Элемент с информацией о документе.</param>
    /// <returns>Основание. Если не смогли получить, то пустая строка.</returns>
    protected virtual string GetSigningReasonFromXml(XElement documentInfo)
    {
      if (documentInfo == null)
        return string.Empty;
      
      // Попытаться получить основание подписания из атрибута с основанием полномочий.
      var signatoryInfo = documentInfo.Element("Подписант");
      if (signatoryInfo != null)
      {
        var signingReason = GetAttributeValueByName(signatoryInfo, "ОснПолн");
        if (!string.IsNullOrWhiteSpace(signingReason))
          return signingReason;
        
        signingReason = GetAttributeValueByName(signatoryInfo, "ОснПолнПодп");
        if (!string.IsNullOrWhiteSpace(signingReason))
          return signingReason;
      }
      
      return string.Empty;
    }
    
    /// <summary>
    /// Получить или создать сведения о документах обмена.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="sender">Отправитель.</param>
    /// <param name="serviceCounterpartyId">ИД контрагента в сервисе обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="messageDate">Дата отправки.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="serviceCounterpartyDepartmentId">Ид подразделение получателя в сервисе обмена.</param>
    /// <returns>Сведения о документах обмена.</returns>
    private static IExchangeDocumentInfo GetOrCreateExchangeInfoWithoutDocument(IDocument document, ICounterparty sender, string serviceCounterpartyId,
                                                                                bool isIncomingMessage, DateTime messageDate, IBoxBase box,
                                                                                string serviceCounterpartyDepartmentId)
    {
      var info = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, document.ServiceEntityId);
      if (info != null)
        return info;
      
      var businessUnitBox = ExchangeCore.PublicFunctions.BoxBase.GetRootBox(box);
      var counterpartyDepartmentBox = GetCounterpartyDepartmentBox(sender, serviceCounterpartyDepartmentId, businessUnitBox);
      var newInfo = ExchangeDocumentInfos.Create();
      newInfo.Box = box;
      newInfo.RootBox = businessUnitBox;
      newInfo.ServiceDocumentId = document.ServiceEntityId;
      newInfo.MessageType = isIncomingMessage ? Exchange.ExchangeDocumentInfo.MessageType.Incoming : Exchange.ExchangeDocumentInfo.MessageType.Outgoing;
      newInfo.ServiceMessageId = document.ServiceMessageId;
      newInfo.Counterparty = sender;
      newInfo.ServiceCounterpartyId = serviceCounterpartyId;
      newInfo.MessageDate = ToTenantTime(messageDate);
      newInfo.NeedSign = document.NeedSign;
      newInfo.CounterpartyDepartmentBox = counterpartyDepartmentBox;
      newInfo.DeliveryConfirmationStatus = ResolveReceiptStatus(document.ReceiptStatus);
      return newInfo;
    }
    
    /// <summary>
    /// Преобразовать DCX статус по ИОП в прикладной статус.
    /// </summary>
    /// <param name="receiptStatus">DCX статус по ИОП.</param>
    /// <returns>Прикладной статус по ИОП.</returns>
    public static Enumeration? ResolveReceiptStatus(NpoComputer.DCX.Common.ReceiptStatus? receiptStatus)
    {
      switch (receiptStatus)
      {
        case ReceiptStatus.NotRequiered:
          return Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.NotRequired;
        case ReceiptStatus.Requiered:
          return Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Required;
        case ReceiptStatus.Requested:
          return Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Requested;
        case ReceiptStatus.Finished:
          return Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Processed;
        default:
          return Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.NotRequired;
      }
    }
    
    /// <summary>
    /// Получить или создать сведения о документах обмена.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="sender">Отправитель.</param>
    /// <param name="serviceCounterpartyId">ИД контрагента в сервисе обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="messageDate">Дата отправки.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <returns>Сведения о документах обмена.</returns>
    private static IExchangeDocumentInfo GetOrCreateExchangeInfoWithoutDocument(IDocument document, ICounterparty sender, string serviceCounterpartyId,
                                                                                bool isIncomingMessage, DateTime messageDate, IBoxBase box)
    {
      return GetOrCreateExchangeInfoWithoutDocument(document, sender, serviceCounterpartyId, isIncomingMessage, messageDate, box, null);
    }
    
    /// <summary>
    /// Связать документы типом связи "Прочие".
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="relatedExchangeDocumentInfo">Информация о связываемом документе обмена.</param>
    public virtual void AddRelations(IOfficialDocument document, IExchangeDocumentInfo relatedExchangeDocumentInfo)
    {
      document.Relations.AddFromOrUpdate(Constants.Module.SimpleRelationRelationName, null, relatedExchangeDocumentInfo.Document);
      document.Save();
    }
    
    #endregion

    #region Создание и заполнение документа

    /// <summary>
    /// Получить или создать документ из сервиса обмена.
    /// </summary>
    /// <param name="document">Документ из сообщения.</param>
    /// <param name="sender">Отправитель.</param>
    /// <param name="serviceCounterpartyId">Id контрагента в сервисе обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="messageDate">Дата сообщения.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <param name="serviceCounterpartyDepartmentId">Ид абонентского ящика подразделения контрагента.</param>
    /// <returns>Документ RX.</returns>
    protected virtual IOfficialDocument GetOrCreateNewExchangeDocument(IDocument document, ICounterparty sender, string serviceCounterpartyId,
                                                                       bool isIncomingMessage, DateTime messageDate, IBoxBase box, string serviceCounterpartyDepartmentId)
    {
      var exchangeDoc = Docflow.OfficialDocuments.Null;
      
      var exchangeDocumentInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, document.ServiceEntityId);
      
      if (exchangeDocumentInfo != null)
        exchangeDoc = exchangeDocumentInfo.Document;
      
      if (exchangeDoc == null)
      {
        var documentFullName = CommonLibrary.FileUtils.NormalizeFileName(document.FileName);
        var documentName = System.IO.Path.GetFileNameWithoutExtension(documentFullName).TrimEnd('.');
        documentName = Functions.Module.GetValidFileName(documentName);
        var newInfo = GetOrCreateExchangeInfoWithoutDocument(document, sender, serviceCounterpartyId, isIncomingMessage, messageDate, box, serviceCounterpartyDepartmentId);
        var convertedDocument = document as NpoComputer.DCX.Common.Document;
        var documentComment = string.IsNullOrEmpty(document.Comment) ? string.Empty : Resources.DocumentCommentFormat(document.Comment).ToString();

        if (string.IsNullOrEmpty(documentName))
          documentName = documentFullName;
        var taxDocumentClassifierCode = string.Empty;
        var functionUTD = string.Empty;

        // Неформализованный документ.
        if (document.DocumentType == DocumentType.Nonformalized)
          exchangeDoc = this.CreateExchangeDocument(newInfo, sender, box, documentName, documentComment);
        else
        {
          var taxDocumentClassifier = GetTaxDocumentClassifier(document);
          taxDocumentClassifierCode = taxDocumentClassifier.TaxDocumentClassifierCode;
          functionUTD = taxDocumentClassifier.TaxDocumentClassifierFunction;
        }

        // Товарная накладная.
        if (document.DocumentType == DocumentType.Waybill &&
            taxDocumentClassifierCode != Constants.Module.TaxDocumentClassifier.GoodsTransferSeller &&
            taxDocumentClassifierCode != Constants.Module.TaxDocumentClassifier.UniversalTransferDocumentSeller)
        {
          var waybill = FinancialFunction.Module.CreateWaybillDocument(documentComment, box, sender, newInfo);
          
          var documentInfo = this.GetInfoFromXML(document, sender);
          
          SetDocumentTotalAmount(waybill, documentInfo);
          
          exchangeDoc = waybill;
        }

        // Cчет-фактура.
        if (document.DocumentType == DocumentType.Invoice ||
            document.DocumentType == DocumentType.InvoiceCorrection ||
            document.DocumentType == DocumentType.InvoiceCorrectionRevision ||
            document.DocumentType == DocumentType.InvoiceRevision ||
            document.DocumentType == DocumentType.GeneralTransferSchfSeller ||
            document.DocumentType == DocumentType.GeneralTransferSchfCorrectionSeller ||
            document.DocumentType == DocumentType.GeneralTransferSchfCorrectionRevisionSeller ||
            document.DocumentType == DocumentType.GeneralTransferSchfRevisionSeller)
        {
          exchangeDoc = this.CreateTaxInvoice(convertedDocument, newInfo, sender, isIncomingMessage, box);
        }

        // Акт.
        if (document.DocumentType == DocumentType.Act &&
            taxDocumentClassifierCode != Constants.Module.TaxDocumentClassifier.WorksTransferSeller &&
            taxDocumentClassifierCode != Constants.Module.TaxDocumentClassifier.UniversalTransferDocumentSeller)
        {
          var statement = FinancialFunction.Module.CreateContractStatementDocument(documentComment, box, sender, newInfo);
          
          var documentInfo = this.GetInfoFromXML(document, sender);
          
          SetDocumentTotalAmount(statement, documentInfo);
          
          exchangeDoc = statement;
        }

        // Универсальный передаточный документ.
        var universalDocumentTaxInvoiceAndBasicTypes = new List<NpoComputer.DCX.Common.DocumentType>()
        {
          DocumentType.GeneralTransferSchfDopSeller,
          DocumentType.GeneralTransferSchfDopRevisionSeller,
          DocumentType.GeneralTransferSchfDopCorrectionSeller,
          DocumentType.GeneralTransferSchfDopCorrectionRevisionSeller
        };
        var universalDocumentBasicTypes = new List<NpoComputer.DCX.Common.DocumentType>()
        {
          DocumentType.GeneralTransferDopSeller,
          DocumentType.GeneralTransferDopRevisionSeller,
          DocumentType.GeneralTransferDopCorrectionSeller,
          DocumentType.GeneralTransferDopCorrectionRevisionSeller
        };
        
        var isUTD155ByXmlContent = taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.UniversalTransferDocumentSeller155 &&
          functionUTD == Constants.Module.FunctionUTDDop;
        
        var isUTDByXmlContent = taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.UniversalTransferDocumentSeller &&
          functionUTD == Constants.Module.FunctionUTDDop;
        
        var isUTDCorrectionByXmlContent = taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.UniversalCorrectionDocumentSeller &&
          functionUTD == Constants.Module.FunctionUTDDopCorrection;
        
        if (universalDocumentTaxInvoiceAndBasicTypes.Contains(document.DocumentType.Value) ||
            universalDocumentBasicTypes.Contains(document.DocumentType.Value) ||
            isUTD155ByXmlContent || isUTDByXmlContent || isUTDCorrectionByXmlContent)
        {
          exchangeDoc = this.CreateUniversalTransferDocument(convertedDocument, newInfo, sender, box, universalDocumentTaxInvoiceAndBasicTypes);
        }
        
        // ДПРР.
        if (document.DocumentType == DocumentType.WorksTransferSeller ||
            document.DocumentType == DocumentType.WorksTransferRevisionSeller ||
            taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.WorksTransferSeller)
        {
          exchangeDoc = this.CreateContractStatementDocument(convertedDocument, newInfo, sender, box);
        }
        
        // ДПТ.
        if (document.DocumentType == DocumentType.GoodsTransferSeller ||
            document.DocumentType == DocumentType.GoodsTransferRevisionSeller ||
            taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.GoodsTransferSeller)
        {
          exchangeDoc = this.CreateWaybillDocument(convertedDocument, newInfo, sender, box);
        }

        if (isIncomingMessage)
          exchangeDoc.ExternalApprovalState = Docflow.OfficialDocument.ExternalApprovalState.Signed;
        else
          exchangeDoc.InternalApprovalState = Docflow.OfficialDocument.InternalApprovalState.Signed;
        
        newInfo.Document = exchangeDoc;
        
        if (ExchangeCore.DepartmentBoxes.Is(box))
          exchangeDoc.Department = ExchangeCore.PublicFunctions.BoxBase.GetDepartment(box);
        
        // Сбрасываем статус эл. обмена, чтобы при создании версии не сбрасывался статус согласования с КА.
        exchangeDoc.ExchangeState = null;

        this.CreateExchangeDocumentVersion(convertedDocument, newInfo, exchangeDoc, sender, isIncomingMessage, box, documentFullName);
        
        newInfo.Save();
      }
      
      return exchangeDoc;
    }
    
    /// <summary>
    /// Получить или создать документ из сервиса обмена.
    /// </summary>
    /// <param name="document">Документ из сообщения.</param>
    /// <param name="sender">Отправитель.</param>
    /// <param name="serviceCounterpartyId">Id контрагента в сервисе обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="messageDate">Дата сообщения.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <returns>Документ RX.</returns>
    protected virtual IOfficialDocument GetOrCreateNewExchangeDocument(IDocument document, ICounterparty sender, string serviceCounterpartyId,
                                                                       bool isIncomingMessage, DateTime messageDate, IBoxBase box)
    {
      return this.GetOrCreateNewExchangeDocument(document, sender, serviceCounterpartyId, isIncomingMessage, messageDate, box);
    }

    /// <summary>
    /// Создать накладную.
    /// </summary>
    /// <param name="document">Документ из сервиса обмена.</param>
    /// <param name="info">Информация о документе.</param>
    /// <param name="sender">Контрагент-отправитель.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <returns>Созданный документ.</returns>
    protected virtual IOfficialDocument CreateWaybillDocument(Document document, IExchangeDocumentInfo info, ICounterparty sender, IBoxBase box)
    {
      var documentInfo = this.GetInfoFromXML(document, sender);
      var waybill = FinancialFunction.Module.CreateWaybillDocument(documentInfo.Comment, box, sender, info);
      waybill.LeadingDocument = documentInfo.Contract;
      waybill.FormalizedServiceType = Docflow.AccountingDocumentBase.FormalizedServiceType.GoodsTransfer;

      if (documentInfo.IsRevision)
        waybill.IsRevision = true;

      SetDocumentTotalAmount(waybill, documentInfo);

      if (!string.IsNullOrEmpty(documentInfo.DocumentNumber))
        document.FormalizedNumber = documentInfo.DocumentNumber;

      DateTime parsedDate;
      if (!string.IsNullOrEmpty(documentInfo.DocumentDate) &&
          TryParseDate(documentInfo, out parsedDate))
        document.FormalizedDate = parsedDate;
      return waybill;
    }

    /// <summary>
    /// Создать акт.
    /// </summary>
    /// <param name="document">Документ из сервиса обмена.</param>
    /// <param name="info">Информация о документе.</param>
    /// <param name="sender">Контрагент-отправитель.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <returns>Созданный документ.</returns>
    protected virtual IOfficialDocument CreateContractStatementDocument(Document document, IExchangeDocumentInfo info, ICounterparty sender,
                                                                        IBoxBase box)
    {
      var documentInfo = this.GetInfoFromXML(document, sender);
      var statement = FinancialFunction.Module.CreateContractStatementDocument(documentInfo.Comment, box, sender, info);
      statement.LeadingDocument = documentInfo.Contract;
      statement.FormalizedServiceType = Docflow.AccountingDocumentBase.FormalizedServiceType.WorksTransfer;

      if (documentInfo.IsRevision)
        statement.IsRevision = true;

      SetDocumentTotalAmount(statement, documentInfo);

      if (!string.IsNullOrEmpty(documentInfo.DocumentNumber))
        document.FormalizedNumber = documentInfo.DocumentNumber;

      DateTime parsedDate;
      if (!string.IsNullOrEmpty(documentInfo.DocumentDate) &&
          TryParseDate(documentInfo, out parsedDate))
        document.FormalizedDate = parsedDate;

      if (!info.NeedSign.Value)
        statement.LifeCycleState = Docflow.OfficialDocument.LifeCycleState.Active;
      return statement;
    }

    /// <summary>
    /// Создать УПД.
    /// </summary>
    /// <param name="document">Документ из сервиса обмена.</param>
    /// <param name="info">Информация о документе.</param>
    /// <param name="sender">Контрагент-отправитель.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <param name="universalDocumentTaxInvoiceAndBasicTypes">Тип документа обмена.</param>
    /// <returns>Созданный документ.</returns>
    protected virtual IOfficialDocument CreateUniversalTransferDocument(Document document, IExchangeDocumentInfo info, ICounterparty sender,
                                                                        IBoxBase box, List<DocumentType> universalDocumentTaxInvoiceAndBasicTypes)
    {
      var documentInfo = this.GetInfoFromXML(document, sender);
      var accounting = universalDocumentTaxInvoiceAndBasicTypes.Contains(document.DocumentType.Value)
        ? FinancialFunction.Module.CreateUniversalTaxInvoiceAndBasic(documentInfo.Comment, box, sender, documentInfo.IsAdjustment, documentInfo.Corrected,
                                                                     info)
        : FinancialFunction.Module.CreateUniversalBasic(documentInfo.Comment, box, sender, documentInfo.IsAdjustment, documentInfo.Corrected, info);

      if (documentInfo.IsRevision)
        accounting.IsRevision = true;

      if (documentInfo.Function != null)
        accounting.FormalizedFunction = documentInfo.Function;

      if (documentInfo.СorrectionRevisionParentDocument != null)
        accounting.Relations.Add(Constants.Module.SimpleRelationRelationName, documentInfo.СorrectionRevisionParentDocument);

      SetDocumentTotalAmount(accounting, documentInfo);

      if (!string.IsNullOrEmpty(documentInfo.DocumentNumber))
        document.FormalizedNumber = documentInfo.DocumentNumber;

      DateTime parsedDate;
      if (!string.IsNullOrEmpty(documentInfo.DocumentDate) &&
          TryParseDate(documentInfo, out parsedDate))
        document.FormalizedDate = parsedDate;

      if (!info.NeedSign.Value)
        accounting.LifeCycleState = Docflow.OfficialDocument.LifeCycleState.Active;
      return accounting;
    }

    /// <summary>
    /// Создать счет-фактуру.
    /// </summary>
    /// <param name="document">Документ из сервиса обмена.</param>
    /// <param name="info">Информация о документе.</param>
    /// <param name="sender">Контрагент-отправитель.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <returns>Созданный документ.</returns>
    protected virtual IOfficialDocument CreateTaxInvoice(Document document, IExchangeDocumentInfo info, ICounterparty sender, bool isIncomingMessage,
                                                         IBoxBase box)
    {
      var documentInfo = this.GetInfoFromXML(document, sender);
      Docflow.IAccountingDocumentBase accounting = null;
      if (isIncomingMessage)
        accounting = FinancialFunction.Module.CreateIncomingTaxInvoiceDocument(documentInfo.Comment, box, sender, documentInfo.IsAdjustment,
                                                                               documentInfo.Corrected, info);
      else
        accounting = FinancialFunction.Module.CreateOutgoingTaxInvoiceDocument(documentInfo.Comment, box, sender, documentInfo.IsAdjustment,
                                                                               documentInfo.Corrected, info);

      if (documentInfo.IsRevision)
        accounting.IsRevision = true;

      if (documentInfo.Function != null)
        accounting.FormalizedFunction = documentInfo.Function;

      if (documentInfo.СorrectionRevisionParentDocument != null)
        accounting.Relations.Add(Constants.Module.SimpleRelationRelationName, documentInfo.СorrectionRevisionParentDocument);

      if (document.DocumentType == DocumentType.GeneralTransferSchfSeller ||
          document.DocumentType == DocumentType.GeneralTransferSchfCorrectionSeller ||
          document.DocumentType == DocumentType.GeneralTransferSchfCorrectionRevisionSeller ||
          document.DocumentType == DocumentType.GeneralTransferSchfRevisionSeller)
        accounting.FormalizedServiceType = Docflow.AccountingDocumentBase.FormalizedServiceType.GeneralTransfer;

      SetDocumentTotalAmount(accounting, documentInfo);

      if (!string.IsNullOrEmpty(documentInfo.DocumentNumber))
        document.FormalizedNumber = documentInfo.DocumentNumber;

      DateTime parsedDate;
      if (!string.IsNullOrEmpty(documentInfo.DocumentDate) &&
          TryParseDate(documentInfo, out parsedDate))
        document.FormalizedDate = parsedDate;
      return accounting;
    }

    /// <summary>
    /// Создать документ обмена.
    /// </summary>
    /// <param name="info">Информация о документе.</param>
    /// <param name="counterparty">Контрагент.</param>
    /// <param name="box">Ящик обмена.</param>
    /// <param name="fileName">Имя файла.</param>
    /// <param name="comment">Комментарий.</param>
    /// <returns>Созданный документ.</returns>
    protected virtual IOfficialDocument CreateExchangeDocument(IExchangeDocumentInfo info, ICounterparty counterparty, IBoxBase box,
                                                               string fileName, string comment)
    {
      var exchangeDoc = Docflow.ExchangeDocuments.Create();
      
      if (fileName.Length > exchangeDoc.Info.Properties.Name.Length)
        fileName = fileName.Substring(0, exchangeDoc.Info.Properties.Name.Length);
      
      if (!string.IsNullOrEmpty(comment) && comment.Length > exchangeDoc.Info.Properties.Note.Length)
        comment = comment.Substring(0, exchangeDoc.Info.Properties.Note.Length);
      
      exchangeDoc.Name = fileName;
      exchangeDoc.Subject = fileName;
      exchangeDoc.Note = comment;
      exchangeDoc.BusinessUnit = ExchangeCore.PublicFunctions.BoxBase.GetBusinessUnit(box);
      exchangeDoc.BusinessUnitBox = ExchangeCore.PublicFunctions.BoxBase.GetRootBox(box);
      exchangeDoc.Counterparty = counterparty;
      
      return exchangeDoc;
    }

    /// <summary>
    /// Создать версию документа.
    /// </summary>
    /// <param name="document">Документ из сервиса обмена.</param>
    /// <param name="info">Информация о документе.</param>
    /// <param name="exchangeDoc">Документ в RX.</param>
    /// <param name="sender">Контрагент-отправитель.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <param name="documentFullName">Полное имя документа.</param>
    protected virtual void CreateExchangeDocumentVersion(Document document, IExchangeDocumentInfo info, IOfficialDocument exchangeDoc,
                                                         ICounterparty sender, bool isIncomingMessage, IBoxBase box, string documentFullName)
    {
      using (var memory = new System.IO.MemoryStream(document.Content))
      {
        IElectronicDocumentVersions version = null;
        // Выключить error-логирование при доступе к зашифрованной версии.
        AccessRights.SuppressSecurityEvents(
          () =>
          {
            // Создать версию. Сохранить в версию.
            exchangeDoc.CreateVersion();
            version = exchangeDoc.LastVersion;
            version.Body.Write(memory);
          });
        version.AssociatedApplication = GetOrCreateAssociatedApplicationByDocumentName(documentFullName);
        info.VersionId = version.Id;
        var accountingDoc = Docflow.AccountingDocumentBases.As(exchangeDoc);
        if (accountingDoc != null && accountingDoc.IsFormalized == true)
        {
          accountingDoc.BusinessUnitBox = ExchangeCore.PublicFunctions.BoxBase.GetRootBox(box);
          var isRegistered =
            Docflow.PublicFunctions.OfficialDocument.TryExternalRegister(accountingDoc, document.FormalizedNumber,
                                                                         document.FormalizedDate);

          accountingDoc.SellerTitleId = version.Id;
          accountingDoc.Subject = string.Empty;

          if (FinancialArchive.Waybills.Is(accountingDoc) ||
              FinancialArchive.ContractStatements.Is(accountingDoc) ||
              FinancialArchive.UniversalTransferDocuments.Is(accountingDoc))
            version.Note = FinancialArchive.Resources.SellerTitleVersionNote;

          if (exchangeDoc.DocumentKind.NumberingType == Docflow.DocumentKind.NumberingType.NotNumerable || !isRegistered)
          {
            if (document.DocumentType == DocumentType.Invoice ||
                document.DocumentType == DocumentType.InvoiceRevision ||
                document.DocumentType == DocumentType.GeneralTransferSchfSeller ||
                document.DocumentType == DocumentType.GeneralTransferSchfRevisionSeller)
              exchangeDoc.Note = Sungero.Exchange.Resources.TaxInvoiceFormat(document.FormalizedNumber, document.FormalizedDateString) +
                Environment.NewLine + exchangeDoc.Note;
            else if (document.DocumentType == DocumentType.InvoiceCorrection ||
                     document.DocumentType == DocumentType.InvoiceCorrectionRevision ||
                     document.DocumentType == DocumentType.GeneralTransferSchfCorrectionSeller ||
                     document.DocumentType == DocumentType.GeneralTransferSchfCorrectionRevisionSeller)
              exchangeDoc.Note =
                Sungero.Exchange.Resources.TaxInvoiceCorrectionFormat(document.FormalizedNumber, document.FormalizedDateString) +
                Environment.NewLine + exchangeDoc.Note;
            else
              exchangeDoc.Note =
                Sungero.Exchange.Resources.IncomingNotNumeratedDocumentNoteFormat(document.FormalizedDateString,
                                                                                  document.FormalizedNumber) +
                Environment.NewLine + exchangeDoc.Note;
          }
        }

        MarkDocumentAsSended(info, exchangeDoc, sender, isIncomingMessage, box, document.SignStatus);

        exchangeDoc.Save();

        this.GrantAccessRightsForUpperBoxResponsibles(exchangeDoc, box);
      }
    }

    private static void CreateVersionFromExchangeDocument(IOfficialDocument document, IDocument exchangeDocument, IAssociatedApplication application)
    {
      using (var memory = new System.IO.MemoryStream(exchangeDocument.Content))
      {
        // Выключить error-логирование при доступе к зашифрованной версии.
        AccessRights.SuppressSecurityEvents(
          () =>
          {
            // Создать версию. Сохранить в версию.
            document.CreateVersion();
            var version = document.LastVersion;
            version.Body.Write(memory);
            version.AssociatedApplication = application;
            document.Save();
          });
      }
    }
    
    private static void SetDocumentTotalAmount(IAccountingDocumentBase document, FormalizedDocumentXML documentInfo)
    {
      if (!string.IsNullOrEmpty(documentInfo.CurrencyCode))
      {
        var currency = Commons.Currencies.GetAll().Where(x => x.NumericCode == documentInfo.CurrencyCode).FirstOrDefault();
        
        if (currency != null)
        {
          document.Currency = currency;
          document.TotalAmount = documentInfo.TotalAmount;
        }
      }
    }
    
    private static bool TryParseDate(FormalizedDocumentXML documentInfo, out DateTime parsedDate)
    {
      var datePattern = "dd.MM.yyyy";
      var dateStyle = System.Globalization.DateTimeStyles.None;
      return DateTime.TryParseExact(documentInfo.DocumentDate, datePattern, null, dateStyle, out parsedDate);
    }

    /// <summary>
    /// Получить информацию из xml тела документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="sender">Контрагент.</param>
    /// <returns>Информацию из xml тела документа.</returns>
    protected virtual FormalizedDocumentXML GetInfoFromXML(IDocument document, ICounterparty sender)
    {
      var xdoc = System.Xml.Linq.XDocument.Load(new System.IO.MemoryStream(document.Content));
      RemoveNamespaces(xdoc);
      var documentComment = string.IsNullOrEmpty(document.Comment) ? string.Empty : Resources.DocumentCommentFormat(document.Comment).ToString();
      var corrected = Docflow.AccountingDocumentBases.Null;
      var contract = Docflow.ContractualDocumentBases.Null;
      var correctionRevisionParentDocument = Docflow.AccountingDocumentBases.Null;
      var documentNumber = string.Empty;
      var documentDate = string.Empty;
      var correctedDocumentNumber = string.Empty;
      var correctedDocumentDate = string.Empty;
      var currencyCode = string.Empty;
      var totalAmount = string.Empty;
      double totalAmountNumeric = 0;
      
      var comment = documentComment;
      if (!string.IsNullOrEmpty(comment) && document.DocumentType != DocumentType.Invoice)
        comment += Environment.NewLine;
      
      // Функция документа, для УПД\УКД.
      Enumeration? function = null;
      
      var isAdjustment = false;
      
      // Признак того, что это исправление (но не исправление корректировки, это отдельный тип).
      var isRevision = false;
      
      // А это любое исправление для галочки в документе.
      var isAnyRevision = false;
      
      // Определяем КНД документа для уточнения его типа.
      var taxDocumentClassifierCode = GetTaxDocumentClassifier(document).TaxDocumentClassifierCode;
      
      // Если пришел с типом старого акта, а в теле КНД от УПД, заносим как УПД.
      var isUTDDop = taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.UniversalTransferDocumentSeller &&
        (document.DocumentType == DocumentType.Act || document.DocumentType == DocumentType.Waybill);
      var isRevisionUTDDop = isUTDDop && xdoc.Descendants("ИспрСчФ").Any();
      if (document.DocumentType == DocumentType.Invoice ||
          document.DocumentType == DocumentType.GeneralTransferSchfSeller ||
          document.DocumentType == DocumentType.GeneralTransferSchfDopSeller ||
          document.DocumentType == DocumentType.GeneralTransferDopSeller ||
          (isUTDDop && !isRevisionUTDDop))
      {
        var taxInvoiceInfo = xdoc.Element("Файл").Element("Документ").Element("СвСчФакт");
        documentNumber = GetAttributeValueByName(taxInvoiceInfo, "НомерСчФ");
        documentDate = GetAttributeValueByName(taxInvoiceInfo, "ДатаСчФ");
        
        currencyCode = GetAttributeValueByName(taxInvoiceInfo, "КодОКВ");
        var totalAmountElement = xdoc.Element("Файл").Element("Документ").Element("ТаблСчФакт").Element("ВсегоОпл");
        totalAmount = GetAttributeValueByName(totalAmountElement, "СтТовУчНалВсего");
        
        if (document.DocumentType == DocumentType.GeneralTransferSchfSeller)
          function = Docflow.AccountingDocumentBase.FormalizedFunction.Schf;
        if (document.DocumentType == DocumentType.GeneralTransferSchfDopSeller)
          function = Docflow.AccountingDocumentBase.FormalizedFunction.SchfDop;
        if (document.DocumentType == DocumentType.GeneralTransferDopSeller || isUTDDop)
          function = Docflow.AccountingDocumentBase.FormalizedFunction.Dop;
      }

      if (document.DocumentType == DocumentType.InvoiceCorrection ||
          document.DocumentType == DocumentType.GeneralTransferSchfCorrectionSeller ||
          document.DocumentType == DocumentType.GeneralTransferSchfDopCorrectionSeller ||
          document.DocumentType == DocumentType.GeneralTransferDopCorrectionSeller)
      {
        var taxInvoiceInfo = xdoc.Element("Файл").Element("Документ").Element("СвКСчФ");
        documentNumber = GetAttributeValueByName(taxInvoiceInfo, "НомерКСчФ");
        documentDate = GetAttributeValueByName(taxInvoiceInfo, "ДатаКСчФ");
        var correctedTaxInvoiceInfo = taxInvoiceInfo.Element("СчФ");
        correctedDocumentNumber = GetAttributeValueByName(correctedTaxInvoiceInfo, "НомерСчФ");
        correctedDocumentDate = GetAttributeValueByName(correctedTaxInvoiceInfo, "ДатаСчФ");
        comment += Resources.TaxInvoiceToFormat(correctedDocumentNumber, correctedDocumentDate);
        
        isAdjustment = true;
        
        if (document.DocumentType == DocumentType.GeneralTransferSchfCorrectionSeller)
          function = Docflow.AccountingDocumentBase.FormalizedFunction.Schf;
        if (document.DocumentType == DocumentType.GeneralTransferSchfDopCorrectionSeller)
          function = Docflow.AccountingDocumentBase.FormalizedFunction.SchfDop;
        if (document.DocumentType == DocumentType.GeneralTransferDopCorrectionSeller)
          function = Docflow.AccountingDocumentBase.FormalizedFunction.Dop;
      }
      
      if (document.DocumentType == DocumentType.InvoiceCorrectionRevision ||
          document.DocumentType == DocumentType.GeneralTransferSchfCorrectionRevisionSeller ||
          document.DocumentType == DocumentType.GeneralTransferSchfDopCorrectionRevisionSeller ||
          document.DocumentType == DocumentType.GeneralTransferDopCorrectionRevisionSeller)
      {
        var taxInvoiceInfo = xdoc.Element("Файл").Element("Документ").Element("СвКСчФ");
        documentNumber = GetAttributeValueByName(taxInvoiceInfo, "НомерКСчФ");
        documentDate = GetAttributeValueByName(taxInvoiceInfo, "ДатаКСчФ");
        var correctedTaxInvoiceInfo = taxInvoiceInfo.Element("ИспрКСчФ");
        correctedDocumentNumber = GetAttributeValueByName(correctedTaxInvoiceInfo, "НомИспрКСчФ");
        correctedDocumentDate = GetAttributeValueByName(correctedTaxInvoiceInfo, "ДатаИспрКСчФ");
        comment += Resources.TaxInvoiceRevisionFormat(correctedDocumentNumber, correctedDocumentDate);

        isAdjustment = true;
        isAnyRevision = true;
        
        if (document.DocumentType == DocumentType.GeneralTransferSchfCorrectionRevisionSeller)
          function = Docflow.AccountingDocumentBase.FormalizedFunction.Schf;
        if (document.DocumentType == DocumentType.GeneralTransferSchfDopCorrectionRevisionSeller)
          function = Docflow.AccountingDocumentBase.FormalizedFunction.SchfDop;
        if (document.DocumentType == DocumentType.GeneralTransferDopCorrectionRevisionSeller)
          function = Docflow.AccountingDocumentBase.FormalizedFunction.Dop;
      }
      
      if (document.DocumentType == DocumentType.InvoiceRevision ||
          document.DocumentType == DocumentType.GeneralTransferSchfRevisionSeller ||
          document.DocumentType == DocumentType.GeneralTransferSchfDopRevisionSeller ||
          document.DocumentType == DocumentType.GeneralTransferDopRevisionSeller ||
          isRevisionUTDDop)
      {
        var taxInvoiceInfo = xdoc.Element("Файл").Element("Документ").Element("СвСчФакт");
        var revisionTaxInvoiceInfo = taxInvoiceInfo.Element("ИспрСчФ");

        // Если это исправление, то номер должен быть как у первичного СФ.
        documentNumber = GetAttributeValueByName(taxInvoiceInfo, "НомерСчФ");
        documentDate = GetAttributeValueByName(taxInvoiceInfo, "ДатаСчФ");
        
        // В примечание запишем номер и дату исправления.
        var initialDocumentNumber = GetAttributeValueByName(revisionTaxInvoiceInfo, "НомИспрСчФ");
        var initialDocumentDate = GetAttributeValueByName(revisionTaxInvoiceInfo, "ДатаИспрСчФ");
        
        comment += Resources.TaxInvoiceRevisionFormat(initialDocumentNumber, initialDocumentDate);

        currencyCode = GetAttributeValueByName(taxInvoiceInfo, "КодОКВ");
        var totalAmountElement = xdoc.Element("Файл").Element("Документ").Element("ТаблСчФакт").Element("ВсегоОпл");
        totalAmount = GetAttributeValueByName(totalAmountElement, "СтТовУчНалВсего");
        
        isRevision = true;
        isAnyRevision = true;
        
        if (document.DocumentType == DocumentType.GeneralTransferSchfRevisionSeller)
          function = Docflow.AccountingDocumentBase.FormalizedFunction.Schf;
        if (document.DocumentType == DocumentType.GeneralTransferSchfDopRevisionSeller)
          function = Docflow.AccountingDocumentBase.FormalizedFunction.SchfDop;
        if (document.DocumentType == DocumentType.GeneralTransferDopRevisionSeller || isRevisionUTDDop)
          function = Docflow.AccountingDocumentBase.FormalizedFunction.Dop;
      }
      
      var isGoodsTransferSeller = taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.GoodsTransferSeller;
      var isRevisionGoodsTransferSeller = xdoc.Descendants("ИспрДокПТ").Any();
      
      // Проверка типа документа по КНД, т.к. из Диадока приходит ДПТ с типом торг-12.
      if (document.DocumentType == DocumentType.GoodsTransferSeller || (isGoodsTransferSeller && !isRevisionGoodsTransferSeller))
      {
        var goodsTransferSellerInfo =
          xdoc.Element("Файл").Element("Документ").Element("СвДокПТПрКроме").Element("СвДокПТПр").Element("ИдентДок");
        documentNumber = GetAttributeValueByName(goodsTransferSellerInfo, "НомДокПТ");
        documentDate = GetAttributeValueByName(goodsTransferSellerInfo, "ДатаДокПТ");
        
        var currencyCodeElement = xdoc.Element("Файл").Element("Документ").Element("СвДокПТПрКроме").Element("СвДокПТПр").Element("ДенИзм");
        currencyCode = GetAttributeValueByName(currencyCodeElement, "КодОКВ");
        var totalAmountElement = xdoc.Element("Файл").Element("Документ").Element("СвДокПТПрКроме").Element("СодФХЖ2").Element("Всего");
        totalAmount = GetAttributeValueByName(totalAmountElement, "СтУчНДСВс");
      }

      if (document.DocumentType == DocumentType.GoodsTransferRevisionSeller || (isGoodsTransferSeller && isRevisionGoodsTransferSeller))
      {
        var goodsTransferSellerInfo =
          xdoc.Element("Файл").Element("Документ").Element("СвДокПТПрКроме").Element("СвДокПТПр");
        var goodsTransferRevisionSellerInfo = goodsTransferSellerInfo.Element("ИспрДокПТ");
        
        var initialDocumentNumber = GetAttributeValueByName(goodsTransferRevisionSellerInfo, "НомИспрДокПТ");
        var initialDocumentDate = GetAttributeValueByName(goodsTransferRevisionSellerInfo, "ДатаИспрДокПТ");
        
        documentNumber = GetAttributeValueByName(goodsTransferSellerInfo.Element("ИдентДок"), "НомДокПТ");
        documentDate = GetAttributeValueByName(goodsTransferSellerInfo.Element("ИдентДок"), "ДатаДокПТ");
        
        comment += Resources.TaxInvoiceRevisionFormat(initialDocumentNumber, initialDocumentDate);
        
        var currencyCodeElement = xdoc.Element("Файл").Element("Документ").Element("СвДокПТПрКроме").Element("СвДокПТПр").Element("ДенИзм");
        currencyCode = GetAttributeValueByName(currencyCodeElement, "КодОКВ");
        var totalAmountElement = xdoc.Element("Файл").Element("Документ").Element("СвДокПТПрКроме").Element("СодФХЖ2").Element("Всего");
        totalAmount = GetAttributeValueByName(totalAmountElement, "СтУчНДСВс");
        
        isRevision = true;
        isAnyRevision = true;
      }
      
      var isWorksTransferSeller = taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.WorksTransferSeller;
      var isRevisionWorksTransferSeller = xdoc.Descendants("ИспрДокПРУ").Any();
      
      // Проверка типа документа по КНД, т.к. из Диадока приходит ДПРР с типом акт старого формата.
      if (document.DocumentType == DocumentType.WorksTransferSeller || (isWorksTransferSeller && !isRevisionWorksTransferSeller))
      {
        var worksTransferSellerInfo =
          xdoc.Element("Файл").Element("Документ").Element("СвДокПРУ").Element("ИдентДок");
        documentNumber = GetAttributeValueByName(worksTransferSellerInfo, "НомДокПРУ");
        documentDate = GetAttributeValueByName(worksTransferSellerInfo, "ДатаДокПРУ");
        
        var currencyCodeElement = xdoc.Element("Файл").Element("Документ").Element("СвДокПРУ").Element("ДенИзм");
        currencyCode = GetAttributeValueByName(currencyCodeElement, "КодОКВ");
        var totalAmountElement = xdoc.Element("Файл").Element("Документ").Element("СвДокПРУ").Element("СодФХЖ1").Element("ОписРабот");
        totalAmount = GetAttributeValueByName(totalAmountElement, "СтУчНДСИт");
      }
      
      if (document.DocumentType == DocumentType.WorksTransferRevisionSeller || (isWorksTransferSeller && isRevisionWorksTransferSeller))
      {
        var worksTransferSellerInfo =
          xdoc.Element("Файл").Element("Документ").Element("СвДокПРУ");
        var worksTransferRevisionSellerInfo = worksTransferSellerInfo.Element("ИспрДокПРУ");
        
        var initialDocumentNumber = GetAttributeValueByName(worksTransferRevisionSellerInfo, "НомИспрДокПРУ");
        var initialDocumentDate = GetAttributeValueByName(worksTransferRevisionSellerInfo, "ДатаИспрДокПРУ");
        
        documentNumber = GetAttributeValueByName(worksTransferSellerInfo.Element("ИдентДок"), "НомДокПРУ");
        documentDate = GetAttributeValueByName(worksTransferSellerInfo.Element("ИдентДок"), "ДатаДокПРУ");
        comment += Resources.TaxInvoiceRevisionFormat(initialDocumentNumber, initialDocumentDate);
        
        var currencyCodeElement = xdoc.Element("Файл").Element("Документ").Element("СвДокПРУ").Element("ДенИзм");
        currencyCode = GetAttributeValueByName(currencyCodeElement, "КодОКВ");
        var totalAmountElement = xdoc.Element("Файл").Element("Документ").Element("СвДокПРУ").Element("СодФХЖ1").Element("ОписРабот");
        totalAmount = GetAttributeValueByName(totalAmountElement, "СтУчНДСИт");
        
        isRevision = true;
        isAnyRevision = true;
      }
      
      // Проверка типа документа по КНД, т.к. из Диадока приходит ДПТ с типом торг-12.
      if (document.DocumentType == DocumentType.Waybill && taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.Waybill)
      {
        var waybillTotalAmountInfo = xdoc.Element("Файл").Element("Документ").Element("СвТНО")
          .Element("ТН").Element("Таблица").Element("ВсегоНакл");
        
        currencyCode = Constants.Module.RoubleCurrencyCode;
        totalAmount = GetAttributeValueByName(waybillTotalAmountInfo, "СумУчНДСВс");
      }
      
      // Проверка типа документа по КНД, т.к. из Диадока приходит ДПРР с типом акт старого формата.
      if (document.DocumentType == DocumentType.Act && taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.Act)
      {
        var actTotalAmountInfo = xdoc.Element("Файл").Element("Документ").Element("СвАктИ").Element("ОписРабот");
        
        currencyCode = Constants.Module.RoubleCurrencyCode;
        totalAmount = GetAttributeValueByName(actTotalAmountInfo, "СумУчНДСИт");
      }
      
      var parentDocument = Docflow.AccountingDocumentBases.Null;
      if (isAdjustment || isRevision)
      {
        var parentDocumentInfo = Sungero.Exchange.ExchangeDocumentInfos.GetAll()
          .Where(x => x.ServiceDocumentId == document.ParentServiceEntityId && Equals(x.Counterparty, sender))
          .FirstOrDefault();

        if (parentDocumentInfo != null && parentDocumentInfo.Document != null)
        {
          parentDocument = Sungero.Docflow.AccountingDocumentBases.As(parentDocumentInfo.Document);
        }
        else if (!string.IsNullOrEmpty(correctedDocumentNumber))
        {
          var datePattern = "dd.MM.yyyy";
          var dateStyle = System.Globalization.DateTimeStyles.None;
          DateTime parsedCorrectedDocumentDate;
          if (!string.IsNullOrEmpty(correctedDocumentDate) &&
              DateTime.TryParseExact(correctedDocumentDate, datePattern, null, dateStyle, out parsedCorrectedDocumentDate))
          {
            parentDocument = Sungero.Docflow.AccountingDocumentBases.GetAll()
              .Where(x => x.RegistrationNumber == correctedDocumentNumber &&
                     x.RegistrationDate == parsedCorrectedDocumentDate && Equals(x.Counterparty, sender))
              .FirstOrDefault();
          }
        }
      }

      if (isAdjustment)
      {
        // Исправление корректировки корректирует первоначальный документ, между корректировкой и исправлением корректировки связь с типом "Прочие".
        if (parentDocument != null &&
            (document.DocumentType == DocumentType.InvoiceCorrectionRevision ||
             document.DocumentType == DocumentType.GeneralTransferSchfCorrectionRevisionSeller ||
             document.DocumentType == DocumentType.GeneralTransferSchfDopCorrectionRevisionSeller ||
             document.DocumentType == DocumentType.GeneralTransferDopCorrectionRevisionSeller))
        {
          correctionRevisionParentDocument = parentDocument;
          corrected = correctionRevisionParentDocument.Corrected;
        }
        else
        {
          corrected = parentDocument;
        }
        
        if (corrected != null)
          contract = corrected.LeadingDocument;
        
        if (corrected != null &&
            document.DocumentType != DocumentType.InvoiceCorrectionRevision &&
            document.DocumentType != DocumentType.GeneralTransferSchfCorrectionRevisionSeller &&
            document.DocumentType != DocumentType.GeneralTransferSchfDopCorrectionRevisionSeller &&
            document.DocumentType != DocumentType.GeneralTransferDopCorrectionRevisionSeller)
          comment = documentComment;
      }
      
      if (isRevision && parentDocument != null)
        contract = parentDocument.LeadingDocument;
      
      if (!string.IsNullOrEmpty(totalAmount) && !string.IsNullOrEmpty(currencyCode))
      {
        // Если распарсить не получилось, валюту не указываем в документе.
        if (!double.TryParse(totalAmount, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out totalAmountNumeric))
          currencyCode = string.Empty;
      }
      
      return Structures.Module.FormalizedDocumentXML.Create(documentNumber, documentDate, isAdjustment, comment, corrected, contract,
                                                            correctionRevisionParentDocument, currencyCode, totalAmountNumeric, isAnyRevision, function);
    }
    
    private static Sungero.Exchange.Structures.Module.ITaxDocumentClassifier GetTaxDocumentClassifier(IDocument document)
    {
      return GetTaxDocumentClassifierByContent(new System.IO.MemoryStream(document.Content));
    }

    /// <summary>
    /// Получить КНД по содержимому документа.
    /// </summary>
    /// <param name="content">Содержимое документа.</param>
    /// <returns>КНД.</returns>
    [Public]
    public static Sungero.Exchange.Structures.Module.ITaxDocumentClassifier GetTaxDocumentClassifierByContent(System.IO.Stream content)
    {
      var xdoc = System.Xml.Linq.XDocument.Load(content);
      RemoveNamespaces(xdoc);
      // Определяем КНД документа для уточнения его типа.
      var documentSection = xdoc.Element("Файл").Element("Документ");
      var taxDocumentClassifierCode = GetAttributeValueByName(documentSection, "КНД");
      var functionUTD = string.Empty;
      
      if (taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.UniversalTransferDocumentSeller ||
          taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.UniversalTransferDocumentSeller155 ||
          taxDocumentClassifierCode == Constants.Module.TaxDocumentClassifier.UniversalCorrectionDocumentSeller)
      {
        functionUTD = GetAttributeValueByName(documentSection, "Функция");
      }
      return TaxDocumentClassifier.Create(taxDocumentClassifierCode, functionUTD);
    }

    /// <summary>
    /// Получить печатную форму из сервиса обмена.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="versionId">Версия документа.</param>
    /// <returns>Признак успешности загрузки печатной формы из сервиса обмена.</returns>
    [Public]
    public virtual bool GeneratePublicBodyFromService(IOfficialDocument document, int versionId)
    {
      var documentInfo = Exchange.PublicFunctions.ExchangeDocumentInfo.Remote.GetExDocumentInfoFromVersion(document, versionId);
      if (documentInfo == null)
        return false;
      
      var client = ExchangeCore.PublicFunctions.BusinessUnitBox.GetPublicClient(documentInfo.RootBox) as NpoComputer.DCX.ClientApi.Client;
      var printedForm = client.GetDocumentPrintedForm(documentInfo.ServiceMessageId, documentInfo.ServiceDocumentId);
      
      if (printedForm != null)
      {
        using (var memory = new System.IO.MemoryStream(printedForm))
        {
          // Выключить error-логирование при доступе к зашифрованной версии.
          AccessRights.SuppressSecurityEvents(
            () =>
            {
              var version = document.Versions.FirstOrDefault(ver => Equals(ver.Id, versionId));
              version.PublicBody.Write(memory);
              version.AssociatedApplication = Content.AssociatedApplications.GetByExtension("pdf");
              document.Save();
            });
        }
        return true;
      }
      
      return false;
    }
    
    #endregion

    #region Отправка задач

    /// <summary>
    /// Стартовать задачу на обработку.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="infos">Информация по обработке документов.</param>
    /// <param name="sender">Отправитель.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="exchangeTaskActiveTextBoundedDocuments">Часть ActiveText для формирования задачи на обработку для связанных документов.</param>
    /// <returns>Признак успешности отправки задачи.</returns>
    protected virtual bool StartExchangeTask(IMessage message, List<IExchangeDocumentInfo> infos, ICounterparty sender,
                                             bool isIncomingMessage, IBoxBase box, string exchangeTaskActiveTextBoundedDocuments)
    {
      var task = this.CreateExchangeTask(message, infos, sender, isIncomingMessage, box, exchangeTaskActiveTextBoundedDocuments);
      if (task != null)
      {
        if (task.Started == null)
          task.Start();
        return true;
      }

      return false;
    }
    
    /// <summary>
    /// Создать задачу на обработку входящих документов эл. обмена.
    /// </summary>
    /// <param name="infos">Информация по документам.</param>
    /// <param name="counterparty">КА из сервиса обмена.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <param name="incomeDate">Дата получения.</param>
    /// <param name="mainProcessingTask">Главная задача.</param>
    /// <returns>Задача на обработку входящих документов эл. обмена.</returns>
    public IExchangeDocumentProcessingTask CreateExchangeTask(List<IExchangeDocumentInfo> infos, ICounterparty counterparty,
                                                              IBoxBase box, DateTime incomeDate, ITask mainProcessingTask)
    {
      var isMainProcessingTaskExists = mainProcessingTask != null;
      var task = isMainProcessingTaskExists ?
        Sungero.Exchange.ExchangeDocumentProcessingTasks.CreateAsSubtask(mainProcessingTask) :
        Sungero.Exchange.ExchangeDocumentProcessingTasks.Create();
      
      task.Box = box;
      task.Counterparty = counterparty;
      task.ExchangeService = ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box);
      task.Assignee = ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(box, counterparty, infos);
      task.Deadline = Sungero.ExchangeCore.PublicFunctions.BoxBase.GetProcessingTaskDeadline(box, task.Assignee);
      task.IncomeDate = incomeDate;
      var isIncomingMessage = infos.FirstOrDefault().MessageType == Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      task.IsIncoming = isIncomingMessage;
      
      var counterpartyDepartmentBox = infos.Select(i => i.CounterpartyDepartmentBox).FirstOrDefault();
      task.CounterpartyDepartmentBox = counterpartyDepartmentBox;
      
      // Если задача не ответственному за текущий ящик, сообщить об этом исполнителю.
      if (Equals(box.Routing, ExchangeCore.DepartmentBox.Routing.BoxResponsible) && !Equals(task.Assignee, box.Responsible) && ExchangeCore.DepartmentBoxes.Is(box))
      {
        var departmentBox = ExchangeCore.DepartmentBoxes.As(box);
        var department = departmentBox.Department;
        var departmentName = department != null ? department.Name : departmentBox.ServiceName;
        
        if (box.Status == ExchangeCore.BoxBase.Status.Closed)
          task.ActiveText = ExchangeDocumentProcessingTasks.Resources.DocumentSentToClosedBoxFormat(departmentName);
        else
          task.ActiveText = ExchangeDocumentProcessingTasks.Resources.DocumentSentToAnotherResponsibleFormat(departmentName);
        
        task.ActiveText += Environment.NewLine;
      }
      
      using (Sungero.Core.CultureInfoExtensions.SwitchTo(TenantInfo.Culture))
        task.Subject = this.GenerateExchangeTaskSubject(task);
      task.Save();
      return task;
    }
    
    /// <summary>
    /// Добавить наблюдателей в задачу на обработку соглашения об аннулировании.
    /// </summary>
    /// <param name="task">Задача на обработку соглашения об аннулировании.</param>
    /// <param name="parentInfo">Информация об основном документе.</param>
    public virtual void AddObserversToCancellationAgreementProcessingTask(IExchangeDocumentProcessingTask task, IExchangeDocumentInfo parentInfo)
    {
      var observers = this.GetCancellationAgreementProcessingTaskObservers(task, parentInfo);
      foreach (var observer in observers)
      {
        if (!Equals(observer, task.Assignee) && !task.Observers.Select(record => record.Observer).Contains(observer))
        {
          var observerRecord = task.Observers.AddNew();
          observerRecord.Observer = observer;
        }
      }
    }
    
    /// <summary>
    /// Получить наблюдателей задачи на обработку соглашения об аннулировании.
    /// </summary>
    /// <param name="task">Задача на обработку соглашения об аннулировании.</param>
    /// <param name="parentInfo">Информация об основном документе.</param>
    /// <returns>Список наблюдателей.</returns>
    public List<IRecipient> GetCancellationAgreementProcessingTaskObservers(IExchangeDocumentProcessingTask task, IExchangeDocumentInfo parentInfo)
    {
      var parentDocumentProcessingTask = this.GetExchangeDocumentProcessingTask(parentInfo);
      var isParentDocumentSentByCounterparty = parentDocumentProcessingTask != null;
      var observers = this.GetResponsiblesFromParentDocument(task, parentInfo);
      if (isParentDocumentSentByCounterparty)
      {
        var parentDocumentProcessingAssignments = ExchangeDocumentProcessingAssignments.GetAll(a => a.MainTask.Id == parentDocumentProcessingTask.Id);
        var performers = parentDocumentProcessingAssignments.Select(a => Recipients.As(a.Performer)).Where(p => p != null);
        observers = observers.Concat(performers).ToList();
      }
      return observers;
    }
    
    /// <summary>
    /// Получить ответственных за основной документ.
    /// </summary>
    /// <param name="task">Задача на обработку соглашения об аннулировании.</param>
    /// <param name="parentInfo">Информация об основном аннулировании.</param>
    /// <returns>Список ответственных.</returns>
    public List<IRecipient> GetResponsiblesFromParentDocument(IExchangeDocumentProcessingTask task, IExchangeDocumentInfo parentInfo)
    {
      var parentDocuments = new List<IOfficialDocument>();
      var parentDocumentProcessingTask = this.GetExchangeDocumentProcessingTask(parentInfo);
      var isParentDocumentSentByCounterparty = parentDocumentProcessingTask != null;
      if (isParentDocumentSentByCounterparty)
        parentDocuments = this.GetParentDocumentsFromProcessingTask(parentDocumentProcessingTask);
      else
        parentDocuments.Add(parentInfo.Document);
      
      var responsibles = new List<IRecipient>();
      foreach (var document in parentDocuments)
      {
        var responsible = this.GetDocumentResponsible(document);
        if (responsible != null)
          responsibles.Add(responsible);
        else if (!isParentDocumentSentByCounterparty)
          responsibles.Add(Recipients.As(document.Author));
      }
      return responsibles;
    }
    
    /// <summary>
    /// Получить основные документы из задачи на обработку основного документа.
    /// </summary>
    /// <param name="task">Задача на обработку основного документа.</param>
    /// <returns>Список основным документов.</returns>
    private List<IOfficialDocument> GetParentDocumentsFromProcessingTask(IExchangeDocumentProcessingTask task)
    {
      var needSigningDocuments = task.NeedSigning.All.Where(doc => !CancellationAgreements.Is(doc));
      var dontNeedSigningDocuments = task.DontNeedSigning.All.Where(doc => !CancellationAgreements.Is(doc));
      return needSigningDocuments.Concat(dontNeedSigningDocuments).Select(doc => OfficialDocuments.As(doc)).ToList();
    }
    
    /// <summary>
    /// Получить ответственного за документ.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>Ответственный.</returns>
    private IRecipient GetDocumentResponsible(IOfficialDocument document)
    {
      IRecipient responsible = null;
      if (Contracts.ContractualDocuments.Is(document))
        responsible = Contracts.ContractualDocuments.As(document).ResponsibleEmployee;
      else if (Docflow.AccountingDocumentBases.Is(document))
        responsible = Docflow.AccountingDocumentBases.As(document).ResponsibleEmployee;
      else if (document.PreparedBy != null)
        responsible = document.PreparedBy;
      
      return responsible;
    }
    
    /// <summary>
    /// Создать задачу на обработку.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="infos">Информация по документам, созданным из сообщения, по которому формируется задача.</param>
    /// <param name="sender">Отправитель.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="exchangeTaskActiveTextBoundedDocuments">Часть ActiveText для формирования задачи на обработку для связанных документов.</param>
    /// <returns>Задача.</returns>
    protected virtual IExchangeDocumentProcessingTask CreateExchangeTask(IMessage message, List<IExchangeDocumentInfo> infos,
                                                                         ICounterparty sender, bool isIncomingMessage, IBoxBase box, string exchangeTaskActiveTextBoundedDocuments)
    {
      var infosForSend = new List<IExchangeDocumentInfo>();
      ITask mainProcessingTask = null;
      IExchangeDocumentProcessingTask parentTask = ExchangeDocumentProcessingTasks.Null;
      foreach (var info in infos)
      {
        parentTask = this.GetExchangeDocumentProcessingTask(info);
        
        if (parentTask == null)
          infosForSend.Add(info);
        else if (mainProcessingTask == null)
          mainProcessingTask = parentTask.MainTask;
      }
      
      if (!infosForSend.Any())
        return ExchangeDocumentProcessingTasks.As(mainProcessingTask) ?? parentTask;
      
      if (infos.Count == 1 && CancellationAgreements.Is(infos.FirstOrDefault().Document))
        mainProcessingTask = this.GetExchangeDocumentProcessingTask(infos.FirstOrDefault().ParentDocumentInfo);
      
      var task = this.CreateExchangeTask(infos, sender, box, message.TimeStamp, mainProcessingTask);
      task.Save();
      
      var text = string.Empty;
      if (!isIncomingMessage)
        text = ExchangeDocumentProcessingTasks.Resources.OutcomingDocumentProcessingTaskActiveTextFormat(ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box));
      else
        text = ExchangeDocumentProcessingTasks.Resources.TaskActiveText;
      
      if (task.ActiveText != string.Empty)
      {
        text += Environment.NewLine;
        text += Environment.NewLine;
        text += task.ActiveText;
      }
      task.ActiveText = text;
      
      var needSign = new List<Docflow.IOfficialDocument>();
      var signed = new List<Docflow.IOfficialDocument>();
      var dontNeedSign = new List<Docflow.IOfficialDocument>();
      foreach (var info in infos.Where(i => i.Document != null))
      {
        var exchangeDocument = info.Document;
        var processingDocument = message.PrimaryDocuments.Single(d => d.ServiceEntityId == info.ServiceDocumentId);
        if (processingDocument.SignStatus == NpoComputer.DCX.Common.SignStatus.Waiting
            || processingDocument.NeedSign && processingDocument.SignStatus == NpoComputer.DCX.Common.SignStatus.None)
        {
          if (isIncomingMessage)
            needSign.Add(exchangeDocument);
          else
            dontNeedSign.Add(exchangeDocument);
        }
        else if (processingDocument.SignStatus == NpoComputer.DCX.Common.SignStatus.Signed)
          signed.Add(exchangeDocument);
        else
          dontNeedSign.Add(exchangeDocument);
      }
      
      foreach (var doc in needSign)
      {
        task.NeedSigning.All.Add(doc);
      }
      
      foreach (var doc in signed)
      {
        if (isIncomingMessage)
        {
          var hyperlink = Hyperlinks.Get(doc);
          task.ActiveText += Environment.NewLine;
          task.ActiveText += Environment.NewLine;
          task.ActiveText += ExchangeDocumentProcessingTasks.Resources.DocumentIsSignedByUsFormat(hyperlink);
        }
        else
        {
          var hyperlink = Hyperlinks.Get(doc);
          task.ActiveText += Environment.NewLine;
          task.ActiveText += Environment.NewLine;
          task.ActiveText += Resources.DocumentIsSignedByBothSidesFormat(hyperlink);
        }
        
        task.DontNeedSigning.All.Add(doc);
      }
      
      var processedDocumentTypes = this.GetSupportedPrimaryDocumentTypes();
      var processingDocuments = message.PrimaryDocuments.Where(x => processedDocumentTypes.Contains(x.DocumentType.Value));
      var rejected = processingDocuments.Where(d => d.SignStatus == SignStatus.Rejected).ToList();
      if (rejected.Any())
      {
        task.ActiveText += Environment.NewLine;
        task.ActiveText += Environment.NewLine;
        
        var documents = string.Join(", ", rejected.Select(r => r.FileName));
        if (rejected.Count == 1)
          task.ActiveText += ExchangeDocumentProcessingTasks.Resources.DocumentIsRejectedByUsFormat(documents);
        else
          task.ActiveText += ExchangeDocumentProcessingTasks.Resources.DocumentsIsRejectedByUsFormat(documents);
      }
      
      foreach (var doc in dontNeedSign)
      {
        task.DontNeedSigning.All.Add(doc);
      }
      
      if (mainProcessingTask == null)
        task.ActiveText += exchangeTaskActiveTextBoundedDocuments;
      else
      {
        task.ActiveText += Environment.NewLine;
        task.ActiveText += Environment.NewLine;
        task.ActiveText += Sungero.Exchange.Resources.AdditionalDocumentSend;
        foreach (var additionalDocument in infosForSend.Select(d => d.Document))
        {
          task.ActiveText += Environment.NewLine;
          task.ActiveText += Hyperlinks.Get(additionalDocument);
        }
      }
      
      // Обработка формализованных документов в сообщении.
      var formalizedDocuments = message.PrimaryDocuments.Where(x => !processedDocumentTypes.Contains(x.DocumentType.Value));
      if (formalizedDocuments.Any())
      {
        task.ActiveText += System.Environment.NewLine;
        task.ActiveText += System.Environment.NewLine;
        task.ActiveText += this.GenerateActiveTextFromUnsupportedDocuments(formalizedDocuments, sender, isIncomingMessage, box, message.TimeStamp, false);
      }
      
      task.Save();
      return task;
    }
    
    private string GenerateExchangeTaskSubject(IExchangeDocumentProcessingTask task)
    {
      var businessUnit = ExchangeCore.PublicFunctions.BoxBase.GetBusinessUnit(task.Box);
      var subject = string.Empty;
      var dateWithUTC = Sungero.Docflow.PublicFunctions.Module.GetDateWithUTCLabel(task.IncomeDate.Value);
      subject = task.IsIncoming == true ?
        ExchangeDocumentProcessingTasks.Resources.TaskSubjectFormat(task.Counterparty, businessUnit, dateWithUTC, task.ExchangeService) :
        ExchangeDocumentProcessingTasks.Resources.TaskSubjectFormat(businessUnit, task.Counterparty, dateWithUTC, task.ExchangeService);
      subject = CutText(subject, task.Info.Properties.Subject.Length);
      return subject;
    }

    /// <summary>
    /// Отправлять задания/уведомления ответственному.
    /// </summary>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="message">Сообщение.</param>
    /// <returns>Признак отправки задания ответственному за ящик/контрагента.</returns>
    protected virtual bool NeedReceiveDocumentProcessingTask(IBoxBase box, IMessage message)
    {
      return ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(box);
    }
    
    #endregion

    #endregion

    #region Обработка ответов от контрагентов
    
    #region Обработка подписания

    /// <summary>
    /// Обработать пришедшие подписи к неформализованным документам.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="client">Клиент к сервису обмена.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="historyOperation">Операция истории - мы подписали или КА подписал.</param>
    /// <param name="historyComment">Комментарий к операции истории.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessNonformalizedSign(IMessage message, IMessageQueueItem queueItem, DcxClient client, IBoxBase box,
                                                    ICounterparty sender, bool isIncomingMessage, Enumeration historyOperation, string historyComment)
    {
      this.LogDebugFormat(message, queueItem, box, "Execute ProcessNonformalizedSign.");
      foreach (var document in this.GetSignedNonformalizedPrimaryDocuments(message))
      {
        var doc = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, document.ServiceEntityId);
        
        var sign = message.Signatures.FirstOrDefault(x => x.DocumentId == document.ServiceEntityId);
        if (sign == null)
        {
          this.LogDebugFormat(message, queueItem, box, "Message not contain a signature.");
          return false;
        }
        
        var certificateInfo = Docflow.PublicFunctions.Module.GetSignatureCertificateInfo(sign.Content);
        var signatoryInfo = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(certificateInfo.SubjectInfo);
        
        // Пропускаем документы, которые были отправлены нами через личный кабинет сервиса обмена, а также документы без подписи.
        if (doc == null)
        {
          if (this.CanProcessMessageLater(message, queueItem, ExchangeCore.PublicFunctions.BoxBase.GetRootBox(box), document.ServiceEntityId))
          {
            this.LogDebugFormat(message, document, "Document not found for received signature.");
            return false;
          }
          
          continue;
        }
        
        // Когда контрагент прислал нам подпись, а у нас нет документа - нужно немного магии с сообщениями.
        if (doc.Document == null)
        {
          // Загружаем первичное сообщение для создания документа, который не был создан сразу.
          doc = this.LoadDocumentWithSecondSign(message, doc, document, client, sender, isIncomingMessage, box);
        }

        var sentVersion = doc.Document.Versions.FirstOrDefault(x => x.Id == doc.VersionId);
        var newDocumentHash = document.Content.GetMD5Hash();
        var versionIsChanged = false;
        
        if (sentVersion != null && (newDocumentHash == Docflow.PublicFunctions.OfficialDocument.GetVersionBodyHash(doc.Document, sentVersion)))
        {
          // Прикрепление к неизмененной версии.
          var signatures = message.Signatures.Where(x => x.DocumentId == document.ServiceEntityId);
          var addedThumbprints = Signatures.Get(sentVersion)
            .Where(s => s.SignCertificate != null)
            .Select(x => x.SignCertificate.Thumbprint);
          foreach (var signature in signatures)
          {
            certificateInfo = Docflow.PublicFunctions.Module.GetSignatureCertificateInfo(signature.Content);
            var signatureIsAlreadyAdded = addedThumbprints.Any(x => x.Equals(certificateInfo.Thumbprint));
            if (!signatureIsAlreadyAdded)
            {
              signatoryInfo = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(certificateInfo.SubjectInfo);
              this.SignDocument(doc, signature, sentVersion, signatoryInfo, message.TimeStamp);
            }
          }
        }
        else
        {
          var outMessage = client.GetMessage(doc.ServiceMessageId);
          
          if (outMessage != null)
          {
            var ourSign = outMessage.Signatures.FirstOrDefault(x => x.DocumentId == document.ServiceEntityId);
            var ourCertificateInfo = Docflow.PublicFunctions.Module.GetSignatureCertificateInfo(ourSign.Content);
            
            var application = GetOrCreateAssociatedApplicationByDocumentName(document.FileName);
            CreateVersionFromExchangeDocument(doc.Document, document, application);
            
            var originalVersion = doc.Document.LastVersion;
            
            // Прикрепляем нашу подпись как внешнюю.
            var ourSignatoryInfo = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(ourCertificateInfo.SubjectInfo);
            this.SignDocument(doc, ourSign, originalVersion, ourSignatoryInfo, outMessage.TimeStamp);
            
            // Прикрепляем подпись контрагента.
            this.SignDocument(doc, sign, originalVersion, signatoryInfo, message.TimeStamp);
            
            sentVersion = originalVersion;
            doc.VersionId = originalVersion.Id;
            doc.Save();
            
            versionIsChanged = true;
          }
        }
        
        this.ProcessSharedSign(doc.Document, doc, isIncomingMessage, box, sentVersion, signatoryInfo, versionIsChanged, historyOperation, historyComment, true);
        this.FillCounterpartyDataFromReplyMessage(message, doc.ServiceDocumentId, doc.Document, sender, isIncomingMessage);
      }
      
      return true;
    }
    
    /// <summary>
    /// Получить подписанные неформализованные документы из сообщения.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <returns>Подписанные неформализованные документы.</returns>
    public virtual List<NpoComputer.DCX.Common.IDocument> GetSignedNonformalizedPrimaryDocuments(NpoComputer.DCX.Common.IMessage message)
    {
      return message.PrimaryDocuments
        .Where(x => x.SignStatus == NpoComputer.DCX.Common.SignStatus.Signed &&
               x.DocumentType == NpoComputer.DCX.Common.DocumentType.Nonformalized)
        .ToList();
    }
    
    /// <summary>
    /// Обработать пришедшие титулы к формализованным документам.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="queueItems">Все элементы очереди.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="historyOperation">Операция истории - мы подписали или КА подписал.</param>
    /// <param name="historyComment">Комментарий к операции истории.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessFormalizedSign(IMessage message, IMessageQueueItem queueItem, List<IMessageQueueItem> queueItems,
                                                 bool isIncomingMessage, IBoxBase box, Enumeration historyOperation, string historyComment)
    {
      this.LogDebugFormat(message, queueItem, box, "Execute ProcessFormalizedSign.");
      // Обработка титулов покупателей.
      foreach (var document in message.ReglamentDocuments.Where(x => this.GetSupportedReglamentDocumentTypes().Contains(x.DocumentType)))
      {
        if (!this.ProcessFormalizedTitlesAndSigns(message, queueItem, queueItems, isIncomingMessage, box, historyOperation, historyComment,
                                                  document.RootServiceEntityId, document.ServiceEntityId, document.Content))
          return false;
      }
      
      // Загрузка ответной подписи на СЧФ для СБИС.
      if (queueItem.RootBox.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis)
      {
        foreach (var document in message.PrimaryDocuments.Where(d => d.SignStatus == NpoComputer.DCX.Common.SignStatus.Signed &&
                                                                d.DocumentType == NpoComputer.DCX.Common.DocumentType.GeneralTransferSchfSeller))
        {
          if (!this.ProcessFormalizedTitlesAndSigns(message, queueItem, queueItems, isIncomingMessage, box, historyOperation, historyComment,
                                                    document.ServiceEntityId, document.ServiceEntityId, null))
            return false;
        }
      }
      
      return true;
    }
    
    /// <summary>
    /// Обработать пришедшие титулы к формализованным документам и ответные подписи на СЧФ из СБИСа.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="queueItems">Все элементы очереди.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="historyOperation">Операция истории - мы подписали или КА подписал.</param>
    /// <param name="historyComment">Комментарий к операции истории.</param>
    /// <param name="rootServiceDocumentId">Ид родительского документа на сервисе.</param>
    /// <param name="serviceDocumentId">Ид документа на сервисе.</param>
    /// <param name="reglamentDocumentContent">Контент титула.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessFormalizedTitlesAndSigns(IMessage message, IMessageQueueItem queueItem, List<IMessageQueueItem> queueItems,
                                                           bool isIncomingMessage, IBoxBase box, Enumeration historyOperation, string historyComment,
                                                           string rootServiceDocumentId, string serviceDocumentId, byte[] reglamentDocumentContent)
    {
      this.LogDebugFormat(message, queueItem, box, "Execute ProcessFormalizedTitlesAndSigns.");
      var doc = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, rootServiceDocumentId);
      if (doc == null)
      {
        var documentInService = message.PrimaryDocuments.FirstOrDefault(d => d.ServiceEntityId == rootServiceDocumentId);
        
        if (documentInService != null && documentInService.BuyerAcceptanceStatus == NpoComputer.DCX.Common.BuyerAcceptanceStatus.Rejected)
          return true;
        
        // Если документ ещё в очереди, обработаем позже.
        if (this.CanProcessMessageLater(message, queueItem, ExchangeCore.PublicFunctions.BoxBase.GetRootBox(box), rootServiceDocumentId))
        {
          this.LogDebugFormat(queueItem, "Document not found for received signature: ServiceEntityId: '{0}', RootServiceEntityId '{1}'.",
                              serviceDocumentId, rootServiceDocumentId);
          return false;
        }
        
        this.LogDebugFormat(message, queueItem, box, "Exit ProcessFormalizedTitlesAndSigns.");
        
        return true;
      }
      
      // Документ был подписан в RX, заканчиваем обработку.
      if ((doc.OutgoingStatus == Exchange.ExchangeDocumentInfo.OutgoingStatus.Signed ||
           doc.OutgoingStatus == Exchange.ExchangeDocumentInfo.OutgoingStatus.Rejected &&
           doc.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Rejected) &&
          doc.ReceiverSignId != null)
      {
        this.LogDebugFormat(message, queueItem, box, "Document with serviceDocumentId = '{0}' contains a signature.", doc.ServiceDocumentId);
        return true;
      }
      
      var sign = message.Signatures.FirstOrDefault(x => x.DocumentId == serviceDocumentId);
      if (sign == null)
      {
        this.LogDebugFormat(message, queueItem, box, "Message not contain a signature for document with id = '{0}'.", serviceDocumentId);
        return false;
      }
      
      var primaryDocument = message.PrimaryDocuments.FirstOrDefault(d => d.ServiceEntityId == rootServiceDocumentId);
      if (primaryDocument != null && primaryDocument.BuyerAcceptanceStatus != null)
        doc.BuyerAcceptanceStatus = this.GetBuyerAcceptanceStatus(primaryDocument);
      
      var reglamentDocument = message.ReglamentDocuments.FirstOrDefault(d => d.ServiceEntityId == serviceDocumentId);
      if (reglamentDocument != null)
      {
        doc.BuyerDeliveryConfirmationStatus = ResolveReceiptStatus(reglamentDocument.ReceiptStatus);
        doc.ExternalBuyerTitleId = reglamentDocument.ServiceEntityId;
      }
      
      var x509certificate = Docflow.PublicFunctions.Module.GetSignatureCertificateInfo(sign.Content);
      var signatoryInfo = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(x509certificate.SubjectInfo);
      
      if (doc.Document != null)
      {
        var formalizedDocument = Sungero.Docflow.AccountingDocumentBases.As(doc.Document);
        if (formalizedDocument != null && reglamentDocumentContent != null)
        {
          using (var memory = new System.IO.MemoryStream(reglamentDocumentContent))
          {
            // Выключить error-логирование при доступе к зашифрованной версии.
            AccessRights.SuppressSecurityEvents(
              () =>
              {
                formalizedDocument.CreateVersion();
                var version = formalizedDocument.LastVersion;
                version.AssociatedApplication = GetOrCreateAssociatedApplicationByDocumentName("file.xml");
                version.Note = FinancialArchive.Resources.BuyerTitleVersionNote;
                formalizedDocument.BuyerTitleId = version.Id;
                version.Body.Write(memory);
                formalizedDocument.Save();
              });
          }
        }

        this.SignDocument(doc, sign, doc.Document.LastVersion, signatoryInfo, message.TimeStamp);
        if (formalizedDocument != null)
        {
          var lastSignature = this.GetLastDocumentSignature(formalizedDocument);
          formalizedDocument.BuyerSignatureId = lastSignature.Id;
        }
        
        var sentVersion = doc.Document.Versions.FirstOrDefault(x => x.Id == doc.VersionId);
        
        if (doc.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Rejected)
        {
          this.ProcessSharedReject(doc, doc.Document, isIncomingMessage, box, sign.Content, historyOperation, historyComment, string.Empty, string.Empty, true);
        }
        else
        {
          this.ProcessSharedSign(doc.Document, doc, isIncomingMessage, box, doc.Document.LastVersion, signatoryInfo, false, historyOperation, historyComment, true);
          this.FillCounterpartyDataFromReplyMessage(message, serviceDocumentId, formalizedDocument, formalizedDocument.Counterparty, isIncomingMessage);
        }
      }

      return true;
    }

    /// <summary>
    /// Обработать подписание документа - как из RX, так и из веба.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="info">Инфошка документа.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="signedVersion">Реально подписанная версия (на случай как раз изменения отправленной версии).</param>
    /// <param name="signatoryInfo">Информация о подписавшем для задач уведомления. Будет пустой, если вызвано из действия "Подписать и отправить".</param>
    /// <param name="sentVersionIsChanged">Признак того, что версия была изменена в RX после отправки (не должно существовать?).</param>
    /// <param name="historyOperation">Операция - подписали мы или контрагент.</param>
    /// <param name="historyComment">Комментарий к операции в истории.</param>
    /// <param name="isAgent">Признак вызова из фонового процесса. Иначе - пользователем в RX.</param>
    protected virtual void ProcessSharedSign(IOfficialDocument document, IExchangeDocumentInfo info, bool isIncomingMessage, IBoxBase box,
                                             IElectronicDocumentVersions signedVersion, string signatoryInfo, bool sentVersionIsChanged,
                                             Enumeration historyOperation, string historyComment, bool isAgent)
    {
      this.LogDebugFormat(info, "Execute ProcessSharedSign.");
      // TODO: Логика походит на обработку отказа, возможно можно заиспользовать методы из него.
      var notSigned = info.OutgoingStatus != Exchange.ExchangeDocumentInfo.OutgoingStatus.Signed;
      var exchangeService = ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box);
      var needReceive = ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(box);
      if (!isIncomingMessage && notSigned)
      {
        var responsible = ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(info.Box, info.Counterparty, new List<IExchangeDocumentInfo>() { info });
        if (isAgent && needReceive && info.DownloadSession == null)
          this.CreateNoticeAfterOurSigning(document, box, true, false, string.Empty);
        info.OutgoingStatus = Exchange.ExchangeDocumentInfo.OutgoingStatus.Signed;
        this.AddTrackingAfterSignOrReject(info, document);
      }
      
      var detailedOperation = new Enumeration(Constants.Module.Exchange.DetailedSign);
      if (isIncomingMessage)
        document.ExternalApprovalState = Docflow.OfficialDocument.ExternalApprovalState.Signed;
      else
        document.InternalApprovalState = Docflow.OfficialDocument.InternalApprovalState.Signed;

      var externalApprovalInTracking = document.Tracking.Where(x => x.Action == Docflow.OfficialDocumentTracking.Action.Endorsement
                                                               && !x.ReturnResult.HasValue && x.ExternalLinkId == info.Id);
      foreach (var trackingString in externalApprovalInTracking)
      {
        trackingString.ReturnResult = Docflow.OfficialDocumentTracking.ReturnResult.Signed;
        
        // Логика по прекращению согласования (контроль возврата и т.д.), уведомление ответственному.
        if (isAgent)
          this.SendDocumentReplyNotice(box, trackingString, signedVersion.Number, sentVersionIsChanged, true, signatoryInfo, false, exchangeService.Name, string.Empty);
      }
      
      document.ExchangeState = Docflow.OfficialDocument.ExchangeState.Signed;
      info.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.Signed;

      if (notSigned)
        document.History.Write(historyOperation, detailedOperation, historyComment, signedVersion.Number);

      // Добавление в очередь генерации PublicBody после диалогового подписания происходит в методах SendBuyerTitle
      // и SendAnswerToNonformalizedDocument, здесь только синхронная агентская генерация.
      var accountingDocument = Docflow.AccountingDocumentBases.As(document);
      if (isAgent)
      {
        if (accountingDocument != null && accountingDocument.IsFormalized == true)
          Docflow.PublicFunctions.Module.Remote.GeneratePublicBodyForFormalizedDocument(accountingDocument, signedVersion.Id, accountingDocument.ExchangeState);
        else
          Docflow.PublicFunctions.Module.Remote.GeneratePublicBodyForNonformalizedDocument(document, signedVersion.Id);
      }
      document.Save();
      info.Save();
    }
    
    /// <summary>
    /// Создать и отправить задачу на обработку подписанного обеими сторонами документа.
    /// </summary>
    /// <param name="message">Сообщение сервиса обмена.</param>
    /// <param name="info">Информация о документе.</param>
    /// <param name="sender">Отправитель.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    protected virtual void SendSignedDocumentProcessingTask(IMessage message, IExchangeDocumentInfo info, ICounterparty sender, IBoxBase box)
    {
      var task = this.CreateExchangeTask(new List<IExchangeDocumentInfo>() { info }, sender, info.Box, info.MessageDate.Value, null);

      var text = string.Empty;
      text = ExchangeDocumentProcessingTasks.Resources.TaskActiveText;
      if (task.ActiveText != string.Empty)
      {
        text += Environment.NewLine;
        text += Environment.NewLine;
        text += task.ActiveText;
      }

      task.ActiveText = text;

      var hyperlink = Hyperlinks.Get(info.Document);
      task.ActiveText += Environment.NewLine;
      task.ActiveText += Environment.NewLine;
      task.ActiveText += Resources.DocumentIsSignedByBothSidesFormat(hyperlink);

      task.ActiveText += this.ProcessBoundedDocuments(message.PrimaryDocuments, new List<Docflow.IOfficialDocument>() { info.Document }, false, box);

      this.GrantAccessRightsForUpperBoxResponsibles(info.Document, box);
      task.DontNeedSigning.All.Add(info.Document);

      task.Save();
      task.Start();
    }
    
    /// <summary>
    /// Обработка подписи по документу, который еще не был загружен.
    /// </summary>
    /// <param name="message">Сообщение с подписью.</param>
    /// <param name="info">Информация о документе.</param>
    /// <param name="document">Документ из сервиса обмена.</param>
    /// <param name="client">Клиент DCX.</param>
    /// <param name="sender">Отправитель.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <returns>Информация о документе с обновлением.</returns>
    protected virtual IExchangeDocumentInfo LoadDocumentWithSecondSign(IMessage message, IExchangeDocumentInfo info, IDocument document, DcxClient client,
                                                                       ICounterparty sender, bool isIncomingMessage, IBoxBase box)
    {
      this.LogDebugFormat(info, "Execute LoadDocumentWithSecondSign.");
      var firstMessage = client.GetMessage(info.ServiceMessageId);
      var firstDocument = firstMessage.PrimaryDocuments.SingleOrDefault(d => d.ServiceEntityId == info.ServiceDocumentId);

      var serviceCounterpartyId = string.Empty;
      if (!isIncomingMessage)
        serviceCounterpartyId = message.Sender.Organization.OrganizationId;
      else
        serviceCounterpartyId = message.Receiver.Organization.OrganizationId;
      var serviceCounterpartyDepartmentId = GetServiceCounterpartyDepartmentId(message, isIncomingMessage);
      info.Document = this.GetOrCreateNewExchangeDocument(firstDocument, sender, serviceCounterpartyId, !isIncomingMessage, firstMessage.TimeStamp, box, serviceCounterpartyDepartmentId);

      // Переполучаем инфошку, она меняется.
      info = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, document.ServiceEntityId);

      // Копипаста подписания при получении нового сообщения.
      var signature = firstMessage.Signatures.FirstOrDefault(x => x.DocumentId == firstDocument.ServiceEntityId);
      if (signature != null)
      {
        var x509certificate = Docflow.PublicFunctions.Module.GetSignatureCertificateInfo(signature.Content);
        var signInfo = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(x509certificate.SubjectInfo);
        this.SignDocument(info, signature, info.Document.LastVersion, signInfo, firstMessage.TimeStamp);
      }

      // Отправить задачу на обработку подписанного обеими сторонами документа.
      var needReceive = ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(box) && Exchange.PublicFunctions.ExchangeDocumentInfo.NeedReceiveTask(info);
      if (needReceive && info.DownloadSession == null)
      {
        this.SendSignedDocumentProcessingTask(message, info, sender, box);
      }

      return info;
    }

    /// <summary>
    /// Подписать документ.
    /// </summary>
    /// <param name="info">Информация о подписываемой версии.</param>
    /// <param name="sign">Подпись.</param>
    /// <param name="version">Версия, которую подписывают.</param>
    /// <param name="signatoryName">Имя подписывающего.</param>
    /// <param name="date">Дата подписи, на случай если её нет в подписи и не получается выполнить импорт легально.</param>
    /// <remarks>В случае если подпись без даты, которая в Sungero обязательна, будет выполнена попытка проставить подпись
    /// хоть как-нибудь. Подпись после этого будет отображаться как невалидная, но она хотя бы будет.
    /// Валидная подпись останется только в сервисе обмена.</remarks>
    protected virtual void SignDocument(IExchangeDocumentInfo info, Signature sign,
                                        IElectronicDocumentVersions version, string signatoryName, DateTime date)
    {
      this.LogDebugFormat(info, "Execute SignDocument.");
      var entity = (Domain.Shared.IExtendedEntity)info.Document;
      entity.Params[ExchangeCore.PublicConstants.BoxBase.JobRunned] = true;

      try
      {
        var unsignedAdditionalInfo = Docflow.PublicFunctions.Module.FormatUnsignedAttribute(Docflow.PublicConstants.Module.UnsignedAdditionalInfoKeyFPoA, sign.FormalizedPoAUnifiedRegNumber);
        Signatures.Import(info.Document, SignatureType.Approval, signatoryName, sign.Content, date, unsignedAdditionalInfo, version);
      }
      catch (Exception ex)
      {
        this.LogDebugFormat(info, "Can't import signature on document, error: {0}", ex);
      }
      
      entity.Params[ExchangeCore.PublicConstants.BoxBase.JobRunned] = false;
      
      var fromCounterparty = GetClient(info.RootBox).OurSubscriber.BoxId != sign.SignerBoxId;
      
      var signature = Signatures.Get(version)
        .Where(s => s.IsExternal == true && s.SignCertificate != null)
        .OrderByDescending(x => x.Id)
        .FirstOrDefault();
      
      if (signature != null)
      {
        if (info.MessageType == Exchange.ExchangeDocumentInfo.MessageType.Incoming ? fromCounterparty : !fromCounterparty)
          info.SenderSignId = signature.Id;
        else
          info.ReceiverSignId = signature.Id;
        info.Save();
      }
      else
      {
        this.LogDebugFormat(info, "Can't find signature on document with version id: '{0}'", version.Id);
      }
    }

    #endregion

    #region Обработка отказа в подписании

    /// <summary>
    /// Обработать документы с отказом в подписании.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Ящик.</param>
    /// <param name="historyOperation">Операция истории - мы отказали или нам отказали.</param>
    /// <param name="historyComment">Комментарий - кто и кому.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessReject(IMessage message, IMessageQueueItem queueItem, bool isIncomingMessage, IBoxBase box,
                                         Enumeration historyOperation, string historyComment)
    {
      this.LogDebugFormat(message, queueItem, box, "Execute ProcessReject.");
      foreach (var reglamentDoc in this.GetRejectReglamentDocuments(message))
      {
        var primaryDocument = message.PrimaryDocuments.FirstOrDefault(x => x.ServiceEntityId == reglamentDoc.ParentServiceEntityId);
        
        if (primaryDocument == null)
          continue;
        
        var doc = Docflow.OfficialDocuments.Null;

        var exchangeDocumentInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, primaryDocument.ServiceEntityId);
        
        if (exchangeDocumentInfo != null)
          doc = exchangeDocumentInfo.Document;
        
        this.LogDebugFormat(message, reglamentDoc, "Processing the invoice amendment request (or rejection).");
        if (exchangeDocumentInfo == null &&
            this.CanProcessMessageLater(message, queueItem, ExchangeCore.PublicFunctions.BoxBase.GetRootBox(box), reglamentDoc.RootServiceEntityId))
        {
          this.LogDebugFormat(message, reglamentDoc, "Document info not found for received invoice amendment request (or rejection): RootServiceEntityId: '{0}'.", reglamentDoc.RootServiceEntityId);
          return false;
        }
        
        // Уведомление об уточнении.
        if (reglamentDoc.DocumentType == ReglamentDocumentType.InvoiceAmendmentRequest &&
            exchangeDocumentInfo != null && !exchangeDocumentInfo.ServiceDocuments.Any(d => d.DocumentId == reglamentDoc.ServiceEntityId))
        {
          this.LogDebugFormat(exchangeDocumentInfo, "Saving invoice amendment request.");
          this.SaveRejectToDocumentInfo(message, exchangeDocumentInfo, reglamentDoc,
                                        isIncomingMessage, Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IReject);
        }
        
        // Отказ в подписании.
        if ((reglamentDoc.DocumentType == ReglamentDocumentType.AmendmentRequest || reglamentDoc.DocumentType == ReglamentDocumentType.Rejection) &&
            exchangeDocumentInfo != null && !exchangeDocumentInfo.ServiceDocuments.Any(d => d.DocumentId == reglamentDoc.ServiceEntityId))
        {
          this.LogDebugFormat(exchangeDocumentInfo, "Saving rejection.");
          this.SaveRejectToDocumentInfo(message, exchangeDocumentInfo, reglamentDoc,
                                        isIncomingMessage, Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Reject);
        }
        
        // Пропускаем документы, которые были отправлены нами через личный кабинет сервиса обмена.
        // Или по которым был отправлен автоматический отказ.
        if (doc == null || exchangeDocumentInfo.OutgoingStatus == Exchange.ExchangeDocumentInfo.OutgoingStatus.Rejected)
        {
          this.LogDebugFormat(exchangeDocumentInfo, "Document not found or received rejection for document.");
          // Инфошка без документа - признак того, что ждали подписи. Больше она не нужна.
          if (exchangeDocumentInfo != null && doc == null)
          {
            ExchangeDocumentInfos.Delete(exchangeDocumentInfo);
            this.LogDebugFormat(exchangeDocumentInfo, "Document info deleted.");
          }
          
          continue;
        }
        
        var signature = message.Signatures.Where(x => x.DocumentId.Equals(reglamentDoc.ServiceEntityId, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
        if (reglamentDoc.DocumentType == ReglamentDocumentType.InvoiceAmendmentRequest)
        {
          this.ProcessSharedInvoiceReject(exchangeDocumentInfo,
                                          doc,
                                          isIncomingMessage,
                                          exchangeDocumentInfo.Box,
                                          signature.Content,
                                          historyOperation,
                                          historyComment,
                                          primaryDocument.Comment,
                                          primaryDocument.Comment,
                                          true);
        }
        else
        {
          this.ProcessSharedReject(exchangeDocumentInfo,
                                   doc,
                                   isIncomingMessage,
                                   exchangeDocumentInfo.Box,
                                   signature.Content,
                                   historyOperation,
                                   historyComment,
                                   primaryDocument.Comment,
                                   primaryDocument.Comment,
                                   true);
        }
      }

      return true;
    }
    
    /// <summary>
    /// Получить отказы в подписании документов из сообщения.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <returns>Отказы в подписании документов.</returns>
    public virtual List<NpoComputer.DCX.Common.IReglamentDocument> GetRejectReglamentDocuments(NpoComputer.DCX.Common.IMessage message)
    {
      return message.ReglamentDocuments.Where(x => x.DocumentType == ReglamentDocumentType.AmendmentRequest ||
                                              x.DocumentType == ReglamentDocumentType.InvoiceAmendmentRequest ||
                                              x.DocumentType == ReglamentDocumentType.Rejection)
        .ToList();
    }

    /// <summary>
    /// Общий для агентов и UI код обработки "уведомления об уточнении" при подписании.
    /// </summary>
    /// <param name="info">Информация о документе в сервисе обмена.</param>
    /// <param name="document">Документ.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Ящик.</param>
    /// <param name="signature">Подпись для уведомлений ответственного.</param>
    /// <param name="historyOperation">Операция истории - отправка отказа или пришедший от КА отказ.</param>
    /// <param name="historyComment">Комментарий истории, обычно перечисляются участники операции.</param>
    /// <param name="serviceComment">Комментарий, пришедший из сервиса. Для уведомлений ответственного.</param>
    /// <param name="rejectNotice">Причина отказа.</param>
    /// <param name="isAgent">Признак вызова из фонового процесса. False используется для вызова из UI.</param>
    protected virtual void ProcessSharedInvoiceReject(IExchangeDocumentInfo info, IOfficialDocument document,
                                                      bool isIncomingMessage, IBoxBase box, byte[] signature,
                                                      Enumeration historyOperation, string historyComment, string serviceComment,
                                                      string rejectNotice, bool isAgent)
    {
      this.LogDebugFormat(info, "Execute ProcessSharedInvoiceReject.");
      var isRejected = info.InvoiceState == Exchange.ExchangeDocumentInfo.InvoiceState.Rejected;
      var needReceive = ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(box);
      if (!isIncomingMessage && !isRejected && isAgent && needReceive && info.DownloadSession == null)
        this.CreateNoticeAfterOurSigning(document, box, false, true, serviceComment);
      
      if (!isRejected)
        info.InvoiceState = Exchange.ExchangeDocumentInfo.InvoiceState.Rejected;
      
      info.Save();
      
      // TODO Zamerov: Карточка не обновляется 46758.
      // Помечаем свойство изменившимся, чтобы отработало перестроение местонахождения при сохранении.
      document.LocationState = document.LocationState;
      
      var sentVersion = document.Versions.FirstOrDefault(x => x.Id == info.VersionId);
      if (isIncomingMessage && isAgent)
      {
        var externalApprovalInTracking = document.Tracking.Where(x => !x.ReturnResult.HasValue && x.ExternalLinkId == info.Id &&
                                                                 (x.Action == Docflow.OfficialDocumentTracking.Action.Sending || x.Action == Docflow.OfficialDocumentTracking.Action.Endorsement));
        var signatoryName = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(signature);
        foreach (var trackingString in externalApprovalInTracking)
          this.SendDocumentReplyNotice(box, trackingString, sentVersion.Number, false, false, signatoryName, true, ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box).Name, serviceComment);
      }
      
      if (!string.IsNullOrEmpty(rejectNotice) && !isRejected)
        document.Note += string.IsNullOrEmpty(document.Note) ? Resources.RejectInvoiceNoticeFormat(rejectNotice) : Environment.NewLine + Resources.RejectInvoiceNoticeFormat(rejectNotice);
      
      var maxLength = document.Info.Properties.Note.Length;
      if (!string.IsNullOrEmpty(document.Note) && document.Note.Length > maxLength)
        document.Note = CutText(document.Note, maxLength);
      
      if (!isRejected)
      {
        var detailedOperation = new Enumeration(Constants.Module.Exchange.DetailedInvoiceReject);
        document.History.Write(historyOperation, detailedOperation, historyComment, sentVersion.Number);
      }
      
      document.Save();
    }

    /// <summary>
    /// Общий для агентов и UI код обработки "отказа" при подписании.
    /// </summary>
    /// <param name="info">Информация о документе в сервисе обмена.</param>
    /// <param name="document">Документ.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Ящик.</param>
    /// <param name="signature">Подпись для уведомлений ответственного.</param>
    /// <param name="historyOperation">Операция истории - отправка отказа или пришедший от КА отказ.</param>
    /// <param name="historyComment">Комментарий истории, обычно перечисляются участники операции.</param>
    /// <param name="serviceComment">Комментарий, пришедший из сервиса. Для уведомлений ответственного.</param>
    /// <param name="rejectNotice">Причина отказа.</param>
    /// <param name="isAgent">Признак вызова из фонового процесса. False используется для вызова из UI.</param>
    protected virtual void ProcessSharedReject(IExchangeDocumentInfo info, IOfficialDocument document, bool isIncomingMessage,
                                               IBoxBase box, byte[] signature,
                                               Enumeration historyOperation, string historyComment,
                                               string serviceComment, string rejectNotice, bool isAgent)
    {
      this.LogDebugFormat(info, "Execute ProcessSharedReject.");
      // Признак того, а можно ли "отказать" по документу в текущей его версии. Если нет - то обновляем только инфошку, документ не трогаем.
      var lastDocumentInfo = Exchange.PublicFunctions.ExchangeDocumentInfo.Remote.GetLastDocumentInfo(document);
      info = ExchangeDocumentInfos.Get(info.Id);
      var canSendAnswer = lastDocumentInfo != null && lastDocumentInfo.Id == info.Id;
      var isRejected = info.OutgoingStatus == Exchange.ExchangeDocumentInfo.OutgoingStatus.Rejected;
      var needReceive = ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(box);
      if (!isIncomingMessage && !isRejected)
      {
        if (isAgent && needReceive && info.DownloadSession == null)
          this.CreateNoticeAfterOurSigning(document, box, false, false, serviceComment);
        
        if (info != null)
          info.OutgoingStatus = Exchange.ExchangeDocumentInfo.OutgoingStatus.Rejected;
        
        this.AddTrackingAfterSignOrReject(info, document);
      }
      
      // Отказ по счёт-фактуре в СО связан с отправкой уточнения и поэтому не должен делать её устаревшей.
      var isTaxInvoice = FinancialArchive.IncomingTaxInvoices.Is(document) || FinancialArchive.OutgoingTaxInvoices.Is(document);
      if (canSendAnswer && !isTaxInvoice)
        Docflow.PublicFunctions.OfficialDocument.SetObsolete(document, false);
      
      this.SetRejectStates(document, info, isIncomingMessage, canSendAnswer, isTaxInvoice);
      info.Save();
      
      var externalApprovalInTracking = document.Tracking.Where(x => x.Action == Docflow.OfficialDocumentTracking.Action.Endorsement &&
                                                               !x.ReturnResult.HasValue && x.ExternalLinkId == info.Id);
      
      var signatoryName = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(signature);
      var rejectedVersion = info.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Rejected
        ? document.LastVersion
        : document.Versions.FirstOrDefault(x => x.Id == info.VersionId);
      
      foreach (var trackingString in externalApprovalInTracking)
      {
        trackingString.ReturnResult = Docflow.OfficialDocumentTracking.ReturnResult.NotSigned;
        
        // Логика по прекращению согласования (контроль возврата и т.д.), уведомление ответственному.
        if (isAgent)
          this.SendDocumentReplyNotice(box, trackingString, rejectedVersion.Number, false, false, signatoryName,
                                       false, ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box).Name, serviceComment);
      }
      
      // Генерация PDF.
      this.GeneratePublicBody(document, rejectedVersion, isAgent);
      
      if (!isRejected)
      {
        this.FillNoteAfterReject(document, rejectNotice);
        this.WriteHistoryAfterReject(document, rejectedVersion, historyOperation, historyComment);
      }
      
      document.Save();
    }
    
    /// <summary>
    /// Добавить информацию о выдаче документа после отказа или подписании.
    /// </summary>
    /// <param name="info">Информация о документе в сервисе обмена.</param>
    /// <param name="document">Документ.</param>
    /// <returns>Информация о выдаче документа.</returns>
    public virtual Docflow.IOfficialDocumentTracking AddTrackingAfterSignOrReject(IExchangeDocumentInfo info, IOfficialDocument document)
    {
      var isReject = info.ExchangeState == Exchange.ExchangeDocumentInfo.ExchangeState.Rejected;
      var responsible = ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(info.Box,
                                                                                                   info.Counterparty,
                                                                                                   new List<IExchangeDocumentInfo>() { info });
      var exchangeServiceName = ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(info.Box).Name;
      using (Sungero.Core.CultureInfoExtensions.SwitchTo(TenantInfo.Culture))
      {
        var tracking = document.Tracking.AddNew();
        tracking.Action = Docflow.OfficialDocumentTracking.Action.Sending;
        tracking.DeliveredTo = Company.Employees.Current ?? responsible;
        tracking.IsOriginal = true;
        tracking.ReturnDeadline = null;
        tracking.Note = isReject ?
          Resources.SendRejectToCounterpartyFormat(exchangeServiceName) :
          Resources.SendSignToCounterpartyFormat(exchangeServiceName);
        tracking.ExternalLinkId = info.Id;
        return tracking;
      }
    }
    
    /// <summary>
    /// Записать причину отказа в примечания документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="rejectReason">Причина отказа.</param>
    public virtual void FillNoteAfterReject(IOfficialDocument document, string rejectReason)
    {
      if (!string.IsNullOrEmpty(rejectReason))
        document.Note += string.IsNullOrEmpty(document.Note)
          ? Resources.RejectNoticeFormat(rejectReason)
          : Environment.NewLine + Resources.RejectNoticeFormat(rejectReason);
      
      var maxLength = document.Info.Properties.Note.Length;
      if (!string.IsNullOrEmpty(document.Note) && document.Note.Length > maxLength)
        document.Note = CutText(document.Note, maxLength);
    }
    
    /// <summary>
    /// Записать в историю, что в подписании документа было отказано.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="rejectedVersion">Версия документа, в подписании которой был получен отказ.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="businessUnitBox">Абонентский ящик НОР.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    public virtual void WriteHistoryAfterReject(IOfficialDocument document, IElectronicDocumentVersions rejectedVersion,
                                                ICounterparty sender, IBusinessUnitBox businessUnitBox,
                                                bool isIncomingMessage)
    {
      var historyInfo = this.GetHistoryInfoAfterReject(sender, businessUnitBox, isIncomingMessage);
      this.WriteHistoryAfterReject(document, rejectedVersion, historyInfo.Operation, historyInfo.Comment);
    }
    
    /// <summary>
    /// Записать в историю, что в подписании документа было отказано.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="rejectedVersion">Версия документа, в подписании которой был получен отказ.</param>
    /// <param name="historyOperation">Операция.</param>
    /// <param name="historyComment">Комментарий к операции.</param>
    public virtual void WriteHistoryAfterReject(IOfficialDocument document, IElectronicDocumentVersions rejectedVersion,
                                                Enumeration historyOperation, string historyComment)
    {
      var detailedOperation = new Enumeration(Constants.Module.Exchange.DetailedReject);
      document.History.Write(historyOperation, detailedOperation, historyComment, rejectedVersion.Number);
    }
    
    /// <summary>
    /// Получить информацию для заполнения истории документа, в подписании которого было отказано.
    /// </summary>
    /// <param name="sender">Контрагент.</param>
    /// <param name="businessUnitBox">Абонентский ящик НОР.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <returns>Операция и комментарий для записи в историю.</returns>
    public virtual Structures.Module.IHistoryInfo GetHistoryInfoAfterReject(ICounterparty sender, IBusinessUnitBox businessUnitBox, bool isIncomingMessage)
    {
      var historyInfo = Exchange.Structures.Module.HistoryInfo.Create();
      historyInfo.Comment = Functions.Module.GetExchangeDocumentHistoryComment(sender.Name, businessUnitBox.ExchangeService.Name);
      historyInfo.Operation = new Enumeration(isIncomingMessage ? Constants.Module.Exchange.GetAnswer : Constants.Module.Exchange.SendAnswer);
      return historyInfo;
    }

    /// <summary>
    /// Установить статусы документа при отказе.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="info">Информация о документе.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="canSendAnswer">Признак смены статуса отказа по документу.</param>
    /// <param name="isTaxInvoice">True, если документ счет-фактура, иначе - false.</param>
    protected virtual void SetRejectStates(IOfficialDocument document, IExchangeDocumentInfo info,
                                           bool isIncomingMessage, bool canSendAnswer, bool isTaxInvoice)
    {
      if (isIncomingMessage)
      {
        if (canSendAnswer)
        {
          document.ExchangeState = Docflow.OfficialDocument.ExchangeState.Rejected;
          document.ExternalApprovalState = Docflow.OfficialDocument.ExternalApprovalState.Unsigned;
        }

        info.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.Rejected;
      }
      else
      {
        if (info.OutgoingStatus == Exchange.ExchangeDocumentInfo.OutgoingStatus.Rejected)
        {
          if (canSendAnswer)
            document.ExchangeState = Docflow.OfficialDocument.ExchangeState.Rejected;

          info.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.Rejected;
        }
        else
        {
          if (canSendAnswer)
            document.ExchangeState = Docflow.OfficialDocument.ExchangeState.Obsolete;

          info.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.Obsolete;
        }

        // Отказ по счёт-фактуре в СО не должен прерывать внутреннее согласование.
        if (canSendAnswer && !isTaxInvoice)
          document.InternalApprovalState = Docflow.OfficialDocument.InternalApprovalState.Aborted;
      }
    }
    
    /// <summary>
    /// Добавить служебный документ с отказом в подписании.
    /// </summary>
    /// <param name="message">Сообщение с отказом в подписании.</param>
    /// <param name="info">Информация о документе.</param>
    /// <param name="document">Служебный документ.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="serviceDocumentType">Тип служебного документа.</param>
    protected virtual void SaveRejectToDocumentInfo(IMessage message, IExchangeDocumentInfo info, IReglamentDocument document,
                                                    bool isIncomingMessage, Enumeration serviceDocumentType)
    {
      this.LogDebugFormat(info, "Execute SaveRejectToDocumentInfo.");
      var serviceDoc = info.ServiceDocuments.AddNew();
      serviceDoc.DocumentId = document.ServiceEntityId;
      serviceDoc.ParentDocumentId = document.ParentServiceEntityId;
      serviceDoc.CounterpartyId = isIncomingMessage ? message.Sender.Organization.OrganizationId : message.Receiver.Organization.OrganizationId;
      serviceDoc.DocumentType = serviceDocumentType;
      serviceDoc.Date = ToTenantTime(document.DateTime ?? message.TimeStamp);
      serviceDoc.Body = document.Content;
      var sign = message.Signatures.Single(s => s.DocumentId == serviceDoc.DocumentId);
      serviceDoc.Sign = sign.Content;
      serviceDoc.FormalizedPoAUnifiedRegNo = sign.FormalizedPoAUnifiedRegNumber;
      info.Save();
    }
    
    /// <summary>
    /// Сгенерировать PublicBody документа.
    /// </summary>
    /// <param name="documentId">ИД документа.</param>
    [Public, Remote]
    public virtual void GeneratePublicBody(int documentId)
    {
      var document = Docflow.OfficialDocuments.Get(documentId);
      if (document != null)
        this.GeneratePublicBodyAsync(document);
    }
    
    /// <summary>
    /// Сгенерировать PublicBody документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="sentVersion">Версия документа для генерации PublicBody.</param>
    /// <param name="isAgent">True - генерация синхронная из фонового процесса, иначе - постановка в очередь.</param>
    protected virtual void GeneratePublicBody(IOfficialDocument document, IElectronicDocumentVersions sentVersion, bool isAgent)
    {
      var accountingDocument = Docflow.AccountingDocumentBases.As(document);
      if (accountingDocument != null && accountingDocument.IsFormalized == true)
      {
        foreach (var accVersion in accountingDocument.Versions)
        {
          // Генерация PDF синхронная, если вызвана из агента и наоборот.
          if (isAgent)
            Docflow.PublicFunctions.Module.Remote.GeneratePublicBodyForFormalizedDocument(accountingDocument, accVersion.Id,
                                                                                          accountingDocument.ExchangeState);
          else
          {
            Docflow.PublicFunctions.Module.GenerateTempPublicBodyForExchangeDocument(accountingDocument, accVersion.Id);
            Exchange.PublicFunctions.Module.EnqueueXmlToPdfBodyConverter(accountingDocument, accVersion.Id, accountingDocument.ExchangeState);
          }
        }
      }
      else
      {
        // Генерация PDF синхронная, если вызвана из агента и наоборот.
        if (isAgent)
          Docflow.PublicFunctions.Module.Remote.GeneratePublicBodyForNonformalizedDocument(document, sentVersion.Id);
        else
        {
          Docflow.PublicFunctions.Module.GenerateTempPublicBodyForExchangeDocument(document, sentVersion.Id);
          Exchange.PublicFunctions.Module.EnqueueXmlToPdfBodyConverter(document, sentVersion.Id, document.ExchangeState);
        }
      }
    }
    
    /// <summary>
    /// Асинхронно сгенерировать PublicBody последней версии документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    protected virtual void GeneratePublicBodyAsync(IOfficialDocument document)
    {
      var version = document.LastVersion;
      if (Exchange.ExchangeDocumentInfos.GetAll().Any(x => Equals(x.Document, document) && x.VersionId == version.Id) ||
          (AccountingDocumentBases.Is(document) && AccountingDocumentBases.As(document).IsFormalized == true))
      {
        Docflow.PublicFunctions.Module.GenerateTempPublicBodyForExchangeDocument(document, version.Id);
        Exchange.PublicFunctions.Module.EnqueueXmlToPdfBodyConverter(document, version.Id, document.ExchangeState);
      }
    }

    #endregion

    #region Уведомления по отказу и подписанию

    /// <summary>
    /// Создать уведомление, если документ был подписан или отправлен отказ нами из сервиса обмена.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="signed">Признак подписания. False - если в подписании было отказано.</param>
    /// <param name="isInvoiceAmendmentRequest">Отправлено уточнение по СФ или УПД.</param>
    /// <param name="reason">Комментарий.</param>
    private void CreateNoticeAfterOurSigning(IOfficialDocument document, IBoxBase box, bool signed, bool isInvoiceAmendmentRequest, string reason)
    {
      var docGuid = document.GetEntityMetadata().GetOriginal().NameGuid;
      var info = Functions.ExchangeDocumentInfo.GetLastDocumentInfo(document);
      var boxResponsible = ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(box, info.Counterparty, new List<IExchangeDocumentInfo>() { info });
      
      var documentProcessingTask = ExchangeDocumentProcessingTasks.GetAll()
        .Where(t => t.AttachmentDetails.Any(att => att.AttachmentId == document.Id && att.EntityTypeGuid == docGuid))
        .FirstOrDefault();
      var documentProcessingPerformers =
        Sungero.Workflow.Assignments.GetAll()
        .Where(a => Equals(a.Task, documentProcessingTask))
        .Select(a => a.Performer);
      
      var performers = new List<IUser>();
      performers.AddRange(documentProcessingPerformers);
      performers.Add(boxResponsible);
      if (Contracts.ContractualDocuments.Is(document))
        performers.Add(Contracts.ContractualDocuments.As(document).ResponsibleEmployee);
      
      var task = Workflow.SimpleTasks.Null;
      if (documentProcessingTask != null)
        task = Workflow.SimpleTasks.CreateAsSubtask(documentProcessingTask);
      else
        task = Workflow.SimpleTasks.Create();
      
      task.AssignmentType = Workflow.SimpleTask.AssignmentType.Notice;
      task.NeedsReview = false;
      
      // При создании подзадачи в нее копируются все вложения.
      var docs = task.AllAttachments.Where(d => !Equals(d, document)).ToList();
      foreach (var doc in docs)
        task.Attachments.Remove(doc);
      
      this.GrantAccessRightsForUpperBoxResponsibles(document, box);
      if (!task.AllAttachments.Where(d => Equals(d, document)).Any())
        task.Attachments.Add(document);
      
      performers = performers.Where(x => x != null).Distinct().ToList();
      foreach (var performer in performers)
      {
        var step = task.RouteSteps.AddNew();
        step.AssignmentType = task.AssignmentType;
        step.Performer = performer;
        step.Deadline = null;
      }
      
      var link = Hyperlinks.Get(document);
      if (signed)
      {
        task.ThreadSubject = Sungero.Exchange.Resources.DocumentSignedThreadSubject;
        task.Subject = Sungero.Exchange.Resources.TaskSubjectTemplateFormat(task.ThreadSubject, document.Name);
        task.ActiveText = Resources.SignNoticeActiveTextObsoleteFormat(link);
      }
      else
      {
        if (isInvoiceAmendmentRequest)
        {
          task.ThreadSubject = Resources.AmendmentNoticeSubjectObsolete;
          task.Subject = Sungero.Exchange.Resources.TaskSubjectTemplateFormat(task.ThreadSubject, document.Name);
          task.ActiveText = Resources.AmendmentNoticeActiveTextObsoleteFormat(link);
        }
        else
        {
          task.ThreadSubject = Resources.RejectNoticeSubjectObsolete;
          task.Subject = Sungero.Exchange.Resources.TaskSubjectTemplateFormat(task.ThreadSubject, document.Name);
          task.ActiveText = Resources.RejectNoticeActiveTextObsoleteFormat(link);
        }

        if (!string.IsNullOrEmpty(reason))
          task.ActiveText += Environment.NewLine + Resources.DocumentCommentFormat(reason);
      }

      task.Subject = CutText(task.Subject, task.Info.Properties.Subject.Length);
      
      task.Save();
      task.Start();
    }

    /// <summary>
    /// Создать уведомление о получении ответа от контрагента.
    /// </summary>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="trackingLine">Строка выдачи.</param>
    /// <param name="signed">Признак подписания. True - если документ подписан контрагентом, иначе - false.</param>
    /// <param name="obsolete">Признак, что документ был отозван нами в сервисе обмена.</param>
    /// <param name="isInvoiceAmendmentRequest">Признак, что отправлено уточнение по СФ или УПД.</param>
    /// <param name="performers">Список пользователей, кому будет отправлено уведомление.</param>
    /// <param name="activeText">Текст уведомления.</param>
    protected virtual void CreateDocumentReplyNotice(IBoxBase box, IOfficialDocumentTracking trackingLine, bool signed, bool obsolete,
                                                     bool isInvoiceAmendmentRequest, List<IUser> performers, string activeText)
    {
      var task = Workflow.SimpleTasks.Null;
      var document = trackingLine.OfficialDocument;
      var docGuid = document.GetEntityMetadata().GetOriginal().NameGuid;
      var parentTask = ExchangeDocumentProcessingTasks.GetAll()
        .Where(t => t.AttachmentDetails.Any(att => att.AttachmentId == document.Id && att.EntityTypeGuid == docGuid))
        .FirstOrDefault();
      
      if (trackingLine.ReturnTask != null)
      {
        task = Workflow.SimpleTasks.CreateAsSubtask(trackingLine.ReturnTask);
        performers.Add(trackingLine.ReturnTask.Author);
      }
      else if (parentTask != null)
      {
        // Подзадача к заданию на обработку, при отзыве формализованного документа в сервисе обмена нашей НОР.
        task = Workflow.SimpleTasks.CreateAsSubtask(parentTask);
      }
      else
      {
        task = Workflow.SimpleTasks.Create();
      }
      
      task.AssignmentType = Workflow.SimpleTask.AssignmentType.Notice;
      task.NeedsReview = false;
      
      this.GrantAccessRightsForUpperBoxResponsibles(trackingLine.OfficialDocument, box);
      if (!task.AllAttachments.Where(d => Equals(d, trackingLine.OfficialDocument)).Any())
        task.Attachments.Add(trackingLine.OfficialDocument);
      
      performers.Add(trackingLine.DeliveredTo);
      performers = performers.Distinct().ToList();
      foreach (var performer in performers)
      {
        var step = task.RouteSteps.AddNew();
        step.Performer = performer;
        step.AssignmentType = Workflow.SimpleTask.AssignmentType.Notice;
        step.Deadline = null;
      }

      if (obsolete)
        task.ThreadSubject = string.Format(Resources.RevocationNoticeOurSubjectTerminate);
      else if (isInvoiceAmendmentRequest)
        task.ThreadSubject = string.Format(Resources.AmendedDocumentSubject);
      else if (signed)
        task.ThreadSubject = string.Format(Resources.AssignDocumentSubject);
      else
        task.ThreadSubject = string.Format(Resources.RejectDocumentSubject);
      
      task.Subject = string.Format(Sungero.Exchange.Resources.TaskSubjectTemplate, task.ThreadSubject, trackingLine.OfficialDocument.Name);
      task.Subject = CutText(task.Subject, task.Info.Properties.Subject.Length);
      
      task.ActiveText = activeText;
      
      task.Save();
      task.Start();
    }

    /// <summary>
    /// Отправить уведомление ответственному о поступлении ответа от контрагента.
    /// </summary>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="trackingString">Строка выдачи.</param>
    /// <param name="versionNumber">Версия документа.</param>
    /// <param name="versionIsChanged">Признак того, что версия была изменена.</param>
    /// <param name="signed">Признак подписания. True - если документ подписан контрагентом, иначе - false.</param>
    /// <param name="signatoryInfo">Информация о контрагенте.</param>
    /// <param name="isInvoiceAmendmentRequest">Отправлено уточнение по СФ или УПД.</param>
    /// <param name="serviceName">Наименование сервиса обмена.</param>
    /// <param name="comment">Комментарии контрагента.</param>
    protected virtual void SendDocumentReplyNotice(IBoxBase box, IOfficialDocumentTracking trackingString, int? versionNumber,
                                                   bool versionIsChanged, bool signed, string signatoryInfo,
                                                   bool isInvoiceAmendmentRequest, string serviceName, string comment)
    {
      var performers = new List<IUser>();
      var activeText = string.Empty;
      var docHyperlink = Hyperlinks.Get(trackingString.OfficialDocument);
      var info = Functions.ExchangeDocumentInfo.GetLastDocumentInfo(trackingString.OfficialDocument);
      var boxResponsible = ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(box,
                                                                                                      info.Counterparty,
                                                                                                      new List<IExchangeDocumentInfo>() { info });
      
      activeText = FillDocumentReplyNoticeTexts(versionNumber, versionIsChanged, signed, signatoryInfo, isInvoiceAmendmentRequest, activeText, comment, serviceName, docHyperlink);

      performers.Add(boxResponsible);
      
      if (trackingString.ReturnTask != null)
      {
        // Агент автоматически выполняет задание на контроль возврата и процесс идет дальше.
        var returnAssignments = Docflow.ApprovalCheckReturnAssignments
          .GetAll()
          .Where(x => Equals(x.Task, trackingString.ReturnTask) && x.Status == Workflow.Assignment.Status.InProcess)
          .ToList();
        
        if (returnAssignments.Any())
        {
          var isMainDocument = Docflow.ApprovalTasks.As(trackingString.ReturnTask).DocumentGroup.OfficialDocuments.Contains(trackingString.OfficialDocument);
          
          if (isMainDocument)
          {
            // Разделено установка признака AutoReturned и выполнение заданий, т.к. при большом количестве исполнителей схема успевает начать свою рассылку.
            foreach (var assignment in returnAssignments)
            {
              assignment.ActiveText = activeText;
              assignment.AutoReturned = true;
              assignment.Save();
            }
          }
          
          foreach (var assignment in returnAssignments)
          {
            if (isMainDocument)
            {
              var completeResult = signed ? Docflow.ApprovalCheckReturnAssignment.Result.Signed : Docflow.ApprovalCheckReturnAssignment.Result.NotSigned;
              assignment.Complete(completeResult);
            }
            
            performers.Add(assignment.Performer);
          }
          
          // Если не подписано, уведомляем всех кто согласовывал и подписывал документ (логика из задачи).
          if (!signed)
            performers.AddRange(Docflow.PublicFunctions.ApprovalTask.GetAllApproversAndSignatories(Docflow.ApprovalTasks.As(trackingString.ReturnTask)));
          
          performers = performers.Distinct().ToList();
        }
      }
      
      if (ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(box) && info.DownloadSession == null)
        this.CreateDocumentReplyNotice(box, trackingString, signed, false, isInvoiceAmendmentRequest, performers, activeText);
    }

    private static string FillDocumentReplyNoticeTexts(int? versionNumber, bool versionIsChanged, bool signed, string signatoryInfo,
                                                       bool isInvoiceAmendmentRequest, string activeText, string comment,
                                                       string serviceName, string documentHyperlink)
    {
      if (signed)
      {
        activeText = string.Format(Resources.AssignDocumentVersion, documentHyperlink, versionNumber, serviceName) +
          Environment.NewLine +
          string.Format(Resources.AssignDocumentBy, signatoryInfo);

        if (versionIsChanged)
          activeText += Environment.NewLine + Resources.DocumentHasChangedAfterSendToCounterpartyFormat(versionNumber);
      }

      if (isInvoiceAmendmentRequest)
      {
        activeText = string.Format(Resources.AmendedDocumentVersion, documentHyperlink, versionNumber.Value);
        activeText += Environment.NewLine;
        activeText += string.Format(Resources.AmendedDocumentBy, signatoryInfo);

        if (!string.IsNullOrEmpty(comment))
        {
          activeText += Environment.NewLine;
          activeText += string.Format(Resources.RejectDocumentComment, comment);
        }
      }

      if (!signed && !isInvoiceAmendmentRequest)
      {
        activeText = string.Format(Resources.RejectDocumentVersion, documentHyperlink, versionNumber.Value);
        activeText += Environment.NewLine;
        activeText += string.Format(Resources.RejectDocumentBy, signatoryInfo);

        if (!string.IsNullOrEmpty(comment))
        {
          activeText += Environment.NewLine;
          activeText += string.Format(Resources.RejectDocumentComment, comment);
        }
      }

      return activeText;
    }
    
    #endregion
    
    #region Обработка аннулирования и отзыва
    
    /// <summary>
    /// Обработать сообщение об аннулировании или отзыве документа.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItems">Все элементы очереди.
    /// Параметр не используется в базовой функции, оставлен для совместимости.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    public virtual bool ProcessCancellationAgreement(NpoComputer.DCX.Common.IMessage message,
                                                     List<IMessageQueueItem> queueItems,
                                                     ICounterparty sender,
                                                     bool isIncomingMessage,
                                                     IBoxBase box)
    {
      this.LogDebugFormat(message, box, "Execute ProcessCancellationAgreement.");
      var result = false;
      var exchangeService = ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box);
      
      foreach (var dcxCancellationAgreement in this.GetRevocationOfferPrimaryDocuments(message))
      {
        var parentInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, dcxCancellationAgreement.ParentServiceEntityId);
        // Если нет инфо и есть основной документ в очереди - повторять обработку СоА.
        var parentDocumentMessageInProcess = this.IsParentDocumentMessageInProcess(message, parentInfo, dcxCancellationAgreement, isIncomingMessage, box);
        if (parentDocumentMessageInProcess)
        {
          this.LogDebugFormat(message, box, "Retry processing for cancellation agreement, parent document message in process, ParentServiceEntityId '{0}'.",
                              dcxCancellationAgreement.ParentServiceEntityId);
          result = false;
          continue;
        }
        
        // Считается уже обработанным, если:
        // -- нет инфо и нет документа в очереди;
        // -- для исходящего основного документа есть инфо, но нет документа (исходящий неформализованный документ с требованием подписи);
        // -- если в инфо стоит статус аннулирования - аннулирован.
        var isNonformalizedOutgoingDocumentCancellation = parentInfo != null && parentInfo.Document == null &&
          parentInfo.MessageType == Exchange.ExchangeDocumentInfo.MessageType.Outgoing;
        var skipProcessing = parentInfo == null || isNonformalizedOutgoingDocumentCancellation ||
          parentInfo.RevocationStatus == Exchange.ExchangeDocumentInfo.RevocationStatus.Revoked;
        if (skipProcessing)
        {
          this.LogDebugFormat(message, box, "Skip processing for cancellation agreement, ParentServiceEntityId '{0}'.",
                              dcxCancellationAgreement.ParentServiceEntityId);
          result = true;
          continue;
        }
        
        var isTwoSidedCancellationAgreement = dcxCancellationAgreement.SignStatus != NpoComputer.DCX.Common.SignStatus.None;
        if (isTwoSidedCancellationAgreement)
        {
          this.ProcessTwoSidedCancellationAgreement(message, parentInfo, dcxCancellationAgreement, sender, isIncomingMessage, box);
        }
        else
        {
          this.ProcessOneSidedCancellationAgreement(message, parentInfo, dcxCancellationAgreement, sender, isIncomingMessage, box);
        }
        
        this.GeneratePublicBodies(parentInfo, box, dcxCancellationAgreement);
        
        result = true;
      }
      
      this.LogDebugFormat(message, box, "Done ProcessCancellationAgreement.");
      return result;
    }
    
    /// <summary>
    /// Получить запросы на аннулирование документов из сообщения.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <returns>Запросы на аннулирование.</returns>
    public virtual List<NpoComputer.DCX.Common.IDocument> GetRevocationOfferPrimaryDocuments(NpoComputer.DCX.Common.IMessage message)
    {
      return message.PrimaryDocuments.Where(x => x.DocumentType == DocumentType.RevocationOffer).ToList();
    }
    
    /// <summary>
    /// Проверить обработанность основного документа.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Сведенья об основном документе обмена.</param>
    /// <param name="dcxCancellationAgreement">Аннулирование из сервиса обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <returns>True если считаем, что документ ещё в очереди и не обрабатывался.</returns>
    public virtual bool IsParentDocumentMessageInProcess(NpoComputer.DCX.Common.IMessage message,
                                                         IExchangeDocumentInfo parentInfo,
                                                         NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                         bool isIncomingMessage, IBoxBase box)
    {
      var inProcess = false;
      if (parentInfo == null)
      {
        var rootBox = ExchangeCore.PublicFunctions.BoxBase.GetRootBox(box);
        var fetchQueueItems = ExchangeCore.MessageQueueItems
          .GetAll()
          .Where(q => Equals(q.RootBox, rootBox) &&
                 q.ProcessingStatus != ExchangeCore.MessageQueueItem.ProcessingStatus.Processed &&
                 q.Documents.Any(d => d.ExternalId == dcxCancellationAgreement.ParentServiceEntityId &&
                                 d.Type == ExchangeCore.MessageQueueItemDocuments.Type.Primary))
          .Any();
        
        if (fetchQueueItems)
        {
          var logParentInfoNotFound = string.Format("Document message is not in process and parent info not found for " +
                                                    "received annulment or cancellation, RootServiceEntityId '{0}'.",
                                                    dcxCancellationAgreement.ParentServiceEntityId);
          this.LogDebugFormat(message, box, logParentInfoNotFound);
          inProcess = true;
        }
      }
      return inProcess;
    }
    
    /// <summary>
    /// Обработать двустороннее аннулирование.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    public virtual void ProcessTwoSidedCancellationAgreement(NpoComputer.DCX.Common.IMessage message,
                                                             IExchangeDocumentInfo parentInfo,
                                                             NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                             ICounterparty sender,
                                                             bool isIncomingMessage,
                                                             IBoxBase box)
    {
      this.LogDebugFormat(parentInfo, "Execute ProcessTwoSidedCancellationAgreement, ParentServiceEntityId '{0}'.",
                          dcxCancellationAgreement.ParentServiceEntityId);
      
      var isAnnulmentAlreadyProcessed = parentInfo.ServiceDocuments.Any(x => x.DocumentId == dcxCancellationAgreement.ServiceEntityId &&
                                                                        x.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Annulment);
      
      var cancellationAgreementInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, dcxCancellationAgreement.ServiceEntityId);
      
      // Если по соглашению об аннулировании был отказ, не загружаем, не обрабатываем, только заносим служебные документы в информацию об основном документе,
      // актуализируем статус аннулирования основного документа, если требуется
      // (историческая загрузка, обработка ситуации когда запрос был отправлен до обновления, а отказ пришёл после).
      var dcxCancellationAgreementIsSigned = dcxCancellationAgreement.SignStatus == NpoComputer.DCX.Common.SignStatus.Signed;
      var dcxCancellationAgreementIsRejected = dcxCancellationAgreement.SignStatus == NpoComputer.DCX.Common.SignStatus.Rejected;
      if (dcxCancellationAgreementIsRejected && cancellationAgreementInfo == null)
      {
        this.LogDebugFormat(parentInfo, "Execute ProcessTwoSidedCancellationAgreement. Skip cancellation agreement, ParentServiceEntityId '{0}'.",
                            dcxCancellationAgreement.ParentServiceEntityId);
        
        if (!isAnnulmentAlreadyProcessed)
        {
          this.AddCancellationAgreementToServiceDocuments(message, parentInfo, dcxCancellationAgreement, isIncomingMessage,
                                                          Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Annulment);
        }
        
        this.SaveRejectToParentDocumentInfo(message, parentInfo, dcxCancellationAgreement, isIncomingMessage);
        
        if (parentInfo.RevocationStatus == Exchange.ExchangeDocumentInfo.RevocationStatus.Waiting)
          this.SetParentDocumentInfoStatesRejected(parentInfo);
        
        return;
      }
      
      if (!isAnnulmentAlreadyProcessed)
        this.ProcessCancellationAgreementRequest(message, parentInfo, dcxCancellationAgreement, sender, isIncomingMessage, box);
      
      // До версии 4.6.100 аннулирование заносилось без документа и без инфо на него,
      // в служебных документах основного документа была запись об аннулировании.
      // Обработка ситуации когда запрос был отправлен до обновления, а ответ пришел после.
      if (isAnnulmentAlreadyProcessed && cancellationAgreementInfo == null && dcxCancellationAgreementIsSigned)
      {
        this.ProcessOldVersionCancellationAgreementReply(parentInfo, dcxCancellationAgreement, message, sender, isIncomingMessage, box);
      }
      else
      {
        // Обработать ответ, только если сообщение - это ответ и в сервисе у соглашения об аннулировании конечный статус: "Подписано" или "Отказано".
        var isCancellationAgreementReply = this.IsCancellationAgreementReply(message, dcxCancellationAgreement, isIncomingMessage, box);
        if (isCancellationAgreementReply && (dcxCancellationAgreementIsSigned || dcxCancellationAgreementIsRejected))
          this.ProcessCancellationAgreementReply(parentInfo, dcxCancellationAgreement, message, sender, isIncomingMessage, box);
      }
    }
    
    /// <summary>
    /// Занести служебный документ с отказом в подписании соглашения об аннулировании в информацию об основном документе.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="dcxCancellationAgreement">Аннулирование из сервиса обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <remarks>Если в сообщении нету служебного документа с отказом, то в информацию об основном документе ничего не занесется.</remarks>
    public virtual void SaveRejectToParentDocumentInfo(NpoComputer.DCX.Common.IMessage message,
                                                       IExchangeDocumentInfo parentInfo,
                                                       NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                       bool isIncomingMessage)
    {
      var rejectDocument = this.GetRejectReglamentDocuments(message)
        .SingleOrDefault(d => d.ParentServiceEntityId == dcxCancellationAgreement.ServiceEntityId);
      
      if (rejectDocument != null)
        this.SaveRejectToDocumentInfo(message, parentInfo, rejectDocument, isIncomingMessage, Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Reject);
    }
    
    /// <summary>
    /// Проверить, что сообщение является ответом на соглашение об аннулировании.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="dcxCancellationAgreement">Аннулирование из сервиса обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <returns>True, если сообщение - это ответ на соглашение об аннулировании, иначе - false. Ответом считается сообщение:
    /// - входящее сообщение (определяется по отправителю) на исходящее соглашение об аннулировании (определяется по типу сообщения);
    /// - исходящее сообщение на входящее соглашение об аннулировании.</returns>
    public virtual bool IsCancellationAgreementReply(NpoComputer.DCX.Common.IMessage message,
                                                     NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                     bool isIncomingMessage, IBoxBase box)
    {
      var cancellationAgreementInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, dcxCancellationAgreement.ServiceEntityId);
      var isIncomingCancellationAgreement = cancellationAgreementInfo.MessageType == Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      return isIncomingCancellationAgreement != isIncomingMessage;
    }
    
    /// <summary>
    /// Обработать созданное соглашение об аннулировании.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    public virtual void ProcessCancellationAgreementRequest(NpoComputer.DCX.Common.IMessage message,
                                                            IExchangeDocumentInfo parentInfo,
                                                            NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                            ICounterparty sender,
                                                            bool isIncomingMessage, IBoxBase box)
    {
      this.LogDebugFormat(parentInfo, "Execute ProcessCancellationAgreementRequest, ParentServiceEntityId '{0}'.",
                          dcxCancellationAgreement.ParentServiceEntityId);
      
      // При параллельных действиях: отправке из сервиса обмена, и при получении от контрагента - сведений о документе обмена нет.
      var cancellationAgreementInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, dcxCancellationAgreement.ServiceEntityId);
      if (cancellationAgreementInfo == null)
      {
        this.LogDebugFormat(parentInfo, "Execute ProcessCancellationAgreementRequest. CreateCancellationAgreement, ParentServiceEntityId '{0}'.",
                            dcxCancellationAgreement.ParentServiceEntityId);
        this.AddCancellationAgreementToServiceDocuments(message, parentInfo, dcxCancellationAgreement, isIncomingMessage,
                                                        Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Annulment);
        cancellationAgreementInfo = this.CreateCancellationAgreementInfoWithoutDocument(parentInfo, dcxCancellationAgreement,
                                                                                        isIncomingMessage, message.TimeStamp, box);
        var cancellationAgreement = this.ImportCancellationAgreement(dcxCancellationAgreement.Content, parentInfo.Document.Id,
                                                                     dcxCancellationAgreement.Comment);
        
        cancellationAgreementInfo.VersionId = cancellationAgreement.LastVersion.Id;
        cancellationAgreementInfo.Document = cancellationAgreement;
        cancellationAgreementInfo.Save();
        
        this.LogDebugFormat(cancellationAgreementInfo, "CancellationAgreementInfo Created, ParentServiceEntityId '{0}'.",
                            dcxCancellationAgreement.ParentServiceEntityId);
        
        // Импорт подписи и указание ИД подписи в инфо соглашения об аннулировании.
        this.SignDocumentFromNewIncomingMessage(message, cancellationAgreement, dcxCancellationAgreement, box);
        this.SetParentExchangeDocumentInfoStateWaiting(parentInfo);
        this.SetCancellationAgreementExchangeDocumentInfoStateWaiting(cancellationAgreementInfo, isIncomingMessage);
        this.SetCancellationAgreementStateWaiting(cancellationAgreement, isIncomingMessage);
        this.AddCancellationAgreementTracking(cancellationAgreementInfo, cancellationAgreement, dcxCancellationAgreement.SignStatus);
        this.SendCancellationAgreementProcessingTaskOrNotice(message, parentInfo, cancellationAgreement, dcxCancellationAgreement, isIncomingMessage);
      }
      else
      {
        this.LogDebugFormat(parentInfo, "Execute ProcessCancellationAgreementRequest. CancellationAgreementInfo Already Exist, ParentServiceEntityId '{0}'.",
                            dcxCancellationAgreement.ParentServiceEntityId);
        this.AddCancellationAgreementToServiceDocuments(message, parentInfo, dcxCancellationAgreement, isIncomingMessage,
                                                        Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Annulment);
        
        this.SetParentExchangeDocumentInfoStateWaiting(parentInfo);
      }
    }
    
    /// <summary>
    /// Обработать ответ на соглашение об аннулировании.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="message">Сообщение.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    public virtual void ProcessCancellationAgreementReply(IExchangeDocumentInfo parentInfo,
                                                          NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                          NpoComputer.DCX.Common.IMessage message,
                                                          ICounterparty sender,
                                                          bool isIncomingMessage, IBoxBase box)
    {
      this.LogDebugFormat(parentInfo, "Execute ProcessCancellationAgreementReply, ParentServiceEntityId '{0}'.",
                          dcxCancellationAgreement.ParentServiceEntityId);
      
      if (dcxCancellationAgreement.SignStatus == NpoComputer.DCX.Common.SignStatus.Signed)
      {
        this.ProcessApprovedCancellationAgreementReply(message, parentInfo, dcxCancellationAgreement, sender, isIncomingMessage, box);
      }
      else if (dcxCancellationAgreement.SignStatus == NpoComputer.DCX.Common.SignStatus.Rejected)
      {
        this.ProcessRejectedCancellationAgreementReply(message, parentInfo, dcxCancellationAgreement, sender, isIncomingMessage, box);
      }
    }
    
    /// <summary>
    /// Обработать ответ на соглашение об аннулировании отправленное до версии 4.6.100.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="message">Сообщение.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    public virtual void ProcessOldVersionCancellationAgreementReply(IExchangeDocumentInfo parentInfo,
                                                                    NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                                    NpoComputer.DCX.Common.IMessage message,
                                                                    ICounterparty sender,
                                                                    bool isIncomingMessage,
                                                                    IBoxBase box)
    {
      this.LogDebugFormat(parentInfo, "Execute ProcessOldVersionCancellationAgreementReply, ParentServiceEntityId '{0}'.",
                          dcxCancellationAgreement.ParentServiceEntityId);
      
      // Для ситуации с обновлением направление соглашения об аннулирование будет обратно направлению сообщения.
      var cancellationAgreementMessageType = isIncomingMessage
        ? Sungero.Exchange.ExchangeDocumentInfo.MessageType.Outgoing
        : Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      var cancellationAgreementInfo = ExchangeDocumentInfos.Null;
      var cancellationAgreement = CancellationAgreements.Null;
      
      this.ImportAnnulmentReplySign(message, parentInfo, cancellationAgreement, dcxCancellationAgreement, box);
      this.SetParentExchangeDocumentInfoStateAfterAnnulment(parentInfo, Exchange.ExchangeDocumentInfo.ExchangeState.Terminated);
      this.SetParentDocumentExchangeStateAfterTwoSidedAnnulment(parentInfo, Exchange.ExchangeDocumentInfo.ExchangeState.Terminated);
      this.SetParentDocumentInfoDeliveryConfirmationStatusAfterAnnulment(parentInfo);
      this.UpdateParentDocumentTracking(parentInfo, cancellationAgreementMessageType);
      var parentDocumentReturnAssignments = this.CompleteParentDocumentReturnTask(parentInfo, cancellationAgreementInfo);
      this.WriteHistoryAfterAnnulment(parentInfo, sender, cancellationAgreementMessageType, box);
      this.SendApprovedCancellationAgreementNotice(message,
                                                   parentInfo,
                                                   cancellationAgreement,
                                                   dcxCancellationAgreement,
                                                   cancellationAgreementInfo,
                                                   cancellationAgreementMessageType,
                                                   box,
                                                   parentDocumentReturnAssignments);
    }
    
    /// <summary>
    /// Обработать подписание аннулирования.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    public virtual void ProcessApprovedCancellationAgreementReply(NpoComputer.DCX.Common.IMessage message,
                                                                  IExchangeDocumentInfo parentInfo,
                                                                  NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                                  ICounterparty sender,
                                                                  bool isIncomingMessage,
                                                                  IBoxBase box)
    {
      this.LogDebugFormat(parentInfo, "Execute ProcessApprovedCancellationAgreementReply, ParentServiceEntityId '{0}'.",
                          dcxCancellationAgreement.ParentServiceEntityId);
      
      var cancellationAgreementInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, dcxCancellationAgreement.ServiceEntityId);
      var cancellationAgreement = CancellationAgreements.As(cancellationAgreementInfo.Document);
      
      this.ImportAnnulmentReplySign(message, parentInfo, cancellationAgreement, dcxCancellationAgreement, box);
      this.SetParentExchangeDocumentInfoStateAfterAnnulment(parentInfo, Exchange.ExchangeDocumentInfo.ExchangeState.Terminated);
      this.SetParentDocumentExchangeStateAfterTwoSidedAnnulment(parentInfo, Exchange.ExchangeDocumentInfo.ExchangeState.Terminated);
      this.SetTwoSidedCancellationAgreementExchangeDocumentInfoStateSigned(cancellationAgreementInfo);
      this.SetTwoSidedCancellationAgreementStateSigned(cancellationAgreement, isIncomingMessage);
      this.SetParentDocumentInfoDeliveryConfirmationStatusAfterAnnulment(parentInfo);
      this.UpdateParentDocumentTracking(parentInfo, cancellationAgreementInfo.MessageType);
      this.UpdateCancellationAgreementTracking(cancellationAgreementInfo);
      var parentDocumentReturnAssignments = this.CompleteParentDocumentReturnTask(parentInfo, cancellationAgreementInfo);
      var cancellationAgreementReturnAssignments = this.CompleteCancellationAgreementReturnTask(cancellationAgreementInfo, Docflow.ApprovalCheckReturnAssignment.Result.Signed);
      this.WriteHistoryAfterAnnulment(parentInfo, sender, cancellationAgreementInfo.MessageType, box);
      this.SendApprovedCancellationAgreementNotice(message, parentInfo, cancellationAgreement, dcxCancellationAgreement, cancellationAgreementInfo, cancellationAgreementInfo.MessageType, box, parentDocumentReturnAssignments);
      this.SendApprovedCancellationAgreementReturnTaskNotice(message, cancellationAgreementInfo, dcxCancellationAgreement, box, cancellationAgreementReturnAssignments);
    }
    
    /// <summary>
    /// Обработать отказ в подписании аннулирования.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    public virtual void ProcessRejectedCancellationAgreementReply(NpoComputer.DCX.Common.IMessage message,
                                                                  IExchangeDocumentInfo parentInfo,
                                                                  NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                                  ICounterparty sender,
                                                                  bool isIncomingMessage, IBoxBase box)
    {
      this.LogDebugFormat(parentInfo, "Execute ProcessRejectedCancellationAgreementReply, ParentServiceEntityId '{0}'.",
                          dcxCancellationAgreement.ParentServiceEntityId);
      
      var cancellationAgreementInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, dcxCancellationAgreement.ServiceEntityId);
      var cancellationAgreement = CancellationAgreements.As(cancellationAgreementInfo.Document);
      var businessUnitBox = cancellationAgreementInfo.RootBox;
      
      var rejectDocument = this.GetRejectReglamentDocuments(message)
        .SingleOrDefault(d => d.ParentServiceEntityId == dcxCancellationAgreement.ServiceEntityId);
      
      this.SetParentDocumentInfoStatesRejected(parentInfo);
      this.SetCancellationAgreementStateRejected(message, cancellationAgreement, dcxCancellationAgreement, cancellationAgreementInfo, isIncomingMessage);
      if (rejectDocument != null)
      {
        this.SaveRejectToDocumentInfo(message, parentInfo, rejectDocument, isIncomingMessage, Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Reject);
        this.SaveRejectToDocumentInfo(message, cancellationAgreementInfo, rejectDocument, isIncomingMessage, Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Reject);
      }
      this.UpdateCancellationAgreementTracking(cancellationAgreementInfo);
      var cancellationAgreementReturnAssignments = this.CompleteCancellationAgreementReturnTask(cancellationAgreementInfo, Docflow.ApprovalCheckReturnAssignment.Result.NotSigned);
      var isRejectedByUs = cancellationAgreementInfo.OutgoingStatus == Exchange.ExchangeDocumentInfo.OutgoingStatus.Rejected;
      if (!isRejectedByUs)
        this.FillNoteAfterReject(cancellationAgreement, rejectDocument?.Comment);
      if (cancellationAgreementInfo.OutgoingStatus != Exchange.ExchangeDocumentInfo.OutgoingStatus.Rejected)
        this.WriteHistoryAfterReject(cancellationAgreement, cancellationAgreement.LastVersion, sender, businessUnitBox, isIncomingMessage);
      this.SendRejectedCancellationAgreementNotice(message, parentInfo, cancellationAgreement, cancellationAgreementInfo, rejectDocument);
      this.SendRejectedCancellationAgreementReturnTaskNotice(message, cancellationAgreementInfo, rejectDocument, box, cancellationAgreementReturnAssignments);
    }
    
    /// <summary>
    /// Добавить аннулирование/отзыв в сведения о документе обмена основного документа.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="documentType">Тип соглашения об аннулировании: аннулирование или отзыв.</param>
    public virtual void AddCancellationAgreementToServiceDocuments(NpoComputer.DCX.Common.IMessage message,
                                                                   IExchangeDocumentInfo parentInfo,
                                                                   NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                                   bool isIncomingMessage,
                                                                   Enumeration documentType)
    {
      if (parentInfo.ServiceDocuments.Any(d => d.DocumentId == dcxCancellationAgreement.ServiceEntityId))
        return;

      var serviceDoc = parentInfo.ServiceDocuments.AddNew();
      serviceDoc.DocumentId = dcxCancellationAgreement.ServiceEntityId;
      serviceDoc.ParentDocumentId = dcxCancellationAgreement.ParentServiceEntityId;
      serviceDoc.CounterpartyId = isIncomingMessage ? message.Sender.Organization.OrganizationId : message.Receiver.Organization.OrganizationId;
      serviceDoc.DocumentType = documentType;
      serviceDoc.Date = ToTenantTime(dcxCancellationAgreement.Date == DateTime.MinValue ? message.TimeStamp : dcxCancellationAgreement.Date);
      serviceDoc.Body = dcxCancellationAgreement.Content;
      
      var signature = message.Signatures.Single(s => s.DocumentId == serviceDoc.DocumentId);
      serviceDoc.Sign = signature.Content;
      serviceDoc.FormalizedPoAUnifiedRegNo = signature.FormalizedPoAUnifiedRegNumber;
      parentInfo.Save();
    }
    
    /// <summary>
    /// Создать сведения о документе обмена для соглашения об аннулировании.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="messageDate">Дата отправки.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <returns>Сведения о документе обмена для соглашения об аннулировании.</returns>
    public virtual IExchangeDocumentInfo CreateCancellationAgreementInfoWithoutDocument(IExchangeDocumentInfo parentInfo,
                                                                                        NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                                                        bool isIncomingMessage, DateTime messageDate, IBoxBase box)
    {
      var serviceCounterpartyDepartmentId = parentInfo.CounterpartyDepartmentBox != null ? parentInfo.CounterpartyDepartmentBox.DepartmentId : null;
      var cancellationAgreementInfo = GetOrCreateExchangeInfoWithoutDocument(dcxCancellationAgreement, parentInfo.Counterparty, parentInfo.ServiceCounterpartyId,
                                                                             isIncomingMessage, messageDate, box, serviceCounterpartyDepartmentId);
      cancellationAgreementInfo.ParentDocumentInfo = parentInfo;
      return cancellationAgreementInfo;
    }
    
    /// <summary>
    /// Добавить запись выдачи в соглашение об аннулировании.
    /// </summary>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="signStatus">Статус подписания соглашения об аннулировании.</param>
    public virtual void AddCancellationAgreementTracking(IExchangeDocumentInfo cancellationAgreementInfo,
                                                         ICancellationAgreement cancellationAgreement,
                                                         NpoComputer.DCX.Common.SignStatus? signStatus)
    {
      var initiatedFromCounterparty = cancellationAgreementInfo.MessageType == Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      if (!initiatedFromCounterparty)
      {
        using (Sungero.Core.CultureInfoExtensions.SwitchTo(TenantInfo.Culture))
          AddTrackingRecordInfo(cancellationAgreementInfo, cancellationAgreement, signStatus != SignStatus.None);
        cancellationAgreement.Save();
      }
    }
    
    /// <summary>
    /// Установить статусы в сведениях об обмене основного документа после отказа в аннулировании.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    public virtual void SetParentDocumentInfoStatesRejected(IExchangeDocumentInfo parentInfo)
    {
      parentInfo.RevocationStatus = Exchange.ExchangeDocumentInfo.RevocationStatus.None;
      parentInfo.Save();
    }
    
    /// <summary>
    /// Установить статусы в сведениях об обмене основного документа после подписания аннулирования.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="exchangeState">Статус электронного обмена.</param>
    public virtual void SetParentExchangeDocumentInfoStateAfterAnnulment(IExchangeDocumentInfo parentInfo, Enumeration exchangeState)
    {
      parentInfo.RevocationStatus = Exchange.ExchangeDocumentInfo.RevocationStatus.Revoked;
      parentInfo.ExchangeState = exchangeState;
      parentInfo.Save();
    }
    
    /// <summary>
    /// Установить статусы основного документа после подписания двухстороннего аннулирования.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="exchangeState">Статус электронного обмена.</param>
    public virtual void SetParentDocumentExchangeStateAfterTwoSidedAnnulment(IExchangeDocumentInfo parentInfo, Enumeration exchangeState)
    {
      var parentDocument = parentInfo.Document;
      
      // Если создали уже новую версию и отправили её в сервис обмена, то документ нельзя делать устаревшим.
      var canMakeObsolete = Equals(parentInfo, Exchange.PublicFunctions.ExchangeDocumentInfo.Remote.GetLastDocumentInfo(parentDocument));
      if (canMakeObsolete)
      {
        Docflow.PublicFunctions.OfficialDocument.SetObsolete(parentDocument, true);
        parentDocument.ExchangeState = exchangeState;
        parentDocument.ExternalApprovalState = null;
        parentDocument.Save();
      }
    }
    
    /// <summary>
    /// Установить статусы основного документа после одностороннего аннулирования.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="exchangeState">Статус электронного обмена.</param>
    public virtual void SetParentDocumentExchangeStateAfterOneSidedAnnulment(IExchangeDocumentInfo parentInfo, Enumeration exchangeState)
    {
      var parentDocument = parentInfo.Document;
      
      // Если создали уже новую версию и отправили её в сервис обмена, то документ нельзя делать устаревшим.
      var canMakeObsolete = Equals(parentInfo, Exchange.PublicFunctions.ExchangeDocumentInfo.Remote.GetLastDocumentInfo(parentDocument));
      if (canMakeObsolete)
      {
        Docflow.PublicFunctions.OfficialDocument.SetObsolete(parentDocument, false);
        parentDocument.ExchangeState = exchangeState;
        parentDocument.ExternalApprovalState = null;
        parentDocument.Save();
      }
    }
    
    /// <summary>
    /// Установить статус ожидания аннулирования в сведениях об обмене основного документа.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    public virtual void SetParentExchangeDocumentInfoStateWaiting(IExchangeDocumentInfo parentInfo)
    {
      parentInfo.RevocationStatus = Exchange.ExchangeDocumentInfo.RevocationStatus.Waiting;
      parentInfo.Save();
    }
    
    /// <summary>
    /// Записать в историю, что документ аннулирован.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="cancellationAgreementMessageType">Тип (направление) соглашения об аннулировании.</param>
    /// <param name="box">Абонентский ящик.</param>
    public virtual void WriteHistoryAfterAnnulment(IExchangeDocumentInfo parentInfo,
                                                   ICounterparty sender,
                                                   Enumeration? cancellationAgreementMessageType,
                                                   IBoxBase box)
    {
      var parentDocument = parentInfo.Document;
      var initiatedFromCounterparty = cancellationAgreementMessageType == Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      
      var version = parentDocument.Versions.SingleOrDefault(v => v.Id == parentInfo.VersionId);
      var versionNumber = version == null ? null : version.Number;
      var senderName = sender.Name;
      if (parentInfo.CounterpartyDepartmentBox != null)
        senderName = Exchange.PublicFunctions.ExchangeDocumentInfo.GetCompanyNameWithDepartment(parentInfo, senderName);
      var exchangeService = ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box);
      var comment = initiatedFromCounterparty ?
        Functions.Module.GetExchangeDocumentHistoryComment(senderName, exchangeService.Name) :
        exchangeService.Name;
      
      var operation = new Enumeration(initiatedFromCounterparty ?
                                      Constants.Module.Exchange.ObsoletedByCounterparty :
                                      Constants.Module.Exchange.ObsoleteOur);
      parentDocument.History.Write(operation, operation, comment, versionNumber);
      parentDocument.Save();
    }
    
    /// <summary>
    /// Отправить уведомление об аннулировании документа нашей организацией.
    /// </summary>
    /// <param name="info">Информация о документе обмена.</param>
    /// <param name="reason">Причина аннулирования/отзыва.</param>
    [Obsolete("Используйте метод SendCancellationAgreementProcessingTaskOrNotice.")]
    protected virtual void CreateRequestedAnnulmentNotice(IExchangeDocumentInfo info, string reason)
    {
      var task = this.CreateRevocationDraftTask(info, false);

      this.FillTaskRequestedAnnulmentText(info, task);
      
      this.FillTaskRevocationReason(task, reason);
      task.Subject = CutText(task.Subject, task.Info.Properties.Subject.Length);
      task.Save();
      task.Start();
    }
    
    /// <summary>
    /// Отправить задачу или уведомление об аннулировании документа первой стороной.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    public virtual void SendCancellationAgreementProcessingTaskOrNotice(NpoComputer.DCX.Common.IMessage message,
                                                                        IExchangeDocumentInfo parentInfo,
                                                                        ICancellationAgreement cancellationAgreement,
                                                                        NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                                        bool isIncomingMessage)
    {
      if (dcxCancellationAgreement.SignStatus != NpoComputer.DCX.Common.SignStatus.Waiting ||
          !ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(parentInfo.Box) ||
          parentInfo.DownloadSession != null)
        return;
      
      var reason = dcxCancellationAgreement.Comment;
      var signature = message.Signatures.Where(x => x.DocumentId.Equals(dcxCancellationAgreement.ServiceEntityId, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
      var signatoryName = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(signature.Content);
      if (isIncomingMessage)
        this.SendCancellationAgreementProcessingTask(message, parentInfo, cancellationAgreement, reason, signatoryName);
      else
        this.SendRequestedCancellationAgreementNotice(parentInfo, cancellationAgreement, reason);
    }
    
    /// <summary>
    /// Отправить задачу на обработку аннулирования со стороны контрагента.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе обмена.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="reason">Причина аннулирования/отзыва.</param>
    /// <param name="signatoryName">ФИО подписавшего соглашение об аннулировании.</param>
    public virtual void SendCancellationAgreementProcessingTask(NpoComputer.DCX.Common.IMessage message,
                                                                IExchangeDocumentInfo parentInfo,
                                                                ICancellationAgreement cancellationAgreement,
                                                                string reason,
                                                                string signatoryName)
    {
      
      var cancellationAgreementInfo = Functions.ExchangeDocumentInfo.GetLastDocumentInfo(cancellationAgreement);
      var task = this.CreateExchangeTask(message, new List<IExchangeDocumentInfo> { cancellationAgreementInfo },
                                         parentInfo.Counterparty, true, cancellationAgreementInfo.Box, string.Empty);
      if (task != null)
      {
        task.DontNeedSigning.All.Add(parentInfo.Document);
        this.AddObserversToCancellationAgreementProcessingTask(task, parentInfo);
        this.FillCancellationAgreementTaskAssignmentText(cancellationAgreement, parentInfo, task, reason);
        task.Subject = CutText(task.Subject, task.Info.Properties.Subject.Length);
        task.Save();
        task.Start();
      }
      
      // Записать задачку, чтобы не пересоздавать их на каждый чих.
      parentInfo.RevocationTask = task;
      parentInfo.Save();
    }
    
    /// <summary>
    /// Отправить уведомление об аннулировании документа второй стороной.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="cancellationAgreementInfo">Информация об аннулировании в сервисе обмена.</param>
    /// <param name="cancellationAgreementMessageType">Тип (направление) соглашения об аннулировании.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="returnAssignments">Список заданий по контролю возврата.</param>
    public virtual void SendApprovedCancellationAgreementNotice(NpoComputer.DCX.Common.IMessage message,
                                                                IExchangeDocumentInfo parentInfo,
                                                                ICancellationAgreement cancellationAgreement,
                                                                NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                                IExchangeDocumentInfo cancellationAgreementInfo,
                                                                Enumeration? cancellationAgreementMessageType,
                                                                IBoxBase box,
                                                                List<IApprovalCheckReturnAssignment> returnAssignments)
    {
      var needSendNotice = (cancellationAgreementInfo == null ||
                            cancellationAgreementInfo.OutgoingStatus != Exchange.ExchangeDocumentInfo.OutgoingStatus.Signed) &&
        ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(parentInfo.Box) && parentInfo.DownloadSession == null;
      if (!needSendNotice)
        return;
      
      var createAssignments = false;
      var initiatedFromCounterparty = cancellationAgreementMessageType == Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      var signature = message.Signatures.Where(x => x.DocumentId.Equals(dcxCancellationAgreement.ServiceEntityId, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
      var signatoryName = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(signature.Content);

      var performers = this.GetCancellationAgreementNoticePerformers(parentInfo, returnAssignments, box);
      var task = this.CreateRevocationDraftTask(parentInfo, cancellationAgreement, createAssignments, performers);
      this.FillTaskAnnulmentNoticeText(cancellationAgreement, parentInfo, initiatedFromCounterparty, task, signatoryName);
      task.Subject = CutText(task.Subject, task.Info.Properties.Subject.Length);
      task.Save();
      task.Start();
    }
    
    /// <summary>
    /// Отправить уведомление об аннулировании документа второй стороной к контролю возврата соглашения об аннулировании.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="returnAssignments">Список заданий по контролю возврата.</param>
    public virtual void SendApprovedCancellationAgreementReturnTaskNotice(NpoComputer.DCX.Common.IMessage message,
                                                                          IExchangeDocumentInfo cancellationAgreementInfo,
                                                                          NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                                          IBoxBase box,
                                                                          List<IApprovalCheckReturnAssignment> returnAssignments)
    {
      if (!returnAssignments.Any())
        return;
      
      var cancellationAgreement = CancellationAgreements.As(cancellationAgreementInfo.Document);
      var createAssignments = false;
      var initiatedFromCounterparty = cancellationAgreementInfo.MessageType == Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      var signature = message.Signatures.Where(x => x.DocumentId.Equals(dcxCancellationAgreement.ServiceEntityId, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
      var signatoryName = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(signature.Content);

      var performers = this.GetCancellationAgreementNoticePerformers(cancellationAgreementInfo, returnAssignments, box);
      var task = this.CreateRevocationDraftTask(cancellationAgreementInfo, cancellationAgreement, createAssignments, performers);
      this.FillTaskAnnulmentNoticeText(cancellationAgreement, cancellationAgreementInfo.ParentDocumentInfo, initiatedFromCounterparty, task, signatoryName);
      task.Subject = CutText(task.Subject, task.Info.Properties.Subject.Length);
      task.Save();
      task.Start();
    }
    
    /// <summary>
    /// Отправить уведомление об отзыве документа.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="returnAssignments">Список заданий по контролю возврата.</param>
    public virtual void SendOneSidedCancellationAgreementNotice(NpoComputer.DCX.Common.IMessage message,
                                                                IExchangeDocumentInfo parentInfo,
                                                                ICancellationAgreement cancellationAgreement,
                                                                NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                                IBoxBase box,
                                                                List<IApprovalCheckReturnAssignment> returnAssignments)
    {
      var needSendNotice = ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(parentInfo.Box) && parentInfo.DownloadSession == null;
      if (!needSendNotice)
        return;
      
      var cancellationAgreementInfo = Exchange.Functions.ExchangeDocumentInfo.GetLastDocumentInfo(cancellationAgreement);
      var performers = this.GetCancellationAgreementNoticePerformers(parentInfo, returnAssignments, box);
      var parentTask = this.GetRevocationDraftTaskParentTask(parentInfo);
      this.SendOneSidedCancellationAgreementNotice(message, dcxCancellationAgreement,
                                                   parentInfo, cancellationAgreementInfo,
                                                   parentTask, performers);
    }
    
    /// <summary>
    /// Отправить уведомление об отзыве документа.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании.</param>
    /// <param name="parentTask">Головная задача.</param>
    /// <param name="performers">Список сотрудников для отправки уведомлений об аннулировании.</param>
    public virtual void SendOneSidedCancellationAgreementNotice(NpoComputer.DCX.Common.IMessage message,
                                                                NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                                IExchangeDocumentInfo parentInfo,
                                                                IExchangeDocumentInfo cancellationAgreementInfo,
                                                                ITask parentTask,
                                                                List<IRecipient> performers)
    {
      var needSendNotice = parentInfo.DownloadSession == null;
      if (!needSendNotice)
        return;
      
      var createAssignments = false;
      var initiatedFromCounterparty = cancellationAgreementInfo.MessageType == Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      var reason = dcxCancellationAgreement.Comment;
      
      var cancellationAgreement = CancellationAgreements.As(cancellationAgreementInfo.Document);
      
      var task = this.CreateRevocationDraftTask(parentInfo, cancellationAgreement, parentTask, createAssignments, performers);
      
      var signature = message.Signatures
        .Where(x => x.DocumentId.Equals(dcxCancellationAgreement.ServiceEntityId, StringComparison.InvariantCultureIgnoreCase))
        .FirstOrDefault();
      var signatoryName = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(signature.Content);
      this.FillTaskCancellationNoticeText(parentInfo, cancellationAgreementInfo, initiatedFromCounterparty, task, reason, signatoryName);
      
      task.Subject = CutText(task.Subject, task.Info.Properties.Subject.Length);
      task.Save();
      task.Start();
    }
    
    /// <summary>
    /// Отправить уведомление об отказе в подписании соглашения об аннулировании.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    /// <param name="rejectDocument">Отказ в подписании.</param>
    public virtual void SendRejectedCancellationAgreementNotice(NpoComputer.DCX.Common.IMessage message,
                                                                IExchangeDocumentInfo parentInfo,
                                                                ICancellationAgreement cancellationAgreement,
                                                                IExchangeDocumentInfo cancellationAgreementInfo,
                                                                NpoComputer.DCX.Common.IReglamentDocument rejectDocument)
    {
      var needSendNotice = cancellationAgreementInfo.OutgoingStatus != Exchange.ExchangeDocumentInfo.OutgoingStatus.Rejected &&
        ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(parentInfo.Box) && parentInfo.DownloadSession == null;
      if (!needSendNotice)
        return;

      var initiatedFromCounterparty = cancellationAgreementInfo.MessageType == Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming;

      var parentTask = this.GetExchangeDocumentProcessingTask(parentInfo);
      var performers = this.GetRevocationTaskPerformers(parentInfo, parentTask);
      var task = this.CreateRevocationDraftTask(parentInfo, cancellationAgreement, false, performers);
      var signature = message.Signatures.Where(x => x.DocumentId.Equals(rejectDocument.ServiceEntityId, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
      var signatoryName = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(signature.Content);
      this.FillTaskRejectedAnnulmentText(parentInfo, task, signatoryName, initiatedFromCounterparty, cancellationAgreement, rejectDocument?.Comment);
      task.Subject = CutText(task.Subject, task.Info.Properties.Subject.Length);
      task.Save();
      task.Start();
    }
    
    /// <summary>
    /// Отправить уведомление об отказе в аннулировании к контролю возврата соглашения об аннулировании.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    /// <param name="rejectDocument">Отказ в подписании.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="returnAssignments">Список заданий по контролю возврата.</param>
    public virtual void SendRejectedCancellationAgreementReturnTaskNotice(NpoComputer.DCX.Common.IMessage message,
                                                                          IExchangeDocumentInfo cancellationAgreementInfo,
                                                                          NpoComputer.DCX.Common.IReglamentDocument rejectDocument,
                                                                          IBoxBase box,
                                                                          List<IApprovalCheckReturnAssignment> returnAssignments)
    {
      if (!returnAssignments.Any())
        return;
      
      var initiatedFromCounterparty = cancellationAgreementInfo.MessageType == Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming;

      var cancellationAgreement = CancellationAgreements.As(cancellationAgreementInfo.Document);
      var createAssignments = false;
      var parentTask = this.GetExchangeDocumentProcessingTask(cancellationAgreementInfo);
      var performers = this.GetCancellationAgreementNoticePerformers(cancellationAgreementInfo, returnAssignments, box);
      var task = this.CreateRevocationDraftTask(cancellationAgreementInfo, cancellationAgreement, createAssignments, performers);
      var signature = message.Signatures.Where(x => x.DocumentId.Equals(rejectDocument.ServiceEntityId, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
      var signatoryName = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(signature.Content);
      this.FillTaskRejectedAnnulmentText(cancellationAgreementInfo.ParentDocumentInfo, task, signatoryName, initiatedFromCounterparty, cancellationAgreement, rejectDocument?.Comment);
      task.Subject = CutText(task.Subject, task.Info.Properties.Subject.Length);
      task.Save();
      task.Start();
    }
    
    /// <summary>
    /// Импортировать вторую подпись на соглашение об аннулировании.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="box">Абонентский ящик.</param>
    public virtual void ImportAnnulmentReplySign(NpoComputer.DCX.Common.IMessage message,
                                                 IExchangeDocumentInfo parentInfo,
                                                 ICancellationAgreement cancellationAgreement,
                                                 NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                 IBoxBase box)
    {
      // Импорт подписи и указание ИД подписи в инфо соглашения об аннулировании.
      if (cancellationAgreement != null)
        this.SignDocumentFromNewIncomingMessage(message, cancellationAgreement, dcxCancellationAgreement, box);
      
      // Занесение второй подписи в служебные документы основного документа.
      var serviceDoc = parentInfo.ServiceDocuments.Single(d => d.DocumentId == dcxCancellationAgreement.ServiceEntityId &&
                                                          d.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Annulment);
      
      var signature = message.Signatures.FirstOrDefault(s => s.DocumentId == dcxCancellationAgreement.ServiceEntityId);
      if (signature != null && !Enumerable.SequenceEqual(serviceDoc.Sign, signature.Content))
      {
        serviceDoc.SecondSign = signature.Content;
        serviceDoc.SecondFormalizedPoAUnifiedRegNo = signature.FormalizedPoAUnifiedRegNumber;
        parentInfo.Save();
      }
    }
    
    /// <summary>
    /// Установить статусы в сведениях об обмене соглашения об аннулировании после подписания аннулирования.
    /// </summary>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    public virtual void SetTwoSidedCancellationAgreementExchangeDocumentInfoStateSigned(IExchangeDocumentInfo cancellationAgreementInfo)
    {
      // Сначала сохраняем сведения о соглашении об аннулировании, чтобы при сохранении документа обновилось местоположение.
      if (cancellationAgreementInfo != null)
      {
        cancellationAgreementInfo.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.Signed;
        cancellationAgreementInfo.Save();
      }
    }
    
    /// <summary>
    /// Установить статусы соглашения об аннулировании после подписания аннулирования.
    /// </summary>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    public virtual void SetTwoSidedCancellationAgreementStateSigned(IOfficialDocument cancellationAgreement, bool isIncomingMessage)
    {
      if (isIncomingMessage)
        cancellationAgreement.ExternalApprovalState = Exchange.CancellationAgreement.ExternalApprovalState.Signed;
      else
        cancellationAgreement.InternalApprovalState = Exchange.CancellationAgreement.InternalApprovalState.Signed;
      cancellationAgreement.ExchangeState = Docflow.OfficialDocument.ExchangeState.Signed;
      cancellationAgreement.Save();
    }
    
    /// <summary>
    /// Установить статусы в сведениях об обмене соглашения об аннулировании после получения аннулирования.
    /// </summary>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    public virtual void SetCancellationAgreementExchangeDocumentInfoStateWaiting(IExchangeDocumentInfo cancellationAgreementInfo, bool isIncomingMessage)
    {
      // Сначала сохраняем сведения о соглашении об аннулировании, чтобы при сохранении документа обновилось местоположение.
      if (isIncomingMessage)
        cancellationAgreementInfo.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.SignRequired;
      else
        cancellationAgreementInfo.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.SignAwaited;
      cancellationAgreementInfo.Save();
    }
    
    /// <summary>
    /// Установить статусы соглашения об аннулировании после получения аннулирования.
    /// </summary>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    public virtual void SetCancellationAgreementStateWaiting(IOfficialDocument cancellationAgreement, bool isIncomingMessage)
    {
      if (isIncomingMessage)
      {
        cancellationAgreement.ExchangeState = Docflow.OfficialDocument.ExchangeState.SignRequired;
        cancellationAgreement.ExternalApprovalState = Exchange.CancellationAgreement.ExternalApprovalState.Signed;
      }
      else
      {
        cancellationAgreement.ExchangeState = Docflow.OfficialDocument.ExchangeState.SignAwaited;
        cancellationAgreement.InternalApprovalState = Exchange.CancellationAgreement.InternalApprovalState.Signed;
      }
      cancellationAgreement.Save();
    }
    
    /// <summary>
    /// Установить статусы соглашения об аннулировании после отказа в аннулировании.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    public virtual void SetCancellationAgreementStateRejected(NpoComputer.DCX.Common.IMessage message,
                                                              ICancellationAgreement cancellationAgreement,
                                                              NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                              IExchangeDocumentInfo cancellationAgreementInfo,
                                                              bool isIncomingMessage)
    {
      if (cancellationAgreement.LifeCycleState != Docflow.OfficialDocument.LifeCycleState.Obsolete)
      {
        Docflow.PublicFunctions.OfficialDocument.SetObsolete(cancellationAgreement, false);
        this.SetRejectStates(cancellationAgreement, cancellationAgreementInfo, isIncomingMessage, true, false);
        if (!isIncomingMessage)
        {
          cancellationAgreement.LifeCycleState = Exchange.CancellationAgreement.LifeCycleState.Obsolete;
          cancellationAgreementInfo.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.Rejected;
        }
      }
    }
    
    /// <summary>
    /// Обработать одностороннее аннулирование (отзыв).
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    public virtual void ProcessOneSidedCancellationAgreement(NpoComputer.DCX.Common.IMessage message,
                                                             IExchangeDocumentInfo parentInfo,
                                                             NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                             ICounterparty sender,
                                                             bool isIncomingMessage,
                                                             IBoxBase box)
    {
      this.LogDebugFormat(parentInfo, "Execute ProcessOneSidedCancellationAgreement, ParentServiceEntityId '{0}'.",
                          dcxCancellationAgreement.ParentServiceEntityId);
      
      var cancellationAgreementInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, dcxCancellationAgreement.ServiceEntityId);
      if (cancellationAgreementInfo == null)
      {
        this.LogDebugFormat(parentInfo, "Execute ProcessOneSidedCancellationAgreement. CreateCancellationAgreement, ParentServiceEntityId '{0}'.",
                            dcxCancellationAgreement.ParentServiceEntityId);

        this.AddCancellationAgreementToServiceDocuments(message, parentInfo, dcxCancellationAgreement, isIncomingMessage,
                                                        Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Cancellation);
        var cancellationAgreement = this.ImportCancellationAgreement(dcxCancellationAgreement.Content, parentInfo.Document.Id,
                                                                     dcxCancellationAgreement.Comment);
        
        cancellationAgreementInfo = this.CreateCancellationAgreementInfoWithoutDocument(parentInfo, dcxCancellationAgreement,
                                                                                        isIncomingMessage, message.TimeStamp, box);
        cancellationAgreementInfo.VersionId = cancellationAgreement.LastVersion.Id;
        cancellationAgreementInfo.Document = cancellationAgreement;
        cancellationAgreementInfo.Save();
        this.LogDebugFormat(cancellationAgreementInfo, "CancellationAgreementInfo Created, ParentServiceEntityId '{0}'.",
                            dcxCancellationAgreement.ParentServiceEntityId);

        this.SignDocumentFromNewIncomingMessage(message, cancellationAgreement, dcxCancellationAgreement, box);
        this.SetParentExchangeDocumentInfoStateAfterAnnulment(parentInfo, Exchange.ExchangeDocumentInfo.ExchangeState.Obsolete);
        this.SetParentDocumentExchangeStateAfterOneSidedAnnulment(parentInfo, Exchange.ExchangeDocumentInfo.ExchangeState.Obsolete);
        this.SetOneSidedCancellationAgreementExchangeDocumentInfoStateSigned(cancellationAgreementInfo, isIncomingMessage);
        this.SetOneSidedCancellationAgreementStateSigned(cancellationAgreement, isIncomingMessage);
        this.SetParentDocumentInfoDeliveryConfirmationStatusAfterAnnulment(parentInfo);
        this.UpdateParentDocumentTracking(parentInfo, cancellationAgreementInfo.MessageType);
        this.AddCancellationAgreementTracking(cancellationAgreementInfo, cancellationAgreement, dcxCancellationAgreement.SignStatus);
        var returnAssignments = this.CompleteParentDocumentReturnTask(parentInfo, cancellationAgreementInfo);
        this.WriteHistoryAfterAnnulment(parentInfo, sender, cancellationAgreementInfo.MessageType, box);
        this.SendOneSidedCancellationAgreementNotice(message, parentInfo, cancellationAgreement, dcxCancellationAgreement, box, returnAssignments);
      }
      else
      {
        this.LogDebugFormat(parentInfo, "Execute ProcessOneSidedCancellationAgreement. CancellationAgreementInfo Already Exist, ParentServiceEntityId '{0}'.",
                            dcxCancellationAgreement.ParentServiceEntityId);
        this.AddCancellationAgreementToServiceDocuments(message, parentInfo, dcxCancellationAgreement, isIncomingMessage,
                                                        Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Cancellation);
        this.SetParentExchangeDocumentInfoStateAfterAnnulment(parentInfo, Exchange.ExchangeDocumentInfo.ExchangeState.Obsolete);
        this.SetParentDocumentExchangeStateAfterOneSidedAnnulment(parentInfo, Exchange.ExchangeDocumentInfo.ExchangeState.Obsolete);
        this.SetParentDocumentInfoDeliveryConfirmationStatusAfterAnnulment(parentInfo);
        this.UpdateParentDocumentTracking(parentInfo, cancellationAgreementInfo.MessageType);
        this.CompleteParentDocumentReturnTask(parentInfo, cancellationAgreementInfo);
        this.WriteHistoryAfterAnnulment(parentInfo, sender, cancellationAgreementInfo.MessageType, box);
        this.CompleteSignedOneSidedCancellationAgreementReturnTask(message, dcxCancellationAgreement, parentInfo, cancellationAgreementInfo);
      }
    }
    
    /// <summary>
    /// Установить статусы в сведениях об обмене одностороннего соглашения об аннулировании после его подписания.
    /// </summary>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    public virtual void SetOneSidedCancellationAgreementExchangeDocumentInfoStateSigned(IExchangeDocumentInfo cancellationAgreementInfo,
                                                                                        bool isIncomingMessage)
    {
      // Сначала сохраняем сведения о соглашении об аннулировании, чтобы при сохранении документа обновилось местоположение.
      if (cancellationAgreementInfo != null)
      {
        cancellationAgreementInfo.ExchangeState = isIncomingMessage ?
          Exchange.ExchangeDocumentInfo.ExchangeState.Received :
          Exchange.ExchangeDocumentInfo.ExchangeState.Sent;
        cancellationAgreementInfo.Save();
      }
    }
    
    /// <summary>
    /// Установить статусы одностороннего соглашения об аннулировании после его подписания.
    /// </summary>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    public virtual void SetOneSidedCancellationAgreementStateSigned(ICancellationAgreement cancellationAgreement,
                                                                    bool isIncomingMessage)
    {
      if (isIncomingMessage)
      {
        cancellationAgreement.ExternalApprovalState = Exchange.CancellationAgreement.ExternalApprovalState.Signed;
        cancellationAgreement.ExchangeState = Docflow.OfficialDocument.ExchangeState.Received;
      }
      else
      {
        cancellationAgreement.InternalApprovalState = Exchange.CancellationAgreement.InternalApprovalState.Signed;
        cancellationAgreement.ExchangeState = Docflow.OfficialDocument.ExchangeState.Sent;
      }
      
      cancellationAgreement.Save();
    }
    
    /// <summary>
    /// Обновить информацию о выдаче основного документа после аннулирования.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="cancellationAgreementMessageType">Тип (направление) соглашения об аннулировании.</param>
    public virtual void UpdateParentDocumentTracking(IExchangeDocumentInfo parentInfo, Enumeration? cancellationAgreementMessageType)
    {
      var tracking = parentInfo.Document.Tracking.FirstOrDefault(x => x.ExternalLinkId == parentInfo.Id);
      if (tracking == null)
        return;
      
      var initiatedFromCounterparty = cancellationAgreementMessageType == Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      var trackingNote = initiatedFromCounterparty ? Resources.CounterpartyRevocationTrackingNote : Resources.RevocationTrackingNote;
      if (tracking.ReturnDeadline.HasValue && !tracking.ReturnDate.HasValue)
      {
        tracking.ReturnResult = Docflow.OfficialDocumentTracking.ReturnResult.NotSigned;
        tracking.Note = trackingNote;
      }
      else
      {
        tracking.Note = trackingNote;
      }
      
      tracking.OfficialDocument.Save();
    }
    
    /// <summary>
    /// Обновить статусы ИОП для основного документа после аннулирования.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    public virtual void SetParentDocumentInfoDeliveryConfirmationStatusAfterAnnulment(IExchangeDocumentInfo parentInfo)
    {
      var sellerStatus = parentInfo.DeliveryConfirmationStatus;
      if (sellerStatus == Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Requested ||
          sellerStatus == Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Required ||
          sellerStatus == Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Generated ||
          sellerStatus == Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Signed)
      {
        parentInfo.DeliveryConfirmationStatus = Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.NotRequired;
      }
      
      var buyerStatus = parentInfo.BuyerDeliveryConfirmationStatus;
      if (buyerStatus == Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Requested ||
          buyerStatus == Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Required ||
          buyerStatus == Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Generated ||
          buyerStatus == Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Signed)
      {
        parentInfo.BuyerDeliveryConfirmationStatus = Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.NotRequired;
      }
      parentInfo.Save();
    }
    
    /// <summary>
    /// Обновить информацию о выдаче соглашения об аннулировании после аннулирования.
    /// </summary>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    public virtual void UpdateCancellationAgreementTracking(IExchangeDocumentInfo cancellationAgreementInfo)
    {
      Docflow.IOfficialDocumentTracking tracking = null;
      var isRejected = cancellationAgreementInfo.ExchangeState == Exchange.ExchangeDocumentInfo.ExchangeState.Rejected;
      tracking = cancellationAgreementInfo.Document.Tracking.FirstOrDefault(x => x.ExternalLinkId == cancellationAgreementInfo.Id);
      if (cancellationAgreementInfo.MessageType == Exchange.ExchangeDocumentInfo.MessageType.Incoming)
      {
        // При ответе из сервиса или из RX на входящее соглашение об аннулировании, результат подписания указываем в примечании.
        var isReplySentFromService = tracking == null;
        if (isReplySentFromService)
        {
          tracking = this.AddTrackingAfterSignOrReject(cancellationAgreementInfo, cancellationAgreementInfo.Document);
        }
        else
        {
          var exchangeServiceName = ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(cancellationAgreementInfo.Box).Name;
          tracking.Note = isRejected ?
            Resources.SendRejectToCounterpartyFormat(exchangeServiceName) :
            Resources.SendSignToCounterpartyFormat(exchangeServiceName);
        }
      }
      else
      {
        tracking.ReturnResult = isRejected ?
          Docflow.OfficialDocumentTracking.ReturnResult.NotSigned :
          Docflow.OfficialDocumentTracking.ReturnResult.Signed;
        tracking.ReturnDate = Calendar.UserToday;
      }
      
      tracking.OfficialDocument.Save();
    }
    
    /// <summary>
    /// Выполнить задания на контроль возврата для основного документа.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    /// <returns>Список выполненных заданий по контролю возврата.</returns>
    public virtual List<IApprovalCheckReturnAssignment> CompleteParentDocumentReturnTask(IExchangeDocumentInfo parentInfo,
                                                                                         IExchangeDocumentInfo cancellationAgreementInfo)
    {
      var returnAssignments = this.GetApprovalCheckReturnAssignment(parentInfo);
      if (cancellationAgreementInfo != null)
        this.AddCancellationAgreementToApprovalTaskAttachments(parentInfo, cancellationAgreementInfo);
      this.CompleteReturnTask(parentInfo, returnAssignments, Resources.CheckReturnRevocationResult, Docflow.ApprovalCheckReturnAssignment.Result.NotSigned);
      
      return returnAssignments;
    }
    
    /// <summary>
    /// Выполнить задания на контроль возврата для соглашения об аннулировании.
    /// </summary>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    /// <param name="completeResult">Результат выполнения задания на контроль возврата.</param>
    /// <returns>Список выполненных заданий по контролю возврата.</returns>
    public virtual List<IApprovalCheckReturnAssignment> CompleteCancellationAgreementReturnTask(IExchangeDocumentInfo cancellationAgreementInfo,
                                                                                                Enumeration completeResult)
    {
      var returnAssignments = this.GetApprovalCheckReturnAssignment(cancellationAgreementInfo);
      var activeText = completeResult == Docflow.ApprovalCheckReturnAssignment.Result.Signed ?
        Resources.ReturnTaskCompleteResultSigned :
        Resources.ReturnTaskCompleteResultNotSigned;
      this.CompleteReturnTask(cancellationAgreementInfo, returnAssignments, activeText, completeResult);
      
      return returnAssignments;
    }
    
    /// <summary>
    /// Выполнить задания на контроль возврата для одностороннего соглашения об аннулировании.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    /// <remarks>Выполнить задания на контроль возврата, если они есть.
    /// Для одностороннего соглашения об аннулировании задача на согласование не сохраняется в выдаче,
    /// поэтому ищем все задачи, в которые аннулирование вложено, как главный документ.</remarks>
    public virtual void CompleteSignedOneSidedCancellationAgreementReturnTask(NpoComputer.DCX.Common.IMessage message,
                                                                              NpoComputer.DCX.Common.IDocument dcxCancellationAgreement,
                                                                              IExchangeDocumentInfo parentInfo,
                                                                              IExchangeDocumentInfo cancellationAgreementInfo)
    {
      var cancellationAgreement = CancellationAgreements.As(cancellationAgreementInfo.Document);
      var approvalTask = Sungero.Docflow.PublicFunctions.Module.Remote.GetApprovalTasks(cancellationAgreement).FirstOrDefault();
      if (approvalTask != null)
      {
        var returnAssignments = ApprovalCheckReturnAssignments
          .GetAll(a => Equals(a.Task, approvalTask) && a.Status == Workflow.AssignmentBase.Status.InProcess)
          .ToList();
        this.CompleteReturnAssignments(returnAssignments,
                                       Resources.ReturnTaskCompleteResultSigned,
                                       Docflow.ApprovalCheckReturnAssignment.Result.Signed);
        
        // 306972: Меняем статус на пустой, тк в регламенте есть сценарий, который устанавливает состояние Согл. с контрагентом = На согласовании.
        this.SetCancellationAgreementExternalApprovalStateEmpty(cancellationAgreement);
        
        var performers = returnAssignments.Select(x => CoreEntities.Recipients.As(x.Performer)).Where(p => p != null).Distinct().ToList();
        this.SendOneSidedCancellationAgreementNotice(message, dcxCancellationAgreement,
                                                     parentInfo, cancellationAgreementInfo,
                                                     approvalTask, performers);
      }
    }
    
    /// <summary>
    /// Установить пустой статус Согл. с контраегнтом для соглашения об аннулировании.
    /// </summary>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    public virtual void SetCancellationAgreementExternalApprovalStateEmpty(ICancellationAgreement cancellationAgreement)
    {
      cancellationAgreement.ExternalApprovalState = null;
      cancellationAgreement.Save();
    }
    
    /// <summary>
    /// Получить задания на контроль возврата по документу.
    /// </summary>
    /// <param name="info">Информация о документе в сервисе обмена.</param>
    /// <returns>Список заданий на контроль возврата.</returns>
    public virtual List<IApprovalCheckReturnAssignment> GetApprovalCheckReturnAssignment(IExchangeDocumentInfo info)
    {
      var tracking = info.Document.Tracking.FirstOrDefault(x => x.ExternalLinkId == info.Id);
      var returnAssignments = new List<IApprovalCheckReturnAssignment>();
      
      if (tracking == null || tracking.ReturnTask == null)
        return returnAssignments;
      
      var approvalTask = ApprovalTasks.As(tracking.ReturnTask);
      if (approvalTask == null)
        return returnAssignments;
      
      returnAssignments = Docflow.ApprovalCheckReturnAssignments
        .GetAll()
        .Where(x => Equals(x.Task, tracking.ReturnTask) && x.Status == Workflow.Assignment.Status.InProcess)
        .ToList();
      
      return returnAssignments;
    }
    
    /// <summary>
    /// Добавить соглашение об аннулировании во вложения задачи на согласование по регламенту.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании в сервисе обмена.</param>
    public virtual void AddCancellationAgreementToApprovalTaskAttachments(IExchangeDocumentInfo parentInfo, IExchangeDocumentInfo cancellationAgreementInfo)
    {
      var tracking = parentInfo.Document.Tracking.FirstOrDefault(x => x.ExternalLinkId == parentInfo.Id);
      var approvalTask = ApprovalTasks.As(tracking?.ReturnTask);
      if (approvalTask == null)
        return;
      
      // Занести СоА во вложения задачи.
      approvalTask.OtherGroup.All.Add(cancellationAgreementInfo.Document);
      approvalTask.Save();
    }
    
    /// <summary>
    /// Выполнить задания на контроль возврата.
    /// </summary>
    /// <param name="info">Информация о документе в сервисе обмена.</param>
    /// <param name="returnAssignments">Список заданий на контроль возврата.</param>
    /// <param name="activeText">Текст для выполненного задания.</param>
    /// <param name="completeResult">Результат выполнения задания на контроль возврата.</param>
    public virtual void CompleteReturnTask(IExchangeDocumentInfo info, List<IApprovalCheckReturnAssignment> returnAssignments, string activeText, Enumeration completeResult)
    {
      var tracking = info.Document.Tracking.FirstOrDefault(x => x.ExternalLinkId == info.Id);
      
      if (tracking == null || !returnAssignments.Any())
        return;
      
      var approvalTask = ApprovalTasks.As(tracking.ReturnTask);
      
      // Выполнять задания на контроль возврата, только если аннулируемый документ в основной группе.
      var isMainDocument = approvalTask.DocumentGroup.OfficialDocuments.Contains(tracking.OfficialDocument);
      if (!isMainDocument)
        return;
      
      this.CompleteReturnAssignments(returnAssignments, activeText, completeResult);
    }
    
    /// <summary>
    /// Выполнить задания на контроль возврата.
    /// </summary>
    /// <param name="returnAssignments">Список заданий на контроль возврата.</param>
    /// <param name="activeText">Текст для выполненного задания.</param>
    /// <param name="completeResult">Результат выполнения задания на контроль возврата.</param>
    public virtual void CompleteReturnAssignments(List<IApprovalCheckReturnAssignment> returnAssignments, string activeText, Enumeration completeResult)
    {
      foreach (var assignment in returnAssignments)
      {
        assignment.ActiveText = activeText;
        assignment.AutoReturned = true;
        assignment.Save();
      }

      foreach (var assignment in returnAssignments)
        assignment.Complete(completeResult);
    }
    
    /// <summary>
    /// Получить список сотрудников для отправки уведомлений об аннулировании.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="returnAssignments">Список заданий по контролю возврата.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <returns>Список сотрудников для отправки уведомлений об аннулировании.</returns>
    public virtual List<IRecipient> GetCancellationAgreementNoticePerformers(IExchangeDocumentInfo parentInfo,
                                                                             List<IApprovalCheckReturnAssignment> returnAssignments,
                                                                             IBoxBase box)
    {
      var performers = new List<IRecipient>();
      var tracking = parentInfo.Document.Tracking.FirstOrDefault(x => x.ExternalLinkId == parentInfo.Id);
      var parentTask = this.GetExchangeDocumentProcessingTask(parentInfo);

      // Основной документ был отправлен на согласование по регламенту, и сейчас в процессе задание на контроль возврата.
      if (tracking != null && tracking.ReturnTask != null)
      {
        foreach (var assignment in returnAssignments)
          performers.Add(assignment.Performer);
        
        performers.AddRange(Docflow.PublicFunctions.ApprovalTask.GetAllApproversAndSignatories(Docflow.ApprovalTasks.As(tracking.ReturnTask)));
        
        // Добавить в список ответственного за а/я.
        var boxResponsible = ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(box, parentInfo.Counterparty,
                                                                                                        new List<IExchangeDocumentInfo>() { parentInfo });
        performers.Add(boxResponsible);
        performers.Add(tracking.ReturnTask.Author);
        performers.Add(tracking.DeliveredTo);
      }
      else
      {
        performers = this.GetRevocationTaskPerformers(parentInfo, parentTask);
      }
      
      return performers.Where(p => p != null).Distinct().ToList();
    }
    
    /// <summary>
    /// Обработать аннулирование.
    /// </summary>
    /// <param name="info">Информация о документе в сервисе обмена.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="fromCounterparty">Признак авторства запроса на аннулирование.</param>
    /// <param name="versionNumber">Номер версии, которую аннулируют.</param>
    /// <param name="comment">Комментарий в истории документа.</param>
    [Obsolete("Используйте метод ProcessTwoSidedCancellationAgreement.")]
    protected virtual void ProcessAnnulment(IExchangeDocumentInfo info, IDocument dcxCancellationAgreement,
                                            bool isIncomingMessage, bool fromCounterparty,
                                            int? versionNumber, string comment)
    {
      this.LogDebugFormat(info, "Execute ProcessAnnulment.");
      // Признак того, а можно ли "аннулировать" документ в текущей его версии. Если нет - то обновляем только инфошку, документ не трогаем.
      var canSendAnswer = Exchange.PublicFunctions.ExchangeDocumentInfo.Remote.GetLastDocumentInfo(info.Document) == info;
      
      if (dcxCancellationAgreement.SignStatus != NpoComputer.DCX.Common.SignStatus.None)
      {
        // Аннулирование.
        if (dcxCancellationAgreement.SignStatus == NpoComputer.DCX.Common.SignStatus.Signed)
        {
          if (ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(info.Box) && info.DownloadSession == null)
            this.CreateRevocationTask(info, fromCounterparty, dcxCancellationAgreement.Comment, true, false);
          if (canSendAnswer)
          {
            Docflow.PublicFunctions.OfficialDocument.SetObsolete(info.Document, true);
            info.Document.ExchangeState = Docflow.OfficialDocument.ExchangeState.Terminated;
            info.Document.ExternalApprovalState = null;
          }
          info.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.Terminated;
          var operation = new Enumeration(fromCounterparty ? Constants.Module.Exchange.ObsoletedByCounterparty : Constants.Module.Exchange.ObsoleteOur);
          info.Document.History.Write(operation, operation, comment, versionNumber);
          info.Document.Save();
          info.Save();
        }
        
        // Запрос на аннулирование обрабатывается только входящий.
        if (dcxCancellationAgreement.SignStatus == NpoComputer.DCX.Common.SignStatus.Waiting && ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(info.Box) &&
            info.DownloadSession == null)
        {
          // Задание на аннулирование, если нет задания в работе.
          if (info.RevocationTask == null || info.RevocationTask.Status != Workflow.Task.Status.InProcess)
          {
            if (isIncomingMessage)
              this.CreateRevocationTask(info, fromCounterparty, dcxCancellationAgreement.Comment, true, true);
            else
              this.SendRequestedCancellationAgreementNotice(info, null, dcxCancellationAgreement.Comment);
          }
        }
        
        if (dcxCancellationAgreement.SignStatus == NpoComputer.DCX.Common.SignStatus.Rejected)
        {
          // Передумали подписывать - откатываем статус подписания.
          info.RevocationStatus = Exchange.ExchangeDocumentInfo.RevocationStatus.None;
          info.Save();
        }
      }
    }
    
    /// <summary>
    /// Обработать отзыв.
    /// </summary>
    /// <param name="info">Информация о документе в сервисе обмена.</param>
    /// <param name="document">Соглашение об аннулировании из сервиса обмена.</param>
    /// <param name="fromCounterparty">Признак авторства отзыва.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="versionNumber">Номер версии, которую отозвали.</param>
    /// <param name="comment">Комментарий в истории документа.</param>
    [Obsolete("Используйте метод ProcessOneSidedCancellationAgreement.")]
    protected virtual void ProcessCancellation(IExchangeDocumentInfo info, IDocument document, bool fromCounterparty,
                                               IBoxBase box, int? versionNumber, string comment)
    {
      this.LogDebugFormat(info, "Execute ProcessCancellation.");
      // Признак того, а можно ли "отозвать" документ в текущей его версии. Если нет - то обновляем только инфошку, документ не трогаем.
      var canSendAnswer = Exchange.PublicFunctions.ExchangeDocumentInfo.Remote.GetLastDocumentInfo(info.Document) == info;
      
      // Изменяем статус документа заранее, чтобы правильно обработать контроль возврата.
      if (canSendAnswer)
      {
        Docflow.PublicFunctions.OfficialDocument.SetObsolete(info.Document, false);
        info.Document.ExchangeState = Docflow.OfficialDocument.ExchangeState.Obsolete;
        info.Document.ExternalApprovalState = null;
        info.Document.Save();
      }
      
      // Отзыв.
      var tracking = info.Document.Tracking.FirstOrDefault(x => x.ExternalLinkId == info.Id);
      if (tracking != null)
      {
        var docHyperlink = Hyperlinks.Get(tracking.OfficialDocument);
        var activeText = Resources.RevocationNoticeOurActiveTextTerminateFormat(docHyperlink).ToString();
        var performers = new List<IUser>();
        
        if (tracking.ReturnTask != null)
        {
          // Агент автоматически выполняет задание на контроль возврата и процесс идет дальше.
          var returnAssignments = Docflow.ApprovalCheckReturnAssignments.GetAll().Where(x => Equals(x.Task, tracking.ReturnTask) && x.Status == Workflow.Assignment.Status.InProcess).ToList();
          
          if (returnAssignments.Any())
          {
            // Разделено установка признака AutoReturned и выполнение заданий, т.к. при большом количестве исполнителей схема успевает начать свою рассылку.
            foreach (var assignment in returnAssignments)
            {
              assignment.ActiveText = Resources.CheckReturnRevocationResult;
              assignment.AutoReturned = true;
              assignment.Save();
            }
            
            foreach (var assignment in returnAssignments)
            {
              var completeResult = Docflow.ApprovalCheckReturnAssignment.Result.NotSigned;
              assignment.Complete(completeResult);
              performers.Add(assignment.Performer);
            }
          }
          
          performers.AddRange(Docflow.PublicFunctions.ApprovalTask.GetAllApproversAndSignatories(Docflow.ApprovalTasks.As(tracking.ReturnTask)));
        }
        
        // Отправить уведомление ответственному за а/я.
        var boxResponsible = ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(box, info.Counterparty,
                                                                                                        new List<IExchangeDocumentInfo>() { info });
        
        performers.Add(boxResponsible);
        performers = performers.Distinct().ToList();
        
        if (!string.IsNullOrEmpty(document.Comment))
        {
          activeText += Environment.NewLine;
          activeText += string.Format(Resources.DocumentComment, document.Comment);
        }
        
        var exchangeService = ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box);
        if (ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(box) && info.DownloadSession == null)
          this.CreateDocumentReplyNotice(box, tracking, false, true, false, performers, activeText);
        
        tracking.ReturnResult = Docflow.OfficialDocumentTracking.ReturnResult.NotSigned;
        tracking.Note = Resources.RevocationTrackingNote;
      }
      else
      {
        if (ExchangeCore.PublicFunctions.BoxBase.NeedReceiveTask(box) && info.DownloadSession == null)
          this.CreateRevocationTask(info, fromCounterparty, document.Comment, false, false);
      }

      var operation = new Enumeration(fromCounterparty ? Constants.Module.Exchange.TerminatedByCounterparty : Constants.Module.Exchange.TerminateOur);
      info.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.Obsolete;
      info.Document.History.Write(operation, operation, comment, versionNumber);
      info.Document.Save();
      info.Save();
    }
    
    /// <summary>
    /// Создать черновик задачи об аннулировании/отзыве документа.
    /// </summary>
    /// <param name="parentInfo">Информация о документе обмена.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="createAssignments">True, если надо отправить задания, false - уведомления.</param>
    /// <param name="performers">Исполнители задачи.</param>
    /// <returns>Черновик задачи.</returns>
    protected virtual ISimpleTask CreateRevocationDraftTask(IExchangeDocumentInfo parentInfo,
                                                            ICancellationAgreement cancellationAgreement,
                                                            bool createAssignments,
                                                            List<IRecipient> performers)
    {
      var parentTask = this.GetRevocationDraftTaskParentTask(parentInfo);
      return this.CreateRevocationDraftTask(parentInfo, cancellationAgreement, parentTask, createAssignments, performers);
    }
    
    /// <summary>
    /// Получить головную задачу для черновика задачи об аннулировании/отзыве документа.
    /// </summary>
    /// <param name="parentInfo">Информация о документе обмена.</param>
    /// <returns>Головная задача.</returns>
    protected virtual ITask GetRevocationDraftTaskParentTask(IExchangeDocumentInfo parentInfo)
    {
      var parentTask = Tasks.Null;
      
      var tracking = parentInfo.Document.Tracking.FirstOrDefault(x => x.ExternalLinkId == parentInfo.Id && x.ReturnTask != null);
      if (tracking != null)
        parentTask = tracking.ReturnTask;
      else if (parentTask == null)
        parentTask = this.GetExchangeDocumentProcessingTask(parentInfo);
      else if (parentInfo.RevocationTask != null)
        parentTask = parentInfo.RevocationTask;
      
      return parentTask;
    }
    
    /// <summary>
    /// Создать черновик задачи об аннулировании/отзыве документа.
    /// </summary>
    /// <param name="parentInfo">Информация о документе обмена.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="parentTask">Головная задача.</param>
    /// <param name="createAssignments">True, если надо отправить задания, false - уведомления.</param>
    /// <param name="performers">Исполнители задачи.</param>
    /// <returns>Черновик задачи.</returns>
    protected virtual ISimpleTask CreateRevocationDraftTask(IExchangeDocumentInfo parentInfo,
                                                            ICancellationAgreement cancellationAgreement,
                                                            ITask parentTask,
                                                            bool createAssignments,
                                                            List<IRecipient> performers)
    {
      var task = parentTask == null ?
        Workflow.SimpleTasks.Create() :
        Workflow.SimpleTasks.CreateAsSubtask(parentTask);
      
      var asgType = createAssignments ? Workflow.SimpleTask.AssignmentType.Assignment : Workflow.SimpleTask.AssignmentType.Notice;
      task.AssignmentType = asgType;
      
      // При создании подзадачи в нее копируются все вложения.
      var docs = task.AllAttachments.Where(d => !Equals(d, parentInfo.Document)).ToList();
      foreach (var doc in docs)
        task.Attachments.Remove(doc);
      
      this.GrantAccessRightsForUpperBoxResponsibles(parentInfo.Document, parentInfo.Box);
      if (!task.AllAttachments.Where(d => Equals(d, parentInfo.Document)).Any())
        task.Attachments.Add(parentInfo.Document);
      
      if (cancellationAgreement != null && !task.AllAttachments.Where(d => Equals(d, cancellationAgreement)).Any())
        task.Attachments.Add(cancellationAgreement);
      
      if (!performers.Any())
        performers.Add(ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(parentInfo.Box, parentInfo.Counterparty, new List<IExchangeDocumentInfo>() { parentInfo }));
      
      foreach (var performer in performers)
      {
        var step = task.RouteSteps.AddNew();
        step.Performer = performer;
        step.AssignmentType = asgType;
        if (!createAssignments)
          step.Deadline = null;
        
        // Задание со сроком в 2 рабочих дня.
        if (createAssignments)
          step.Deadline = Calendar.Now.AddWorkingHours(performer, 16);
      }
      task.NeedsReview = false;
      
      return task;
    }
    
    /// <summary>
    /// Получить задачу по обработке документа обмена.
    /// </summary>
    /// <param name="info">Информация о документе обмена.</param>
    /// <returns>Задача по обработке документа обмена.</returns>
    public virtual IExchangeDocumentProcessingTask GetExchangeDocumentProcessingTask(IExchangeDocumentInfo info)
    {
      var docGuid = info.Document.GetEntityMetadata().GetOriginal().NameGuid;
      var parentTask = ExchangeDocumentProcessingTasks.GetAll()
        .Where(t => t.AttachmentDetails.Any(att => att.AttachmentId == info.Document.Id && att.EntityTypeGuid == docGuid))
        .FirstOrDefault();
      
      return parentTask;
    }
    
    /// <summary>
    /// Сгенерировать публичные тела для основного документа и соглашения об аннулировании.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе в сервисе обмена.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="dcxCancellationAgreement">Соглашение об аннулировании из сервиса обмена.</param>
    public virtual void GeneratePublicBodies(IExchangeDocumentInfo parentInfo,
                                             IBoxBase box,
                                             NpoComputer.DCX.Common.IDocument dcxCancellationAgreement)
    {
      // Для формализованных финансовых документов преобразовать оба титула.
      var parentDocument = parentInfo.Document;
      var accountingDocument = Docflow.AccountingDocumentBases.As(parentDocument);
      if (accountingDocument != null && accountingDocument.IsFormalized == true)
      {
        foreach (var version in parentDocument.Versions)
          Docflow.PublicFunctions.Module.GeneratePublicBodyForExchangeDocument(parentDocument,
                                                                               version.Id,
                                                                               parentDocument.ExchangeState);
      }
      else
      {
        Docflow.PublicFunctions.Module.GeneratePublicBodyForExchangeDocument(parentDocument,
                                                                             parentInfo.VersionId.Value,
                                                                             parentDocument.ExchangeState);
      }
      
      var cancellationAgreementInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box,
                                                                                                   dcxCancellationAgreement.ServiceEntityId);
      if (cancellationAgreementInfo != null)
        Docflow.PublicFunctions.Module.GeneratePublicBodyForExchangeDocument(cancellationAgreementInfo.Document,
                                                                             cancellationAgreementInfo.VersionId.Value,
                                                                             cancellationAgreementInfo.Document.ExchangeState);
    }
    
    /// <summary>
    /// Создать черновик задачи об аннулировании/отзыве документа.
    /// </summary>
    /// <param name="info">Информация о документе обмена.</param>
    /// <param name="createAssignments">True, если надо отправить задания, false - уведомления.</param>
    /// <returns>Черновик задачи.</returns>
    [Obsolete("Используйте метод CreateRevocationDraftTask с параметром соглашение об аннулировании.")]
    protected virtual ISimpleTask CreateRevocationDraftTask(IExchangeDocumentInfo info, bool createAssignments)
    {
      var parentTask = this.GetExchangeDocumentProcessingTask(info);
      var performers = this.GetRevocationTaskPerformers(info, parentTask);
      return this.CreateRevocationDraftTask(info, null, createAssignments, performers);
    }
    
    /// <summary>
    /// Создать и стартовать задачу об аннулировании/отзыве контрагентом.
    /// </summary>
    /// <param name="info">Информация о документе обмена.</param>
    /// <param name="fromCounterparty">Аннулирование пришло от контрагента.</param>
    /// <param name="reason">Причина аннулирования/отзыва.</param>
    /// <param name="isAnnulment">True - если аннулирован, false - если отозван.</param>
    /// <param name="createAssignments">True, если надо отправить задания, false - уведомления.</param>
    /// <remarks>Еще обновляется статус и ИД задачи на аннулирование в инфошке.</remarks>
    [Obsolete("Выделены конкретные методы под каждый случай уведомления SendApprovedCancellationAgreementNotice, SendRejectedCancellationAgreementNotice, SendOneSidedCancellationAgreementNotice, SendCancellationAgreementProcessingTaskOrNotice.")]
    protected virtual void CreateRevocationTask(IExchangeDocumentInfo info, bool fromCounterparty, string reason, bool isAnnulment, bool createAssignments)
    {
      var parentTask = this.GetExchangeDocumentProcessingTask(info);
      var performers = this.GetRevocationTaskPerformers(info, parentTask);
      var task = this.CreateRevocationDraftTask(info, null, createAssignments, performers);
      var signatoryName = string.Empty;
      
      if (createAssignments)
        this.FillCancellationAgreementTaskAssignmentText(null, info, task, reason);
      else if (isAnnulment)
        this.FillTaskAnnulmentNoticeText(null, info, fromCounterparty, task, signatoryName);
      else
        this.FillTaskCancellationNoticeText(info, null, fromCounterparty, task, reason, signatoryName);
      
      task.Subject = CutText(task.Subject, task.Info.Properties.Subject.Length);
      task.Save();
      task.Start();

      // Если было создано задание - запишем задачку, чтобы не пересоздавать их на каждый чих.
      if (createAssignments)
        info.RevocationTask = task;

      info.Save();
    }
    
    /// <summary>
    /// Отправить уведомление об аннулировании документа нашей организацией.
    /// </summary>
    /// <param name="parentInfo">Информация о документе обмена.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="reason">Причина аннулирования/отзыва.</param>
    protected virtual void SendRequestedCancellationAgreementNotice(IExchangeDocumentInfo parentInfo,
                                                                    ICancellationAgreement cancellationAgreement,
                                                                    string reason)
    {
      var parentTask = this.GetExchangeDocumentProcessingTask(parentInfo);
      var performers = this.GetRevocationTaskPerformers(parentInfo, parentTask);
      var task = this.CreateRevocationDraftTask(parentInfo, cancellationAgreement, false, performers);

      this.FillTaskRequestedAnnulmentText(parentInfo, task, cancellationAgreement);
      
      this.FillTaskRevocationReason(task, reason);
      task.Subject = CutText(task.Subject, task.Info.Properties.Subject.Length);
      task.Save();
      task.Start();
    }
    
    /// <summary>
    /// Заполнить тему и текст задания на обработку аннулирования.
    /// </summary>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="parentInfo">Информация о документе обмена.</param>
    /// <param name="task">Задача на аннулирование документа.</param>
    /// <param name="reason">Причина аннулирования/отзыва.</param>
    public virtual void FillCancellationAgreementTaskAssignmentText(ICancellationAgreement cancellationAgreement, IExchangeDocumentInfo parentInfo, ITask task, string reason)
    {
      var link = Hyperlinks.Get(parentInfo.Document);
      
      var cancellationAgreementLink = cancellationAgreement != null ? Hyperlinks.Get(cancellationAgreement) : null;
      task.ActiveText = Resources.RevocationNoticeActiveTextInProcessFormat(cancellationAgreementLink, link, reason);
      task.Subject = Exchange.Resources.TaskSubjectTemplateFormat(Exchange.Resources.RevocationTaskThreadSubjectRequestedAnnulment, parentInfo.Document.Name);
      task.ThreadSubject = Exchange.Resources.RevocationTaskThreadSubjectRequestedAnnulment;
    }
    
    /// <summary>
    /// Заполнить тему и текст задания на обработку аннулирования.
    /// </summary>
    /// <param name="info">Информация о документе обмена.</param>
    /// <param name="task">Задача на аннулирование документа.</param>
    /// <param name="reason">Причина аннулирования/отзыва.</param>
    [Obsolete("Используйте метод FillCancellationAgreementTaskAssignmentText с параметром соглашение об аннулировании.")]
    protected virtual void FillTaskAssignmentText(IExchangeDocumentInfo info, ISimpleTask task, string reason)
    {
      this.FillCancellationAgreementTaskAssignmentText(null, info, task, reason);
    }
    
    /// <summary>
    /// Заполнить тему и текст уведомления об аннулировании.
    /// </summary>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="parentInfo">Информация о документе обмена.</param>
    /// <param name="initiatedFromCounterparty">Аннулирование пришло от контрагента.</param>
    /// <param name="task">Задача на аннулирование документа.</param>
    /// <param name="signatoryName">Имя подписанта.</param>
    protected virtual void FillTaskAnnulmentNoticeText(ICancellationAgreement cancellationAgreement,
                                                       IExchangeDocumentInfo parentInfo,
                                                       bool initiatedFromCounterparty,
                                                       ISimpleTask task,
                                                       string signatoryName)
    {
      var link = Hyperlinks.Get(parentInfo.Document);
      var parentDocumentName = parentInfo.Document != null ? parentInfo.Document.Name : string.Empty;
      
      if (initiatedFromCounterparty)
      {
        task.ThreadSubject = Resources.RevocationNoticeThreadSubjectObsolete;
        task.Subject = Sungero.Exchange.Resources.TaskSubjectTemplateFormat(Resources.RevocationNoticeSubjectObsolete,
                                                                            parentDocumentName);
        task.ActiveText = Resources.RevocationNoticeActiveTextObsoleteFormat(link);
      }
      else
      {
        task.ThreadSubject = Resources.RevocationNoticeOurThreadSubjectObsolete;
        if (cancellationAgreement != null)
        {
          task.Subject = Sungero.Exchange.Resources.TaskSubjectTemplateFormat(Resources.RevocationNoticeOurSubjectObsolete,
                                                                              cancellationAgreement.Name);
          task.ActiveText = Resources.RevocationNoticeWithCancellationAgreementOurActiveTextObsoleteFormat(Hyperlinks.Get(cancellationAgreement),
                                                                                                           signatoryName,
                                                                                                           link);
        }
        else
        {
          task.Subject = Sungero.Exchange.Resources.RevocationNoticeOurSubjectObsolete;
          task.ActiveText = Resources.RevocationNoticeWithCancellationAgreementOurActiveTextOldVersionFormat(signatoryName, link);
        }
      }
    }
    
    /// <summary>
    /// Заполнить тему и текст уведомления об аннулировании.
    /// </summary>
    /// <param name="info">Информация о документе обмена.</param>
    /// <param name="fromCounterparty">Аннулирование пришло от контрагента.</param>
    /// <param name="task">Задача на аннулирование документа.</param>
    /// <param name="reason">Причина аннулирования/отзыва.</param>
    [Obsolete("Используйте метод FillTaskAnnulmentNoticeText с параметром соглашение об аннулировании.")]
    protected virtual void FillTaskAnnulmentNoticeText(IExchangeDocumentInfo info, bool fromCounterparty, ISimpleTask task, string reason)
    {
      var link = Hyperlinks.Get(info.Document);
      task.ThreadSubject = fromCounterparty
        ? Resources.RevocationNoticeThreadSubjectObsolete
        : Resources.RevocationNoticeOurThreadSubjectObsolete;
      task.Subject = string.Format(Sungero.Exchange.Resources.TaskSubjectTemplate, task.ThreadSubject, info.Document.Name);
      
      task.ActiveText = fromCounterparty
        ? Resources.RevocationNoticeActiveTextObsoleteFormat(link)
        : Resources.RevocationNoticeOurActiveTextObsoleteFormat(link);
      
      this.FillTaskRevocationReason(task, reason);
    }
    
    /// <summary>
    /// Заполнить тему и текст уведомления об отзыве.
    /// </summary>
    /// <param name="info">Информация о документе обмена.</param>
    /// <param name="fromCounterparty">Аннулирование пришло от контрагента.</param>
    /// <param name="task">Задача на аннулирование документа.</param>
    /// <param name="reason">Причина аннулирования/отзыва.</param>
    [Obsolete("Используйте версию с большим количеством параметров.")]
    protected virtual void FillTaskCancellationNoticeText(IExchangeDocumentInfo info, bool fromCounterparty, ISimpleTask task, string reason)
    {
      this.FillTaskCancellationNoticeText(info, null, fromCounterparty, task, reason, null);
    }
    
    /// <summary>
    /// Заполнить тему и текст уведомления об отзыве.
    /// </summary>
    /// <param name="parentInfo">Информация об основном документе обмена.</param>
    /// <param name="cancellationAgreementInfo">Информация о соглашении об аннулировании.</param>
    /// <param name="fromCounterparty">Аннулирование пришло от контрагента.</param>
    /// <param name="task">Задача на аннулирование документа.</param>
    /// <param name="reason">Причина аннулирования/отзыва.</param>
    /// <param name="signatoryName">ФИО подписавшего соглашение об аннулировании.</param>
    protected virtual void FillTaskCancellationNoticeText(IExchangeDocumentInfo parentInfo,
                                                          IExchangeDocumentInfo cancellationAgreementInfo,
                                                          bool fromCounterparty,
                                                          ISimpleTask task,
                                                          string reason,
                                                          string signatoryName)
    {
      var parentDocumentLink = Hyperlinks.Get(parentInfo.Document);
      task.ThreadSubject = fromCounterparty
        ? Resources.RevocationNoticeSubjectTerminate
        : Resources.RevocationNoticeOurSubjectTerminate;
      task.Subject = string.Format(Sungero.Exchange.Resources.TaskSubjectTemplate, Exchange.Resources.CancellationNoticeDocumentCancelled, parentInfo.Document.Name);
      
      var cancellationAgreementLink = Hyperlinks.Get(cancellationAgreementInfo.Document);
      task.ActiveText = fromCounterparty
        ? Resources.RevocationNoticeActiveTextTerminateFormat(cancellationAgreementLink, signatoryName, reason, parentDocumentLink)
        : Resources.RevocationNoticeOurActiveTextTerminateFormat(cancellationAgreementLink, signatoryName, reason, parentDocumentLink);
    }
    
    /// <summary>
    /// Заполнить тему и текст уведомления о запросе на аннулирование.
    /// </summary>
    /// <param name="info">Информация о документе обмена.</param>
    /// <param name="task">Задача на аннулирование документа.</param>
    [Obsolete("Используйте версию с большим количеством параметров.")]
    protected virtual void FillTaskRequestedAnnulmentText(IExchangeDocumentInfo info, ISimpleTask task)
    {
      this.FillTaskRequestedAnnulmentText(info, task, null);
    }
    
    /// <summary>
    /// Заполнить тему и текст уведомления о запросе на аннулирование.
    /// </summary>
    /// <param name="info">Информация о документе обмена.</param>
    /// <param name="task">Задача на аннулирование документа.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    protected virtual void FillTaskRequestedAnnulmentText(IExchangeDocumentInfo info, ISimpleTask task, ICancellationAgreement cancellationAgreement)
    {
      var link = Hyperlinks.Get(info.Document);
      var cancellationAgreementLink = Hyperlinks.Get(cancellationAgreement);
      
      task.Subject = Resources.RequestedAnnulmentNoticeSubjectInProcessFormat(info.Document.Name);
      task.ThreadSubject = Sungero.Exchange.Resources.RevocationTaskThreadSubjectRequestedAnnulment;
      task.ActiveText = Resources.RevocationNoticeActiveTextRequestedAnnulmentFormat(cancellationAgreementLink, link);
    }
    
    /// <summary>
    /// Заполнить тему и текст уведомления об отказе в аннулировании.
    /// </summary>
    /// <param name="parentInfo">Информация о документе обмена.</param>
    /// <param name="task">Задача на отправку уведомления.</param>
    /// <param name="signatoryName">Имя подписанта.</param>
    /// <param name="initiatedFromCounterparty">True - отказ отправлен от контрагента, False - от нашей организации.</param>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="rejectReason">Причина отказа.</param>
    protected virtual void FillTaskRejectedAnnulmentText(IExchangeDocumentInfo parentInfo, ISimpleTask task, string signatoryName,
                                                         bool initiatedFromCounterparty, ICancellationAgreement cancellationAgreement, string rejectReason)
    {
      var link = Hyperlinks.Get(parentInfo.Document);
      var cancellationAgreementLink = Hyperlinks.Get(cancellationAgreement);
      task.ThreadSubject = Resources.AnnulmentRejectedThreadSubject;
      task.Subject = Sungero.Exchange.Resources.TaskSubjectTemplateFormat(task.ThreadSubject, parentInfo.Document.Name);

      task.ActiveText = initiatedFromCounterparty ? Resources.AnnulmentRejectedOurNoticeActiveTextObsoleteFormat(cancellationAgreementLink, signatoryName, rejectReason, link)
        : Resources.AnnulmentRejectedActiveTextFormat(cancellationAgreementLink, signatoryName, rejectReason, link);
    }
    
    /// <summary>
    /// Заполнить причину аннулирования/отзыва.
    /// </summary>
    /// <param name="task">Задача на аннулирование документа.</param>
    /// <param name="reason">Причина аннулирования/отзыва.</param>
    protected virtual void FillTaskRevocationReason(ISimpleTask task, string reason)
    {
      if (!string.IsNullOrWhiteSpace(reason))
      {
        task.ActiveText += Environment.NewLine;
        task.ActiveText += Resources.RevocationNoticeReasonFormat(reason);
      }
    }

    /// <summary>
    /// Получить исполнителей задачи об аннулировании/отзыве контрагентом.
    /// </summary>
    /// <param name="info">Информация о документе.</param>
    /// <param name="parentTask">Основная задача.</param>
    /// <returns>Исполнители.</returns>
    protected virtual List<IRecipient> GetRevocationTaskPerformers(IExchangeDocumentInfo info, IExchangeDocumentProcessingTask parentTask)
    {
      var performers = new List<IRecipient>();
      if (parentTask != null)
      {
        // Ответственный за ящик.
        performers.Add(ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(info.Box,
                                                                                                  info.Counterparty,
                                                                                                  new List<IExchangeDocumentInfo>() { info }));
        
        // Исполнители заданий на обработку.
        var assignmentPerformers = ExchangeDocumentProcessingAssignments.GetAll(a => Equals(a.Task, parentTask)).Select(a => a.Performer);
        performers.AddRange(assignmentPerformers);
        
        // Ответственный за договорной документ.
        var contract = Contracts.ContractualDocuments.As(info.Document);
        if (contract != null && contract.ResponsibleEmployee != null)
          performers.Add(contract.ResponsibleEmployee);
      }
      else
      {
        // Ответственный за возврат из выдачи.
        var tracking = info.Document.Tracking.FirstOrDefault(t => t.ExternalLinkId == info.Id);
        if (tracking != null && tracking.DeliveredTo != null)
          performers.Add(tracking.DeliveredTo);

        // Кто подготовил документ.
        if (info.Document.PreparedBy != null)
          performers.Add(info.Document.PreparedBy);

        // Ответственный за договорной документ (или автор).
        var contract = Contracts.ContractualDocuments.As(info.Document);
        if (contract != null)
        {
          if (contract.ResponsibleEmployee != null)
            performers.Add(contract.ResponsibleEmployee);
          else
            performers.Add(contract.Author);
        }
      }
      
      return performers.Where(p => p != null).Distinct().ToList();
    }
    
    /// <summary>
    /// Обработать сообщение об аннулировании или отзыве документа.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItems">Все элементы очереди.
    /// Параметр не используется в базовой функции, оставлен для совместимости.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    [Obsolete("Метод переименован в ProcessCancellationAgreement")]
    public virtual bool ProcessAnnulmentOrCancellation(NpoComputer.DCX.Common.IMessage message, List<IMessageQueueItem> queueItems,
                                                       ICounterparty sender, bool isIncomingMessage, IBoxBase box)
    {
      return this.ProcessCancellationAgreement(message, queueItems, sender, isIncomingMessage, box);
    }
    
    #endregion

    #region Обработка ИОП

    /// <summary>
    /// Проверить сообщение на наличие подтверждений получения\отправки.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="counterpartyId">Контрагент, от которого получено сообщение. Ожидается сервисный контрагент.</param>
    /// <param name="box">Ящик, через который получено.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessInvoiceConfirmation(IMessage message, IMessageQueueItem queueItem, string counterpartyId, IBusinessUnitBox box)
    {
      this.LogDebugFormat(message, queueItem, box, "Execute ProcessInvoiceConfirmation.");
      foreach (var confirmation in this.GetInvoiceConfirmationReglamentDocuments(message))
      {
        var info = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, confirmation.RootServiceEntityId);
        if (info != null && !info.ServiceDocuments.Any(d => d.DocumentId == confirmation.ServiceEntityId))
        {
          var document = info.ServiceDocuments.AddNew();
          if (confirmation.ParentServiceEntityId == info.ServiceDocumentId)
            document.DocumentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IConfirmation;
          else
          {
            switch (confirmation.ParentReglamentDocumentType)
            {
              case ReglamentDocumentType.AmendmentRequest:
              case ReglamentDocumentType.InvoiceAmendmentRequest:
              case ReglamentDocumentType.Rejection:
                document.DocumentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IRjConfirmation;
                break;
              case ReglamentDocumentType.InvoiceReceipt:
              case ReglamentDocumentType.Receipt:
                document.DocumentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IRConfirmation;
                break;
              default:
                document.DocumentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.BTConfirmation;
                break;
            }
          }
          
          document.DocumentId = confirmation.ServiceEntityId;
          document.ParentDocumentId = confirmation.ParentServiceEntityId;
          document.CounterpartyId = counterpartyId;
          document.Date = ToTenantTime(confirmation.DateTime ?? message.TimeStamp);
          document.Body = confirmation.Content;
          document.Sign = message.Signatures.Single(s => s.DocumentId == document.DocumentId).Content;
          info.Save();
        }
        
        if (info == null && this.CanProcessMessageLater(message, queueItem, box, confirmation.RootServiceEntityId))
        {
          this.LogDebugFormat(message, confirmation, "Document info not found for received InvoiceConfirmation: RootServiceEntityId: '{0}'.", confirmation.RootServiceEntityId);
          return false;
        }
      }

      return true;
    }
    
    /// <summary>
    /// Получить подтверждения доставки документов из сообщения.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <returns>Подтверждения доставки документов.</returns>
    public virtual List<NpoComputer.DCX.Common.IReglamentDocument> GetInvoiceConfirmationReglamentDocuments(NpoComputer.DCX.Common.IMessage message)
    {
      return message.ReglamentDocuments.Where(d => d.DocumentType == ReglamentDocumentType.InvoiceConfirmation).ToList();
    }

    /// <summary>
    /// Обработка ИОП.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessReceiptNotice(IMessage message, IMessageQueueItem queueItem, ICounterparty sender,
                                                bool isIncomingMessage, IBusinessUnitBox businessUnitBox)
    {
      this.LogDebugFormat(message, queueItem, businessUnitBox, "Execute ProcessReceiptNotice.");
      foreach (var document in this.GetReceiptReglamentDocuments(message))
      {
        var exchangeDocumentInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(businessUnitBox, document.RootServiceEntityId);
        if (exchangeDocumentInfo != null)
        {
          if (!exchangeDocumentInfo.ServiceDocuments.Any(d => d.DocumentId == document.ServiceEntityId) ||
              businessUnitBox.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis)
          {
            var isReceiptOnSellerTitle = document.ParentServiceEntityId == document.RootServiceEntityId;
            var isReceiptOnBuyerTitle = document.ParentServiceEntityId == exchangeDocumentInfo.ExternalBuyerTitleId;
            
            var parent = exchangeDocumentInfo.ServiceDocuments.SingleOrDefault(d => d.DocumentId == document.ParentServiceEntityId);
            if (parent == null && !isReceiptOnSellerTitle && !isReceiptOnBuyerTitle)
            {
              this.LogDebugFormat(message, document, "Service document not found for received Receipt: ParentServiceEntityId: '{0}'.", document.ParentServiceEntityId);
              return false;
            }
            
            Enumeration? documentType = null;
            if (document.DocumentType == ReglamentDocumentType.InvoiceReceipt)
            {
              // ИОП на титул продавца или титул покупателя.
              if (parent == null && (isReceiptOnSellerTitle || isReceiptOnBuyerTitle))
                documentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IReceipt;
              
              // ИОП на служебный документ.
              if (parent != null)
              {
                if (parent.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IReject)
                  documentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IRReceipt;
                if (parent.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IConfirmation)
                  documentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.ICReceipt;
                if (parent.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IRConfirmation)
                  documentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IRCReceipt;
                if (parent.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.NoteReceipt)
                  documentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.RNoteReceipt;
              }
            }
            
            if (document.DocumentType == ReglamentDocumentType.Receipt)
            {
              var parentServiceEntity = exchangeDocumentInfo.ServiceDocuments.FirstOrDefault(d => d.DocumentId == document.ParentServiceEntityId);
              if (parentServiceEntity != null)
              {
                documentType = (parentServiceEntity.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IConfirmation) ?
                  Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.ICReceipt :
                  Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Receipt;
              }
              else
                documentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Receipt;
            }
            
            bool isNewServiceDocument = false;
            var serviceDoc =
              exchangeDocumentInfo.ServiceDocuments.FirstOrDefault(d => d.DocumentType == documentType &&
                                                                   d.ParentDocumentId == document.ParentServiceEntityId);
            if (serviceDoc == null)
            {
              serviceDoc = exchangeDocumentInfo.ServiceDocuments.AddNew();
              isNewServiceDocument = true;
            }
            
            serviceDoc.DocumentId = document.ServiceEntityId;
            serviceDoc.ParentDocumentId = document.ParentServiceEntityId;
            serviceDoc.CounterpartyId = isIncomingMessage ? message.Sender.Organization.OrganizationId : message.Receiver.Organization.OrganizationId;
            serviceDoc.DocumentType = documentType;
            serviceDoc.Date = ToTenantTime(document.DateTime ?? message.TimeStamp);
            serviceDoc.Body = document.Content;
            var signature = message.Signatures.Single(s => s.DocumentId == serviceDoc.DocumentId);
            serviceDoc.Sign = signature.Content;
            serviceDoc.FormalizedPoAUnifiedRegNo = signature.FormalizedPoAUnifiedRegNumber;
            serviceDoc.Certificate = null;

            if (isReceiptOnSellerTitle)
              exchangeDocumentInfo.DeliveryConfirmationStatus = Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Processed;
            if (isReceiptOnBuyerTitle)
              exchangeDocumentInfo.BuyerDeliveryConfirmationStatus = Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Processed;

            exchangeDocumentInfo.Save();
            
            // Если получили ИОП на сам документ - надо записать в историю.
            if (serviceDoc.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IReceipt ||
                serviceDoc.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Receipt)
            {
              exchangeDocumentInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(businessUnitBox, document.ParentServiceEntityId);
              if (exchangeDocumentInfo != null && exchangeDocumentInfo.Document != null && isNewServiceDocument)
              {
                var senderName = string.Empty;
                if (sender != null)
                {
                  senderName = sender.Name;
                  if (exchangeDocumentInfo.CounterpartyDepartmentBox != null)
                    senderName = Exchange.PublicFunctions.ExchangeDocumentInfo.GetCompanyNameWithDepartment(exchangeDocumentInfo, senderName);
                }
                else if (message.Sender != null && message.Sender.Organization != null)
                {
                  senderName = message.Sender.Organization.Name;
                  if (exchangeDocumentInfo.CounterpartyDepartmentBox != null)
                    senderName = Exchange.PublicFunctions.ExchangeDocumentInfo.GetCompanyNameWithDepartment(exchangeDocumentInfo, senderName);
                }
                else
                  senderName = Sungero.Exchange.Resources.NoneCounterparty;

                var historyComment = Functions.Module.GetExchangeDocumentHistoryComment(senderName, businessUnitBox.ExchangeService.Name);
                
                var detailedOperation = new Enumeration(isIncomingMessage ? Constants.Module.Exchange.GetReadMark : Constants.Module.Exchange.SendReadMark);
                var sentVersion = exchangeDocumentInfo.Document.Versions.FirstOrDefault(x => x.Id == exchangeDocumentInfo.VersionId);
                exchangeDocumentInfo.Document.History.Write(detailedOperation, detailedOperation, historyComment, sentVersion.Number);
                exchangeDocumentInfo.Save();
              }
            }
          }
        }
        
        if (exchangeDocumentInfo == null && this.CanProcessMessageLater(message, queueItem, businessUnitBox, document.RootServiceEntityId))
        {
          this.LogDebugFormat(message, document, "Document info not found for received Receipt: ParentServiceEntityId: '{0}'.", document.ParentServiceEntityId);
          return false;
        }
      }
      
      return true;
    }
    
    /// <summary>
    /// Получить ИОП-ы из сообщения.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <returns>ИОП-ы.</returns>
    public virtual List<NpoComputer.DCX.Common.IReglamentDocument> GetReceiptReglamentDocuments(NpoComputer.DCX.Common.IMessage message)
    {
      return message.ReglamentDocuments
        .Where(d => d.DocumentType == ReglamentDocumentType.Receipt || d.DocumentType == ReglamentDocumentType.InvoiceReceipt)
        .ToList();
    }
    
    /// <summary>
    /// Обработка УОП.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessNoteReceipt(IMessage message, IMessageQueueItem queueItem, ICounterparty sender,
                                              bool isIncomingMessage, IBusinessUnitBox businessUnitBox)
    {
      this.LogDebugFormat(message, queueItem, businessUnitBox, "Execute ProcessNoteReceipt.");
      foreach (var document in this.GetNotificationReceiptReglamentDocuments(message))
      {
        var exchangeDocumentInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(businessUnitBox, document.RootServiceEntityId);
        if (exchangeDocumentInfo != null)
        {
          var documentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.NoteReceipt;
          var serviceDoc = exchangeDocumentInfo.ServiceDocuments.FirstOrDefault(d => d.DocumentType == documentType) ?? exchangeDocumentInfo.ServiceDocuments.AddNew();
          serviceDoc.DocumentId = document.ServiceEntityId;
          serviceDoc.ParentDocumentId = document.ParentServiceEntityId;
          serviceDoc.CounterpartyId = isIncomingMessage ? message.Sender.Organization.OrganizationId : message.Receiver.Organization.OrganizationId;
          serviceDoc.DocumentType = documentType;
          serviceDoc.Date = ToTenantTime(document.DateTime ?? message.TimeStamp);
          serviceDoc.Body = document.Content;
          serviceDoc.Sign = message.Signatures.Single(s => s.DocumentId == serviceDoc.DocumentId).Content;

          exchangeDocumentInfo.Save();
          
          // Записать в историю.
          this.ProcessRecordHistory(document, isIncomingMessage, sender, documentType, businessUnitBox);
        }
        
        if (exchangeDocumentInfo == null && this.CanProcessMessageLater(message, queueItem, businessUnitBox, document.RootServiceEntityId))
        {
          this.LogDebugFormat(message, document, "Document info not found for received NotificationReceipt: RootServiceEntityId: '{0}'.", document.RootServiceEntityId);
          return false;
        }
      }
      
      return true;
    }
    
    /// <summary>
    /// Получить УОП-ы из сообщения.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <returns>УОП-ы.</returns>
    public virtual List<NpoComputer.DCX.Common.IReglamentDocument> GetNotificationReceiptReglamentDocuments(NpoComputer.DCX.Common.IMessage message)
    {
      return message.ReglamentDocuments.Where(d => d.DocumentType == ReglamentDocumentType.NotificationReceipt).ToList();
    }
    
    /// <summary>
    /// Обработка ИОП на УОП.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Элемент очереди.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="businessUnitBox">Абонентский ящик.</param>
    /// <returns>Признак успешности обработки сообщения.</returns>
    protected virtual bool ProcessReceiptOfNoteReceipt(IMessage message, IMessageQueueItem queueItem, ICounterparty sender,
                                                       bool isIncomingMessage, IBusinessUnitBox businessUnitBox)
    {
      this.LogDebugFormat(message, queueItem, businessUnitBox, "Execute ProcessReceiptOfNoteReceipt");
      foreach (var document in this.GetNotificationOnReceiptOfNotificationReceiptReglamentDocuments(message))
      {
        var exchangeDocumentInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(businessUnitBox, document.RootServiceEntityId);
        if (exchangeDocumentInfo != null)
        {
          var documentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.RNoteReceipt;
          var serviceDoc = exchangeDocumentInfo.ServiceDocuments.FirstOrDefault(d => d.DocumentType == documentType) ?? exchangeDocumentInfo.ServiceDocuments.AddNew();
          serviceDoc.DocumentId = document.ServiceEntityId;
          serviceDoc.ParentDocumentId = document.ParentServiceEntityId;
          serviceDoc.CounterpartyId = isIncomingMessage ? message.Sender.Organization.OrganizationId : message.Receiver.Organization.OrganizationId;
          serviceDoc.DocumentType = documentType;
          serviceDoc.Date = ToTenantTime(document.DateTime ?? message.TimeStamp);
          serviceDoc.Body = document.Content;
          var signature = message.Signatures.Single(s => s.DocumentId == serviceDoc.DocumentId);
          serviceDoc.Sign = signature.Content;
          serviceDoc.FormalizedPoAUnifiedRegNo = signature.FormalizedPoAUnifiedRegNumber;
          serviceDoc.GeneratedName = document.FileName;
          serviceDoc.StageId = document.DocflowStageId;
          exchangeDocumentInfo.Save();
          
          // Записать в историю.
          this.ProcessRecordHistory(document, isIncomingMessage, sender, documentType, businessUnitBox);
        }
        
        if (exchangeDocumentInfo == null)
        {
          if (this.CanProcessMessageLater(message, queueItem, businessUnitBox, document.RootServiceEntityId))
          {
            this.LogDebugFormat(message, document, "Acknowledgment receipt not found for received receipt notification: RootServiceEntityId: '{0}'.", document.RootServiceEntityId);
            return false;
          }
        }
      }
      
      return true;
    }
    
    /// <summary>
    /// Получить ИОП-ы на УОП-ы из сообщения.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <returns>ИОП-ы на УОП-ы.</returns>
    public virtual List<NpoComputer.DCX.Common.IReglamentDocument> GetNotificationOnReceiptOfNotificationReceiptReglamentDocuments(NpoComputer.DCX.Common.IMessage message)
    {
      return message.ReglamentDocuments
        .Where(d => d.DocumentType == ReglamentDocumentType.NotificationOnReceiptOfNotificationReceipt)
        .ToList();
    }
    
    /// <summary>
    /// Записать в историю информацию обработки УОП/ИОП на УОП.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="sender">Контрагент.</param>
    /// <param name="documentType">Тип документа.</param>
    /// <param name="businessUnitBox">Абонентский ящик НОР.</param>
    protected virtual void ProcessRecordHistory(NpoComputer.DCX.Common.IReglamentDocument document, bool isIncomingMessage, ICounterparty sender,
                                                Sungero.Core.Enumeration documentType, IBusinessUnitBox businessUnitBox)
    {
      var exchangeDocumentInfo = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(businessUnitBox, document.ParentServiceEntityId);
      if (exchangeDocumentInfo != null && exchangeDocumentInfo.Document != null &&
          exchangeDocumentInfo.DeliveryConfirmationStatus != Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Sent)
      {
        var senderName = sender.Name;
        if (exchangeDocumentInfo.CounterpartyDepartmentBox != null)
          senderName = Exchange.PublicFunctions.ExchangeDocumentInfo.GetCompanyNameWithDepartment(exchangeDocumentInfo, senderName);
        var historyComment = Functions.Module.GetExchangeDocumentHistoryComment(senderName, businessUnitBox.ExchangeService.Name);
        var operation = new Enumeration(isIncomingMessage ? Constants.Module.Exchange.GetRNoteReceiptReadMark : Constants.Module.Exchange.SendRNoteReceiptReadMark);
        
        if (documentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.NoteReceipt)
          operation = new Enumeration(isIncomingMessage ? Constants.Module.Exchange.GetNoteReceiptReadMark : Constants.Module.Exchange.SendNoteReceiptReadMark);
        
        var detailedOperation = new Enumeration(isIncomingMessage ? Constants.Module.Exchange.GetReadMark : Constants.Module.Exchange.SendReadMark);
        
        var sentVersion = exchangeDocumentInfo.Document.Versions.FirstOrDefault(x => x.Id == exchangeDocumentInfo.VersionId);
        exchangeDocumentInfo.Document.History.Write(operation, detailedOperation, historyComment, sentVersion.Number);
        
        if (this.FixReceiptNotificationForSbis(exchangeDocumentInfo))
          exchangeDocumentInfo.DeliveryConfirmationStatus = Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Sent;
        exchangeDocumentInfo.Save();
      }
    }

    /// <summary>
    /// Проверить признак получения ИОПа и отправки УОПа для Sbis.
    /// </summary>
    /// <param name="info">Информация о документе обмена.</param>
    /// <returns>Признак получения ИОПа и отправки УОПа для Sbis.</returns>
    [Obsolete("Метод устарел в связи с отказом СБИСа от УОПа после выхода регламента 14Н.")]
    [Remote]
    public virtual bool FixReceiptNotificationForSbis(Exchange.IExchangeDocumentInfo info)
    {
      if (info.RootBox.ExchangeService.ExchangeProvider != ExchangeCore.ExchangeService.ExchangeProvider.Sbis)
      {
        return false;
      }
      
      var docs = ExchangeDocumentInfos.GetAll()
        .Where(x => Equals(x.RootBox, info.RootBox) && x.Document != null &&
               x.ServiceDocuments.Any(d => (d.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IReceipt ||
                                            d.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Receipt) && d.Date != null) &&
               x.ServiceDocuments.Any(d => d.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.NoteReceipt && d.Date != null));
      return docs != null;
    }
    
    #endregion
    
    #region Обновление статусов по полученным ответам

    /// <summary>
    /// Обработать документ как отправленный - как из RX, так и из веба.
    /// </summary>
    /// <param name="info">Сведения о документе обмена.</param>
    /// <param name="document">Документ.</param>
    /// <param name="receiver">Контрагент.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="signStatus">Текущий статус документа - не подписывается, ожидает подписи, подписан двумя сторонами.</param>
    public static void MarkDocumentAsSended(IExchangeDocumentInfo info, IOfficialDocument document, ICounterparty receiver,
                                            bool isIncomingMessage, IBoxBase box, NpoComputer.DCX.Common.SignStatus? signStatus)
    {
      if (!isIncomingMessage)
        using (Sungero.Core.CultureInfoExtensions.SwitchTo(TenantInfo.Culture))
      {
        AddTrackingRecordInfo(info, document, signStatus != SignStatus.None);
      }
      
      if (signStatus == SignStatus.Waiting)
      {
        if (isIncomingMessage)
        {
          document.ExchangeState = Docflow.OfficialDocument.ExchangeState.SignRequired;
          if (info != null)
            info.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.SignRequired;
        }
        else
        {
          document.ExchangeState = Docflow.OfficialDocument.ExchangeState.SignAwaited;
          if (info != null)
            info.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.SignAwaited;
        }
      }
      else if (signStatus == SignStatus.Signed)
      {
        document.ExchangeState = Docflow.OfficialDocument.ExchangeState.Signed;
        if (info != null)
        {
          info.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.Signed;
        }
      }
      else
      {
        if (isIncomingMessage)
        {
          document.ExchangeState = Docflow.OfficialDocument.ExchangeState.Received;
          if (info != null)
            info.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.Received;
        }
        else
        {
          document.ExchangeState = Docflow.OfficialDocument.ExchangeState.Sent;
          if (info != null)
            info.ExchangeState = Exchange.ExchangeDocumentInfo.ExchangeState.Sent;
        }
      }
      
      if (!isIncomingMessage)
      {
        var sendDocument = new Enumeration(Constants.Module.Exchange.SendDocument);
        var exchangeServiceName = ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box).Name;
        var receiverName = receiver.Name;
        if (info != null && info.CounterpartyDepartmentBox != null)
          receiverName = Exchange.PublicFunctions.ExchangeDocumentInfo.GetCompanyNameWithDepartment(info, receiverName);
        var historyComment = Functions.Module.GetExchangeDocumentHistoryComment(receiverName, exchangeServiceName);
        document.History.Write(sendDocument, sendDocument, historyComment, document.LastVersion.Number);
      }
      
      document.Save();
      info.Save();
    }

    /// <summary>
    /// Получить комментарий для записи в историю документа.
    /// </summary>
    /// <param name="counterpartyName">Имя контрагента.</param>
    /// <param name="exchangeServiceName">Имя сервиса обмена.</param>
    /// <returns>Комментарий в истории документа.</returns>
    public virtual string GetExchangeDocumentHistoryComment(string counterpartyName, string exchangeServiceName)
    {
      var maxLength = Docflow.PublicConstants.OfficialDocument.DocumentHistoryCommentMaxLength - exchangeServiceName.Length - 1;
      var counterpartyCutName = Docflow.PublicFunctions.Module.CutText(counterpartyName, maxLength).Trim('\"');
      return string.Format("{0}|{1}", counterpartyCutName, exchangeServiceName);
    }
    
    /// <summary>
    /// Добавить запись выдачи в документе и установить статус согласования с КА.
    /// </summary>
    /// <param name="info">Информация о документе в сервисе обмена.</param>
    /// <param name="document">Документ.</param>
    /// <param name="needSign">Признак требования подписания.</param>
    private static void AddTrackingRecordInfo(IExchangeDocumentInfo info, IOfficialDocument document, bool needSign)
    {
      var tracking = document.Tracking.AddNew();
      if (needSign)
      {
        document.ExternalApprovalState = Sungero.Docflow.OfficialDocument.ExternalApprovalState.OnApproval;
        document.ExchangeState = Docflow.OfficialDocument.ExchangeState.SignAwaited;
        tracking.Action = Docflow.OfficialDocumentTracking.Action.Endorsement;
      }
      else
      {
        tracking.Action = Docflow.OfficialDocumentTracking.Action.Sending;
        document.ExchangeState = Docflow.OfficialDocument.ExchangeState.Sent;
        tracking.ReturnDeadline = null;
      }
      
      tracking.IsOriginal = true;
      tracking.DeliveredTo = Company.Employees.Current ??
        ExchangeCore.PublicFunctions.BoxBase.Remote.GetExchangeDocumentResponsible(info.Box, info.Counterparty, new List<IExchangeDocumentInfo>() { info });
      tracking.Note = Exchange.Resources.SendToCounterpartyNoteFormat(ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(info.Box));
      tracking.ExternalLinkId = info.Id;
    }
    
    #endregion
    
    #endregion
    
    #region Служебные методы обработки сообщений из сервиса обмена

    public static NpoComputer.DCX.ClientApi.Client GetClient(IBusinessUnitBox box)
    {
      var client = ExchangeCore.PublicFunctions.BusinessUnitBox.GetPublicClient(box) as NpoComputer.DCX.ClientApi.Client;
      if (client == null)
        throw AppliedCodeException.Create("Ошибка при создании клиента.");
      
      return client;
    }

    /// <summary>
    /// Проверка необходимости сохранения сообщения в очереди.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="queueItem">Текущий элемент очереди, чтобы игнорировать его при поиске других элементов.</param>
    /// <param name="box">Головной ящик.</param>
    /// <param name="rootServiceDocumentId">ИД основного документа.</param>
    /// <returns>True, если сообщение еще можно обработать. False, если сообщение уже не нужно.</returns>
    protected virtual bool CanProcessMessageLater(IMessage message, IMessageQueueItem queueItem, IBusinessUnitBox box, string rootServiceDocumentId)
    {
      // Если документ уже аннулировали, то принимать ничего уже не надо.
      var root = message.PrimaryDocuments.FirstOrDefault(d => d.ServiceEntityId == rootServiceDocumentId);
      if (root != null && root.RevocationStatus == RevocationStatus.RevocationAccepted)
        return false;
      
      // Если документ не лежит в очереди - сообщение больше не нужно.
      if (!ExchangeCore.MessageQueueItems.GetAll(q => Equals(q.RootBox, box) &&
                                                 !Equals(q, queueItem) &&
                                                 q.ProcessingStatus != ExchangeCore.MessageQueueItem.ProcessingStatus.Processed &&
                                                 q.Documents.Any(d => d.ExternalId == rootServiceDocumentId)).Any())
        return false;
      
      return true;
    }
    
    /// <summary>
    /// Получить ссылку на документ в вебе.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>Ссылка на документ в вебе.</returns>
    [Public, Remote]
    public static string GetDocumentHyperlink(Docflow.IOfficialDocument document)
    {
      var docInfo = Functions.ExchangeDocumentInfo.GetLastDocumentInfo(document);
      if (docInfo == null)
        return string.Empty;
      
      try
      {
        var client = GetClient(docInfo.RootBox);
        return client.GetDocumentUri(docInfo.ServiceMessageId, docInfo.ServiceDocumentId).ToString();
      }
      catch (AppliedCodeException)
      {
        throw;
      }
      catch (Exception ex)
      {
        Log.Exception(ex);
      }
      return string.Empty;
    }
    
    /// <summary>
    /// Получить ссылку на документ в вебе.
    /// </summary>
    /// <param name="messageQueueItem">Элемент очереди сообщений.</param>
    /// <returns>Ссылка на документ в вебе.</returns>
    [Public, Remote]
    public static string GetDocumentHyperlink(IMessageQueueItem messageQueueItem)
    {
      try
      {
        var client = GetClient(messageQueueItem.RootBox);

        // Для формирования ссылки СБИС достаточно ИД сообщения.
        if (Equals(messageQueueItem.RootBox.ExchangeService.ExchangeProvider, ExchangeCore.ExchangeService.ExchangeProvider.Sbis))
          return client.GetDocumentUri(string.Empty, messageQueueItem.ExternalId).ToString();

        // Для регламентных документов запрашиваем информацию из сервиса.
        var message = client.GetMessage(messageQueueItem.ExternalId);
        var document = message.PrimaryDocuments.FirstOrDefault();
        if (document != null)
        {
          // Формирование ссылки для уведомления об аннулировании.
          if (document.DocumentType == NpoComputer.DCX.Common.DocumentType.RevocationOffer &&
              message.ParentServiceMessageId != null &&
              document.ParentServiceEntityId != null)
            return client.GetDocumentUri(message.ParentServiceMessageId, document.ParentServiceEntityId).ToString();
          
          return client.GetDocumentUri(document.ServiceMessageId, document.ServiceEntityId).ToString();
        }
        else
        {
          // Если в сообщении только сервисные документы, ищем основной документ и его сообщение.
          var reglamentDocument = message.ReglamentDocuments.FirstOrDefault();
          return reglamentDocument != null ? client.GetDocumentUri(message.ParentServiceMessageId, reglamentDocument.RootServiceEntityId).ToString() : string.Empty;
        }
      }
      catch (AppliedCodeException)
      {
        throw;
      }
      catch (Exception ex)
      {
        Log.Exception(ex);
      }
      
      return string.Empty;
    }

    /// <summary>
    /// Привести дату к тенантному времени.
    /// </summary>
    /// <param name="datetime">Дата, пришедшая из МКДО.</param>
    /// <returns>Дата во времени тенанта.</returns>
    private static DateTime ToTenantTime(DateTime datetime)
    {
      return Docflow.PublicFunctions.Module.ToTenantTime(datetime);
    }
    
    /// <summary>
    /// Получить список поддерживаемых основных типов документов.
    /// </summary>
    /// <returns>Список типов.</returns>
    protected virtual List<DocumentType> GetSupportedPrimaryDocumentTypes()
    {
      return new List<NpoComputer.DCX.Common.DocumentType>()
      {
        DocumentType.Nonformalized,
        DocumentType.Waybill,
        DocumentType.Invoice,
        DocumentType.InvoiceCorrection,
        DocumentType.InvoiceCorrectionRevision,
        DocumentType.InvoiceRevision,
        DocumentType.Act,
        DocumentType.GeneralTransferSchfSeller,
        DocumentType.GeneralTransferSchfRevisionSeller,
        DocumentType.GeneralTransferSchfDopSeller,
        DocumentType.GeneralTransferSchfDopRevisionSeller,
        DocumentType.GeneralTransferSchfDopCorrectionSeller,
        DocumentType.GeneralTransferSchfDopCorrectionRevisionSeller,
        DocumentType.GeneralTransferSchfCorrectionSeller,
        DocumentType.GeneralTransferSchfCorrectionRevisionSeller,
        DocumentType.GeneralTransferDopSeller,
        DocumentType.GeneralTransferDopRevisionSeller,
        DocumentType.GeneralTransferDopCorrectionSeller,
        DocumentType.GeneralTransferDopCorrectionRevisionSeller,
        DocumentType.WorksTransferSeller,
        DocumentType.WorksTransferRevisionSeller,
        DocumentType.GoodsTransferSeller,
        DocumentType.GoodsTransferRevisionSeller,
        DocumentType.RevocationOffer
      };
    }
    
    /// <summary>
    /// Получить список поддерживаемых регламентных типов документов.
    /// </summary>
    /// <returns>Список типов.</returns>
    protected virtual List<ReglamentDocumentType> GetSupportedReglamentDocumentTypes()
    {
      return new List<NpoComputer.DCX.Common.ReglamentDocumentType>()
      {
        ReglamentDocumentType.ActClientTitle,
        ReglamentDocumentType.WaybillBuyerTitle,
        ReglamentDocumentType.GeneralTransferBuyer,
        ReglamentDocumentType.GeneralTransferCorrectionBuyer,
        ReglamentDocumentType.GoodsTransferBuyer,
        ReglamentDocumentType.WorksTransferBuyer
      };
    }
    
    /// <summary>
    /// Получить список поддерживаемых регламентных типов документов.
    /// </summary>
    /// <returns>Список типов.</returns>
    protected virtual List<ReglamentDocumentType> GetSupportedServiceDocumentTypes()
    {
      return new List<NpoComputer.DCX.Common.ReglamentDocumentType>()
      {
        ReglamentDocumentType.AmendmentRequest,
        ReglamentDocumentType.InvoiceAmendmentRequest,
        ReglamentDocumentType.Rejection,
        ReglamentDocumentType.Receipt,
        ReglamentDocumentType.InvoiceReceipt,
        ReglamentDocumentType.InvoiceConfirmation,
        ReglamentDocumentType.NotificationReceipt,
        ReglamentDocumentType.NotificationOnReceiptOfNotificationReceipt
      };
    }
    
    private static string GetAttributeValueByName(System.Xml.Linq.XElement element, string attributeName)
    {
      var attribute = element.Attribute(attributeName);
      return attribute == null ? string.Empty : attribute.Value;
    }

    /// <summary>
    /// Получить или создать приложение-обработчик для документа.
    /// </summary>
    /// <param name="documentName">Имя документа.</param>
    /// <returns>Приложение-обработчик.</returns>
    [Public, Remote(IsPure = true)]
    public static Sungero.Content.IAssociatedApplication GetOrCreateAssociatedApplicationByDocumentName(string documentName)
    {
      // Определить приложение-обработчик. Если его нет - создать.
      var documentFullName = CommonLibrary.FileUtils.NormalizeFileName(documentName);
      var ext = System.IO.Path.GetExtension(documentFullName).TrimStart('.').ToLower();
      var application = Content.AssociatedApplications.GetByExtension(ext);
      
      // Если разрешения у файла нет, то использовать unknown.
      if (string.IsNullOrWhiteSpace(ext))
        application = Sungero.Content.AssociatedApplications.GetAll()
          .SingleOrDefault(x => x.Sid == Docflow.PublicConstants.Module.UnknownAppSid);
      if (application == null)
      {
        application = Content.AssociatedApplications.Create();
        application.Extension = ext;
        using (Sungero.Core.CultureInfoExtensions.SwitchTo(TenantInfo.Culture))
          application.Name = Resources.AssociatedApplicationFormat(ext);
        application.MonitoringType = Content.AssociatedApplication.MonitoringType.ByProcessAndWindow;
        application.FilesType = Content.FilesTypes.GetAll().FirstOrDefault(f => f.Name == Docflow.Resources.Initialize_FileTypes_Other) ??
          Content.FilesTypes.GetAll().FirstOrDefault();
        application.Save();
        
        Functions.Module.LogDebugFormat(string.Format("Associated application \"{0}\" has been created", ext));
      }
      
      return application;
    }

    /// <summary>
    /// Получить последнюю подпись для документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>Подпись.</returns>
    protected virtual ISignature GetLastDocumentSignature(IOfficialDocument document)
    {
      var version = document.LastVersion;
      if (version == null)
        return null;
      
      return Signatures.Get(version).Where(x => x.SignCertificate != null).OrderByDescending(x => x.Id).FirstOrDefault();
    }
    
    /// <summary>
    /// Проверить, что сообщение содержит документы неподдерживаемого типа.
    /// </summary>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    /// <returns>True, если содержит, иначе False.</returns>
    public virtual bool IsMessageWithUnsupportedDocuments(NpoComputer.DCX.Common.IMessage message)
    {
      return message.PrimaryDocuments.All(x => (!this.GetSupportedPrimaryDocumentTypes().Contains(x.DocumentType.Value) ||
                                                (x.DocumentType == DocumentType.Nonformalized && x.IsUnknownDocumentType == true)));
    }
    
    #endregion

    #endregion
    
    #region Отправка документов и ответов по документам
    
    #region Подготовка и отправка сообщений

    /// <summary>
    /// Отправить ответ.
    /// </summary>
    /// <param name="documents">Документы.</param>
    /// <param name="counterparty">Контрагент.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <param name="certificate">Сертификат.</param>
    /// <param name="isAgent">Признак вызова из фонового процесса. Иначе - пользователем в RX.</param>
    [Remote, Public]
    public void SendAnswers(List<Docflow.IOfficialDocument> documents, Parties.ICounterparty counterparty, Sungero.ExchangeCore.IBusinessUnitBox box,
                            ICertificate certificate, bool isAgent)
    {
      if (!documents.Any())
        return;
      
      if (HasNotApprovedDocuments(documents.ToArray()))
        throw AppliedCodeException.Create(Resources.SendCounterpartyNotApproved);
      
      foreach (var document in documents)
      {
        var signature = this.GetDocumentSignature(document, certificate);
        if (signature == null)
          throw AppliedCodeException.Create(Resources.SendCounterpartyNotApproved);
      }

      var sendPackageAnswersForCancellationAgreement = box.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis
        && documents.Count > 1 && documents.All(doc => CancellationAgreements.Is(doc));

      if (sendPackageAnswersForCancellationAgreement)
      {
        try
        {
          this.SendPackageAnswersForCancellationAgreement(documents, box, counterparty, certificate);
        }
        catch (AppliedCodeException)
        {
          throw;
        }
        catch (Exception ex)
        {
          throw AppliedCodeException.Create(ex.Message, ex);
        }
      }

      foreach (var document in documents)
      {
        try
        {
          if (!sendPackageAnswersForCancellationAgreement)
            Docflow.PublicFunctions.OfficialDocument.SendAnswer(document, box, counterparty, certificate, isAgent);
          
          var info = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(document);
          var sendSignOperation = new Enumeration(Constants.Module.Exchange.SendAnswer);
          
          var counterpartyName = counterparty.Name;
          if (info != null && info.CounterpartyDepartmentBox != null)
            counterpartyName = Exchange.PublicFunctions.ExchangeDocumentInfo.GetCompanyNameWithDepartment(info, counterpartyName);
          var comment = Functions.Module.GetExchangeDocumentHistoryComment(counterpartyName, box.ExchangeService.Name);
          
          if (info.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Rejected)
          {
            var signature = this.GetDocumentSignature(document, certificate);
            this.ProcessSharedReject(info, document, false, box, signature.Body, sendSignOperation, comment, string.Empty, string.Empty, false);
          }
          else
            this.ProcessSharedSign(document, info, false, box, document.LastVersion, string.Empty, false, sendSignOperation, comment, false);
        }
        catch (AppliedCodeException)
        {
          throw;
        }
        catch (Exception ex)
        {
          throw AppliedCodeException.Create(ex.Message, ex);
        }
      }
    }
    
    /// <summary>
    /// Отправить ответ на пакет соглашений об аннулировании в сервис обмена.
    /// </summary>
    /// <param name="documents">Пакет соглашений об аннулировании.</param>
    /// <param name="senderBox">Абонентский ящик отправителя.</param>
    /// <param name="receiver">Получатель (головная организация или филиал контрагента).</param>
    /// <param name="certificate">Сертификат, которым подписаны документы.</param>
    public virtual void SendPackageAnswersForCancellationAgreement(List<Sungero.Docflow.IOfficialDocument> documents,
                                                                   Sungero.ExchangeCore.IBusinessUnitBox senderBox,
                                                                   Sungero.Parties.ICounterparty receiver,
                                                                   ICertificate certificate)
    {
      var client = Functions.Module.GetClient(senderBox);
      
      foreach (var document in documents)
      {
        var documentInfo = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(document);
        var signature = Functions.Module.GetDocumentSignature(document, certificate);
        
        if (!Functions.Module.ValidateBeforeSendAnswer(document, documentInfo, signature, client))
          return;
      }
      
      var primaryDocuments = new List<NpoComputer.DCX.Common.IDocument>();
      var dcxSigns = new List<NpoComputer.DCX.Common.Signature>();
      var sendedDocumentInfos = new Dictionary<IExchangeDocumentInfo, int>();
      
      foreach (var document in documents)
      {
        var documentInfo = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(document);
        var serviceDocumentId = documentInfo.ServiceDocumentId;
        var parentServiceDocumentId = documentInfo.ParentDocumentInfo.ServiceDocumentId;
        var serviceMessageId = documentInfo.ParentDocumentInfo.ServiceMessageId;
        var signature = Functions.Module.GetDocumentSignature(document, certificate);
        var sign = Functions.Module.CreateExchangeDocumentSignature(senderBox, serviceDocumentId,
                                                                    signature.Body, signature.FormalizedPoAUnifiedRegNumber);
        var cancellationAgreement = CancellationAgreements.As(document);
        var primaryDocument = Functions.Module.CreatePrimaryDocumentForCancellationAgreement(cancellationAgreement, serviceMessageId,
                                                                                             parentServiceDocumentId,
                                                                                             serviceDocumentId, true,
                                                                                             cancellationAgreement.Reason);
        primaryDocuments.Add(primaryDocument);
        dcxSigns.Add(sign);
        sendedDocumentInfos.Add(documentInfo, signature.Id);
      }
      
      var firstDocumentInfo = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(documents.First());
      var sentMessage = Functions.Module.SendMessage(primaryDocuments,
                                                     new List<NpoComputer.DCX.Common.IReglamentDocument>(),
                                                     dcxSigns,
                                                     client, receiver, firstDocumentInfo.ServiceCounterpartyId,
                                                     null, senderBox, null, firstDocumentInfo.ParentDocumentInfo.ServiceMessageId);
      foreach (var sendedInfo in sendedDocumentInfos)
      {
        var sendedDocumentInfo = sendedInfo.Key;
        var signId = sendedInfo.Value;
        sendedDocumentInfo.ReceiverSignId = signId;
        sendedDocumentInfo.Save();
        
        // Добавить номер машиночитаемой подписи в тело подписи для Сбис.
        var currentSignature = Functions.Module.GetDocumentSignature(sendedDocumentInfo.Document, certificate);
        Functions.Module.AddFPoaUnifiedRegNumberToSignatureData(sendedDocumentInfo.Document, currentSignature, senderBox,
                                                                sentMessage, sendedDocumentInfo.ServiceDocumentId);
      }
    }
    
    /// <summary>
    /// Отправить ответ на пакет документов.
    /// </summary>
    /// <param name="documents">Документы пакета.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <param name="certificate">Сертификат.</param>
    /// <param name="isAgent">Признак вызова из фонового процесса. Иначе - пользователем в RX.</param>
    [Obsolete("Теперь функция не актуальна, т.к. реализована поддержка частичного подписания.")]
    public virtual void SendAnswerDocumentsPackage(List<Docflow.IOfficialDocument> documents, Sungero.ExchangeCore.IBusinessUnitBox box,
                                                   ICertificate certificate, bool isAgent)
    {
      var client = GetClient(box);
      var reglamentDocuments = new List<NpoComputer.DCX.Common.IReglamentDocument>();
      var signatures = new List<NpoComputer.DCX.Common.Signature>();
      var serviceCounterpartyId = string.Empty;
      var serviceMessageId = string.Empty;
      var sentDocuments = new Dictionary<int, IOfficialDocument>();
      
      foreach (var document in documents)
      {
        var exchangeDocumentInfo = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(document);
        serviceCounterpartyId = exchangeDocumentInfo.ServiceCounterpartyId;
        serviceMessageId = exchangeDocumentInfo.ServiceMessageId;

        var signature = this.GetDocumentSignature(document, certificate);
        signatures.Add(CreateExchangeDocumentSignature(box,
                                                       exchangeDocumentInfo.ExternalBuyerTitleId ?? exchangeDocumentInfo.ServiceDocumentId,
                                                       signature.Body, signature.FormalizedPoAUnifiedRegNumber));
        
        exchangeDocumentInfo.ReceiverSignId = signature.Id;
        exchangeDocumentInfo.Save();
        
        var accountingDocument = AccountingDocumentBases.As(document);
        
        // У СФ ИД титула будет пустым всегда.
        if (accountingDocument != null && accountingDocument.BuyerTitleId != null)
        {
          sentDocuments.Add(accountingDocument.BuyerTitleId.Value, document);
          var version = accountingDocument.Versions.Single(v => v.Id == accountingDocument.BuyerTitleId);
          byte[] receipt;
          using (var memory = new System.IO.MemoryStream())
          {
            version.Body.Read().CopyTo(memory);
            receipt = memory.ToArray();
          }
          
          accountingDocument.BuyerSignatureId = signature.Id;
          
          var docWithCertificate = Structures.Module.ReglamentDocumentWithCertificate.Create(FinancialArchive.Resources.BuyerTitleVersionNote, receipt,
                                                                                             certificate, signature.Body, exchangeDocumentInfo.ServiceDocumentId,
                                                                                             box, accountingDocument, exchangeDocumentInfo.ServiceMessageId, null, null,
                                                                                             exchangeDocumentInfo.ServiceCounterpartyId, false,
                                                                                             exchangeDocumentInfo, false, null, null);
          
          var type = NpoComputer.DCX.Common.ReglamentDocumentType.WaybillBuyerTitle;
          
          if (accountingDocument.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.WorksTransfer)
            type = NpoComputer.DCX.Common.ReglamentDocumentType.WorksTransferBuyer;
          
          if (accountingDocument.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.Act)
            type = NpoComputer.DCX.Common.ReglamentDocumentType.ActClientTitle;
          
          if (accountingDocument.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.GoodsTransfer)
            type = NpoComputer.DCX.Common.ReglamentDocumentType.GoodsTransferBuyer;
          
          if (accountingDocument.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.GeneralTransfer)
            type = accountingDocument.IsAdjustment == true ?
              NpoComputer.DCX.Common.ReglamentDocumentType.GeneralTransferCorrectionBuyer :
              NpoComputer.DCX.Common.ReglamentDocumentType.GeneralTransferBuyer;
          
          var serviceDocument = this.CreateReglamentExchangeServiceDocument(docWithCertificate, type);
          reglamentDocuments.Add(serviceDocument);
        }
        else
          sentDocuments.Add(exchangeDocumentInfo.VersionId.Value, document);
      }
      
      try
      {
        this.SendMessage(new List<NpoComputer.DCX.Common.IDocument>(),
                         reglamentDocuments, signatures, client, null, serviceCounterpartyId, null, box, null, serviceMessageId);
      }
      catch (Exception ex)
      {
        throw AppliedCodeException.Create(ex.Message, ex);
      }
      
      foreach (var document in sentDocuments)
      {
        if (isAgent)
        {
          Docflow.PublicFunctions.Module.GeneratePublicBodyForExchangeDocument(document.Value, document.Key, Docflow.OfficialDocument.ExchangeState.Signed);
        }
        else
        {
          Docflow.PublicFunctions.Module.GenerateTempPublicBodyForExchangeDocument(document.Value, document.Key);
          Functions.Module.EnqueueXmlToPdfBodyConverter(document.Value, document.Key, Docflow.OfficialDocument.ExchangeState.Signed);
        }
      }
    }
    
    /// <summary>
    /// Отправить ответ на неформализованный документ.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="counterparty">Контрагент.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <param name="certificate">Сертификат.</param>
    /// <param name="isAgent">Признак вызова из фонового процесса. Иначе - пользователем в RX.</param>
    [Public]
    public virtual void SendAnswerToNonformalizedDocument(Docflow.IOfficialDocument document, Parties.ICounterparty counterparty,
                                                          ExchangeCore.IBusinessUnitBox box, ICertificate certificate, bool isAgent)
    {
      var client = GetClient(box);
      var documentInfo = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(document);
      var signature = this.GetDocumentSignature(document, certificate);
      
      if (!this.ValidateBeforeSendAnswer(document, documentInfo, signature, client))
        return;

      var serviceDocumentId = documentInfo.ServiceDocumentId;
      var dcxSign = CreateExchangeDocumentSignature(box, serviceDocumentId, signature.Body, signature.FormalizedPoAUnifiedRegNumber);

      try
      {
        var sentMessage = this.SendMessage(new List<NpoComputer.DCX.Common.IDocument>(),
                                           new List<NpoComputer.DCX.Common.IReglamentDocument>(),
                                           new List<NpoComputer.DCX.Common.Signature>() { dcxSign },
                                           client, counterparty, documentInfo.ServiceCounterpartyId,
                                           null, box, null, documentInfo.ServiceMessageId);
        
        documentInfo.ReceiverSignId = signature.Id;
        documentInfo.Save();
        
        Functions.Module.AddFPoaUnifiedRegNumberToSignatureData(document, signature,
                                                                box, sentMessage,
                                                                serviceDocumentId);
        
        if (isAgent)
        {
          Docflow.PublicFunctions.Module.GeneratePublicBodyForExchangeDocument(document, documentInfo.VersionId.Value, Docflow.OfficialDocument.ExchangeState.Signed);
        }
        else
        {
          Docflow.PublicFunctions.Module.GenerateTempPublicBodyForExchangeDocument(document, documentInfo.VersionId.Value);
          Functions.Module.EnqueueXmlToPdfBodyConverter(document, documentInfo.VersionId.Value, Docflow.OfficialDocument.ExchangeState.Signed);
        }
        
        this.LogDebugFormat(documentInfo, "Send answer to nonformalized document.");
      }
      catch (Exception ex)
      {
        throw AppliedCodeException.Create(ex.Message, ex);
      }
    }
    
    /// <summary>
    /// Валидация перед отправкой ответа на документ.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <param name="signature">Подпись.</param>
    /// <param name="client">Клиент.</param>
    /// <returns>True, если валидация прошла успешно, и False, если были ошибки.</returns>
    public virtual bool ValidateBeforeSendAnswer(Docflow.IOfficialDocument document, IExchangeDocumentInfo documentInfo,
                                                 Structures.Module.Signature signature,
                                                 NpoComputer.DCX.ClientApi.Client client)
    {
      // Нет информации об обмене по последней версии.
      if (documentInfo == null)
        return false;
      
      var serviceDocumentId = documentInfo.ServiceDocumentId;
      if (string.IsNullOrEmpty(serviceDocumentId))
        return false;
      
      if (signature == null)
        throw AppliedCodeException.Create(Resources.SendCounterpartyAddendaNotSigned);
      
      if (documentInfo.NeedSign == true)
      {
        DocumentAllowedAnswer allowedAnswers = this.GetDocumentAllowedAnswers(document, documentInfo, client);
        // Если документ требовал подписания, проверяем - не подписан/отказан он в вебе.
        if (!allowedAnswers.CanSendSign)
          return false;
      }
      else
      {
        // Не отправляем подписи по документам, которые не требовали подписания.
        return false;
      }
      
      return true;
    }

    /// <summary>
    /// Отправить титул покупателя для накладной или акта.
    /// </summary>
    /// <param name="waybill">Накладная или акт.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="certificate">Сертификат.</param>
    /// <param name="isAgent">Признак вызова из фонового процесса. False используется для вызова из UI.</param>
    [Public]
    public virtual void SendBuyerTitle(Docflow.IAccountingDocumentBase waybill, ExchangeCore.IBusinessUnitBox box, ICertificate certificate, bool isAgent)
    {
      if (box == null)
        throw AppliedCodeException.Create(Resources.BoxIsNotValid);
      
      if (certificate == null)
        throw AppliedCodeException.Create(Resources.CertificateNotFound);
      
      var docsWithCertificate = Structures.Module.ReglamentDocumentWithCertificate.Create();
      var externalDocumentInfo = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(waybill);
      try
      {
        if (externalDocumentInfo == null)
          return;
        
        var version = waybill.Versions.Single(v => v.Id == waybill.BuyerTitleId);
        byte[] receipt;
        var documentName = string.Empty;
        using (var memory = new System.IO.MemoryStream())
        {
          version.Body.Read().CopyTo(memory);
          receipt = memory.ToArray();
          try
          {
            var encoding = Encoding.GetEncoding(1251);
            var title = XDocument.Parse(encoding.GetString(receipt));
            documentName = title.Element("Файл").Attribute("ИдФайл").Value + ".xml";
          }
          catch (Exception ex)
          {
            Logger.Error("Can't parse document name from xml", ex);
          }
        }
        
        var sign = Signatures.Get(version)
          .FirstOrDefault(s => s.SignCertificate != null && s.SignCertificate.Thumbprint == certificate.Thumbprint);
        var signBody = sign.GetDataSignature();
        var unifiedRegistrationNumber = Docflow.PublicFunctions.Module.GetUnsignedAttribute(sign, Docflow.PublicConstants.Module.UnsignedAdditionalInfoKeyFPoA);
        
        waybill.BuyerSignatureId = sign.Id;
        externalDocumentInfo.ReceiverSignId = sign.Id;
        externalDocumentInfo.Save();
        
        docsWithCertificate = Structures.Module.ReglamentDocumentWithCertificate.Create(string.IsNullOrEmpty(documentName) ? FinancialArchive.Resources.BuyerTitleVersionNote : documentName,
                                                                                        receipt, certificate, signBody, externalDocumentInfo.ServiceDocumentId,
                                                                                        box, waybill, externalDocumentInfo.ServiceMessageId, null, null,
                                                                                        externalDocumentInfo.ServiceCounterpartyId, false,
                                                                                        externalDocumentInfo, false, null, unifiedRegistrationNumber);
      }
      catch (Exception ex)
      {
        if (ex is CommonLibrary.Exceptions.PlatformException)
          throw;
        
        throw AppliedCodeException.Create(Resources.ErrorWhileSendingDocToCounterparty, ex);
      }
      
      // По идее, разницы у титулов на уровне тел нет, пока сделаем просто перебором.
      var type = NpoComputer.DCX.Common.ReglamentDocumentType.WaybillBuyerTitle;
      var exchangeService = waybill.BusinessUnitBox.ExchangeService.ExchangeProvider;
      
      if (waybill.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.WorksTransfer &&
          exchangeService != ExchangeCore.ExchangeService.ExchangeProvider.Diadoc)
        type = NpoComputer.DCX.Common.ReglamentDocumentType.WorksTransferBuyer;
      
      if (waybill.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.Act ||
          exchangeService == ExchangeCore.ExchangeService.ExchangeProvider.Diadoc &&
          waybill.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.WorksTransfer)
        type = NpoComputer.DCX.Common.ReglamentDocumentType.ActClientTitle;
      
      if (waybill.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.GoodsTransfer &&
          exchangeService != ExchangeCore.ExchangeService.ExchangeProvider.Diadoc)
        type = NpoComputer.DCX.Common.ReglamentDocumentType.GoodsTransferBuyer;
      
      if (waybill.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.GeneralTransfer)
        type = waybill.IsAdjustment == true ?
          NpoComputer.DCX.Common.ReglamentDocumentType.GeneralTransferCorrectionBuyer :
          NpoComputer.DCX.Common.ReglamentDocumentType.GeneralTransferBuyer;
      
      this.SendServiceDocument(new List<Structures.Module.ReglamentDocumentWithCertificate> { docsWithCertificate }, box, type);
      
      if (isAgent)
      {
        Docflow.PublicFunctions.Module.GeneratePublicBodyForExchangeDocument(waybill, waybill.BuyerTitleId.Value, Docflow.OfficialDocument.ExchangeState.Signed);
      }
      else
      {
        Docflow.PublicFunctions.Module.GenerateTempPublicBodyForExchangeDocument(waybill, waybill.BuyerTitleId.Value);
        Functions.Module.EnqueueXmlToPdfBodyConverter(waybill, waybill.BuyerTitleId.Value, Docflow.OfficialDocument.ExchangeState.Signed);
      }
    }

    /// <summary>
    /// Отправить уведомления об уточнении документов.
    /// </summary>
    /// <param name="signedDocuments">Подписанные уведомления об уточнении.</param>
    /// <param name="receiver">Получатель уведомления.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="note">Комментарий.</param>
    [Remote]
    public void SendAmendmentRequest(List<Structures.Module.ReglamentDocumentWithCertificate> signedDocuments, Parties.ICounterparty receiver,
                                     ExchangeCore.IBoxBase box, string note)
    {
      var receiverName = receiver.Name;
      var operation = new Enumeration(Constants.Module.Exchange.SendAnswer);
      var exchangeDocumentInfo = signedDocuments.Select(i => i.Info).FirstOrDefault();
      if (exchangeDocumentInfo != null && exchangeDocumentInfo.CounterpartyDepartmentBox != null)
        receiverName = Exchange.PublicFunctions.ExchangeDocumentInfo.GetCompanyNameWithDepartment(exchangeDocumentInfo, receiverName);
      var comment = Functions.Module.GetExchangeDocumentHistoryComment(receiverName, ExchangeCore.PublicFunctions.BoxBase.GetExchangeService(box).Name);
      
      var businessUnitBox = ExchangeCore.PublicFunctions.BoxBase.GetRootBox(box);

      // Для счетов-фактур и УПД тип служебного документа в сервисе другой.
      var invoiceDocuments = signedDocuments.Where(d => d.IsInvoiceFlow).ToList();
      
      // Отправляем одним сообщением УОУ на комплект документов из СБИС.
      var packageProcessingSbis = signedDocuments.Count > 1 && businessUnitBox.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis;
      if (packageProcessingSbis)
      {
        if (signedDocuments.Any(doc => CancellationAgreements.Is(doc.LinkedDocument)))
          this.SendServiceDocument(signedDocuments,
                                   businessUnitBox, ReglamentDocumentType.AmendmentRequest);
        else
          this.SendServiceDocument(signedDocuments,
                                   businessUnitBox, ReglamentDocumentType.InvoiceAmendmentRequest);
      }

      if (!packageProcessingSbis && invoiceDocuments.Any())
        this.SendServiceDocument(invoiceDocuments,
                                 businessUnitBox, ReglamentDocumentType.InvoiceAmendmentRequest);
      
      foreach (var document in invoiceDocuments)
      {
        var doc = document.LinkedDocument;
        var info = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, document.ParentDocumentId);
        this.ProcessSharedInvoiceReject(info, doc, false, box, document.Signature, operation, comment, string.Empty, note, false);
      }
      
      var notInvoiceDocuments = signedDocuments.Where(d => !d.IsInvoiceFlow).ToList();
      var reglamentDocumentType = businessUnitBox.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Diadoc ?
        ReglamentDocumentType.Rejection : ReglamentDocumentType.AmendmentRequest;
      if (!packageProcessingSbis && notInvoiceDocuments.Any())
        this.SendServiceDocument(notInvoiceDocuments, businessUnitBox, reglamentDocumentType);
      
      foreach (var document in notInvoiceDocuments)
      {
        var doc = document.LinkedDocument;
        var info = Functions.ExchangeDocumentInfo.GetExDocumentInfoByExternalId(box, document.ParentDocumentId);
        this.ProcessSharedReject(info, doc, false, box, document.Signature, operation, comment, string.Empty, note, false);
      }
    }

    /// <summary>
    /// Отправить извещения о получении документов.
    /// </summary>
    /// <param name="signedDocuments">Подписанные извещения о получении.</param>
    /// <param name="box">Абонентский ящик.</param>
    [Remote]
    public virtual void SendDeliveryConfirmation(List<Structures.Module.ReglamentDocumentWithCertificate> signedDocuments, ExchangeCore.IBusinessUnitBox box)
    {
      this.LogDebugFormat(box, "Execute SendDeliveryConfirmation.");
      // Нельзя разделять по типам служебок для СБИС, потому что не будет работать отправка комплектов формализованный + неформализованный.
      if (box.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis)
        this.SendServiceDocument(signedDocuments, box, NpoComputer.DCX.Common.ReglamentDocumentType.InvoiceReceipt);
      else
      {
        // Для счетов-фактур и УПД тип служебного документа в сервисе другой.
        var invoiceDocuments = signedDocuments.Where(d => d.IsInvoiceFlow).ToList();
        if (invoiceDocuments.Any())
          this.SendServiceDocument(invoiceDocuments, box, NpoComputer.DCX.Common.ReglamentDocumentType.InvoiceReceipt);
        
        var notInvoiceDocuments = signedDocuments.Where(d => !d.IsInvoiceFlow).ToList();
        if (notInvoiceDocuments.Any())
          this.SendServiceDocument(notInvoiceDocuments, box, NpoComputer.DCX.Common.ReglamentDocumentType.Receipt);
      }

      var rootReceipts = signedDocuments.Where(d => d.IsRootDocumentReceipt == true);
      foreach (var receipt in rootReceipts)
      {
        var counterpartyName = receipt.Info.Counterparty.Name;
        if (receipt.Info.CounterpartyDepartmentBox != null)
          counterpartyName = Exchange.PublicFunctions.ExchangeDocumentInfo.GetCompanyNameWithDepartment(receipt.Info, counterpartyName);
        var comment = Functions.Module.GetExchangeDocumentHistoryComment(counterpartyName, box.ExchangeService.Name);
        this.FixReceiptNotification(receipt.Info, comment, true);
      }
      
      RequeueMessagesGet();
    }

    /// <summary>
    /// Отправить служебные документы сервиса обмена.
    /// </summary>
    /// <param name="signedDocuments">Коллекция подписанных документов.</param>
    /// <param name="box">Ящик.</param>
    /// <param name="documentType">Тип документа.</param>
    protected virtual void SendServiceDocument(List<ReglamentDocumentWithCertificate> signedDocuments, IBusinessUnitBox box, ReglamentDocumentType documentType)
    {
      var client = GetClient(box);
      var processedMessagesId = new List<string>();
      
      // Для СБИС хранится составной ParentDocumentId, первая часть которого - ИД сообщения, вторая - ИД документа.
      // ИОПы необходимо отправлять одним сообщением на весь комплект документов.
      foreach (var serviceDocuments in signedDocuments.GroupBy(d => d.ParentDocumentId.Split('#').First()))
      {
        var document = serviceDocuments.First();
        var serviceDocumentsToSend = new List<NpoComputer.DCX.Common.IReglamentDocument>();
        var serviceDocumentsSigns = new List<NpoComputer.DCX.Common.Signature>();
        var isBuyerTitle = this.GetSupportedReglamentDocumentTypes().Contains(documentType);
        var isRoaming = this.IsRoamingExchange(document.Info.Counterparty, box);
        
        foreach (var reglamentDocument in serviceDocuments)
        {
          var currentDocumentType = documentType;
          var serviceDocument = this.CreateReglamentExchangeServiceDocument(reglamentDocument, currentDocumentType);
          if (isBuyerTitle && isRoaming)
            serviceDocument.NeedReceipt = true;
          serviceDocumentsToSend.Add(serviceDocument);
          
          var sign = CreateExchangeDocumentSignature(box, serviceDocument.ServiceEntityId,
                                                     reglamentDocument.Signature, reglamentDocument.FormalizedPoAUnifiedRegNumber);

          serviceDocumentsSigns.Add(sign);
          this.LogDebugFormat(reglamentDocument.Info, "Execute SendServiceDocument. Prepare service document with DocumentType = {0}, LinkedDocumentId = {1}.",
                              reglamentDocument.ReglamentDocumentType, reglamentDocument.LinkedDocument.Id);
        }
        
        try
        {
          var sentMessage = this.SendMessage(new List<NpoComputer.DCX.Common.IDocument>(),
                                             serviceDocumentsToSend, serviceDocumentsSigns, client, null,
                                             document.ServiceCounterpartyId, null, box, null, document.ServiceMessageId);
          
          this.LogDebugFormat(document.Info, serviceDocumentsToSend, "Execute SendServiceDocument. Send service document: ServiceCounterpartyId = {0}.", document.ServiceCounterpartyId);
          
          var needUpdateSign = box.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis && isBuyerTitle &&
            signedDocuments.Any(d => !string.IsNullOrEmpty(d.FormalizedPoAUnifiedRegNumber));
          if (needUpdateSign)
          {
            var signedDocument = signedDocuments.First();
            var patсhedSignature = sentMessage.Signatures.Single(s => string.Equals(s.DocumentId, signedDocument.Info.ExternalBuyerTitleId));
            Docflow.PublicFunctions.Module.SetDataSignature(signedDocument.LinkedDocument, (int)signedDocument.Info.ReceiverSignId, patсhedSignature.Content);
          }
          
          if (isBuyerTitle)
          {
            var sentDocument = sentMessage.DocumentIds.First();
            var buyerReceiptStatus = ResolveReceiptStatus(sentDocument.ReceiptStatus);
            document.Info.ExternalBuyerTitleId = sentDocument.ServiceId;
            document.Info.BuyerDeliveryConfirmationStatus = buyerReceiptStatus;
            document.Info.Save();
          }
        }
        catch (NpoComputer.DCX.Common.Exceptions.WorkflowViolationException ex)
        {
          if (documentType == ReglamentDocumentType.InvoiceReceipt || documentType == ReglamentDocumentType.Receipt)
          {
            var innerExceptionText = ex.InnerException != null
              ? string.Format("{0}. ", ex.InnerException.Message)
              : string.Empty;
            var reglamentDocumentTypeValue = document.ReglamentDocumentType.HasValue ? document.ReglamentDocumentType.Value.Value : string.Empty;
            var debugText = string.Format("{0}Receipt notice with Name = '{1}', ReglamentDocumentType = '{2}', ParentDocumentId = '{3}' already sent. " +
                                          "Start the job of receiving messages from the exchange service.",
                                          innerExceptionText, document.Name, reglamentDocumentTypeValue, document.ParentDocumentId);
            this.LogDebugFormat(debugText);
          }
          else if (isBuyerTitle)
          {
            this.LogDebugFormat(ex.Message);
            throw AppliedCodeException.Create(Resources.OneOrMoreDocumentAlreadyProcessing);
          }
          else
            throw;
        }
        catch (Exception e)
        {
          if (documentType == ReglamentDocumentType.InvoiceReceipt || documentType == ReglamentDocumentType.Receipt)
          {
            this.LogDebugFormat(e.ToString());
          }
          else
            throw;
        }
      }
    }
    
    /// <summary>
    /// Отправить пакет служебных документов сервиса обмена.
    /// </summary>
    /// <param name="signedDocuments">Коллекция подписанных документов.</param>
    /// <param name="box">Ящик.</param>
    /// <param name="documentType">Тип документа.</param>
    [Obsolete("Теперь функция не актуальна, т.к. реализована поддержка частичного подписания.")]
    protected virtual void SendPackageServiceDocuments(List<ReglamentDocumentWithCertificate> signedDocuments, IBusinessUnitBox box, ReglamentDocumentType documentType)
    {
      var client = GetClient(box);
      var serviceDocuments = new List<NpoComputer.DCX.Common.IReglamentDocument>();
      var signatures = new List<NpoComputer.DCX.Common.Signature>();
      var serviceCounterpartyId = signedDocuments.First().ServiceCounterpartyId;
      var serviceMessageId = signedDocuments.First().ServiceMessageId;

      foreach (var document in signedDocuments)
      {
        var serviceDoc = this.CreateReglamentExchangeServiceDocument(document, documentType);
        serviceDocuments.Add(serviceDoc);
        signatures.Add(CreateExchangeDocumentSignature(box, serviceDoc.ServiceEntityId, document.Signature, null));
        Logger.Debug(string.Format("Prepare receipt notification with ExchangeDocumentInfoId = {0}, DocumentType = {1}, LinkedDocumentId = {2}, ServiceMessageId = {3}",
                                   document.Info.Id, document.ReglamentDocumentType, document.LinkedDocument.Id, document.ServiceMessageId));
      }
      
      try
      {
        this.SendMessage(new List<NpoComputer.DCX.Common.IDocument>(), serviceDocuments, signatures,
                         client, null, serviceCounterpartyId, null, box, null, serviceMessageId);
        
        foreach (var document in signedDocuments)
          Logger.Debug(string.Format("Send receipt notification: ServiceCounterpartyId = {0}, ParentDocumentId = {1}, ServiceMessageId = {2}",
                                     document.ServiceCounterpartyId, document.ParentDocumentId, document.ServiceMessageId));
      }
      catch (Exception e)
      {
        throw e;
      }
    }

    /// <summary>
    /// Создать сообщение в сервис обмена.
    /// </summary>
    /// <param name="primaryDocuments">Список основных документов.</param>
    /// <param name="reglamentDocuments">Список регламентных документов.</param>
    /// <param name="signs">Список подписей.</param>
    /// <param name="client">Клиент.</param>
    /// <param name="receiver">Получатель.</param>
    /// <param name="serviceCounterpartyId">Внешний ИД контрагента.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="parentServiceMessageId">ИД сообщения, для которого отправляется ответ.</param>
    /// <returns>Результат отправки.</returns>
    [Obsolete("Используйте версию с большим количеством параметров.")]
    protected NpoComputer.DCX.Common.Message CreateMessage(List<IDocument> primaryDocuments, List<IReglamentDocument> reglamentDocuments,
                                                           List<Signature> signs, DcxClient client, ICounterparty receiver, string serviceCounterpartyId, IBusinessUnitBox box,
                                                           string parentServiceMessageId)
    {
      return this.CreateMessage(primaryDocuments, reglamentDocuments, signs, client, receiver, serviceCounterpartyId, null, box, null, parentServiceMessageId);
    }
    
    /// <summary>
    /// Создать сообщение в сервис обмена.
    /// </summary>
    /// <param name="primaryDocuments">Список основных документов.</param>
    /// <param name="reglamentDocuments">Список регламентных документов.</param>
    /// <param name="signs">Список подписей.</param>
    /// <param name="client">Клиент.</param>
    /// <param name="receiver">Получатель (головная организация или филиал контрагента).</param>
    /// <param name="receiverServiceHeadId">Внешний ИД головной организации контрагента.</param>
    /// <param name="receiverServiceDepartmentId">Внешний ИД подразделения контрагента.</param>
    /// <param name="senderBox">Абонентский ящик.</param>
    /// <param name="senderServiceDepartmentId">Внешний ИД подразделения абонентского ящика.</param>
    /// <param name="parentServiceMessageId">ИД сообщения, для которого отправляется ответ.</param>
    /// <returns>Результат отправки.</returns>
    public NpoComputer.DCX.Common.Message CreateMessage(List<NpoComputer.DCX.Common.IDocument> primaryDocuments, List<NpoComputer.DCX.Common.IReglamentDocument> reglamentDocuments,
                                                        List<NpoComputer.DCX.Common.Signature> signs, NpoComputer.DCX.ClientApi.Client client,
                                                        ICounterparty receiver, string receiverServiceHeadId, string receiverServiceDepartmentId,
                                                        IBusinessUnitBox senderBox, string senderServiceDepartmentId,
                                                        string parentServiceMessageId)
    {
      ICounterpartyExchangeBoxes receiverLine = null;

      if (receiver != null)
      {
        receiverLine = receiver.ExchangeBoxes.Where(x => Equals(x.Box, senderBox) && x.IsDefault == true).SingleOrDefault();
        if (receiverLine == null)
          throw AppliedCodeException.Create(string.Format("Для контрагента c ИД {0} не установлена связь через указанный абонентский ящик с ИД {1}.", receiver.Id, senderBox.Id));

        if (string.IsNullOrEmpty(receiverServiceDepartmentId))
          receiverServiceDepartmentId = receiverLine.CounterpartyBranchId;
      }

      if (receiverServiceHeadId == string.Empty)
        receiverServiceHeadId = receiverLine.OrganizationId;

      var counterpartyBoxId = client.GetContact(receiverServiceHeadId).Organization.BoxId;

      var message = new NpoComputer.DCX.Common.Message()
      {
        IsReply = !string.IsNullOrEmpty(parentServiceMessageId),
        ParentServiceMessageId = parentServiceMessageId,
        PrimaryDocuments = primaryDocuments,
        ReglamentDocuments = reglamentDocuments,
        Signatures = signs.ToList(),
        Receiver = new NpoComputer.DCX.Common.Subscriber()
        {
          BoxId = counterpartyBoxId
        }
      };

      if (!string.IsNullOrEmpty(receiverServiceDepartmentId))
      {
        message.ToDepartment = new NpoComputer.DCX.Common.Department()
        {
          Id = receiverServiceDepartmentId
        };
      }
      
      if (!string.IsNullOrEmpty(senderServiceDepartmentId))
      {
        message.FromDepartment = new NpoComputer.DCX.Common.Department()
        {
          Id = senderServiceDepartmentId
        };
      }

      return message;
    }
    
    /// <summary>
    /// Отправить сообщение в сервис обмена.
    /// </summary>
    /// <param name="primaryDocuments">Список основных документов.</param>
    /// <param name="reglamentDocuments">Список регламентных документов.</param>
    /// <param name="signs">Список подписей.</param>
    /// <param name="client">Клиент.</param>
    /// <param name="receiver">Получатель.</param>
    /// <param name="serviceCounterpartyId">Внешний ИД контрагента.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="parentServiceMessageId">ИД сообщения, для которого отправляется ответ.</param>
    /// <returns>Результат отправки.</returns>
    [Obsolete("Используйте версию с большим количеством параметров.")]
    public virtual NpoComputer.DCX.Common.SentMessage SendMessage(List<NpoComputer.DCX.Common.IDocument> primaryDocuments,
                                                                  List<NpoComputer.DCX.Common.IReglamentDocument> reglamentDocuments,
                                                                  List<NpoComputer.DCX.Common.Signature> signs, NpoComputer.DCX.ClientApi.Client client,
                                                                  ICounterparty receiver, string serviceCounterpartyId, IBusinessUnitBox box,
                                                                  string parentServiceMessageId)
    {
      return this.SendMessage(primaryDocuments, reglamentDocuments, signs, client, receiver, serviceCounterpartyId, null, box, null, parentServiceMessageId);
    }
    
    /// <summary>
    /// Отправить сообщение в сервис обмена.
    /// </summary>
    /// <param name="primaryDocuments">Список основных документов.</param>
    /// <param name="reglamentDocuments">Список регламентных документов.</param>
    /// <param name="signs">Список подписей.</param>
    /// <param name="client">Клиент.</param>
    /// <param name="receiver">Получатель (головная организация или филиал контрагента).</param>
    /// <param name="receiverServiceHeadId">Внешний ИД головной организации контрагента.</param>
    /// <param name="receiverServiceDepartmentId">Внешний ИД подразделения контрагента.</param>
    /// <param name="senderBox">Абонентский ящик отправителя.</param>
    /// <param name="senderServiceDepartmentId">Внешний ИД подразделения абонентского ящика отправителя.</param>
    /// <param name="parentServiceMessageId">ИД сообщения, для которого отправляется ответ.</param>
    /// <returns>Результат отправки.</returns>
    public virtual NpoComputer.DCX.Common.SentMessage SendMessage(List<NpoComputer.DCX.Common.IDocument> primaryDocuments,
                                                                  List<NpoComputer.DCX.Common.IReglamentDocument> reglamentDocuments,
                                                                  List<NpoComputer.DCX.Common.Signature> signs, NpoComputer.DCX.ClientApi.Client client,
                                                                  ICounterparty receiver, string receiverServiceHeadId, string receiverServiceDepartmentId,
                                                                  IBusinessUnitBox senderBox, string senderServiceDepartmentId,
                                                                  string parentServiceMessageId)
    {
      var message = this.CreateMessage(primaryDocuments, reglamentDocuments, signs, client, receiver, receiverServiceHeadId, receiverServiceDepartmentId,
                                       senderBox, senderServiceDepartmentId, parentServiceMessageId);
      
      return client.SendMessage(message);
    }
    
    /// <summary>
    /// Отправить сообщение в сервис обмена c УОП.
    /// </summary>
    /// <param name="primaryDocuments">Список основных документов.</param>
    /// <param name="reglamentDocuments">Список регламентных документов.</param>
    /// <param name="signs">Список подписей.</param>
    /// <param name="client">Клиент.</param>
    /// <param name="receiver">Получатель.</param>
    /// <param name="serviceCounterpartyId">Внешний ИД контрагента.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="parentServiceMessageId">ИД сообщения, для которого отправляется ответ.</param>
    /// <param name="document">Документ, для которого отправляется ответ.</param>
    /// <returns>Результат отправки.</returns>
    protected virtual SentMessage SendMessageWithServiceDocument(List<IDocument> primaryDocuments, List<IReglamentDocument> reglamentDocuments,
                                                                 List<Signature> signs, DcxClient client, ICounterparty receiver, string serviceCounterpartyId, IBusinessUnitBox box,
                                                                 string parentServiceMessageId, Docflow.IOfficialDocument document)
    {
      var message = this.CreateMessage(primaryDocuments, reglamentDocuments, signs, client, receiver, serviceCounterpartyId, null, box, null, parentServiceMessageId);
      
      if (string.IsNullOrEmpty(serviceCounterpartyId))
      {
        var receiverLine = receiver.ExchangeBoxes.Where(x => Equals(x.Box, box) && x.IsDefault == true).SingleOrDefault();
        if (receiverLine == null)
          throw AppliedCodeException.Create(string.Format("Для контрагента c ИД {0} не установлена связь через указанный абонентский ящик с ИД {1}.", receiver.Id, box.Id));
        
        serviceCounterpartyId = receiverLine.OrganizationId;
      }
      
      var counterpartyBoxId = client.GetContact(serviceCounterpartyId).Organization.BoxId;
      
      if (receiver != null && message.IsReply)
      {
        var cert = box.CertificateReceiptNotifications;
        var counterpartyBox = receiver.ExchangeBoxes.First(b => b.OrganizationId == counterpartyBoxId);
        if (counterpartyBox != null && counterpartyBox.Box.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis)
          this.PrepareReplyMessage(message, cert, client, primaryDocuments, box, document);
      }
      
      return client.SendMessage(message);
    }
    
    /// <summary>
    /// Подготовить ответное сообщение к отправке на сервис.
    /// В методе генерируются служебные документы для отправки на сервис результатов подписания документов сообщения.
    /// Служебки генерируются только при наличии установленного сертификата для подписания.
    /// </summary>
    /// <param name="outcomeReplyMessage">Сообщение, которое будет отправлено на сервис.</param>
    /// <param name="certificate">Исходящее ответное сообщение из справочника.</param>
    /// <param name="client">Клиент.</param>
    /// <param name="documents">Список документов.</param>
    /// <param name="box">Яшик нашего абонента.</param>
    /// <param name="document">Основной документ.</param>
    public void PrepareReplyMessage(NpoComputer.DCX.Common.IMessage outcomeReplyMessage, ICertificate certificate,
                                    NpoComputer.DCX.ClientApi.Client client, List<NpoComputer.DCX.Common.IDocument> documents,
                                    IBusinessUnitBox box, IOfficialDocument document)
    {
      var exchangeDocumentInfo = Exchange.PublicFunctions.ExchangeDocumentInfo.Remote.GetIncomingExDocumentInfo(document);
      if (certificate == null)
        return;
      else
        Logger.DebugFormat("No signing certificate found. ServiceMessageId = {0}", outcomeReplyMessage.ServiceMessageId);
      
      var signerInfo = NpoComputer.DCX.Common.SignerInfo.CreateFromSignature(certificate.X509Certificate);
      foreach (var sign in outcomeReplyMessage.Signatures)
        sign.SignerInfo = signerInfo;
      
      var documentsForSign = client.PrepareReplyMessage(outcomeReplyMessage, signerInfo);
      
      var documentsToSave = new List<Structures.Module.ReglamentDocumentWithCertificate>();
      foreach (var documentForSign in documentsForSign)
      {
        if (documentForSign is NpoComputer.DCX.Common.IReglamentDocument)
        {
          var doc = (NpoComputer.DCX.Common.IReglamentDocument)documentForSign;
          Logger.DebugFormat("PrepareReplyMessage. ServiceEntityId = {0}, ServiceMessageId = {1}", doc.ServiceEntityId, outcomeReplyMessage.ServiceMessageId);
          var documentPriority = new Dictionary<string, byte[]>();
          documentPriority.Add(doc.ServiceEntityId, doc.Content);
          var signs = ExternalSignatures.Sign(certificate, documentPriority);
          
          // TODO Использовать конструктор в прикладной CreateExchangeDocumentSignature.
          var sign = new NpoComputer.DCX.Common.Signature
          {
            DocumentId = doc.ServiceEntityId,
            Content = signs[doc.ServiceEntityId],
            SignerInfo = signerInfo
          };
          
          outcomeReplyMessage.Signatures.Add(sign);
          
          if (doc.DocumentType == ReglamentDocumentType.NotificationReceipt)
          {
            var type = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.NoteReceipt;
            
            var docWithCertificate = Structures.Module.ReglamentDocumentWithCertificate.Create(documentForSign.FileName, documentForSign.Content,
                                                                                               certificate, sign.Content, documentForSign.ParentServiceEntityId,
                                                                                               box, document, null, documentForSign.ServiceEntityId, null,
                                                                                               exchangeDocumentInfo.ServiceCounterpartyId, false,
                                                                                               exchangeDocumentInfo, false, type, null);

            var isSendJobEnabled = PublicFunctions.Module.Remote.IsJobEnabled(PublicConstants.Module.SendSignedReceiptNotificationsId);
            this.SaveDeliveryConfirmationSigns(new List<Structures.Module.ReglamentDocumentWithCertificate> { docWithCertificate });
            
            var info = docWithCertificate.Info;
            
            var serviceDocument = info.ServiceDocuments.FirstOrDefault(d => d.DocumentType == docWithCertificate.ReglamentDocumentType);
            serviceDocument.CounterpartyId = docWithCertificate.ServiceCounterpartyId;
            serviceDocument.DocumentId = docWithCertificate.ServiceDocumentId;
            serviceDocument.ParentDocumentId = docWithCertificate.ParentDocumentId;
            serviceDocument.StageId = doc.DocflowStageId;
            serviceDocument.Date = doc.DateTime;
            info.Save();
          }
        }
      }
    }

    /// <summary>
    /// Отправить документ в сервис обмена.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="addenda">Приложения.</param>
    /// <param name="receiver">Получатель.</param>
    /// <param name="box">Абонентский ящик отправителя.</param>
    /// <param name="certificate">Сертификат, которым подписаны документы.</param>
    /// <param name="needSign">Требовать подписание от контрагента.</param>
    /// <param name="comment">Комментарий к сообщению в сервисе.</param>
    [Remote, Public]
    public virtual void SendDocuments(Sungero.Docflow.IOfficialDocument document, List<Sungero.Docflow.IOfficialDocument> addenda,
                                      Parties.ICounterparty receiver, ExchangeCore.IBusinessUnitBox box,
                                      ICertificate certificate, bool needSign, string comment)
    {
      this.SendDocuments(document, addenda, receiver, null, box, null, certificate, needSign, comment);
    }
    
    /// <summary>
    /// Отправить документ в сервис обмена.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="addenda">Приложения.</param>
    /// <param name="receiver">Получатель (головная организация или филиал контрагента).</param>
    /// <param name="receiverServiceDepartmentId">Внешний ИД подразделения контрагента.</param>
    /// <param name="senderBox">Абонентский ящик отправителя.</param>
    /// <param name="senderServiceDepartmentId">Внешний ИД подразделения абонентского ящика отправителя.</param>
    /// <param name="certificate">Сертификат, которым подписаны документы.</param>
    /// <param name="needSign">Требовать подписание от контрагента.</param>
    /// <param name="comment">Комментарий к сообщению в сервисе.</param>
    [Remote, Public]
    public virtual void SendDocuments(Sungero.Docflow.IOfficialDocument document, List<Sungero.Docflow.IOfficialDocument> addenda,
                                      Parties.ICounterparty receiver, string receiverServiceDepartmentId,
                                      ExchangeCore.IBusinessUnitBox senderBox, string senderServiceDepartmentId,
                                      ICertificate certificate, bool needSign, string comment)
    {
      if (HasNotApprovedDocuments(document, addenda))
        throw AppliedCodeException.Create(Resources.SendCounterpartyNotApproved);
      
      var documents = new List<Sungero.Docflow.IOfficialDocument>() { document };
      documents.AddRange(addenda);
      this.ValidateDocumentsBeforeSending(documents, certificate);
      
      var primaryDocuments = this.CreatePrimaryExchangeDocuments(document, addenda, receiver, receiverServiceDepartmentId,
                                                                 senderBox, senderServiceDepartmentId, certificate, needSign, comment);
      var signatures = this.CreateExchangeDocumentsSignatures(documents, receiver, receiverServiceDepartmentId,
                                                              senderBox, senderServiceDepartmentId,
                                                              certificate, needSign, comment);
      var client = GetClient(senderBox);
      var sentMessage = this.SendMessage(primaryDocuments, new List<NpoComputer.DCX.Common.IReglamentDocument>(), signatures, client,
                                         receiver, string.Empty, receiverServiceDepartmentId, senderBox, senderServiceDepartmentId, string.Empty);
      
      foreach (var ids in sentMessage.DocumentIds)
      {
        var doc = documents.Where(x => x.Id.ToString() == ids.LocalId).Single();
        var isPrimaryDocument = doc.Id == document.Id;
        var deliveryConfirmationStatus = ResolveReceiptStatus(ids.ReceiptStatus);
        this.ProcessDocumentAfterSendingToCounterparty(doc, receiver, receiverServiceDepartmentId, senderBox, certificate,
                                                       sentMessage, deliveryConfirmationStatus, ids.ServiceId, isPrimaryDocument, needSign);
      }
    }
    
    /// <summary>
    /// Проверить документы перед отправкой.
    /// </summary>
    /// <param name="documents">Документы.</param>
    /// <param name="certificate">Сертификат, которым подписаны документы.</param>
    /// <exception cref="AppliedCodeException">На документы нет прав на отправку контрагенту,
    /// или документы не подписаны.</exception>
    public virtual void ValidateDocumentsBeforeSending(List<Sungero.Docflow.IOfficialDocument> documents,
                                                       ICertificate certificate)
    {
      foreach (var doc in documents)
      {
        if (!doc.AccessRights.CanUpdate() || !doc.AccessRights.CanSendByExchange())
          throw AppliedCodeException.Create(Resources.SendCounterpartyAddendaNotRightFormat(doc.Name));
        
        var signature = this.GetDocumentSignature(doc, certificate);
        if (signature == null)
          throw AppliedCodeException.Create(Resources.SendCounterpartyAddendaNotSigned);
      }
    }
    
    /// <summary>
    /// Создать документы сервиса обмена из документов RX.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="addenda">Приложения.</param>
    /// <param name="receiver">Получатель (головная организация или филиал контрагента).</param>
    /// <param name="receiverServiceDepartmentId">Внешний ИД подразделения контрагента.</param>
    /// <param name="senderBox">Абонентский ящик отправителя.</param>
    /// <param name="senderServiceDepartmentId">Внешний ИД подразделения абонентского ящика отправителя.</param>
    /// <param name="certificate">Сертификат, которым подписаны документы.</param>
    /// <param name="needSign">Требовать подписание от контрагента.</param>
    /// <param name="comment">Комментарий к сообщению в сервисе.</param>
    /// <returns>Документы сервиса обмена.</returns>
    public virtual List<NpoComputer.DCX.Common.IDocument> CreatePrimaryExchangeDocuments(IOfficialDocument document,
                                                                                         List<Sungero.Docflow.IOfficialDocument> addenda,
                                                                                         Parties.ICounterparty receiver, string receiverServiceDepartmentId,
                                                                                         ExchangeCore.IBusinessUnitBox senderBox, string senderServiceDepartmentId,
                                                                                         ICertificate certificate, bool needSign, string comment)
    {
      var primaryDocuments = new List<NpoComputer.DCX.Common.IDocument>();
      var isRoamingExchange = this.IsRoamingExchange(receiver, senderBox);
      var exchangeServiceDocument = Functions.Module.CreatePrimaryExchangeServiceDocument(document, needSign, comment);
      if (isRoamingExchange)
        exchangeServiceDocument.NeedReceipt = true;
      primaryDocuments.Add(exchangeServiceDocument);
      
      foreach (var doc in addenda)
      {
        var documentNeedsSign = Docflow.PublicFunctions.OfficialDocument.NeedCounterpartySign(doc, senderBox, false, needSign);
        var exchangeServiceDocumentAddenda = Functions.Module.CreatePrimaryExchangeServiceDocument(doc,
                                                                                                   documentNeedsSign,
                                                                                                   string.Empty);
        if (isRoamingExchange)
          exchangeServiceDocumentAddenda.NeedReceipt = true;
        primaryDocuments.Add(exchangeServiceDocumentAddenda);
      }
      
      return primaryDocuments;
    }
    
    /// <summary>
    /// Создать подписи для документов обмена.
    /// </summary>
    /// <param name="documents">Документы.</param>
    /// <param name="receiver">Получатель (головная организация или филиал контрагента).</param>
    /// <param name="receiverServiceDepartmentId">Внешний ИД подразделения контрагента.</param>
    /// <param name="senderBox">Абонентский ящик отправителя.</param>
    /// <param name="senderServiceDepartmentId">Внешний ИД подразделения абонентского ящика отправителя.</param>
    /// <param name="certificate">Сертификат, которым подписаны документы.</param>
    /// <param name="needSign">Требовать подписание от контрагента.</param>
    /// <param name="comment">Комментарий к сообщению в сервисе.</param>
    /// <returns>Подписи сервиса обмена.</returns>
    public virtual List<NpoComputer.DCX.Common.Signature> CreateExchangeDocumentsSignatures(List<Sungero.Docflow.IOfficialDocument> documents,
                                                                                            Parties.ICounterparty receiver, string receiverServiceDepartmentId,
                                                                                            ExchangeCore.IBusinessUnitBox senderBox, string senderServiceDepartmentId,
                                                                                            ICertificate certificate, bool needSign, string comment)
    {
      var signatures = new List<NpoComputer.DCX.Common.Signature>();
      foreach (var doc in documents)
      {
        var signature = this.GetDocumentSignature(doc, certificate);
        var dcxSign = CreateExchangeDocumentSignature(senderBox, doc.Id.ToString(), signature.Body, signature.FormalizedPoAUnifiedRegNumber);
        signatures.Add(dcxSign);
      }
      return signatures;
    }

    /// <summary>
    /// Добавить номер машиночитаемой подписи в тело подписи для Сбис.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="signature">Структура с подписью.</param>
    /// <param name="senderBox">Абонентский ящик отправителя.</param>
    /// <param name="sentMessage">Отправленное сообщение.</param>
    /// <param name="serviceDocumentId">ИД документа в сервисе.</param>
    public virtual void AddFPoaUnifiedRegNumberToSignatureData(Sungero.Docflow.IOfficialDocument document,
                                                               Structures.Module.Signature signature,
                                                               ExchangeCore.IBusinessUnitBox senderBox,
                                                               NpoComputer.DCX.Common.SentMessage sentMessage,
                                                               string serviceDocumentId)
    {
      var needUpdateSign = senderBox.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis &&
        !string.IsNullOrEmpty(signature.FormalizedPoAUnifiedRegNumber);
      if (needUpdateSign)
      {
        var patсhedSignature = sentMessage.Signatures.Single(s => string.Equals(s.DocumentId, serviceDocumentId));
        Docflow.PublicFunctions.Module.SetDataSignature(document, signature.Id, patсhedSignature.Content);
      }
    }

    /// <summary>
    /// Обработать документ после отправки контрагенту.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="receiver">Получатель (головная организация или филиал контрагента).</param>
    /// <param name="receiverServiceDepartmentId">Внешний ИД подразделения контрагента.</param>
    /// <param name="senderBox">Абонентский ящик отправителя.</param>
    /// <param name="certificate">Сертификат, которым подписаны документы.</param>
    /// <param name="sentMessage">Отправленное сообщение.</param>
    /// <param name="deliveryConfirmationStatus">Статус ИОП.</param>
    /// <param name="serviceId">ИД в сервисе обмена.</param>
    /// <param name="isPrimaryDocument">Признак, что документ основной.</param>
    /// <param name="needSign">Признак Требуется подписание.</param>
    public virtual void ProcessDocumentAfterSendingToCounterparty(Sungero.Docflow.IOfficialDocument document,
                                                                  ICounterparty receiver,
                                                                  string receiverServiceDepartmentId,
                                                                  ExchangeCore.IBusinessUnitBox senderBox,
                                                                  ICertificate certificate,
                                                                  NpoComputer.DCX.Common.SentMessage sentMessage,
                                                                  Enumeration? deliveryConfirmationStatus,
                                                                  string serviceId,
                                                                  bool isPrimaryDocument,
                                                                  bool needSign)
    {
      document.DeliveryMethod = Docflow.PublicFunctions.MailDeliveryMethod.Remote.GetExchangeDeliveryMethod();
      
      var needCounterpartySign = Docflow.PublicFunctions.OfficialDocument.NeedCounterpartySign(document, senderBox, isPrimaryDocument, needSign);
      var counterpartyDepartmentBox = GetCounterpartyDepartmentBox(receiver, receiverServiceDepartmentId, senderBox);
      var info = SaveExternalDocumentInfo(document, serviceId, sentMessage.ServiceMessageId,
                                          needCounterpartySign, receiver, counterpartyDepartmentBox,
                                          senderBox, deliveryConfirmationStatus);
      
      var signature = this.GetDocumentSignature(document, certificate);
      info.SenderSignId = signature.Id;
      
      this.AddFPoaUnifiedRegNumberToSignatureData(document, signature, senderBox, sentMessage, document.Id.ToString());
      
      var accountingDoc = Docflow.AccountingDocumentBases.As(document);
      if (accountingDoc != null)
        accountingDoc.BusinessUnitBox = senderBox;
      
      var exchangeStateAfterSending = needCounterpartySign ? Docflow.OfficialDocument.ExchangeState.SignAwaited : Docflow.OfficialDocument.ExchangeState.Sent;
      if (accountingDoc != null && accountingDoc.IsFormalized == true)
      {
        accountingDoc.SellerSignatureId = this.GetDocumentSignature(accountingDoc, certificate).Id;
        Docflow.PublicFunctions.Module.GenerateTempPublicBodyForExchangeDocument(accountingDoc, accountingDoc.SellerTitleId.Value);
        Functions.Module.EnqueueXmlToPdfBodyConverter(accountingDoc, accountingDoc.SellerTitleId.Value, exchangeStateAfterSending);
      }
      else
      {
        Docflow.PublicFunctions.Module.GenerateTempPublicBodyForExchangeDocument(document, info.VersionId.Value);
        Functions.Module.EnqueueXmlToPdfBodyConverter(document, info.VersionId.Value, exchangeStateAfterSending);
      }
      
      MarkDocumentAsSended(info, document, receiver, false, senderBox, needCounterpartySign ? SignStatus.Waiting : SignStatus.None);
    }
    
    /// <summary>
    /// Проверить, что с получателем установлен обмен через роуминг.
    /// </summary>
    /// <param name="receiver">Получатель.</param>
    /// <param name="box">Абонентский ящик отправителя.</param>
    /// <returns>True - если с получателем установлен обмен через роуминг.</returns>
    public bool IsRoamingExchange(Parties.ICounterparty receiver, ExchangeCore.IBusinessUnitBox box)
    {
      var exchangeBox = receiver.ExchangeBoxes
        .FirstOrDefault(b => Equals(b.Status, Sungero.Parties.CounterpartyExchangeBoxes.Status.Active) &&
                        Equals(b.Box, box) &&
                        b.IsDefault == true);
      
      if (exchangeBox == null)
        return false;

      return exchangeBox.IsRoaming == true;
    }
    
    /// <summary>
    /// Сохранить ИД документа в сервисе обмена.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="serviceId">ИД в сервисе обмена.</param>
    /// <param name="messageId">ИД сообщения.</param>
    /// <param name="needSign">Признак требования подписания.</param>
    /// <param name="counterparty">Контрагент - получатель.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="receiptStatus">Статус ИОП.</param>
    /// <returns>ExternalLink.</returns>
    protected static IExchangeDocumentInfo SaveExternalDocumentInfo(IOfficialDocument document, string serviceId, string messageId, bool needSign,
                                                                    ICounterparty counterparty, IBusinessUnitBox box, Enumeration? receiptStatus)
    {
      return SaveExternalDocumentInfo(document, serviceId, messageId, needSign, counterparty, null, box, receiptStatus);
    }
    
    /// <summary>
    /// Сохранить ИД документа в сервисе обмена.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="serviceId">ИД в сервисе обмена.</param>
    /// <param name="messageId">ИД сообщения.</param>
    /// <param name="needSign">Признак требования подписания.</param>
    /// <param name="counterparty">Контрагент - получатель.</param>
    /// <param name="counterpartyDepartmentBox">Подразделение получателя.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="receiptStatus">Статус ИОП.</param>
    /// <returns>ExternalLink.</returns>
    public static IExchangeDocumentInfo SaveExternalDocumentInfo(IOfficialDocument document, string serviceId, string messageId, bool needSign,
                                                                 ICounterparty counterparty, ExchangeCore.ICounterpartyDepartmentBox counterpartyDepartmentBox,
                                                                 IBusinessUnitBox box, Enumeration? receiptStatus)
    {
      var receiverLine = counterparty.ExchangeBoxes.Where(x => Equals(x.Box, box) && x.IsDefault == true).SingleOrDefault();
      if (receiverLine == null)
        throw AppliedCodeException.Create(string.Format("Для контрагента c ИД {0} не установлена связь через указанный абонентский ящик с ИД {1}.", counterparty.Id, box.Id));
      var serviceCounterpartyId = receiverLine.OrganizationId;
      
      var newInfo = ExchangeDocumentInfos.Create();
      
      newInfo.Document = document;
      newInfo.Box = box;
      newInfo.RootBox = box;
      newInfo.ServiceDocumentId = serviceId;
      newInfo.MessageType = Exchange.ExchangeDocumentInfo.MessageType.Outgoing;
      newInfo.ServiceMessageId = messageId;
      newInfo.Counterparty = counterparty;
      newInfo.CounterpartyDepartmentBox = counterpartyDepartmentBox;
      newInfo.MessageDate = Calendar.Now;
      newInfo.NeedSign = needSign;
      newInfo.VersionId = document.LastVersion.Id;
      newInfo.DeliveryConfirmationStatus = receiptStatus;
      newInfo.ServiceCounterpartyId = serviceCounterpartyId;
      
      newInfo.Save();
      return newInfo;
    }

    /// <summary>
    /// Получить подпись документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="certificate">Сертификат.</param>
    /// <returns>Подпись.</returns>
    public virtual Structures.Module.Signature GetDocumentSignature(IOfficialDocument document, ICertificate certificate)
    {
      var version = document.LastVersion;
      if (version == null)
        return null;
      
      var keyFPoA = Docflow.PublicConstants.Module.UnsignedAdditionalInfoKeyFPoA + Docflow.PublicConstants.Module.UnsignedAdditionalInfoSeparator.KeyValue;
      var signature = Signatures.Get(version).Where(x => x.IsValid && x.SignCertificate != null)
        .Where(x => x.SignCertificate.Thumbprint.Equals(certificate.Thumbprint, StringComparison.InvariantCultureIgnoreCase))
        .OrderByDescending(x => !string.IsNullOrEmpty(x.UnsignedAdditionalInfo) && x.UnsignedAdditionalInfo.Contains(keyFPoA))
        .ThenByDescending(x => x.Id)
        .FirstOrDefault();
      
      if (signature == null)
        return null;
      
      var unifiedRegistrationNumber = Docflow.PublicFunctions.Module.GetUnsignedAttribute(signature, Docflow.PublicConstants.Module.UnsignedAdditionalInfoKeyFPoA);
      
      return Structures.Module.Signature.Create(signature.GetDataSignature(), signature.Id, unifiedRegistrationNumber);
    }
    
    /// <summary>
    /// Создать основной документ сервиса обмена для соглашения об аннулировании.
    /// </summary>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="serviceMessageId">ИД сообщения на сервисе.</param>
    /// <param name="parentServiceEntityId">ИД родительской сущности на сервисе.</param>
    /// <param name="serviceEntityId">ИД сущности на сервисе.</param>
    /// <param name="needSign">Требуется подписание.</param>
    /// <param name="comment">Комментарий.</param>
    /// <returns>Основной документ сервиса обмена для соглашения об аннулировании.</returns>
    public virtual NpoComputer.DCX.Common.IDocument CreatePrimaryDocumentForCancellationAgreement(Exchange.ICancellationAgreement cancellationAgreement,
                                                                                                  string serviceMessageId, string parentServiceEntityId,
                                                                                                  string serviceEntityId,
                                                                                                  bool needSign, string comment)
    {
      byte[] content;
      using (var memory = new System.IO.MemoryStream())
      {
        using (var sourceStream = cancellationAgreement.LastVersion.Body.Read())
          sourceStream.CopyTo(memory);
        content = memory.ToArray();
      }
      
      var documentName = Functions.Module.GetExchangeDocumentName(cancellationAgreement);
      documentName = Functions.Module.GetValidFileName(documentName);
      var fileName = string.Format("{0}.{1}", documentName, cancellationAgreement.LastVersion.BodyAssociatedApplication.Extension);
      fileName = CommonLibrary.FileUtils.NormalizeFileName(fileName);
      
      return new NpoComputer.DCX.Common.Document()
      {
        ServiceMessageId = serviceMessageId,
        ParentServiceEntityId = parentServiceEntityId,
        ServiceEntityId = serviceEntityId,
        DocumentType = Functions.Module.GetDCXDocumentType(cancellationAgreement),
        FileName = fileName,
        Content = content,
        NeedSign = needSign,
        Comment = comment,
        Date = cancellationAgreement.DocumentDate.Value
      };
    }

    /// <summary>
    /// Создать новый основной документ из документа RX.
    /// </summary>
    /// <param name="document">Документ RX.</param>
    /// <param name="needSign">Требуется подписание.</param>
    /// <param name="comment">Комментарий.</param>
    /// <returns>Основной документ сервиса обмена.</returns>
    public virtual NpoComputer.DCX.Common.IDocument CreatePrimaryExchangeServiceDocument(IOfficialDocument document, bool needSign, string comment)
    {
      byte[] content;
      using (var memory = new System.IO.MemoryStream())
      {
        using (var sourceStream = document.LastVersion.Body.Read())
          sourceStream.CopyTo(memory);
        content = memory.ToArray();
      }
      
      var documentName = Functions.Module.GetExchangeDocumentName(document);
      documentName = Functions.Module.GetValidFileName(documentName);
      var fileName = string.Format("{0}.{1}", documentName, document.LastVersion.BodyAssociatedApplication.Extension);
      fileName = CommonLibrary.FileUtils.NormalizeFileName(fileName);
      
      return new NpoComputer.DCX.Common.Document()
      {
        ServiceEntityId = document.Id.ToString(),
        DocumentType = Functions.Module.GetDCXDocumentType(document),
        FileName = fileName,
        Content = content,
        NeedSign = needSign,
        Comment = comment,
        Date = document.DocumentDate.Value
      };
    }
    
    /// <summary>
    /// Получить имя документа для отправки в сервис обмена.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>Имя документа для отправки в сервис обмена.</returns>
    public virtual string GetExchangeDocumentName(IOfficialDocument document)
    {
      var extensionLength = document.LastVersion.BodyAssociatedApplication.Extension.Length;
      var documentNameMaxLength = Constants.Module.ExchangeDocumentMaxLength - extensionLength - 2;
      return Sungero.Docflow.PublicFunctions.Module.CutText(document.Name, documentNameMaxLength);
    }

    /// <summary>
    /// Создать подпись для документа обмена.
    /// </summary>
    /// <param name="box">Наш абонентский ящик.</param>
    /// <param name="documentId">ИД документа.</param>
    /// <param name="signature">Подпись.</param>
    /// <param name="formalizedPoAUnifiedRegNumber">Единый регистрационный номер эл. доверенности.</param>
    /// <returns>Подпись сервиса обмена.</returns>
    public static NpoComputer.DCX.Common.Signature CreateExchangeDocumentSignature(IBusinessUnitBox box, string documentId, byte[] signature, string formalizedPoAUnifiedRegNumber)
    {
      string formalizedPoALink = null;
      string formalizedPoALinkTitle = null;
      
      if (box.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis && !string.IsNullOrEmpty(formalizedPoAUnifiedRegNumber))
      {
        var serviceFormalizedPoA = box.FormalizedPoAInfos.Where(p => string.Equals(formalizedPoAUnifiedRegNumber, p.UnifiedRegistrationNumber)).SingleOrDefault();
        formalizedPoALink = serviceFormalizedPoA != null ? serviceFormalizedPoA.Url : Functions.Module.GetFormalizedPoALink(formalizedPoAUnifiedRegNumber);
        formalizedPoALinkTitle = serviceFormalizedPoA != null ? serviceFormalizedPoA.Description : Functions.Module.GetFormalizedPoALinkTitle(formalizedPoAUnifiedRegNumber);
      }
      
      return new NpoComputer.DCX.Common.Signature()
      {
        Content = signature,
        DocumentId = documentId,
        FormalizedPoAUnifiedRegNumber = formalizedPoAUnifiedRegNumber,
        FormalizedPoALink = formalizedPoALink,
        FormalizedPoALinkTitle = formalizedPoALinkTitle
      };
    }

    /// <summary>
    /// Создать новый регламентный документ из временного документа.
    /// </summary>
    /// <param name="document">Временный документ.</param>
    /// <param name="documentType">Тип регламентного документа.</param>
    /// <returns>Регламентный документ сервиса обмена.</returns>
    public virtual NpoComputer.DCX.Common.ReglamentDocument CreateReglamentExchangeServiceDocument(
      Sungero.Exchange.Structures.Module.ReglamentDocumentWithCertificate document,
      NpoComputer.DCX.Common.ReglamentDocumentType documentType)
    {
      var documentId = this.GetReglamentDocumentId(document, documentType);
      var stageId = this.GetReglamentDocumentStageId(document, documentType);

      if (string.IsNullOrEmpty(documentId))
        documentId = Guid.NewGuid().ToString();
      
      return new NpoComputer.DCX.Common.ReglamentDocument()
      {
        ServiceEntityId = documentId,
        DocumentType = documentType,
        FileName = document.Name,
        ParentServiceEntityId = document.ParentDocumentId,
        Content = document.Content,
        DocflowStageId = stageId
      };
    }
    
    /// <summary>
    /// Получить ИД регламентного документа на сервисе.
    /// </summary>
    /// <param name="document">Регламентный документ.</param>
    /// <param name="documentType">Тип регламентного документа.</param>
    /// <returns>ИД регламентного документа на сервисе.</returns>
    protected virtual string GetReglamentDocumentId(ReglamentDocumentWithCertificate document, ReglamentDocumentType documentType)
    {
      return this.GetSupportedReglamentDocumentTypes().Contains(documentType) ? document.Info.ExternalBuyerTitleId : document.ServiceDocumentId;
    }
    
    /// <summary>
    /// Получить ИД этапа регламентного документа на сервисе.
    /// </summary>
    /// <param name="document">Регламентный документ.</param>
    /// <param name="documentType">Тип регламентного документа.</param>
    /// <returns>ИД этапа регламентного документа на сервисе.</returns>
    protected virtual string GetReglamentDocumentStageId(ReglamentDocumentWithCertificate document, ReglamentDocumentType documentType)
    {
      return this.GetSupportedReglamentDocumentTypes().Contains(documentType) ? document.Info.StageId : document.ServiceDocumentStageId;
    }
    
    /// <summary>
    /// Получить тип документа в DCX.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>Тип документа в DCX.</returns>
    public virtual NpoComputer.DCX.Common.DocumentType GetDCXDocumentType(IOfficialDocument document)
    {
      var documentType = NpoComputer.DCX.Common.DocumentType.Nonformalized;
      
      if (Exchange.CancellationAgreements.Is(document))
        documentType = NpoComputer.DCX.Common.DocumentType.RevocationOffer;
      
      var accounting = Docflow.AccountingDocumentBases.As(document);
      if (accounting != null && accounting.FormalizedServiceType != null)
      {
        var exchangeService = accounting.BusinessUnitBox.ExchangeService.ExchangeProvider;
        if (accounting.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.Act)
          documentType = DocumentType.Act;
        if (accounting.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.GeneralTransfer)
        {
          if (accounting.IsAdjustment == true)
          {
            if (accounting.IsRevision == true)
            {
              if (accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.Schf)
                if (exchangeService == ExchangeCore.ExchangeService.ExchangeProvider.Diadoc)
                  documentType = DocumentType.InvoiceCorrectionRevision;
                else
                  documentType = DocumentType.GeneralTransferSchfCorrectionRevisionSeller;
              if (accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.SchfDop)
                documentType = DocumentType.GeneralTransferSchfDopCorrectionRevisionSeller;
              if (accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.Dop)
                documentType = DocumentType.GeneralTransferDopCorrectionRevisionSeller;
            }
            else
            {
              if (accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.Schf)
                if (exchangeService == ExchangeCore.ExchangeService.ExchangeProvider.Diadoc)
                  documentType = DocumentType.InvoiceCorrection;
                else
                  documentType = DocumentType.GeneralTransferSchfCorrectionSeller;
              if (accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.SchfDop)
                documentType = DocumentType.GeneralTransferSchfDopCorrectionSeller;
              if (accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.Dop)
                documentType = DocumentType.GeneralTransferDopCorrectionSeller;
            }
          }
          else
          {
            if (accounting.IsRevision == true)
            {
              if (accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.Schf)
                if (exchangeService == ExchangeCore.ExchangeService.ExchangeProvider.Diadoc)
                  documentType = DocumentType.InvoiceRevision;
                else
                  documentType = DocumentType.GeneralTransferSchfRevisionSeller;
              if (accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.SchfDop)
                documentType = DocumentType.GeneralTransferSchfDopRevisionSeller;
              if (accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.Dop)
                documentType = DocumentType.GeneralTransferDopRevisionSeller;
            }
            else
            {
              if (accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.Schf)
                if (exchangeService == ExchangeCore.ExchangeService.ExchangeProvider.Diadoc)
                  documentType = DocumentType.Invoice;
                else
                  documentType = DocumentType.GeneralTransferSchfSeller;
              if (accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.SchfDop)
                documentType = DocumentType.GeneralTransferSchfDopSeller;
              if (accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.Dop)
                documentType = DocumentType.GeneralTransferDopSeller;
            }
          }
        }
        if (accounting.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.GoodsTransfer)
          if (exchangeService == ExchangeCore.ExchangeService.ExchangeProvider.Diadoc)
            documentType = DocumentType.Waybill;
          else
            documentType = accounting.IsRevision == true ? DocumentType.GoodsTransferRevisionSeller : DocumentType.GoodsTransferSeller;
        if (accounting.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.Invoice)
        {
          if (accounting.IsAdjustment == true)
          {
            if (accounting.IsRevision == true)
              documentType = DocumentType.InvoiceCorrectionRevision;
            else
              documentType = DocumentType.InvoiceCorrection;
          }
          else
          {
            if (accounting.IsRevision == true)
              documentType = DocumentType.InvoiceRevision;
            else
              documentType = DocumentType.Invoice;
          }
        }
        if (accounting.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.Waybill)
          documentType = DocumentType.Waybill;
        if (accounting.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.WorksTransfer)
          if (exchangeService == ExchangeCore.ExchangeService.ExchangeProvider.Diadoc)
            documentType = DocumentType.Act;
          else
            documentType = accounting.IsRevision == true ? DocumentType.WorksTransferRevisionSeller : DocumentType.WorksTransferSeller;
      }
      return documentType;
    }
    
    #endregion

    #region Генерация титула
    
    /// <summary>
    /// Сгенерировать титул покупателя.
    /// </summary>
    /// <param name="statement">Документ.</param>
    /// <param name="buyerTitle">Структура с данными для генерации титула.</param>
    [Public]
    public virtual void GenerateBuyerTitle(Docflow.IAccountingDocumentBase statement, Docflow.Structures.AccountingDocumentBase.IBuyerTitle buyerTitle)
    {
      if (statement.IsFormalized != true)
        return;
      
      byte[] sellerTitle;
      using (var memory = new System.IO.MemoryStream())
      {
        statement.Versions.Single(v => v.Id == statement.SellerTitleId).Body.Read().CopyTo(memory);
        sellerTitle = memory.ToArray();
      }
      
      var exchangeInfo = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(statement);
      if (exchangeInfo == null)
        return;
      
      var client = GetClient(ExchangeCore.PublicFunctions.BoxBase.GetRootBox(exchangeInfo.Box));
      
      var title = new NpoComputer.DCX.Common.BuyerTitle();
      title.AcceptanceDate = buyerTitle.AcceptanceDate.Value;
      title.OrganizationName = statement.BusinessUnit.LegalName;
      title.ActOfDisagreement = this.GetActOfDisagreementText(buyerTitle);
      title.SignResult = this.GetBuyerTitleSignResult(buyerTitle);
      title.Signer.FirstName = buyerTitle.Signatory.Person.FirstName;
      title.Signer.LastName = buyerTitle.Signatory.Person.LastName;
      title.Signer.MiddleName = buyerTitle.Signatory.Person.MiddleName;
      title.Signer.JobTitle = this.GetBuyerSignatoryJobTitle(buyerTitle);
      title.Signer.TIN = statement.BusinessUnit.TIN;
      title.Signer.SignerPowers = Functions.Module.GetSignerPowers(buyerTitle.SignatoryPowers);
      title.Signer.PowersBase = buyerTitle.SignatoryPowersBase;
      
      title.SellerTitle = sellerTitle;
      if (statement.IsAdjustment == true)
      {
        title.DocumentTypeNamedId = statement.IsRevision == true ? Constants.Module.DocumentTypeNamedId.UniversalCorrectionDocumentRevision
          : Constants.Module.DocumentTypeNamedId.UniversalCorrectionDocument;
        title.DocumentVersion = Constants.Module.UCDVersion;
      }
      
      if (buyerTitle.Consignee != null)
      {
        title.Consignee = new Consignee();
        title.Consignee.FirstName = buyerTitle.Consignee.Person.FirstName;
        title.Consignee.LastName = buyerTitle.Consignee.Person.LastName;
        title.Consignee.MiddleName = buyerTitle.Consignee.Person.MiddleName;
        title.Consignee.JobTitle = this.GetBuyerConsigneeJobTitle(buyerTitle);
        title.Consignee.PowersBase = buyerTitle.ConsigneePowersBase;
        
        this.FillAttorney(title.Consignee, buyerTitle.ConsigneePowerOfAttorney, buyerTitle.ConsigneeOtherReason);
      }
      
      FileFromService xml = null;
      
      var signResultAccepted = buyerTitle.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Accepted;
      
      if (statement.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.WorksTransfer)
      {
        title.OperationContent = signResultAccepted ? "Результаты работ (оказанных услуг) приняты без претензий" : title.ActOfDisagreement;
        this.LogDebugFormat(exchangeInfo, "Start GenerateWorksTransferXmlForBuyer.");
        xml = client.GenerateWorksTransferXmlForBuyer(title, exchangeInfo.ServiceMessageId, exchangeInfo.ServiceDocumentId);
      }
      
      if (statement.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.Act)
      {
        title.OperationContent = signResultAccepted ? "Услуги оказаны в полном объеме" : title.ActOfDisagreement;
        this.LogDebugFormat(exchangeInfo, "Start GenerateActXmlForBuyer.");
        xml = client.GenerateActXmlForBuyer(title, exchangeInfo.ServiceMessageId, exchangeInfo.ServiceDocumentId);
      }
      
      if (statement.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.GeneralTransfer &&
          statement.FormalizedFunction != Docflow.AccountingDocumentBase.FormalizedFunction.Schf)
      {
        if (statement.IsAdjustment != true)
          title.OperationContent = string.IsNullOrEmpty(buyerTitle.ActOfDisagreement) ? this.GetBuyerAcceptanceStatusText(buyerTitle) : title.ActOfDisagreement;
        else
          title.OperationContent = "С изменением стоимости согласен";
        
        this.LogDebugFormat(exchangeInfo, "Start {0}.", statement.IsAdjustment == true ? "GenerateUniversalTransferCorrectionDocumentXmlForBuyer" : "GenerateUniversalTransferDocumentXmlForBuyer");
        xml = statement.IsAdjustment == true ?
          client.GenerateUniversalTransferCorrectionDocumentXmlForBuyer(title, exchangeInfo.ServiceMessageId, exchangeInfo.ServiceDocumentId) :
          client.GenerateUniversalTransferDocumentXmlForBuyer(title, exchangeInfo.ServiceMessageId, exchangeInfo.ServiceDocumentId);
      }
      
      if (statement.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.GoodsTransfer)
      {
        title.OperationContent = signResultAccepted ? "Перечисленные в документе ценности приняты без претензий" : title.ActOfDisagreement;
        this.LogDebugFormat(exchangeInfo, "Start GenerateGoodsTransferXmlForBuyer.");
        xml = client.GenerateGoodsTransferXmlForBuyer(title, exchangeInfo.ServiceMessageId, exchangeInfo.ServiceDocumentId);
      }
      
      if (statement.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.Waybill)
      {
        title.OperationContent = signResultAccepted ? "Товары переданы" : title.ActOfDisagreement;
        this.LogDebugFormat(exchangeInfo, "Start GenerateTorg12XmlForBuyer.");
        xml = client.GenerateTorg12XmlForBuyer(title, exchangeInfo.ServiceMessageId, exchangeInfo.ServiceDocumentId);
      }
      
      if (xml != null)
      {
        using (var memory = new System.IO.MemoryStream(xml.Content))
        {
          if (!HasUnsignedBuyerTitle(statement))
          {
            // При создании версии чистится статус эл. обмена, восстанавливаем его.
            var exchangeState = statement.ExchangeState;
            statement.CreateVersion();
            statement.ExchangeState = exchangeState;
          }
          
          var version = statement.LastVersion;
          version.AssociatedApplication = GetOrCreateAssociatedApplicationByDocumentName("file.xml");
          version.Note = FinancialArchive.Resources.BuyerTitleVersionNote;
          statement.BuyerTitleId = version.Id;
          statement.OurSignatory = buyerTitle.Signatory;
          statement.OurSigningReason = buyerTitle.SignatureSetting;
          version.Body.Write(memory);
          statement.Save();
        }
        
        // Сохранить ИД титула покупателя и ИД этапа. ID передается для СБИС, для Диадок - пустое значение.
        exchangeInfo = ExchangeDocumentInfos.Get(exchangeInfo.Id);
        exchangeInfo.ExternalBuyerTitleId = xml.ServiceDocumentId;
        exchangeInfo.StageId = xml.StageId;
        
        if (statement.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.GeneralTransfer && statement.IsAdjustment != true)
          exchangeInfo.BuyerAcceptanceStatus = buyerTitle.BuyerAcceptanceStatus;
        exchangeInfo.Save();
      }
    }
    
    /// <summary>
    /// Получить текст разногласий.
    /// </summary>
    /// <param name="buyerTitle">Титул покупателя.</param>
    /// <returns>Текст разногласий.</returns>
    protected virtual string GetActOfDisagreementText(Docflow.Structures.AccountingDocumentBase.IBuyerTitle buyerTitle)
    {
      var actOfDisagreementText = !string.IsNullOrEmpty(buyerTitle.ActOfDisagreement) ? ": " + buyerTitle.ActOfDisagreement : string.Empty;
      if (buyerTitle.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.PartiallyAccepted)
        return "Принято с разногласиями" + actOfDisagreementText;
      else if (buyerTitle.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Rejected)
        return "Не принято" + actOfDisagreementText;
      else
        return string.Empty;
    }
    
    /// <summary>
    /// Получить текст расшифровки кода итога.
    /// </summary>
    /// <param name="buyerTitle">Титул покупателя.</param>
    /// <returns>Текст расшифровки кода итога.</returns>
    protected virtual string GetBuyerAcceptanceStatusText(Docflow.Structures.AccountingDocumentBase.IBuyerTitle buyerTitle)
    {
      if (buyerTitle.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Accepted)
        return "Товары (работы, услуги, права) приняты без расхождений (претензий)";
      else if (buyerTitle.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.PartiallyAccepted)
        return "Товары (работы, услуги, права) приняты с расхождениями (претензией)";
      else if (buyerTitle.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Rejected)
        return "Товары (работы, услуги, права) не приняты";

      return string.Empty;
    }
    
    /// <summary>
    /// Получить результат приемки для титула покупателя.
    /// </summary>
    /// <param name="buyerTitle">Титул покупателя.</param>
    /// <returns>Результат приемки в DCX.</returns>
    protected virtual SignResult GetBuyerTitleSignResult(Docflow.Structures.AccountingDocumentBase.IBuyerTitle buyerTitle)
    {
      if (buyerTitle.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Accepted)
        return SignResult.Signed;
      else if (buyerTitle.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.PartiallyAccepted)
        return SignResult.SignedWithAct;
      else if (buyerTitle.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Rejected)
        return SignResult.NotAccepted;
      
      throw AppliedCodeException.Create(string.Format("Unsupported BuyerAcceptanceStatus: '{0}'.", buyerTitle.BuyerAcceptanceStatus));
    }
    
    /// <summary>
    /// Получить наименование должности грузополучателя для титула покупателя.
    /// </summary>
    /// <param name="buyerTitle">Титул покупателя.</param>
    /// <returns>Наименование должности.</returns>
    public virtual string GetBuyerConsigneeJobTitle(Docflow.Structures.AccountingDocumentBase.IBuyerTitle buyerTitle)
    {
      if (buyerTitle == null)
        return null;
      
      var signatoryJobTitle = this.GetBuyerSignatoryJobTitle(buyerTitle);
      var consigneeJobTitle = buyerTitle.Consignee.JobTitle != null ? buyerTitle.Consignee.JobTitle.Name : null;
      return Docflow.PublicFunctions.Module.CutText(buyerTitle.Signatory == buyerTitle.Consignee ? signatoryJobTitle : consigneeJobTitle,
                                                    Docflow.PublicConstants.AccountingDocumentBase.JobTitleMaxLength);
    }
    
    /// <summary>
    /// Получить наименование должности подписанта для титула покупателя.
    /// </summary>
    /// <param name="buyerTitle">Титул покупателя.</param>
    /// <returns>Наименование должности.</returns>
    public virtual string GetBuyerSignatoryJobTitle(Docflow.Structures.AccountingDocumentBase.IBuyerTitle buyerTitle)
    {
      if (buyerTitle == null)
        return null;
      
      var settingJobTitle = buyerTitle.SignatureSetting != null && buyerTitle.SignatureSetting.JobTitle != null ? buyerTitle.SignatureSetting.JobTitle.Name : null;
      var signatoryJobTitle = buyerTitle.Signatory.JobTitle != null ? buyerTitle.Signatory.JobTitle.Name : null;
      return Docflow.PublicFunctions.Module.CutText(settingJobTitle != null ? settingJobTitle : signatoryJobTitle,
                                                    Docflow.PublicConstants.AccountingDocumentBase.JobTitleMaxLength);
    }
    
    /// <summary>
    /// Заполнить информацию о подписанте.
    /// </summary>
    /// <param name="title">Титул покупателя.</param>
    /// <param name="consignee">Подписывающий.</param>
    /// <param name="powerOfAttorney">Доверенность.</param>
    /// <param name="otherReason">Основание подписания.</param>
    [Obsolete("Используйте метод FillAttorney без параметра Титул покупателя.")]
    protected virtual void FillAttorney(BuyerTitle title, Consignee consignee, IPowerOfAttorneyBase powerOfAttorney, string otherReason)
    {
      this.FillAttorney(consignee, powerOfAttorney, otherReason);
    }
    
    /// <summary>
    /// Заполнить информацию о подписанте.
    /// </summary>
    /// <param name="consignee">Подписывающий.</param>
    /// <param name="powerOfAttorney">Доверенность.</param>
    /// <param name="otherReason">Основание подписания.</param>
    protected virtual void FillAttorney(Consignee consignee, IPowerOfAttorneyBase powerOfAttorney, string otherReason)
    {
      if (consignee == null)
        return;
      
      if (powerOfAttorney != null)
      {
        consignee.PowersBase = Docflow.SignatureSettings.Info.Properties.Reason.GetLocalizedValue(Docflow.SignatureSetting.Reason.PowerOfAttorney);
        
        var number = string.Empty;
        if (Docflow.FormalizedPowerOfAttorneys.Is(powerOfAttorney))
          number = Docflow.FormalizedPowerOfAttorneys.As(powerOfAttorney).UnifiedRegistrationNumber;
        else
          number = powerOfAttorney.RegistrationNumber;
        
        if (!string.IsNullOrWhiteSpace(number))
          consignee.PowersBase += Docflow.OfficialDocuments.Resources.Number + number;
        
        if (powerOfAttorney.RegistrationDate != null)
          consignee.PowersBase += Docflow.OfficialDocuments.Resources.DateFrom + powerOfAttorney.RegistrationDate.Value.ToString("d");
      }
      else if (!string.IsNullOrWhiteSpace(otherReason))
        consignee.PowersBase = otherReason;
    }

    /// <summary>
    /// Заполнить информацию о подписанте.
    /// </summary>
    /// <param name="consignee">Подписывающий.</param>
    /// <param name="signatureSetting">Право подписи.</param>
    protected virtual void FillSignerPowersBase(Consignee consignee, Docflow.ISignatureSetting signatureSetting)
    {
      if (consignee == null || signatureSetting == null)
        return;
      
      consignee.PowersBase = Docflow.PublicFunctions.Module.GetPowersBase(signatureSetting);
    }
    
    /// <summary>
    /// Проверка наличия неподписанного титула покупателя.
    /// </summary>
    /// <param name="statement">Документ.</param>
    /// <returns>Признак наличия неподписанного титула покупателя.</returns>
    [Public, Remote]
    public static bool HasUnsignedBuyerTitle(Docflow.IAccountingDocumentBase statement)
    {
      if (statement.BuyerTitleId != null)
      {
        var existingBuyerTitle = statement.Versions.Where(x => x.Id == statement.BuyerTitleId).FirstOrDefault();
        if (existingBuyerTitle != null && !Signatures.Get(existingBuyerTitle).Any())
          return true;
      }
      
      return false;
    }
    
    /// <summary>
    /// Проверка, запрошено ли УОУ контрагентом.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>True, если запрошено УОУ.</returns>
    [Public, Remote, Obsolete("Теперь функция не актуальна, т.к. реализована поддержка частичного подписания.")]
    public virtual bool IsInvoiceAmendmentRequest(IOfficialDocument document)
    {
      var documentInfo = Sungero.Exchange.PublicFunctions.ExchangeDocumentInfo.Remote.GetLastDocumentInfo(document);
      if (documentInfo == null)
        return false;
      
      var serviceDocuments = documentInfo.ServiceDocuments
        .Where(x => x.Date != null && (x.DocumentType == ExchDocumentType.Reject || x.DocumentType == ExchDocumentType.IReject))
        .OrderByDescending(x => x.Date);
      return serviceDocuments.Any();
    }
    
    /// <summary>
    /// Проверка возможности отправки ответной подписи по документу.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>True - если можно отправить подпись, иначе  - false.</returns>
    [Public, Remote]
    public virtual bool CanSendSign(IOfficialDocument document)
    {
      var documentInfo = Sungero.Exchange.PublicFunctions.ExchangeDocumentInfo.Remote.GetLastDocumentInfo(document);
      if (documentInfo == null)
        return false;
      
      var client = GetClient(documentInfo.RootBox);
      var allowedAnswers = this.GetDocumentAllowedAnswers(document, documentInfo, client);
      return allowedAnswers.CanSendSign;
    }
    
    /// <summary>
    /// Получить область полномочий.
    /// </summary>
    /// <param name="authority">Полномочие.</param>
    /// <returns>Область полномочий.</returns>
    public virtual NpoComputer.DCX.Common.SignerPowers GetSignerPowers(string authority)
    {
      if (authority == Docflow.AccountingDocumentBases.Resources.PropertiesFillingDialog_HasAuthority_SignSchf)
        return SignerPowers.InvoiceSigner;
      
      if (authority == Docflow.AccountingDocumentBases.Resources.PropertiesFillingDialog_HasAuthority_Register)
        return SignerPowers.PersonDocumentedOperation;
      
      if (authority == Docflow.AccountingDocumentBases.Resources.PropertiesFillingDialog_HasAuthority_Deal)
        return SignerPowers.PersonMadeOperation;
      
      if (authority == Docflow.AccountingDocumentBases.Resources.PropertiesFillingDialog_HasAuthority_DealAndRegister)
        return SignerPowers.MadeAndSignOperation;
      
      if (authority == Docflow.AccountingDocumentBases.Resources.PropertiesFillingDialog_HasAuthority_SignSchfAndRegister)
        return SignerPowers.ResponsibleForOperationAndSignerForInvoice;
      
      if (authority == Docflow.AccountingDocumentBases.Resources.PropertiesFillingDialog_HasAuthority_Other)
        return SignerPowers.Other;
      
      throw AppliedCodeException.Create(Sungero.Exchange.Resources.NotFoundAuthority);
    }
    
    #endregion
    
    #region Генерация извещений о получении
    
    /// <summary>
    /// Получить сгенерированные извещения о получении.
    /// </summary>
    /// <param name="documentInfos">Информация о документах МКДО.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="certificate">Сертификат для подписания ИОП.</param>
    /// <param name="generateServiceDocuments">Сгенерировать ИОП.</param>
    /// <returns>Извещения о получении и сертификат, которым они должны быть подписаны.</returns>
    [Remote]
    public virtual System.Collections.Generic.List<Structures.Module.ReglamentDocumentWithCertificate> GetGeneratedDeliveryConfirmationDocuments(List<Exchange.IExchangeDocumentInfo> documentInfos,
                                                                                                                                                 ExchangeCore.IBusinessUnitBox box, ICertificate certificate, bool generateServiceDocuments)
    {
      if (certificate == null)
        throw AppliedCodeException.Create(Resources.CertificateNotFound);
      
      this.LogDebugFormat(box, "Execute GetGeneratedDeliveryConfirmationDocuments.");
      var processedMessagesId = new List<string>();
      var client = GetClient(box);
      var documents = new List<Structures.Module.ReglamentDocumentWithCertificate>();
      foreach (var docInfo in documentInfos)
      {
        this.LogDebugFormat(docInfo, "Execute GetGeneratedDeliveryConfirmationDocuments. Processing document info.");
        var documentInfo = ExchangeDocumentInfos.Get(docInfo.Id);
        
        // Удаляем ранее сгенерированные ИОП, если они сгенерированы под другой сертификат.
        if (generateServiceDocuments)
        {
          var serviceDocsList = documentInfo.ServiceDocuments.Where(d => d.Date == null && !Equals(certificate, d.Certificate) && d.Sign == null).ToList();
          foreach (var serviceDoc in serviceDocsList)
          {
            documentInfo.ServiceDocuments.Remove(serviceDoc);
            this.LogDebugFormat(documentInfo, "Execute GetGeneratedDeliveryConfirmationDocuments. Delete old receipts for info.");
          }
          documentInfo.Save();
        }
        
        var isInvoiceFlow = Functions.Module.IsInvoiceFlowDocument(documentInfo.Document);
        // Извещение о получении документа.
        var needProcessReceipt = GetUnprocessedReceiptStatuses().Contains(documentInfo.DeliveryConfirmationStatus);
        var needProcessBuyerReceipt = GetUnprocessedBuyerReceiptStatuses().Contains(documentInfo.BuyerDeliveryConfirmationStatus);
        if (needProcessReceipt || needProcessBuyerReceipt)
        {
          var documentType = isInvoiceFlow ?
            Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IReceipt :
            Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Receipt;
          
          var documentReceipts = documentInfo.ServiceDocuments
            .Where(d => d.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IReceipt ||
                   d.DocumentType == Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Receipt);
          var generated = needProcessReceipt ?
            documentReceipts.FirstOrDefault(r => r.ParentDocumentId == documentInfo.ServiceDocumentId) :
            documentReceipts.FirstOrDefault(r => r.ParentDocumentId == documentInfo.ExternalBuyerTitleId);
          
          byte[] content = null;
          string name = string.Empty;
          byte[] signature = null;
          var serviceDocumentId = string.Empty;
          var serviceDocumentStageId = string.Empty;
          var parentDocumentId = string.Empty;
          var receipts = new List<IReglamentDocument>();
          if (generated == null && generateServiceDocuments)
          {
            var canSendDeliveryConfirmation = false;
            try
            {
              canSendDeliveryConfirmation = client.CanSendDeliveryConfirmation(documentInfo.ServiceDocumentId, documentInfo.ServiceMessageId);
              this.LogDebugFormat(documentInfo, "Execute GetGeneratedDeliveryConfirmationDocuments. CanSendDeliveryConfirmation = '{0}' for info.", canSendDeliveryConfirmation);
            }
            catch (Exception ex)
            {
              this.LogDebugFormat(documentInfo, "Execute GetGeneratedDeliveryConfirmationDocuments. Error while getting document from the service to generate delivery confirmation: {0}.",
                                  ex.Message);
            }
            
            // Для СБИС хранится составной ServiceDocumentId, первая часть которого - ИД сообщения, вторая - ИД документа.
            // ИОПы необходимо отправлять одним сообщением на весь комплект документов.
            if (canSendDeliveryConfirmation && !processedMessagesId.Contains(documentInfo.ServiceDocumentId.Split('#').First()))
              receipts = this.GetReglamentDocuments(documentInfo, certificate, client);
          }
          else if (generated != null && Equals(generated.Certificate, certificate))
          {
            this.LogDebugFormat(documentInfo, "Execute GetGeneratedDeliveryConfirmationDocuments. Get reglament documents from system for info.");
            content = generated.Body;
            name = generated.GeneratedName;
            signature = generated.Sign;
            serviceDocumentId = generated.DocumentId;
            serviceDocumentStageId = generated.StageId;
            parentDocumentId = generated.ParentDocumentId;
          }

          if (content != null)
          {
            var serviceDocument = Structures.Module.ReglamentDocumentWithCertificate.Create(name, content, certificate, signature, parentDocumentId,
                                                                                            box, documentInfo.Document,
                                                                                            documentInfo.ServiceMessageId, serviceDocumentId, serviceDocumentStageId,
                                                                                            documentInfo.ServiceCounterpartyId, true, documentInfo, isInvoiceFlow,
                                                                                            documentType, null);
            documents.Add(serviceDocument);
          }
          
          if (receipts.Any())
          {
            foreach (var receipt in receipts)
            {
              // Получить тип служебного документа заново, т.к. по комплектам СБИСа ИОПы приходят пачкой на одну инфошку из комплекта.
              var rootDocumentInfo = documentInfos.Where(info => Equals(info.ServiceDocumentId, receipt.RootServiceEntityId)).FirstOrDefault();
              if (rootDocumentInfo != null)
                isInvoiceFlow = Functions.Module.IsInvoiceFlowDocument(rootDocumentInfo.Document);
              if (receipt.DocumentType == ReglamentDocumentType.Receipt)
                documentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Receipt;
              if (receipt.DocumentType == ReglamentDocumentType.InvoiceReceipt)
                documentType = Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IReceipt;
              
              content = receipt.Content;
              name = receipt.FileName;
              serviceDocumentId = receipt.ServiceEntityId;
              serviceDocumentStageId = receipt.DocflowStageId;
              
              this.LogDebugFormat(documentInfo,
                                  "Execute GetGeneratedDeliveryConfirmationDocuments. Generate receipt notification with DocumentType = {0}, ServiceCounterpartyId = {1}, ServiceEntityId = {2}, serviceDocumentStageId = {3}.",
                                  documentType, documentInfo.ServiceCounterpartyId, serviceDocumentId, serviceDocumentStageId);
              
              var parentDocumentInfo = documentInfos.Where(i => i.ServiceDocumentId == receipt.ParentServiceEntityId ||
                                                           i.ExternalBuyerTitleId == receipt.ParentServiceEntityId).FirstOrDefault();
              // Для письма СБИС есть 2 сущности: текстовое сообщение в XML и тело документа.
              // Текстовое сообщение не загружается в RX, но на него сервис генерирует ИОП.
              if (parentDocumentInfo != null)
              {
                var serviceDocument = Structures.Module.ReglamentDocumentWithCertificate.Create(name, content, certificate, signature, receipt.ParentServiceEntityId, box, parentDocumentInfo.Document,
                                                                                                parentDocumentInfo.ServiceMessageId, serviceDocumentId, serviceDocumentStageId, parentDocumentInfo.ServiceCounterpartyId,
                                                                                                true, parentDocumentInfo, isInvoiceFlow, documentType, null);
                documents.Add(serviceDocument);
              }
            }
          }
        }
        processedMessagesId.Add(documentInfo.ServiceDocumentId.Split('#').First());
      }
      return documents;
    }
    
    /// <summary>
    /// Получить служебные документы с сервиса обмена.
    /// </summary>
    /// <param name="documentInfo">Информация о документе обмена.</param>
    /// <param name="certificate">Сертификат.</param>
    /// <param name="client">Клиент.</param>
    /// <returns>Список служебных документов.</returns>
    private List<IReglamentDocument> GetReglamentDocuments(IExchangeDocumentInfo documentInfo, ICertificate certificate, DcxClient client)
    {
      this.LogDebugFormat(documentInfo, "Execute GetReglamentDocuments. Try get reglament documents from service for info.");
      bool isDocflowFinished = false;
      var dcxDocument = new NpoComputer.DCX.Common.Document();
      dcxDocument.ServiceMessageId = documentInfo.ServiceMessageId;
      dcxDocument.ServiceEntityId = documentInfo.ServiceDocumentId;
      dcxDocument.DocumentType = NpoComputer.DCX.Common.DocumentType.Nonformalized;
      var accountingDocument = Docflow.AccountingDocumentBases.As(documentInfo.Document);
      
      if (accountingDocument != null)
      {
        var exchangeService = accountingDocument.BusinessUnitBox.ExchangeService.ExchangeProvider;
        
        if (accountingDocument.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.WorksTransfer &&
            exchangeService != ExchangeCore.ExchangeService.ExchangeProvider.Diadoc)
          dcxDocument.DocumentType = NpoComputer.DCX.Common.DocumentType.WorksTransferSeller;
        
        if (accountingDocument.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.Act ||
            exchangeService == ExchangeCore.ExchangeService.ExchangeProvider.Diadoc &&
            accountingDocument.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.WorksTransfer)
          dcxDocument.DocumentType = NpoComputer.DCX.Common.DocumentType.Act;
        
        if (accountingDocument.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.GoodsTransfer &&
            exchangeService != ExchangeCore.ExchangeService.ExchangeProvider.Diadoc)
          dcxDocument.DocumentType = NpoComputer.DCX.Common.DocumentType.GoodsTransferSeller;
        
        if (accountingDocument.FormalizedServiceType == Docflow.AccountingDocumentBase.FormalizedServiceType.GeneralTransfer)
        {
          if (accountingDocument.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.Dop)
          {
            dcxDocument.DocumentType = accountingDocument.IsAdjustment == true ?
              NpoComputer.DCX.Common.DocumentType.GeneralTransferDopCorrectionSeller :
              NpoComputer.DCX.Common.DocumentType.GeneralTransferDopSeller;
          }
          else
          {
            dcxDocument.DocumentType = accountingDocument.IsAdjustment == true ?
              NpoComputer.DCX.Common.DocumentType.GeneralTransferSchfDopCorrectionSeller :
              NpoComputer.DCX.Common.DocumentType.GeneralTransferSchfDopSeller;
          }
        }
      }
      
      var signerInfo = NpoComputer.DCX.Common.SignerInfo.CreateFromSignature(certificate.X509Certificate);
      return client.GetNextReglamentDocuments(dcxDocument, signerInfo, out isDocflowFinished);
    }
    
    /// <summary>
    /// Сгенерировать уведомление об уточнении.
    /// </summary>
    /// <param name="documents">Документы.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="note">Комментарий.</param>
    /// <param name="throwError">Не гасить ошибку.</param>
    /// <param name="certificate">Сертификат для подписания УОУ.</param>
    /// <param name="sendInvoiceAmendmentRequest">True для УОУ, False для отказа.</param>
    /// <returns>Уведомления об уточнении и сертификат, которым они должны быть подписаны.</returns>
    [Remote]
    public virtual List<Structures.Module.ReglamentDocumentWithCertificate> GenerateAmendmentRequestDocuments(List<Docflow.IOfficialDocument> documents,
                                                                                                              ExchangeCore.IBoxBase box, string note,
                                                                                                              bool throwError, ICertificate certificate,
                                                                                                              bool sendInvoiceAmendmentRequest)
    {
      if (box == null)
        throw AppliedCodeException.Create(Resources.BoxIsNotValid);
      
      if (certificate == null)
        throw AppliedCodeException.Create(Resources.CertificateNotFound);
      
      var rootBox = ExchangeCore.PublicFunctions.BoxBase.GetRootBox(box);
      var docsWithCertificates = new List<Structures.Module.ReglamentDocumentWithCertificate>();
      foreach (var document in documents)
      {
        bool packageProcessing = false;
        try
        {
          var externalDocumentInfo = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(document);
          if (externalDocumentInfo == null)
            continue;
          
          var client = GetClient(rootBox);
          var allowedAnswers = this.GetDocumentAllowedAnswers(document, externalDocumentInfo, client);
          
          // Убеждаемся, что можно отправить хоть что-то.
          if (!allowedAnswers.CanSendInvoiceAmendmentRequest && !allowedAnswers.CanSendAmendmentRequest)
            throw AppliedCodeException.Create(Resources.AnswerIsAlreadySent);
          
          // Если нельзя отправить УОУ - отправляем отказ и наоборот.
          var isInvoiceAmendmentRequest = sendInvoiceAmendmentRequest;
          if (isInvoiceAmendmentRequest && !allowedAnswers.CanSendInvoiceAmendmentRequest)
            isInvoiceAmendmentRequest = false;
          if (!isInvoiceAmendmentRequest && !allowedAnswers.CanSendAmendmentRequest)
            isInvoiceAmendmentRequest = true;
          
          var rejects = new List<NpoComputer.DCX.Common.IReglamentDocument>();
          var packageDocumentsExchangeInfos = GetPackageDocumentsExchangeInfos(externalDocumentInfo.ServiceMessageId);
          var documentIds = documents.Select(d => d.Id);
          packageDocumentsExchangeInfos = packageDocumentsExchangeInfos.Where(info => documentIds.Contains(info.Document.Id)).ToList();
          var invoiceExchangeInfoIds = new List<int>();
          if (rootBox.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis &&
              packageDocumentsExchangeInfos.Count > 1)
          {
            var tempDocs = new List<NpoComputer.DCX.Common.IDocument>();
            foreach (IExchangeDocumentInfo info in packageDocumentsExchangeInfos)
            {
              var allowedAnswersSbis = this.GetDocumentAllowedAnswers(info.Document, info, client);
              if (allowedAnswersSbis.CanSendAmendmentRequest || allowedAnswersSbis.CanSendInvoiceAmendmentRequest)
              {
                tempDocs.Add(this.CreateDocumentForAmendmentRequest(info));
                if (allowedAnswersSbis.CanSendInvoiceAmendmentRequest == true)
                  invoiceExchangeInfoIds.Add(info.Id);
              }
            }
            // Для Сбиса передаем сразу сертификат, чтобы не искать в хранилище по отпечатку.
            rejects = client.GenerateInvoiceAmendmentRequestsForPackage(tempDocs, certificate.X509Certificate, note);
            packageProcessing = true;
          }
          else
          {
            var tempDoc = this.CreateDocumentForAmendmentRequest(externalDocumentInfo);

            // Для Сбиса передаем сразу сертификат, чтобы не искать в хранилище по отпечатку.
            // Для Сбиса генерируем всегда только уведомление об уточнении, т.к. нет разделения между отказом и уведомлением об уточнении.
            if (externalDocumentInfo.RootBox.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis)
              rejects.Add(client.GenerateInvoiceAmendmentRequest(tempDoc, certificate.X509Certificate, note));
            else
            {
              if (isInvoiceAmendmentRequest)
                rejects.Add(client.GenerateInvoiceAmendmentRequest(tempDoc, certificate.Thumbprint, note));
              else
                rejects.Add(client.GenerateAmendmentRequest(tempDoc, note, certificate.Thumbprint));
            }
          }
          
          foreach (NpoComputer.DCX.Common.IReglamentDocument reject in rejects)
          {
            var exchangeDocumentInfo = ExchangeDocumentInfos.GetAll().Where(i => i.ServiceDocumentId == reject.ParentServiceEntityId && Equals(i.RootBox, box)).First();
            var exchangeDocument = OfficialDocuments.Get(exchangeDocumentInfo.Document.Id);
            var formalizedPoA = Docflow.PublicFunctions.OfficialDocument.GetFormalizedPoA(exchangeDocument, Employees.Current, certificate);
            var serviceMessageId = CancellationAgreements.Is(document) ? externalDocumentInfo.ParentDocumentInfo.ServiceMessageId : externalDocumentInfo.ServiceMessageId;
            
            if (packageProcessing)
              isInvoiceAmendmentRequest = invoiceExchangeInfoIds.Contains(exchangeDocumentInfo.Id);
            var documentType = isInvoiceAmendmentRequest ? Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.IReject :
              Exchange.ExchangeDocumentInfoServiceDocuments.DocumentType.Reject;
            var doc = Structures.Module.ReglamentDocumentWithCertificate.Create(reject.FileName, reject.Content, certificate,
                                                                                null, exchangeDocumentInfo.ServiceDocumentId,
                                                                                rootBox,
                                                                                exchangeDocument, serviceMessageId, reject.ServiceEntityId, reject.DocflowStageId,
                                                                                exchangeDocumentInfo.ServiceCounterpartyId, false,
                                                                                exchangeDocumentInfo, isInvoiceAmendmentRequest, documentType, formalizedPoA?.UnifiedRegistrationNumber);
            docsWithCertificates.Add(doc);
          }
        }
        catch (AppliedCodeException ex)
        {
          // Гасить исключение, если операция недоступна в сервисе.
          if (ex.Message != Resources.AnswerIsAlreadySent || throwError)
            throw;
        }
        catch (Exception ex)
        {
          if (ex is CommonLibrary.Exceptions.PlatformException)
            throw;
          
          throw AppliedCodeException.Create(Resources.AmendmentRequestError);
        }
        
        if (packageProcessing)
          break;
      }
      return docsWithCertificates;
    }
    
    /// <summary>
    /// Создать документ для уведомления об уточнении.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Документ сервиса обмена для уведомления об уточнении.</returns>
    public virtual NpoComputer.DCX.Common.IDocument CreateDocumentForAmendmentRequest(IExchangeDocumentInfo documentInfo)
    {
      var isCancellationAgreement = CancellationAgreements.Is(documentInfo.Document);
      var document = new NpoComputer.DCX.Common.Document()
      {
        ServiceMessageId = isCancellationAgreement ? documentInfo.ParentDocumentInfo.ServiceMessageId : documentInfo.ServiceMessageId,
        ServiceEntityId = documentInfo.ServiceDocumentId
      };
      if (isCancellationAgreement)
        document.DocumentType = DocumentType.RevocationOffer;
      return document;
    }
    
    /// <summary>
    /// Сгенерировать извещение о получении на служебный документ.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="documentInfo">Информация.</param>
    /// <param name="client">Dcx клиент.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <param name="documentType">Тип служебного документа, на который генерируется ИОП.</param>
    /// <param name="certificate">Сертификат.</param>
    /// <param name="reglamentDocumentType">Тип служебного документа, который будет сгенерирован.</param>
    /// <param name="generateServiceDocuments">Перегенерировать ИОП.</param>
    /// <returns>Извещение о получении на служебный документ.</returns>
    protected virtual ReglamentDocumentWithCertificate GenerateInvoiceServiceDeliveryConfirmation(IOfficialDocument document,
                                                                                                  IExchangeDocumentInfo documentInfo, DcxClient client, IBusinessUnitBox box, Enumeration documentType,
                                                                                                  ICertificate certificate, Enumeration reglamentDocumentType, bool generateServiceDocuments)
    {
      var parentServiceDocument = documentInfo.ServiceDocuments.First(d => d.DocumentType == documentType);
      var generatedDocument = documentInfo.ServiceDocuments.FirstOrDefault(d => d.DocumentType == reglamentDocumentType);
      byte[] content = null;
      var name = string.Empty;
      byte[] signature = null;
      var serviceDocumentId = string.Empty;
      var serviceDocumentStageId = string.Empty;
      if (generatedDocument == null && generateServiceDocuments)
      {
        // Для Сбиса передаем сразу сертификат, чтобы не искать в хранилище по отпечатку.
        var receipt = box.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis ?
          client.GenerateInvoiceDeliveryConfirmation(parentServiceDocument.DocumentId, certificate.X509Certificate, documentInfo.ServiceDocumentId, documentInfo.ServiceMessageId) :
          client.GenerateInvoiceDeliveryConfirmation(parentServiceDocument.DocumentId, certificate.Thumbprint, documentInfo.ServiceDocumentId, documentInfo.ServiceMessageId);
        content = receipt.Content;
        name = receipt.FileName;
        serviceDocumentId = receipt.ServiceEntityId;
        serviceDocumentStageId = receipt.DocflowStageId;
        Logger.DebugFormat("Generate receipt notification with ExchangeDocumentInfoId = {0}, DocumentType = {1}, CounterpartyId = {2}," +
                           " ParentDocumentId = {3} LinkedDocumentId = {4}, ServiceMessageId = {5}, serviceDocumentId = {6}, serviceDocumentStageId = {7}",
                           documentInfo.Id, reglamentDocumentType, parentServiceDocument.CounterpartyId, parentServiceDocument.DocumentId,
                           document.Id, documentInfo.ServiceMessageId, serviceDocumentId, serviceDocumentStageId);
      }
      else if (generatedDocument != null && Equals(generatedDocument.Certificate, certificate))
      {
        content = generatedDocument.Body;
        name = generatedDocument.GeneratedName;
        signature = generatedDocument.Sign;
      }

      return content != null ?
        Structures.Module.ReglamentDocumentWithCertificate.Create(name, content, certificate, signature, parentServiceDocument.DocumentId, box, document,
                                                                  documentInfo.ServiceMessageId, serviceDocumentId, serviceDocumentStageId, parentServiceDocument.CounterpartyId, false, documentInfo,
                                                                  true, reglamentDocumentType, null)
        : null;
    }
    
    #endregion
    
    #region Формирование соглашения об аннулировании
    
    /// <summary>
    /// Импортировать соглашение об аннулировании.
    /// </summary>
    /// <param name="body">Тело соглашения.</param>
    /// <param name="leadingDocumentId">ИД аннулируемого документа.</param>
    /// <param name="reason">Причина аннулирования.</param>
    /// <param name="extentionObject">Дополнительные параметры.
    /// Параметр не используется в базовой функции, добавлен для передачи параметров в перекрытии.</param>
    /// <returns>ИД соглашения об аннулировании.</returns>
    [Public(WebApiRequestType = RequestType.Post)]
    public virtual int ImportCancellationAgreement(byte[] body, int leadingDocumentId, string reason, string extentionObject)
    {
      var errorMessage = this.ValidateBeforeImportCancellationAgreement(body, leadingDocumentId, reason, extentionObject);
      if (!string.IsNullOrEmpty(errorMessage))
      {
        Logger.ErrorFormat("{0} LeadingDocumentId - {1}.", errorMessage, leadingDocumentId);
        throw AppliedCodeException.Create(errorMessage);
      }
      
      var cancellationAgreement = this.ImportCancellationAgreement(body, leadingDocumentId, reason);
      this.FinalizeCancellationAgreementImporting(cancellationAgreement, extentionObject);
      return cancellationAgreement.Id;
    }
    
    /// <summary>
    /// Импортировать соглашение об аннулировании.
    /// </summary>
    /// <param name="body">Тело соглашения.</param>
    /// <param name="leadingDocumentId">ИД аннулируемого документа.</param>
    /// <param name="reason">Причина аннулирования.</param>
    /// <returns>Соглашение об аннулировании.</returns>
    /// <remarks>Используется для импорта соглашений об аннулировании из сервиса обмена (без валидаций).</remarks>
    public virtual ICancellationAgreement ImportCancellationAgreement(byte[] body, int leadingDocumentId, string reason)
    {
      var leadingDocument = OfficialDocuments.GetAll(d => d.Id == leadingDocumentId).FirstOrDefault();
      
      // Для импорта подписант не нужен.
      var cancellationAgreement = this.CreateCancellationAgreement(leadingDocument, reason, null);
      
      Functions.CancellationAgreement.CreateCancellationAgreementVersion(cancellationAgreement, body);
      Docflow.PublicFunctions.Module.GeneratePublicBodyForExchangeDocument(cancellationAgreement,
                                                                           cancellationAgreement.LastVersion.Id,
                                                                           cancellationAgreement.ExchangeState);
      
      return cancellationAgreement;
    }
    
    /// <summary>
    /// Создать соглашение об аннулировании.
    /// </summary>
    /// <param name="leadingDocument">Аннулируемый документ.</param>
    /// <param name="reason">Причина аннулирования.</param>
    /// <param name="ourSignatory">Подписант.</param>
    /// <returns>Соглашение об аннулировании.</returns>
    [Public, Remote]
    public virtual ICancellationAgreement CreateCancellationAgreement(IOfficialDocument leadingDocument, string reason, IEmployee ourSignatory)
    {
      var cancellationAgreement = CancellationAgreements.Create();
      var exchangeDocumentInfo = Functions.ExchangeDocumentInfo.GetLastDocumentInfo(leadingDocument);
      
      cancellationAgreement.LeadingDocument = leadingDocument;
      cancellationAgreement.BusinessUnit = exchangeDocumentInfo.RootBox.BusinessUnit;
      cancellationAgreement.Department = leadingDocument.Department;
      cancellationAgreement.Counterparty = exchangeDocumentInfo.Counterparty;
      cancellationAgreement.Reason = CutText(reason, cancellationAgreement.Info.Properties.Reason.Length);
      cancellationAgreement.OurSignatory = ourSignatory;
      cancellationAgreement.LifeCycleState = Exchange.CancellationAgreement.LifeCycleState.Draft;
      return cancellationAgreement;
    }
    
    /// <summary>
    /// Валидация импорта соглашения об аннулировании.
    /// </summary>
    /// <param name="body">Тело соглашения.</param>
    /// <param name="leadingDocumentId">ИД аннулируемого документа.</param>
    /// <param name="reason">Причина аннулирования.</param>
    /// <param name="options">Дополнительные параметры.</param>
    /// <returns>Сообщение об ошибке или пустая строка, если ошибок нет.</returns>
    [Public]
    public virtual string ValidateBeforeImportCancellationAgreement(byte[] body, int leadingDocumentId, string reason, string options)
    {
      var leadingDocument = OfficialDocuments.GetAll(d => d.Id == leadingDocumentId).FirstOrDefault();
      if (leadingDocument == null)
        return Docflow.Resources.PrimaryDocumentNotFoundError;
      
      if (Functions.ExchangeDocumentInfo.GetLastDocumentInfo(leadingDocument) == null)
        return Docflow.OfficialDocuments.Resources.DocumentNotFromService;
      
      if (this.IsLeadingDocumentRevoked(leadingDocument))
        return Sungero.Exchange.Resources.LeadingDocumentAlreadyCancelled;
      
      var isCancellationAgreementInProcess = false;
      AccessRights.AllowRead(() => { isCancellationAgreementInProcess = this.IsCancellationAgreementInProcess(leadingDocument); });
      if (isCancellationAgreementInProcess)
        return Sungero.Exchange.Resources.CancellationAgreementAlreadyExistsOutgoingMessage;
      
      if (string.IsNullOrEmpty(reason))
        return Resources.EmptyReasonError;
      
      if (body == null || body.Length == 0)
        return Resources.EmptyBodyError;
      
      return string.Empty;
    }
    
    /// <summary>
    /// Постобработка импортированного соглашения об аннулировании.
    /// </summary>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <param name="options">Дополнительные параметры.</param>
    /// <remarks>Метод создан для реализации дополнительной логики при импорте соглашения об аннулировании на перекрытии.</remarks>
    [Public]
    public virtual void FinalizeCancellationAgreementImporting(ICancellationAgreement cancellationAgreement, string options)
    {
      
    }
    
    /// <summary>
    /// Валидация перед созданием соглашения об аннулировании.
    /// </summary>
    /// <param name="leadingDocumentId">ИД основного документа.</param>
    /// <returns>Сообщение об ошибке или пустая строка, если создать соглашение об аннулировании можно.</returns>
    [Public, Remote]
    public virtual string ValidateBeforeCreatingCancellationAgreement(int leadingDocumentId)
    {
      var result = string.Empty;
      AccessRights.AllowRead(
        () =>
        {
          var leadingDocument = OfficialDocuments.GetAll(doc => doc.Id == leadingDocumentId).FirstOrDefault();
          if (leadingDocument == null)
          {
            result = Docflow.Resources.PrimaryDocumentNotFoundError;
            return;
          }
          
          if (this.IsLeadingDocumentRevoked(leadingDocument))
          {
            result = Sungero.Exchange.Resources.LeadingDocumentAlreadyCancelled;
            return;
          }
          
          if (this.IsCancellationAgreementInProcess(leadingDocument))
            result = Sungero.Exchange.Resources.CancellationAgreementAlreadyExists;
        });
      
      return result;
    }
    
    /// <summary>
    /// Валидация перед отправкой в сервис обмена соглашения об аннулировании.
    /// </summary>
    /// <param name="leadingDocument">Основной документ.</param>
    /// <returns>Сообщение об ошибке или пустая строка, если отправить соглашение об аннулировании можно.</returns>
    [Public, Remote]
    public virtual string ValidateBeforeSendingCancellationAgreement(IOfficialDocument leadingDocument)
    {
      var result = string.Empty;
      if (leadingDocument == null)
        return Docflow.Resources.PrimaryDocumentNotFoundError;
      
      if (this.IsLeadingDocumentRevoked(leadingDocument))
        return Sungero.Exchange.Resources.LeadingDocumentAlreadyCancelledDetailed;
      
      return result;
    }
    
    /// <summary>
    /// Проверить, аннулирован ли основной документ.
    /// </summary>
    /// <param name="leadingDocument">Основной документ.</param>
    /// <returns>True - если аннулирован, иначе - false.</returns>
    public virtual bool IsLeadingDocumentRevoked(IOfficialDocument leadingDocument)
    {
      var leadingDocumentInfo = Exchange.Functions.ExchangeDocumentInfo.GetLastDocumentInfo(leadingDocument);
      return leadingDocumentInfo.RevocationStatus == Exchange.ExchangeDocumentInfo.RevocationStatus.Revoked;
    }
    
    /// <summary>
    /// Проверить, находится ли соглашение об аннулировании в процессе подписания.
    /// </summary>
    /// <param name="leadingDocument">Основной документ.</param>
    /// <returns>True - если аннулирование в процессе, иначе - false.</returns>
    public virtual bool IsCancellationAgreementInProcess(IOfficialDocument leadingDocument)
    {
      var lastLeadingDocumentInfo = Functions.ExchangeDocumentInfo.GetLastDocumentInfo(leadingDocument);
      var сancellationAgreements = CancellationAgreements.GetAll().Where(x => Equals(x.LeadingDocument, leadingDocument));
      
      // До версии 4.6.100 при аннулировании не создавалась СоА, но в инфо осн. документа проставлялся статус запроса аннулирования.
      var lastCancellationAgreement = сancellationAgreements
        .OrderByDescending(ca => ca.Created)
        .FirstOrDefault();
      if (lastCancellationAgreement == null)
        return lastLeadingDocumentInfo.RevocationStatus == Exchange.ExchangeDocumentInfo.RevocationStatus.Waiting;
      
      // Если СоА отправлено, то есть информация, и можно найти все активные СоА по ней.
      var cancellationAgreementInfos = ExchangeDocumentInfos.GetAll().Where(x => Equals(x.ParentDocumentInfo, lastLeadingDocumentInfo));
      var cancellationAgreementsInProcessInService = cancellationAgreementInfos
        .Select(ca => ca.Document)
        .Where(ca => ca != null && ca.LifeCycleState != Docflow.OfficialDocument.LifeCycleState.Obsolete);
      if (cancellationAgreementsInProcessInService.Any())
        return true;
      
      // Считаем только СоА в разработке, т.к. действующие имеют инфо, а устаревшие игнорируются.
      var draftCancellationAgreements = сancellationAgreements
        .Where(x => x.ExchangeState == null &&
               (x.LifeCycleState == null || x.LifeCycleState != Docflow.OfficialDocument.LifeCycleState.Obsolete));
      if (draftCancellationAgreements.Any())
        return true;
      
      return false;
    }
    
    #endregion

    #region Отправка документов через диалог

    /// <summary>
    /// Формирование вспомогательной информации о документе для отправки контрагенту.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>Структура с дополнительной информацией.</returns>
    [Remote(IsPure = true)]
    public virtual Structures.Module.SendToCounterpartyInfo GetInfoForSendToCounterparty(Docflow.IOfficialDocument document)
    {
      if (document == null)
        return Structures.Module.SendToCounterpartyInfo.Create();
      
      if (CancellationAgreements.Is(document))
      {
        var cancellationAgreement = CancellationAgreements.As(document);
        return this.GetCancellationAgreementInfoForSendToCounterparty(cancellationAgreement);
      }
      
      var accountingDocument = Docflow.AccountingDocumentBases.As(document);
      if (accountingDocument != null && accountingDocument.IsFormalized == true)
        return this.GetFormalizedDocumentInfoForSendToCounterparty(accountingDocument);

      return this.GetNonFormalizedDocumentInfoForSendToCounterparty(document);
    }
    
    /// <summary>
    /// Формирование вспомогательной информации о соглашении об аннулировании для отправки контрагенту.
    /// </summary>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <returns>Структура с дополнительной информацией.</returns>
    public virtual Structures.Module.SendToCounterpartyInfo GetCancellationAgreementInfoForSendToCounterparty(ICancellationAgreement cancellationAgreement)
    {
      var result = Structures.Module.SendToCounterpartyInfo.Create();
      result.HasError = false;
      
      // Нельзя отправлять уже отправленные документы.
      var lastDocumentInfo = Functions.ExchangeDocumentInfo.GetLastDocumentInfo(cancellationAgreement);
      if (lastDocumentInfo != null && lastDocumentInfo.MessageType == Exchange.ExchangeDocumentInfo.MessageType.Outgoing)
      {
        result.Error = Exchange.Resources.DocumentIsAlreadySentToCounterparty;
        result.HasError = true;
        return result;
      }
      
      var cancellationAgreementInfo = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(cancellationAgreement);
      var parentInfo = Functions.ExchangeDocumentInfo.GetLastDocumentInfo(cancellationAgreement.LeadingDocument);
      
      result = this.FillSignInfo(cancellationAgreement, cancellationAgreementInfo, result);
      
      result.Counterparties = new List<Parties.ICounterparty>();
      result.DefaultCounterparty = parentInfo.Counterparty;
      result.CounterpartyDepartmentBox = parentInfo.CounterpartyDepartmentBox;
      var defaultCounterpartyError = this.CheckExchangeForCounterparty(result.DefaultCounterparty,
                                                                       cancellationAgreement,
                                                                       cancellationAgreement.BusinessUnit,
                                                                       result);
      if (!string.IsNullOrWhiteSpace(defaultCounterpartyError))
      {
        result.Error = defaultCounterpartyError;
        result.HasError = true;
        return result;
      }
      
      result.Boxes = new List<IBusinessUnitBox>();
      result.DefaultBox = ExchangeCore.PublicFunctions.BusinessUnitBox.Remote.GetConnectedBoxes()
        .Where(a => Equals(a, parentInfo.RootBox))
        .SingleOrDefault();
      if (result.DefaultBox == null)
      {
        result.Error = Resources.BoxIsNotValid;
        result.HasError = true;
        return result;
      }
      
      result = this.FillSignByCounterparty(cancellationAgreement, cancellationAgreementInfo, result, cancellationAgreement.BusinessUnit, null);
      if (result.AnswerIsSent)
        return result;
      
      // Вложить СоА на другие документы пакета как приложения (актуально только для СБИС).
      var addenda = this.GetSbisCancellationAgreementAddenda(cancellationAgreement);
      result.Addenda = new List<AddendumInfo>();
      foreach (var document in addenda)
      {
        var addendumInfo = new AddendumInfo() { Addendum = document };
        result.Addenda.Add(addendumInfo);
      }
      result.HasAddendaToSend = result.Addenda.Any();
      
      result = this.FillCertificates(cancellationAgreement, result.DefaultBox, result);
      
      return result;
    }
    
    /// <summary>
    /// Получить пакет соглашений об аннулировании, в который входит переданное соглашение об аннулировании, для СБИС.
    /// </summary>
    /// <param name="cancellationAgreement">Соглашение об аннулировании.</param>
    /// <returns>Приложения к соглашению об аннулировании.</returns>
    [Remote]
    public virtual List<IOfficialDocument> GetSbisCancellationAgreementAddenda(ICancellationAgreement cancellationAgreement)
    {
      var cancellationAgreementInfo = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(cancellationAgreement);
      var parentInfo = Functions.ExchangeDocumentInfo.GetLastDocumentInfo(cancellationAgreement.LeadingDocument);
      var addenda = new List<IOfficialDocument>();
      var isSbis = parentInfo.RootBox.ExchangeService.ExchangeProvider == ExchangeCore.ExchangeService.ExchangeProvider.Sbis;
      if (isSbis)
      {
        // Отправка ответа на входящие СоА.
        if (cancellationAgreementInfo != null)
        {
          var packageCancellationAgreementInfos = GetPackageCancellationAgreementInfos(cancellationAgreementInfo.ServiceMessageId)
            .Where(info => CancellationAgreements.Is(info.Document) && info.Id != cancellationAgreementInfo.Id);
          
          foreach (var packageInfo in packageCancellationAgreementInfos)
          {
            var client = GetClient(packageInfo.RootBox);
            var allowedAnswers = this.GetDocumentAllowedAnswers(packageInfo.Document, packageInfo, client);
            if (allowedAnswers.CanSendAmendmentRequest || allowedAnswers.CanSendSign)
              addenda.Add(packageInfo.Document);
          }
        }
        else
        {
          // Отправка исходящего СоА.
          var packageDocuments = GetSbisPackageDocuments(parentInfo.RootBox, parentInfo.ServiceMessageId);
          var parentDocumentsIds = packageDocuments.Where(d => !CancellationAgreements.Is(d)).Select(d => d.Id);
          
          var packageCancellationAgreements = CancellationAgreements.GetAll()
            .Where(x => parentDocumentsIds.Contains(x.LeadingDocument.Id))
            .ToList();
          
          foreach (var packageCancellationAgreement in packageCancellationAgreements)
          {
            if (!Equals(packageCancellationAgreement, cancellationAgreement) && !packageDocuments.Contains(packageCancellationAgreement))
              addenda.Add(packageCancellationAgreement);
          }
        }
      }
      
      return addenda;
    }
    
    /// <summary>
    /// Формирование вспомогательной информации о документе для отправки контрагенту.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>Структура с дополнительной информацией.</returns>
    [Remote(IsPure = true)]
    public virtual Structures.Module.SendToCounterpartyInfo GetFormalizedDocumentInfoForSendToCounterparty(Docflow.IAccountingDocumentBase document)
    {
      var result = Structures.Module.SendToCounterpartyInfo.Create();
      
      // Нельзя отправлять уже отправленные формализованные документы.
      var lastDocumentInfo = Functions.ExchangeDocumentInfo.GetLastDocumentInfo(document);
      if (lastDocumentInfo != null && lastDocumentInfo.MessageType == Exchange.ExchangeDocumentInfo.MessageType.Outgoing)
      {
        result.Error = Exchange.Resources.DocumentIsAlreadySentToCounterparty;
        result.HasError = true;
        return result;
      }
      
      return this.GetNonFormalizedDocumentInfoForSendToCounterparty(document);
    }
    
    /// <summary>
    /// Формирование вспомогательной информации о документе для отправки контрагенту.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>Структура с дополнительной информацией.</returns>
    public virtual Structures.Module.SendToCounterpartyInfo GetNonFormalizedDocumentInfoForSendToCounterparty(Docflow.IOfficialDocument document)
    {
      var result = Structures.Module.SendToCounterpartyInfo.Create();
      
      result.HasError = false;
      var exchangeDocumentInfo = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(document);
      result = this.FillSignInfo(document, exchangeDocumentInfo, result);
      
      var businessUnit = document.BusinessUnit;
      result = this.FillCounterpartyInfo(document, businessUnit, result);
      if (result.HasError)
        return result;
      
      result = this.FillExchangeBoxes(document, exchangeDocumentInfo, result, businessUnit);
      
      result.CounterpartyDepartmentBox = exchangeDocumentInfo?.CounterpartyDepartmentBox;
      result.CounterpartyDepartments = GetCounterpartyDepartments(result.DefaultCounterparty, result.DefaultBox);
      
      result = this.FillSignByCounterparty(document, exchangeDocumentInfo, result, businessUnit, null);
      if (result.AnswerIsSent)
        return result;
      
      result = this.FillAddendaInfo(document, result);
      
      result = this.FillCertificates(document, result.DefaultBox, result);
      
      return result;
    }
    
    /// <summary>
    /// Заполнить признаки подписанности документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="exchangeDocumentInfo">Информация о документе.</param>
    /// <param name="result">Вспомогательная информация о документе для отправки контрагенту.</param>
    /// <returns>Информация о документе с заполненной информацией о подписях.</returns>
    public virtual SendToCounterpartyInfo FillSignInfo(IOfficialDocument document,
                                                       IExchangeDocumentInfo exchangeDocumentInfo,
                                                       SendToCounterpartyInfo result)
    {
      result.CanApprove = !Docflow.PublicFunctions.OfficialDocument.Remote.GetApprovalValidationErrors(document, true).Any();
      result.HasApprovalSignature = !HasNotApprovedDocuments(document);
      result.IsSignedByCounterparty = Docflow.PublicFunctions.OfficialDocument.Remote.CanSendAnswer(document);
      
      result.BuyerAcceptanceStatus = exchangeDocumentInfo?.BuyerAcceptanceStatus;
      return result;
    }
    
    /// <summary>
    /// Заполнить абонентские ящики.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="exchangeDocumentInfo">Информация о документе.</param>
    /// <param name="result">Вспомогательная информация о документе для отправки контрагенту.</param>
    /// <param name="businessUnit">Наша организация.</param>
    /// <returns>Информация о документе с заполненными абоненскими ящиками.</returns>
    public virtual SendToCounterpartyInfo FillExchangeBoxes(IOfficialDocument document,
                                                            IExchangeDocumentInfo exchangeDocumentInfo, SendToCounterpartyInfo result,
                                                            IBusinessUnit businessUnit)
    {
      var allBoxes = GetAllExchangeBoxesToCounterparty(document, result.Counterparties);
      result.Boxes = allBoxes
        .Where(b => b.ConnectionStatus == ExchangeCore.BusinessUnitBox.ConnectionStatus.Connected)
        .ToList();
      if (!result.Boxes.Any())
      {
        if (!allBoxes.Any())
          result.Error = Resources.BoxesNotFound;
        else
          result.Error = Resources.BoxesNotConnected;
        
        result.HasError = true;
        return result;
      }
      
      if (result.IsSignedByCounterparty)
      {
        result.DefaultBox = ExchangeCore.PublicFunctions.BusinessUnitBox.Remote.GetConnectedBoxes()
          .Where(a => Equals(a, exchangeDocumentInfo.RootBox)).SingleOrDefault();
        if (result.DefaultBox == null)
        {
          result.Error = Resources.BoxIsNotValid;
          result.HasError = true;
        }
      }
      else
      {
        var businessUnitOrder = businessUnit ?? Company.PublicFunctions.BusinessUnit.Remote.GetBusinessUnit(Company.Employees.Current);
        if (Docflow.AccountingDocumentBases.Is(document) && Docflow.AccountingDocumentBases.As(document).IsFormalized == true)
          result.DefaultBox = Docflow.AccountingDocumentBases.As(document).BusinessUnitBox;
        if (result.DefaultBox == null)
          result.DefaultBox = result.Boxes.OrderByDescending(x => Equals(x.BusinessUnit, businessUnitOrder)).First();
      }
      
      return result;
    }

    /// <summary>
    /// Заполнить в информации о документе сертификаты для подписания.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <param name="result">Вспомогательная информация о документе для отправки контрагенту.</param>
    /// <returns>Информация о документе с заполненными сертификатами.</returns>
    [Remote(IsPure = true)]
    public virtual SendToCounterpartyInfo FillCertificates(IOfficialDocument document,
                                                           ExchangeCore.IBusinessUnitBox box,
                                                           SendToCounterpartyInfo result)
    {
      if (box != null && (result.HasApprovalSignature || result.IsSignedByCounterparty))
      {
        result.Certificates = this.GetDocumentCertificatesToBox(document, box);
        result.IsSignedByUs = result.Certificates.Certificates.Any();
      }
      else
      {
        result.Certificates = Structures.Module.DocumentCertificatesInfo
          .Create(new List<ICertificate>(), result.CanApprove, new List<ICertificate>());
        result.IsSignedByUs = false;
      }
      
      return result;
    }
    
    /// <summary>
    /// Заполнить варианты отправки ответа контрагенту.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="exchangeDocumentInfo">Информация о документе.</param>
    /// <param name="result">Вспомогательная информация о документе для отправки контрагенту.</param>
    /// <param name="businessUnit">Наша организация.</param>
    /// <param name="documentAllowedAnswers">Допустимые варианты подписания/отказа/УОУ на документ.</param>
    /// <returns>Информация о документе с вариантами отправки ответа контрагенту.</returns>
    protected virtual SendToCounterpartyInfo FillSignByCounterparty(IOfficialDocument document,
                                                                    IExchangeDocumentInfo exchangeDocumentInfo, SendToCounterpartyInfo result,
                                                                    IBusinessUnit businessUnit, NpoComputer.DCX.Common.DocumentAllowedAnswer documentAllowedAnswers)
    {
      if (!result.IsSignedByCounterparty)
        return result;
      
      if (result.DefaultBox == null)
      {
        result.Error = Resources.BoxIsNotValid;
        result.HasError = true;
        return result;
      }
      
      result.ParentDocumentId = exchangeDocumentInfo.ServiceDocumentId;
      if (documentAllowedAnswers == null)
        documentAllowedAnswers = this.GetAllowedAnswers(document, exchangeDocumentInfo, result, businessUnit);
      
      result.CanSendSignAsAnswer = documentAllowedAnswers.CanSendSign;
      result.CanSendAmendmentRequestAsAnswer = documentAllowedAnswers.CanSendAmendmentRequest;
      result.CanSendInvoiceAmendmentRequestAsAnswer = documentAllowedAnswers.CanSendInvoiceAmendmentRequest;
      result.AnswerIsSent = !result.CanSendAmendmentRequestAsAnswer && !result.CanSendSignAsAnswer && !result.CanSendInvoiceAmendmentRequestAsAnswer;
      
      if (document.Versions.Count > 1 && exchangeDocumentInfo != null)
      {
        var hasSign = Signatures
          .Get(document.Versions.OrderBy(v => v.Number).First())
          .Any(x => x.SignatureType == SignatureType.Approval && x.IsExternal == true);
        if (hasSign)
          result.NeedRejectFirstVersion = documentAllowedAnswers.CanSendAmendmentRequest;
      }
      
      return result;
    }
    
    /// <summary>
    /// Получить допустимые варианты подписания/отказа/УОУ на документ.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="exchangeDocumentInfo">Информация о документе.</param>
    /// <param name="result">Вспомогательная информация о документе для отправки контрагенту.</param>
    /// <param name="businessUnit">Наша организация.</param>
    /// <returns>Допустимые варианты подписания/отказа/УОУ на документ.</returns>
    public virtual NpoComputer.DCX.Common.DocumentAllowedAnswer GetAllowedAnswers(IOfficialDocument document,
                                                                                  IExchangeDocumentInfo exchangeDocumentInfo,
                                                                                  SendToCounterpartyInfo result,
                                                                                  IBusinessUnit businessUnit)
    {
      if (result.DefaultBox == null)
        return null;
      
      return this.GetDocumentAllowedAnswers(document, exchangeDocumentInfo, GetClient(result.DefaultBox));
    }

    /// <summary>
    /// Получить допустимые варианты подписания/отказа/УОУ на документ.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="exchangeDocumentInfo">Информация о документе.</param>
    /// <param name="client">Клиент.</param>
    /// <returns>Допустимые варианты подписания/отказа/УОУ на документ.</returns>
    public virtual NpoComputer.DCX.Common.DocumentAllowedAnswer GetDocumentAllowedAnswers(IOfficialDocument document,
                                                                                          IExchangeDocumentInfo exchangeDocumentInfo,
                                                                                          NpoComputer.DCX.ClientApi.Client client)
    {
      DocumentAllowedAnswer documentAllowedAnswers = null;
      
      if (CancellationAgreements.Is(document))
      {
        var parentServiceDocumentId = exchangeDocumentInfo.ParentDocumentInfo.ServiceDocumentId;
        var parentServiceMessageId = exchangeDocumentInfo.ParentDocumentInfo.ServiceMessageId;
        documentAllowedAnswers = client.GetCancellationAgreementAllowedAnswers(parentServiceDocumentId,
                                                                               parentServiceMessageId,
                                                                               exchangeDocumentInfo.ServiceDocumentId);
      }
      else
        documentAllowedAnswers = client.GetAllowedAnswers(exchangeDocumentInfo.ServiceDocumentId,
                                                          exchangeDocumentInfo.ServiceMessageId,
                                                          exchangeDocumentInfo.ExternalBuyerTitleId);
      return documentAllowedAnswers;
    }

    /// <summary>
    /// Заполнить вспомогательную информацию о приложениях, которые будут отправлены с основным документом.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="result">Вспомогательная информация о документе для отправки контрагенту.</param>
    /// <returns>Информация о документе и его приложениях для отправки контрагенту.</returns>
    protected virtual SendToCounterpartyInfo FillAddendaInfo(IOfficialDocument document, SendToCounterpartyInfo result)
    {
      var addenda = document.Relations
        .GetRelated().Union(document.Relations.GetRelatedFrom()).Distinct()
        .Select(e => Docflow.OfficialDocuments.As(e))
        .Where(d => d != null && d.HasVersions && d.AccessRights.CanUpdate() && d.AccessRights.CanSendByExchange()).ToList();

      result.Addenda = new List<Exchange.Structures.Module.AddendumInfo>();
      foreach (var addendum in addenda)
      {
        var addendumInfo = Exchange.Structures.Module.AddendumInfo.Create();
        addendumInfo.Addendum = addendum;

        var exchangeDocInfo = Functions.ExchangeDocumentInfo.GetIncomingExDocumentInfo(addendum);
        
        addendumInfo.BuyerAcceptanceStatus = exchangeDocInfo?.BuyerAcceptanceStatus;
        
        if (result.IsSignedByCounterparty)
        {
          if (!addendum.HasVersions)
            continue;

          if (exchangeDocInfo == null || exchangeDocInfo.NeedSign != true)
            continue;

          // Нельзя отвечать на документы, которые не требуют от нас подписания.
          if (addendum.ExchangeState != Docflow.OfficialDocument.ExchangeState.SignRequired)
            continue;
        }
        else
        {
          if (exchangeDocInfo != null && addendum.Versions.Count > 1)
            addendumInfo.NeedRejectFirstVersion = true;

          // Нельзя отправлять документы, у которых есть какой-то статус МКДО, они или пришли нам или уже ушли от нас.
          if (addendum.ExchangeState != null)
            continue;
        }

        result.Addenda.Add(addendumInfo);
      }

      result.HasAddendaToSend = result.Addenda.Any();
      return result;
    }
    
    /// <summary>
    /// Заполнить информацию о контрагентах.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="businessUnit">Наша организация.</param>
    /// <param name="result">Информация о документе для отправки контрагенту.</param>
    /// <returns>Информация о документе с данными о контрагентах.</returns>
    protected virtual SendToCounterpartyInfo FillCounterpartyInfo(IOfficialDocument document, IBusinessUnit businessUnit, SendToCounterpartyInfo result)
    {
      result.Counterparties = new List<Parties.ICounterparty>();
      
      var allBusinessUnitCounterparties = Parties.PublicFunctions.Counterparty.Remote.GetExchangeCounterparty(businessUnit);
      var documentCounterparties = Functions.ExchangeDocumentInfo.GetDocumentCounterparties(document);
      var extendedDocumentCounterparties = new List<ICounterparty>();
      
      var hasDocumentCounterparties = documentCounterparties != null && documentCounterparties.Any();
      if (hasDocumentCounterparties)
      {
        // В основном в документах 1 контрагент, работаем как раньше.
        if (documentCounterparties.Count < 2)
        {
          result.DefaultCounterparty = documentCounterparties.FirstOrDefault();
        }
        else
        {
          documentCounterparties = allBusinessUnitCounterparties.Intersect(documentCounterparties).ToList();
          
          // Если после фильтрации кто-то остался - он по умолчанию.
          if (documentCounterparties.Count < 2)
            result.DefaultCounterparty = documentCounterparties.FirstOrDefault();
        }
        
        var accountingDocument = Docflow.AccountingDocumentBases.As(document);
        if (accountingDocument != null && accountingDocument.IsFormalized == true)
        {
          var box = accountingDocument.BusinessUnitBox;
          allBusinessUnitCounterparties = allBusinessUnitCounterparties
            .Where(x => x.ExchangeBoxes.Any(b => Equals(b.Box, box)))
            .ToList();
        }
        
        foreach (var documentCounterparty in documentCounterparties)
        {
          var tin = documentCounterparty.TIN;
          var counterpartiesWithSameTin = allBusinessUnitCounterparties
            .Where(c => !string.IsNullOrEmpty(c.TIN) && Equals(c.TIN, tin))
            .ToList();
          if (counterpartiesWithSameTin.Any())
            extendedDocumentCounterparties.AddRange(counterpartiesWithSameTin);
        }
      }

      if (result.DefaultCounterparty != null)
      {
        if (extendedDocumentCounterparties.Any())
          result.Counterparties.AddRange(extendedDocumentCounterparties);
        if (!result.Counterparties.Contains(result.DefaultCounterparty))
          result.Counterparties.Add(result.DefaultCounterparty);
        
        var error = this.CheckExchangeForCounterparty(result.DefaultCounterparty, document, businessUnit, result);
        if (!string.IsNullOrWhiteSpace(error))
        {
          result.Error = error;
          result.HasError = true;
        }
      }
      else if (hasDocumentCounterparties)
      {
        result.Counterparties.AddRange(documentCounterparties);
        if (extendedDocumentCounterparties.Any())
          result.Counterparties.AddRange(extendedDocumentCounterparties.Except(result.Counterparties).ToList());
      }
      else
      {
        result.Counterparties.AddRange(allBusinessUnitCounterparties);
      }

      return result;
    }
    
    /// <summary>
    /// Проверить, что у контрагента настроен обмен.
    /// </summary>
    /// <param name="counterparty">Контрагент.</param>
    /// <param name="document">Документ.</param>
    /// <param name="businessUnit">Наша организация.</param>
    /// <param name="result">Информация о документе для отправки контрагенту.</param>
    /// <returns>Текст ошибки, если с контрагентом не установлен обмен, иначе - пустая строка.</returns>
    public virtual string CheckExchangeForCounterparty(Parties.ICounterparty counterparty,
                                                       Docflow.IOfficialDocument document,
                                                       IBusinessUnit businessUnit,
                                                       SendToCounterpartyInfo result)
    {
      if (counterparty == null)
        return string.Empty;
      
      var parties = counterparty.ExchangeBoxes
        .Where(x => Equals(x.Status, Parties.CounterpartyExchangeBoxes.Status.Active) && x.IsDefault == true);
      
      if (businessUnit != null)
        parties = parties.Where(x => Equals(x.Box.BusinessUnit, businessUnit));
      
      if (!parties.Any())
      {
        if (!result.IsSignedByCounterparty)
          return Resources.NeedSetExchangeForCounterparty;
        else
          return Resources.NoExchangeThroughThisService;
      }
      
      return string.Empty;
    }
    
    /// <summary>
    /// Получить сертификаты для подписания документов, которые будут отправлены через сервис обмена.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="box">Абонентский ящик обмена.</param>
    /// <returns>Информация о сертификатах.</returns>
    [Remote(IsPure = true)]
    public virtual Structures.Module.DocumentCertificatesInfo GetDocumentCertificatesToBox(Docflow.IOfficialDocument document,
                                                                                           ExchangeCore.IBusinessUnitBox box)
    {
      var version = document.LastVersion;
      var signatures = Signatures.Get(version).Where(s => s.IsExternal != true && s.IsValid && s.SignCertificate != null).ToList();
      var allowedCertificates = new List<ICertificate>();
      var allowedCertificatesThumbprints = new List<Structures.Module.Certificate>();
      
      var allCertificates = Certificates.GetAll().ToList();
      var currentUserCertificates = allCertificates.Where(x => Equals(x.Owner, Users.Current) && x.Enabled == true).ToList();
      var canSign = currentUserCertificates.Any();

      if (box.HasExchangeServiceCertificates == true)
      {
        var exchangeCertificates = box.ExchangeServiceCertificates.Select(x => x.Certificate).ToList();
        
        signatures = signatures
          .Where(s => exchangeCertificates.Any(x => x.Thumbprint.Equals(s.SignCertificate.Thumbprint, StringComparison.InvariantCultureIgnoreCase)))
          .ToList();
      }
      
      allowedCertificatesThumbprints = signatures
        .GroupBy(s => s.Signatory)
        .Select(x => x.OrderByDescending(s => Equals(s.SignatureType, SignatureType.Approval))
                .ThenByDescending(s => s.SigningDate)
                .First())
        .OrderByDescending(s => Equals(s.SignatureType, SignatureType.Approval))
        .ThenByDescending(s => s.SigningDate)
        .Select(s => Structures.Module.Certificate.Create(s.SignCertificate.Thumbprint, s.Signatory))
        .ToList();
      
      foreach (var cert in allowedCertificatesThumbprints)
      {
        var existCert = allCertificates.FirstOrDefault(x => x.Thumbprint.Equals(cert.Thumbprint, StringComparison.InvariantCultureIgnoreCase) &&
                                                       Equals(x.Owner, cert.Owner));
        
        if (existCert != null)
          allowedCertificates.Add(existCert);
      }
      
      allowedCertificates = allowedCertificates.Distinct().ToList();
      
      return Structures.Module.DocumentCertificatesInfo.Create(allowedCertificates, canSign, currentUserCertificates);
    }
    
    /// <summary>
    /// Получить подключенные ящики сервисов обмена для отправки документа контрагентам.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="counterparties">Контрагенты.</param>
    /// <returns>Список подключенных ящиков сервисов обмена.</returns>
    [Remote(IsPure = true)]
    public static List<ExchangeCore.IBusinessUnitBox> GetConnectedExchangeBoxesToCounterparty(Docflow.IOfficialDocument document,
                                                                                              List<Parties.ICounterparty> counterparties)
    {
      return GetAllExchangeBoxesToCounterparty(document, counterparties)
        .Where(b => b.ConnectionStatus == ExchangeCore.BusinessUnitBox.ConnectionStatus.Connected)
        .ToList();
    }
    
    /// <summary>
    /// Получить абонентский ящик подразделения контрагента.
    /// </summary>
    /// <param name="counterparty">Контрагент.</param>
    /// <param name="counterpartyDepartmentId">ИД подразделения контрагента в сервисе обмена.</param>
    /// <param name="businessUnitBox">Аб. ящик нашей организации.</param>
    /// <returns>Абонентский ящик подразделения контрагента.</returns>
    [Public, Remote(IsPure = true)]
    public static ExchangeCore.ICounterpartyDepartmentBox GetCounterpartyDepartmentBox(ICounterparty counterparty,
                                                                                       string counterpartyDepartmentId,
                                                                                       IBusinessUnitBox businessUnitBox)
    {
      if (string.IsNullOrEmpty(counterpartyDepartmentId))
        return CounterpartyDepartmentBoxes.Null;
      
      var counterpartyDepartmentBoxes = CounterpartyDepartmentBoxes.GetAll()
        .Where(x => Equals(x.Counterparty, counterparty) && Equals(x.DepartmentId, counterpartyDepartmentId) &&
               Equals(x.Box, businessUnitBox) && x.Status == ExchangeCore.CounterpartyDepartmentBox.Status.Active)
        .SingleOrDefault();
      if (counterpartyDepartmentBoxes == null)
      {
        var message = string.Format("Unknown counterparty department box with service Id: '{0}'. Counterparty Id: '{1}'. It is necessary to synchronize counterparties.",
                                    counterpartyDepartmentId, counterparty.Id.ToString());
        Functions.Module.LogDebugFormat(businessUnitBox, message);
      }
      return counterpartyDepartmentBoxes;
    }
    
    /// <summary>
    /// Получить ид подразделения контрагента в сервисе обмена.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <returns>Ид подразделения контрагента в сервисе обмена.</returns>
    protected static string GetServiceCounterpartyDepartmentId(IMessage message, bool isIncomingMessage)
    {
      var department = isIncomingMessage ? message.FromDepartment : message.ToDepartment;
      var serviceCounterpartyDepartmentId = string.Empty;
      if (department != null && string.IsNullOrEmpty(department.Kpp))
        serviceCounterpartyDepartmentId = department.Id;
      return serviceCounterpartyDepartmentId;
    }
    
    /// <summary>
    /// Получить ид филиала контрагента в сервисе обмена.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <returns>Ид филиала контрагента в сервисе обмена.</returns>
    protected static string GetServiceCounterpartyBranchId(IMessage message, bool isIncomingMessage)
    {
      var branch = isIncomingMessage ? message.FromDepartment : message.ToDepartment;
      var serviceCounterpartyBranchId = string.Empty;
      if (branch != null && !branch.IsHead && !string.IsNullOrEmpty(branch.Kpp))
        serviceCounterpartyBranchId = branch.Id;
      return serviceCounterpartyBranchId;
    }
    
    /// <summary>
    /// Получить ид филиала контрагента ИП в сервисе обмена.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="isIncomingMessage">Признак входящего сообщения.</param>
    /// <returns>Ид филиала контрагента ИП в сервисе обмена.</returns>
    protected static string GetIndividualServiceCounterpartyBranchId(IMessage message, bool isIncomingMessage)
    {
      var branch = isIncomingMessage ? message.FromDepartment : message.ToDepartment;
      var serviceCounterpartyBranchId = string.Empty;
      if (branch != null && !branch.IsHead)
        serviceCounterpartyBranchId = branch.Id;
      return serviceCounterpartyBranchId;
    }
    
    /// <summary>
    /// Получить абонентские ящики подразделений контрагента.
    /// </summary>
    /// <param name="counterparty">Контрагент.</param>
    /// <param name="businessUnitBox">Аб. ящик нашей организации.</param>
    /// <returns>Абонентские ящики подразделений контрагента.</returns>
    [Remote(IsPure = true)]
    public static List<ExchangeCore.ICounterpartyDepartmentBox> GetCounterpartyDepartments(ICounterparty counterparty,
                                                                                           IBusinessUnitBox businessUnitBox)
    {
      var counterpartyDepartmentBoxes = new List<ExchangeCore.ICounterpartyDepartmentBox>();
      
      if (counterparty != null && businessUnitBox != null)
      {
        var parentBranchId = counterparty.ExchangeBoxes
          .Where(x => Equals(x.Box, businessUnitBox) && x.IsDefault == true)
          .SingleOrDefault()?.CounterpartyBranchId ?? string.Empty;
        
        counterpartyDepartmentBoxes = CounterpartyDepartmentBoxes.GetAll()
          .Where(x => Equals(x.Counterparty, counterparty) &&
                 Equals(x.Box, businessUnitBox) && Equals(x.ParentBranchId ?? string.Empty, parentBranchId ?? string.Empty) &&
                 x.Status == ExchangeCore.CounterpartyDepartmentBox.Status.Active)
          .ToList();
      }
      
      return counterpartyDepartmentBoxes;
    }

    /// <summary>
    /// Получить все ящики сервисов обмена для отправки документа контрагентам.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="counterparties">Контрагенты.</param>
    /// <returns>Список всех ящиков сервисов обмена.</returns>
    [Remote(IsPure = true)]
    public static List<ExchangeCore.IBusinessUnitBox> GetAllExchangeBoxesToCounterparty(Docflow.IOfficialDocument document, List<Parties.ICounterparty> counterparties)
    {
      var boxes = counterparties.SelectMany(c => c.ExchangeBoxes
                                            .Where(b => b.Status == Parties.CounterpartyExchangeBoxes.Status.Active && b.IsDefault == true)
                                            .Select(b => b.Box)).ToList();
      
      boxes = boxes.Distinct().Where(x => x.Status == Sungero.CoreEntities.DatabookEntry.Status.Active).ToList();
      if (document.BusinessUnit != null)
        boxes = boxes.Where(x => Equals(x.BusinessUnit, document.BusinessUnit)).ToList();
      
      return boxes;
    }

    /// <summary>
    /// Проверить, что есть неподписанные документы.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="documents">Приложения.</param>
    /// <returns>True - если есть неподписанные документы, иначе - false.</returns>
    public static bool HasNotApprovedDocuments(IOfficialDocument document, List<IOfficialDocument> documents)
    {
      var notSigned = HasNotApprovedDocuments(document);
      if (notSigned)
        return true;
      
      return HasNotApprovedDocuments(documents.ToArray());
    }

    private static bool HasNotApprovedDocuments(IOfficialDocument document)
    {
      return HasNotApprovedDocuments(new[] { document });
    }

    private static bool HasNotApprovedDocuments(params IOfficialDocument[] documents)
    {
      foreach (var document in documents)
      {
        var signed = Signatures.Get(document.LastVersion)
          .Any(s => s.SignatureType == SignatureType.Approval && s.IsExternal != true && s.IsValid);
        if (!signed)
          return true;
      }
      return false;
    }
    
    #endregion
    
    #endregion
    
    #region Отправка извещений о получении
    
    /// <summary>
    /// Получить список информации о документах, для которых требуется отправить ИОП.
    /// </summary>
    /// <param name="box">Абонентский ящик нашей организации.</param>
    /// <param name="skip">Количество пропускаемых записей.</param>
    /// <param name="take">Количество получаемых записей.</param>
    /// <param name="withoutGenerated">True, если хотим получить только инфошки, по которым ещё надо выполнить генерацию ИОП.</param>
    /// <returns>Информация о документах, для которых требуется отправить ИОП.</returns>
    [Public, Remote(IsPure = true)]
    public List<IExchangeDocumentInfo> GetDocumentInfosWithoutReceiptNotificationPart(Sungero.ExchangeCore.IBusinessUnitBox box,
                                                                                      int skip, int take, bool withoutGenerated)
    {
      var documentInfos = this.GetDocumentInfosWithoutReceiptNotification(box, withoutGenerated).ToList();
      // Получить инфошки по сообщениям для обработки служебных документов сообщений целиком(одновременная отправка ИОПов на комплект. СБИС).
      var messagesIds = documentInfos.Select(d => d.ServiceMessageId).Distinct().Skip(skip).Take(take).ToList();
      documentInfos = documentInfos.Where(info => messagesIds.Contains(info.ServiceMessageId)).ToList();
      
      // Рассчитываем, что объем данных, запрошенных в take, будет небольшой.
      var documentIds = documentInfos.Select(d => d.Document.Id).Distinct().ToList();
      var availableIds = Docflow.OfficialDocuments.GetAll(d => documentIds.Contains(d.Id)).Select(d => d.Id).ToList();
      return documentInfos.Where(i => availableIds.Contains(i.Document.Id)).ToList();
    }

    /// <summary>
    /// Получить список информации о документах, для которых требуется отправить ИОП.
    /// </summary>
    /// <param name="box">Абонентский ящик нашей организации.</param>
    /// <param name="withoutGenerated">True, если хотим получить только инфошки, по которым ещё надо выполнить генерацию ИОП.</param>
    /// <returns>Информация о документах, для которых требуется отправить ИОП.</returns>
    [Public]
    public IQueryable<IExchangeDocumentInfo> GetDocumentInfosWithoutReceiptNotification(Sungero.ExchangeCore.IBusinessUnitBox box, bool withoutGenerated)
    {
      var documentInfos = ExchangeDocumentInfos.GetAll()
        .Where(x => Equals(x.RootBox, box) && x.Document != null &&
               (GetUnprocessedReceiptStatuses().Contains(x.DeliveryConfirmationStatus) ||
                GetUnprocessedBuyerReceiptStatuses().Contains(x.BuyerDeliveryConfirmationStatus)));
      
      if (withoutGenerated)
        return documentInfos.Where(x => x.DeliveryConfirmationStatus == Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Required ||
                                   x.BuyerDeliveryConfirmationStatus == Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Required);
      
      return documentInfos;
    }
    
    /// <summary>
    /// Получить статусы необработанных ИОП на основной документ.
    /// </summary>
    /// <returns>Статусы необработанных ИОП на основной документ.</returns>
    public static List<Enumeration?> GetUnprocessedReceiptStatuses()
    {
      return new List<Enumeration?>() {
        Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Required,
        Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Generated,
        Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Signed
      };
    }
    
    /// <summary>
    /// Получить статусы необработанных ИОП на титул покупателя.
    /// </summary>
    /// <returns>Статусы необработанных ИОП на титул покупателя.</returns>
    public static List<Enumeration?> GetUnprocessedBuyerReceiptStatuses()
    {
      return new List<Enumeration?>() {
        Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Required,
        Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Generated,
        Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Signed
      };
    }

    /// <summary>
    /// Определить, отправлены ли служебные документы.
    /// </summary>
    /// <param name="documentInfos">Список информации о документах обмена.</param>
    /// <returns>True - если документы не отправлены, иначе - false.</returns>
    [Remote(IsPure = true)]
    public bool IsReglamentDocumentsNotSent(List<IExchangeDocumentInfo> documentInfos)
    {
      var documentInfoIds = documentInfos.Select(i => i.ServiceDocumentId).ToList();
      return ExchangeDocumentInfos.GetAll(info => documentInfoIds.Contains(info.ServiceDocumentId))
        .Any(info => !Equals(info.DeliveryConfirmationStatus, Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Sent));
    }
    
    /// <summary>
    /// Получить список документов, для которых требуется отправить ИОП.
    /// </summary>
    /// <param name="box">Абонентский ящик нашей организации.</param>
    /// <returns>Список документов, для которых требуется отправить ИОП.</returns>
    [Remote]
    public IQueryable<Content.IElectronicDocument> GetDocumentsWithoutReceiptNotification(Sungero.ExchangeCore.IBusinessUnitBox box)
    {
      return this.GetDocumentInfosWithoutReceiptNotification(box, false).Select(d => d.Document);
    }
    
    /// <summary>
    /// Проставить признак получения ИОПа.
    /// </summary>
    /// <param name="info">Информация о документе МКДО.</param>
    /// <param name="comment">Комментарий.</param>
    /// <param name="sent">True - отправка ИОП из RX, иначе - false.</param>
    [Remote]
    public virtual void FixReceiptNotification(Exchange.IExchangeDocumentInfo info, string comment, bool sent)
    {
      var operation = new Enumeration(Constants.Module.Exchange.SendReadMark);
      this.LogDebugFormat(info, "Execute FixReceiptNotification.");
      info = Exchange.ExchangeDocumentInfos.GetAll(i => i.Id == info.Id).SingleOrDefault();
      
      if (info != null)
      {
        // ИОП на основной документ отправляется на входящие документы, ИОП на титул покупателя - на исходящие.
        var isIncomingMessage = info.MessageType == Exchange.ExchangeDocumentInfo.MessageType.Incoming;
        var needUpdateReceiptStatus = isIncomingMessage &&
          info.DeliveryConfirmationStatus != Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Sent &&
          info.DeliveryConfirmationStatus != Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Processed;
        var needUpdateBuyerReceiptStatus = !isIncomingMessage &&
          info.BuyerDeliveryConfirmationStatus != Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Sent &&
          info.BuyerDeliveryConfirmationStatus != Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Processed;
        
        if (needUpdateReceiptStatus || needUpdateBuyerReceiptStatus)
        {
          if (sent)
          {
            var version = info.Document.Versions.Where(v => v.Id == info.VersionId).Single();
            info.Document.History.Write(operation, operation, comment, version.Number);
          }
          
          var client = GetClient(info.RootBox);
          if (!client.CanSendDeliveryConfirmation(info.ServiceDocumentId, info.ServiceMessageId))
          {
            if (isIncomingMessage)
              info.DeliveryConfirmationStatus = Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Sent;
            else
              info.BuyerDeliveryConfirmationStatus = Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Sent;
          }

          info.Save();
          
          var receiptSentInfo = sent ? "Receipts sent successfully." : "Receipts have already been sent.";
          this.LogDebugFormat(info, string.Format("Execute FixReceiptNotification. {0}", receiptSentInfo));
        }
      }
    }
    
    /// <summary>
    /// Проставить признак получения ИОПа.
    /// </summary>
    /// <param name="documentInfos">Информация о документах МКДО.</param>
    /// <param name="comment">Комментарий.</param>
    [Remote]
    public virtual void FixReceiptNotification(List<Exchange.IExchangeDocumentInfo> documentInfos, string comment)
    {
      foreach (var info in documentInfos)
        this.FixReceiptNotification(info, comment, false);
    }
    
    /// <summary>
    /// Создать задачу на отправку извещений о получении документов.
    /// </summary>
    /// <param name="box">Абонентский ящик нашей организации.</param>
    /// <returns>Задача на отправку извещений о получении документов.</returns>
    [Remote, Public]
    public IReceiptNotificationSendingTask CreateReceiptNotificationSendingTask(Sungero.ExchangeCore.IBusinessUnitBox box)
    {
      var task = Sungero.Exchange.ReceiptNotificationSendingTasks.Create();
      task.Box = box;
      task.Subject = CutText(Sungero.Exchange.ReceiptNotificationSendingTasks.Resources.TaskSubjectFormat(box.Name), task.Info.Properties.Subject.Length);
      task.ActiveText = Sungero.Exchange.ReceiptNotificationSendingTasks.Resources.TaskActiveTextFormat(box.ExchangeService.Name);
      task.MaxDeadline = Calendar.Now.AddWorkingHours(box.Responsible, 4);
      task.Save();
      return task;
    }
    
    /// <summary>
    /// Сохранить служебные документы, которые будут подписаны.
    /// </summary>
    /// <param name="documentsToSign">Сервисный документ, сертификат, которым он должен быть подписан и подпись.</param>
    [Remote]
    public virtual void SaveDeliveryConfirmationSigns(List<Structures.Module.ReglamentDocumentWithCertificate> documentsToSign)
    {
      foreach (var doc in documentsToSign)
      {
        var info = ExchangeDocumentInfos.Get(doc.Info.Id);
        if (Locks.GetLockInfo(info).IsLockedByOther)
          continue;
        var serviceDocument = info.ServiceDocuments.FirstOrDefault(sd => sd.DocumentType == doc.ReglamentDocumentType &&
                                                                   sd.ParentDocumentId == doc.ParentDocumentId);
        if (serviceDocument == null)
        {
          serviceDocument = info.ServiceDocuments.AddNew();
          serviceDocument.DocumentType = doc.ReglamentDocumentType;
        }
        serviceDocument.DocumentId = doc.ServiceDocumentId;
        serviceDocument.ParentDocumentId = doc.ParentDocumentId;
        serviceDocument.Sign = doc.Signature;
        serviceDocument.Certificate = doc.Certificate;
        serviceDocument.Body = doc.Content;
        serviceDocument.GeneratedName = doc.Name;
        serviceDocument.StageId = doc.ServiceDocumentStageId;
        serviceDocument.FormalizedPoAUnifiedRegNo = doc.FormalizedPoAUnifiedRegNumber;

        var isBuyerTitle = info.ExternalBuyerTitleId == doc.ParentDocumentId;
        var isSellerTitle = info.ServiceDocumentId == doc.ParentDocumentId;
        if (isSellerTitle)
          info.DeliveryConfirmationStatus = Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Signed;
        if (isBuyerTitle)
          info.BuyerDeliveryConfirmationStatus = Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Signed;
        info.Save();
      }
    }
    
    #endregion

    #region Общие сервисные методы

    /// <summary>
    /// Создать элемент очереди конвертации версий документов.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="versionId">ИД версии документа.</param>
    /// <param name="exchangeStatus">Статус обмена.</param>
    [Public]
    public void EnqueueXmlToPdfBodyConverter(Sungero.Docflow.IOfficialDocument document, int versionId, Enumeration? exchangeStatus)
    {
      var queueItem = ExchangeCore.BodyConverterQueueItems.Create();
      queueItem.Document = document;
      queueItem.VersionId = versionId;
      queueItem.ExchangeState = exchangeStatus;
      queueItem.ProcessingStatus = ExchangeCore.MessageQueueItem.ProcessingStatus.NotProcessed;
      queueItem.Save();
    }

    /// <summary>
    /// Убрать пространства имен.
    /// </summary>
    /// <param name="document">Документ.</param>
    [Public]
    public static void RemoveNamespaces(System.Xml.Linq.XDocument document)
    {
      foreach (var rootElements in document.Root.Descendants())
      {
        var attributesWithNamespace = rootElements.Attributes().Where(x => x.IsNamespaceDeclaration).ToList();
        foreach (var attributeWithNamespace in attributesWithNamespace)
          attributeWithNamespace.Remove();
      }
      foreach (var element in document.Descendants().Where(x => x.Name.NamespaceName.Any()).ToList())
        element.Name = element.Name.LocalName;
    }

    /// <summary>
    /// Обрезать длинную строку.
    /// </summary>
    /// <param name="text">Строка.</param>
    /// <param name="maxLength">Максимальная длина строки.</param>
    /// <returns>Строка указанной длины.</returns>
    [Public]
    public static string CutText(string text, int maxLength)
    {
      if (text.Length > maxLength)
        return Sungero.Exchange.Resources.Ellipsis_CutTextFormat(text.Substring(0, maxLength - 1));
      
      return text;
    }

    /// <summary>
    /// Проверка фонового процесса.
    /// </summary>
    /// <param name="id">Id фонового процесса.</param>
    /// <returns>Включен ли фоновый процесс.</returns>
    [Public, Remote]
    public static bool IsJobEnabled(string id)
    {
      var isJobEnabled = false;
      AccessRights.AllowRead(
        () =>
        {
          var guid = Guid.Parse(id);
          var job = CoreEntities.Jobs.GetAll(x => Equals(x.JobId, guid)).FirstOrDefault();
          isJobEnabled = job != null && job.Status == Sungero.CoreEntities.DatabookEntry.Status.Active;
        });
      return isJobEnabled;
    }
    
    /// <summary>
    /// Запустить фоновый процесс "Электронный обмен. Получение сообщений".
    /// </summary>
    [Public, Remote]
    public static void RequeueMessagesGet()
    {
      Jobs.GetMessages.Enqueue();
    }
    
    /// <summary>
    /// Запустить фоновый процесс "Электронный обмен. Получение исторических сообщений из сервисов обмена".
    /// </summary>
    [Public, Remote]
    public static void RequeueGetHistoricalMessages()
    {
      Jobs.GetHistoricalMessages.Enqueue();
    }
    
    /// <summary>
    /// Запустить фоновый процесс "Электронный обмен. Преобразование документов в PDF".
    /// </summary>
    [Public, Remote]
    public static void RequeueBodyConverterJob()
    {
      Jobs.BodyConverterJob.Enqueue();
    }
    
    /// <summary>
    /// Запустить фоновый процесс "Электронный обмен. Создание извещений о получении документов".
    /// </summary>
    [Public, Remote]
    public static void RequeueGenerateServiceDocuments()
    {
      Jobs.CreateReceiptNotifications.Enqueue();
    }
    
    /// <summary>
    /// Запустить фоновый процесс "Электронный обмен. Отправка извещений о получении документов".
    /// </summary>
    [Public, Remote]
    public static void RequeueSendSignedReceiptNotifications()
    {
      Jobs.SendSignedReceiptNotifications.Enqueue();
    }

    /// <summary>
    /// Заменить спец. символы и зарезервированные слова.
    /// </summary>
    /// <param name="name">Имя файла без расширения.</param>
    /// <returns>Преобразованное имя файла.</returns>
    [Public, Remote]
    public virtual string GetValidFileName(string name)
    {
      var replacement = "_";
      var normalizedName = name.Trim();
      
      if (string.IsNullOrEmpty(normalizedName))
        normalizedName = replacement;

      var specialWordsPattern = @"^(CON|PRN|AUX|CLOCK\$|NUL|COM0|COM1|COM2|COM3|COM4|COM5|COM6|COM7|COM8|COM9|LPT0|LPT1|LPT2|LPT3|LPT4|LPT5|LPT6|LPT7|LPT8|LPT9)(\.+|\.*$)";
      normalizedName = System.Text.RegularExpressions.Regex.Replace(normalizedName, specialWordsPattern, replacement, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

      var specialSimbolPattern = @"\\|\/|\:|\*|\?|\<|\>|\||\'|""";
      normalizedName = System.Text.RegularExpressions.Regex.Replace(normalizedName, specialSimbolPattern, replacement);

      normalizedName = System.Text.RegularExpressions.Regex.Replace(normalizedName, @"\.$", replacement);

      this.LogDebugFormat(string.Format("Normalize File Name {0} To {1}", name, normalizedName));
      return normalizedName;
    }

    /// <summary>
    /// Получить сертификаты.
    /// </summary>
    /// <param name="owner">Владелец сертификата.</param>
    /// <returns>Список сертификатов.</returns>
    [Remote(IsPure = true)]
    public virtual IQueryable<ICertificate> GetCertificates(IUser owner)
    {
      return Certificates.GetAll().Where(x => Equals(x.Owner, owner) && x.Enabled == true);
    }

    /// <summary>
    /// Получить список с информацией по документам обмена.
    /// </summary>
    /// <param name="serviceMessageId">Ид сообщения.</param>
    /// <returns>Список с информацией по документам обмена.</returns>
    [Public, Remote]
    public static List<IExchangeDocumentInfo> GetPackageDocumentsExchangeInfos(string serviceMessageId)
    {
      return ExchangeDocumentInfos
        .GetAll()
        .Where(d => d.ServiceMessageId == serviceMessageId && d.MessageType.Value == Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming)
        .ToList();
    }
    
    /// <summary>
    /// Получить список с информацией по соглашениям по аннулированию пакета документов.
    /// </summary>
    /// <param name="serviceMessageId">ИД сообщения.</param>
    /// <returns>Список с информацией по соглашениям по аннулированию пакета документов.</returns>
    [Public, Remote]
    public static List<IExchangeDocumentInfo> GetPackageCancellationAgreementInfos(string serviceMessageId)
    {
      var packageCancellationAgreementInfos = new List<IExchangeDocumentInfo>();
      
      if (string.IsNullOrEmpty(serviceMessageId))
        return packageCancellationAgreementInfos;
      
      var rootMessageId = serviceMessageId.Split('#').First();
      
      return ExchangeDocumentInfos
        .GetAll()
        .Where(d => d.ServiceMessageId.Contains(rootMessageId) && d.MessageType.Value == Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming)
        .ToList();
    }
    
    /// <summary>
    /// Получить пакет документов из сообщения СБИС.
    /// </summary>
    /// <param name="rootBox">Ведущий аб. ящик.</param>
    /// <param name="serviceMessageId">ИД сообщения.</param>
    /// <returns>Пакет документов СБИС.</returns>
    [Public, Remote]
    public static IQueryable<IOfficialDocument> GetSbisPackageDocuments(IBusinessUnitBox rootBox, string serviceMessageId)
    {
      var result = new List<IOfficialDocument>().AsQueryable();
      
      if (string.IsNullOrEmpty(serviceMessageId))
        return result;
      
      var rootMessageId = serviceMessageId.Split('#').First();
      
      result = ExchangeDocumentInfos.GetAll()
        .Where(info => Equals(info.RootBox, rootBox))
        .Where(info => info.ServiceMessageId.Contains(rootMessageId))
        .Where(info => info.Document != null)
        .Select(info => info.Document);
      
      return result;
    }
    
    /// <summary>
    /// Определить наличие прав у пользователя на документы комплекта.
    /// </summary>
    /// <param name="exchangeDocumentsInfos">Список информации по документам обмена.</param>
    /// <returns>True - если есть права на все документы комплекта, иначе - false.</returns>
    [Public, Remote]
    public static bool HasRightsToPackageExchangeDocuments(List<IExchangeDocumentInfo> exchangeDocumentsInfos)
    {
      var documentsIds = exchangeDocumentsInfos.Select(d => d.Document.Id).ToArray();
      var packageDocuments = Docflow.OfficialDocuments.GetAll(d => documentsIds.Contains(d.Id));
      if (packageDocuments.Count() != exchangeDocumentsInfos.Count())
        return false;
      foreach (var document in packageDocuments)
      {
        if (!document.AccessRights.CanUpdate())
          return false;
      }
      return true;
    }

    /// <summary>
    /// Проверка накопленных ошибок обмена.
    /// </summary>
    /// <param name="box">Абонентский ящик.</param>
    public virtual void RunExchangeCheckup(ExchangeCore.IBusinessUnitBox box)
    {
      var poisonedMessagePeriodBegin = Calendar.Now.AddDays(-Constants.Module.PoisonedMessagePeriod);
      var poisonedQueueItemsCount = MessageQueueItems.GetAll().Where(q => Equals(q.Box, box) && q.Created != null && q.Created < poisonedMessagePeriodBegin &&
                                                                     q.ProcessingStatus != Sungero.ExchangeCore.MessageQueueItem.ProcessingStatus.Suspended &&
                                                                     q.DownloadSession == null).Count();
      if (poisonedQueueItemsCount > 0)
        this.LogErrorFormat(box, "Business unit box contains {0} poisoned messages.", poisonedQueueItemsCount);

      var counterpartyExternalIds = CounterpartyQueueItems.GetAll(c => Equals(c.Box, box) && c.MatchingTask != null).Select(q => q.ExternalId).ToList();
      var counterpartyConflict = MessageQueueItems.GetAll().Where(q => Equals(q.Box, box) && q.CounterpartyExternalId != null && q.CounterpartyExternalId != string.Empty &&
                                                                  counterpartyExternalIds.Contains(q.CounterpartyExternalId) &&
                                                                  q.ProcessingStatus != Sungero.ExchangeCore.MessageQueueItem.ProcessingStatus.Suspended &&
                                                                  q.DownloadSession == null);
      var counterpartyConflictCount = counterpartyConflict.Count();
      if (counterpartyConflictCount > 0)
        this.LogErrorFormat(box, "{0} messages with unresolved counterparty conflicts for business unit box.", counterpartyConflictCount);
      
      var suspendedQueueItemsCount = MessageQueueItems.GetAll().Where(q => Equals(q.Box, box) &&
                                                                      Equals(q.ProcessingStatus, ExchangeCore.MessageQueueItem.ProcessingStatus.Suspended) &&
                                                                      q.DownloadSession == null).Count();
      if (suspendedQueueItemsCount > 0)
        this.LogDebugFormat(box, "Business unit box contains {0} suspended messages.", suspendedQueueItemsCount);
    }
    
    /// <summary>
    /// Получить статус приемки.
    /// </summary>
    /// <param name="primaryDocument">Документ сообщения.</param>
    /// <returns>Статус приемки.</returns>
    public Enumeration? GetBuyerAcceptanceStatus(NpoComputer.DCX.Common.IDocument primaryDocument)
    {
      switch (primaryDocument.BuyerAcceptanceStatus)
      {
        case NpoComputer.DCX.Common.BuyerAcceptanceStatus.Accepted:
          return Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Accepted;

        case NpoComputer.DCX.Common.BuyerAcceptanceStatus.PartiallyAccepted:
          return Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.PartiallyAccepted;

        case NpoComputer.DCX.Common.BuyerAcceptanceStatus.Rejected:
          return Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Rejected;

        default:
          return null;
      }
    }

    /// <summary>
    /// Получить ссылку на эл. доверенность в сервисе.
    /// </summary>
    /// <param name="unifiedRegistrationNumber">Единый рег. № эл. доверенности.</param>
    /// <returns>Ссылка на эл. доверенность в сервисе.</returns>
    public virtual string GetFormalizedPoALink(string unifiedRegistrationNumber)
    {
      return PublicConstants.Module.DefaultFormalizedPoALink;
    }

    /// <summary>
    /// Получить текстовое описание ссылки на эл. доверенность.
    /// </summary>
    /// <param name="unifiedRegistrationNumber">Единый рег. № эл. доверенности.</param>
    /// <returns>Текстовое описание ссылки на эл. доверенность.</returns>
    public virtual string GetFormalizedPoALinkTitle(string unifiedRegistrationNumber)
    {
      return Resources.SbisFormalizedPoALinkTitleFormat(unifiedRegistrationNumber);
    }
    
    /// <summary>
    /// Получить сертификаты сервиса обмена для указанного сотрудника.
    /// </summary>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="employee">Сотрудник.</param>
    /// <returns>Список сертификатов.</returns>
    [Remote(IsPure = true)]
    public virtual List<ICertificate> GetExchangeCertificatesForEmployee(ExchangeCore.IBoxBase box, Company.IEmployee employee)
    {
      var businessUnitBox = ExchangeCore.PublicFunctions.BoxBase.GetRootBox(box);
      var certificates = businessUnitBox.HasExchangeServiceCertificates == true
        ? businessUnitBox.ExchangeServiceCertificates.Where(x => Equals(x.Certificate.Owner, employee) && x.Certificate.Enabled == true).Select(x => x.Certificate)
        : this.GetCertificates(employee);
      
      return certificates.ToList();
    }

    #endregion
    
    #region Вычисление данных для отчета протокол электронного обмена
    
    /// <summary>
    /// Получить данные для отчета протокол эл. обмена.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>Данные для отчета протокол эл. обмена.</returns>
    [Public]
    public virtual List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo> GetExchangeOrderReportRows(IOfficialDocument document)
    {
      var reportRows = new List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo>();
      
      var documentInfo = Sungero.Exchange.PublicFunctions.ExchangeDocumentInfo.Remote.GetLastDocumentInfo(document);
      var leadingDocumentReportRows = this.GetLeadingDocumentReportRows(documentInfo);
      var cancellationAgreementReportRows = this.GetCancellationAgreementReportRows(documentInfo);
      
      reportRows.AddRange(leadingDocumentReportRows);
      reportRows.AddRange(cancellationAgreementReportRows);
      return reportRows;
    }
    
    /// <summary>
    /// Получить строки отчета для ведущего документа.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строки отчета для ведущего документа.</returns>
    [Public]
    public virtual List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo> GetLeadingDocumentReportRows(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var leadingDocumentReportRows = new List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo>();
      var sellerTitleOrDocumentReportRow = this.GetSellerTitleOrDocumentReportRow(documentInfo);
      leadingDocumentReportRows.Add(sellerTitleOrDocumentReportRow);
      
      var serviceDocumentsReportRows = this.GetServiceDocumentsReportRows(documentInfo);
      leadingDocumentReportRows.AddRange(serviceDocumentsReportRows);
      
      var buyerTitleAndServiceDocumentsReportRows = this.GetBuyerTitleAndServiceDocumentsReportRows(documentInfo);
      leadingDocumentReportRows.AddRange(buyerTitleAndServiceDocumentsReportRows);
      
      var rejectDocumentAndServiceDocumentsReportRows = this.GetRejectDocumentAndServiceDocumentsReportRows(documentInfo);
      leadingDocumentReportRows.AddRange(rejectDocumentAndServiceDocumentsReportRows);
      
      var documentStatusReportRow = this.GetDocumentStatusReportRow(documentInfo);
      var lastDocumentRow = leadingDocumentReportRows.LastOrDefault();
      lastDocumentRow.Status = documentStatusReportRow;
      
      return leadingDocumentReportRows;
    }
    
    /// <summary>
    /// Получить строки отчета для соглашения об аннулировании.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строки отчета для соглашения об аннулировании.</returns>
    [Public]
    public virtual List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo> GetCancellationAgreementReportRows(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var cancellationAgreementReportRows = new List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo>();
      
      var lastCancellation = documentInfo.ServiceDocuments.Where(s => (Equals(s.DocumentType, ExchDocumentType.Cancellation) ||
                                                                       Equals(s.DocumentType, ExchDocumentType.Annulment)) &&
                                                                 s.Date != null)
        .OrderByDescending(s => s.Date)
        .FirstOrDefault();
      
      if (lastCancellation == null || documentInfo.ExchangeState == Exchange.ExchangeDocumentInfo.ExchangeState.Rejected)
        return cancellationAgreementReportRows;
      
      var cancellationCounterpartyNames = this.GetCancellationCounterpartyNamesFromBody(documentInfo, lastCancellation);
      
      var cancellationReportRow = this.GetCancellationReportRow(documentInfo, lastCancellation,
                                                                cancellationCounterpartyNames.SenderName);
      if (cancellationReportRow != null)
        cancellationAgreementReportRows.Add(cancellationReportRow);
      
      var cancellationReplyReportRow = this.GetCancellationReplyReportRow(documentInfo, lastCancellation,
                                                                          cancellationCounterpartyNames.ReceiverName);

      if (cancellationReplyReportRow != null)
        cancellationAgreementReportRows.Add(cancellationReplyReportRow);
      
      if (cancellationAgreementReportRows.Any())
      {
        var cancellationStatusReportRow = this.GetCancellationStatusReportRow(documentInfo);
        var lastCancellationRow = cancellationAgreementReportRows.LastOrDefault();
        lastCancellationRow.Status = cancellationStatusReportRow;
      }
      
      return cancellationAgreementReportRows;
    }
    
    /// <summary>
    /// Получить строку отчета для титула продавца или неформализованного документа.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строка отчета для титула продавца или неформализованного документа.</returns>
    [Public]
    public virtual Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo GetSellerTitleOrDocumentReportRow(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var groupName = documentInfo.Document.Name;
      
      var signature = Signatures.Get(documentInfo.Document).SingleOrDefault(x => x.Id == documentInfo.SenderSignId);
      var signatureContent = signature.GetDataSignature();
      var signatureInfo = ExternalSignatures.GetSignatureInfo(signatureContent);
      
      if (signatureInfo.SignatureFormat == SignatureFormat.Hash)
        throw AppliedCodeException.Create(Docflow.Resources.IncorrectSignatureFormat);
      
      var sellerTitleOrDocumentReportRow = Sungero.Docflow.Structures.ExchangeOrderReport.ExchangeOrderInfo.Create();
      var certificateInfo = Sungero.Docflow.PublicFunctions.Module.GetSignatureCertificateInfo(signatureContent);
      var parsedSubject = Sungero.Docflow.PublicFunctions.Module.ParseCertificateSubject(certificateInfo.SubjectInfo);
      var cadesBesSignatureInfo = signatureInfo.AsCadesBesSignatureInfo();
      var signDate = cadesBesSignatureInfo.SignDate;
      var senderName = this.GetDocumentSenderNameForReport(documentInfo);
      
      sellerTitleOrDocumentReportRow.SendedFrom = this.SendedFrom(senderName, Sungero.Docflow.Server.ModuleFunctions.GetCertificateOwnerShortName(parsedSubject));
      sellerTitleOrDocumentReportRow.Date = this.DateWithTimeReportFormat(signDate);
      sellerTitleOrDocumentReportRow.DocumentName = documentInfo.Document.Name;
      sellerTitleOrDocumentReportRow.MessageType = documentInfo.MessageType == Sungero.Exchange.ExchangeDocumentInfo.MessageType.Incoming ?
        Resources.ExchangeOrderReportTitleAccepted : Resources.ExchangeOrderReportTitleSended;
      sellerTitleOrDocumentReportRow.GroupName = documentInfo.Document.Name;
      
      return sellerTitleOrDocumentReportRow;
    }
    
    /// <summary>
    /// Получить строки отчета для служебных документов.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строки отчета для служебных документов.</returns>
    [Public]
    public virtual List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo> GetServiceDocumentsReportRows(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var serviceDocumentsReportRows = new List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo>();
      
      var serviceDocuments = documentInfo.ServiceDocuments.Where(s => string.Equals(s.ParentDocumentId, documentInfo.ServiceDocumentId) &&
                                                                 s.DocumentType != ExchDocumentType.Cancellation &&
                                                                 s.DocumentType != ExchDocumentType.Annulment &&
                                                                 s.DocumentType != ExchDocumentType.Reject &&
                                                                 s.DocumentType != ExchDocumentType.IReject &&
                                                                 s.Date != null);
      var groupName = documentInfo.Document.Name;
      var serviceDocumentSenderName = this.GetDocumentReceiverNameForReport(documentInfo);
      
      foreach (var serviceDocument in serviceDocuments)
      {
        var messageType = this.GetServiceDocumentMessageType(serviceDocument.DocumentType.Value, documentInfo.MessageType.Value);
        var serviceDocumentReportRow = this.GetServiceDocumentReportRow(serviceDocument, serviceDocumentSenderName,
                                                                        messageType, serviceDocument.Sign, groupName);
        serviceDocumentsReportRows.Add(serviceDocumentReportRow);
        
        var receiptConfirmationReportRow = this.GetReceiptConfirmationReportRow(documentInfo, serviceDocument, groupName);
        if (receiptConfirmationReportRow != null)
          serviceDocumentsReportRows.Add(receiptConfirmationReportRow);
      }
      
      return serviceDocumentsReportRows;
    }
    
    /// <summary>
    /// Получить строку отчета для подтверждение даты отправки извещения о получении.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <param name="receipt">Извещение о получении.</param>
    /// <param name="groupName">Имя группы.</param>
    /// <returns>Строка отчета для подтверждение даты отправки извещения о получении.</returns>
    public virtual Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo GetReceiptConfirmationReportRow(Exchange.IExchangeDocumentInfo documentInfo,
                                                                                                                     Exchange.IExchangeDocumentInfoServiceDocuments receipt,
                                                                                                                     string groupName)
    {
      if (receipt.DocumentType != ExchDocumentType.Receipt &&
          receipt.DocumentType != ExchDocumentType.IReceipt)
        return null;
      
      var receiptConfirmation = documentInfo.ServiceDocuments.Where(s => string.Equals(s.ParentDocumentId, receipt.DocumentId) &&
                                                                    s.DocumentType == ExchDocumentType.IRConfirmation)
        .FirstOrDefault();
      
      if (receiptConfirmation == null)
        return null;
      
      var receiptConfirmationReportRow = Sungero.Docflow.Structures.ExchangeOrderReport.ExchangeOrderInfo.Create();
      var receiptConfirmationMessageType = this.GetServiceDocumentMessageType(receiptConfirmation.DocumentType.Value, documentInfo.MessageType.Value);
      var receiptConfirmationSenderName = Resources.ExchangeOrderReportOperator;
      
      return this.GetServiceDocumentReportRow(receiptConfirmation, receiptConfirmationSenderName,
                                              receiptConfirmationMessageType, receiptConfirmation.Sign, groupName);
    }
    
    /// <summary>
    ///  Получить строки отчета для титула покупателя и служебных документов.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строки отчета для ответа на документ.</returns>
    [Public]
    public virtual List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo> GetBuyerTitleAndServiceDocumentsReportRows(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var buyerTitleAndServiceDocumentsReportRows = new List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo>();
      var buyerTitleOrSecondSignatureReportRow = this.GetBuyerTitleOrSecondSignatureReportRow(documentInfo);
      if (buyerTitleOrSecondSignatureReportRow != null)
        buyerTitleAndServiceDocumentsReportRows.Add(buyerTitleOrSecondSignatureReportRow);
      
      var buyerTitleServiceDocumentsReportRows = this.GetBuyerTitleServiceDocumentsReportRows(documentInfo);
      if (buyerTitleServiceDocumentsReportRows != null)
        buyerTitleAndServiceDocumentsReportRows.AddRange(buyerTitleServiceDocumentsReportRows);
      
      return buyerTitleAndServiceDocumentsReportRows;
    }
    
    /// <summary>
    /// Получить строки отчета для отказа по документу и служебных документов.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строки отчета для служебных документов по ответу на документ.</returns>
    [Public]
    public virtual List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo> GetRejectDocumentAndServiceDocumentsReportRows(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var rejectDocumentAndServiceDocumentsReportRows = new List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo>();
      var rejectDocumentReportRow = this.GetRejectDocumentReportRow(documentInfo);
      if (rejectDocumentReportRow != null)
        rejectDocumentAndServiceDocumentsReportRows.Add(rejectDocumentReportRow);
      
      var rejectServiceDocumentsRows = this.GetRejectServiceDocumentsReportRows(documentInfo);
      if (rejectServiceDocumentsRows != null)
        rejectDocumentAndServiceDocumentsReportRows.AddRange(rejectServiceDocumentsRows);
      
      return rejectDocumentAndServiceDocumentsReportRows;
    }
    
    /// <summary>
    /// Получить строки отчета для служебных документов титула покупателя.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строки отчета для служебных документов титула покупателя.</returns>
    public virtual List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo> GetBuyerTitleServiceDocumentsReportRows(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var buyerTitleServiceDocumentsReportRows = new List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo>();
      var groupName = documentInfo.Document.Name;
      var buyerTitleServiceDocuments = documentInfo.ServiceDocuments.Where(s => string.Equals(s.ParentDocumentId, documentInfo.ExternalBuyerTitleId) &&
                                                                           s.DocumentType != ExchDocumentType.Cancellation &&
                                                                           s.DocumentType != ExchDocumentType.Annulment &&
                                                                           s.DocumentType != ExchDocumentType.Reject &&
                                                                           s.DocumentType != ExchDocumentType.IReject &&
                                                                           s.Date != null);
      if (!buyerTitleServiceDocuments.Any())
        return null;

      var serviceDocumentSenderName = this.GetDocumentSenderNameForReport(documentInfo);
      var buyerTitleMessageType = documentInfo.MessageType == Exchange.ExchangeDocumentInfo.MessageType.Incoming ?
        Exchange.ExchangeDocumentInfo.MessageType.Outgoing : Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      
      foreach (var serviceDocument in buyerTitleServiceDocuments)
      {
        var serviceDocumentReportRow = Sungero.Docflow.Structures.ExchangeOrderReport.ExchangeOrderInfo.Create();
        var serviceDocumentMessageType = this.GetServiceDocumentMessageType(serviceDocument.DocumentType.Value, buyerTitleMessageType);
        serviceDocumentReportRow = this.GetServiceDocumentReportRow(serviceDocument,
                                                                    serviceDocumentSenderName, serviceDocumentMessageType,
                                                                    serviceDocument.Sign, groupName);
        
        buyerTitleServiceDocumentsReportRows.Add(serviceDocumentReportRow);
        
        var receiptConfirmationReportRow = this.GetReceiptConfirmationReportRow(documentInfo, serviceDocument, groupName);
        if (receiptConfirmationReportRow != null)
          buyerTitleServiceDocumentsReportRows.Add(receiptConfirmationReportRow);
      }
      
      return buyerTitleServiceDocumentsReportRows;
    }
    
    /// <summary>
    /// Получить строки отчета для отказа по документу.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строки отчета для отказа по документу.</returns>
    public virtual Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo GetRejectDocumentReportRow(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var reject = documentInfo.ServiceDocuments.Where(s => string.Equals(s.ParentDocumentId, documentInfo.ServiceDocumentId) &&
                                                       (s.DocumentType == ExchDocumentType.Reject ||
                                                        s.DocumentType == ExchDocumentType.IReject) &&
                                                       s.Date != null)
        .LastOrDefault();
      
      if (reject == null)
        return null;
      
      var messageType = Equals(documentInfo.MessageType.Value, Exchange.ExchangeDocumentInfo.MessageType.Outgoing) ?
        Resources.ExchangeOrderReportRejectAccepted :
        Resources.ExchangeOrderReportRejectSended;
      // Для уведомления об уточнении использовать средний род.
      if (reject.DocumentType == ExchDocumentType.IReject)
        messageType = Equals(documentInfo.MessageType.Value, Exchange.ExchangeDocumentInfo.MessageType.Outgoing) ?
          Resources.ExchangeOrderReportMessageAccepted :
          Resources.ExchangeOrderReportMessageSended;
      
      var groupName = documentInfo.Document.Name;
      var rejectSenderName = this.GetDocumentReceiverNameForReport(documentInfo);
      var rejectDocumentReportRow = this.GetServiceDocumentReportRow(reject, rejectSenderName,
                                                                     messageType, reject.Sign, groupName);
      
      return rejectDocumentReportRow;
    }
    
    /// <summary>
    /// Получить строки отчета для служебных документов по отказу на документ.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строки отчета для служебных документов по отказу на документ.</returns>
    public virtual List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo> GetRejectServiceDocumentsReportRows(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var reject = documentInfo.ServiceDocuments.Where(s => string.Equals(s.ParentDocumentId, documentInfo.ServiceDocumentId) &&
                                                       (s.DocumentType == ExchDocumentType.Reject ||
                                                        s.DocumentType == ExchDocumentType.IReject) &&
                                                       s.Date != null)
        .FirstOrDefault();
      
      if (reject == null)
        return null;
      
      var rejectServiceDocumentsReportRows = new List<Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo>();
      var messageType = Equals(documentInfo.MessageType.Value, Exchange.ExchangeDocumentInfo.MessageType.Outgoing) ?
        Resources.ExchangeOrderReportRejectAccepted :
        Resources.ExchangeOrderReportRejectSended;
      
      var rejectSenderName = this.GetDocumentReceiverNameForReport(documentInfo);
      var rejectServiceDocuments = documentInfo.ServiceDocuments.Where(s => string.Equals(s.ParentDocumentId, reject.DocumentId) &&
                                                                       s.Date != null);
      var groupName = documentInfo.Document.Name;
      
      foreach (var serviceDocument in rejectServiceDocuments)
      {
        var serviceDocumentSenderName = this.GetDocumentReceiverNameForReport(documentInfo);
        var serviceDocumentMessageType = this.GetServiceDocumentMessageType(serviceDocument.DocumentType.Value, documentInfo.MessageType.Value);
        var rejectServiceDocumentReportRow = this.GetServiceDocumentReportRow(serviceDocument, serviceDocumentSenderName,
                                                                              serviceDocumentMessageType, serviceDocument.Sign,
                                                                              groupName);
        rejectServiceDocumentsReportRows.Add(rejectServiceDocumentReportRow);
        
      }
      
      return rejectServiceDocumentsReportRows;
    }
    
    /// <summary>
    /// Получить статус документооборота документа для отчета.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строка со статусом документооборота.</returns>
    [Public]
    public virtual string GetDocumentStatusReportRow(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var completeStatus = Resources.ExchangeOrderReportCompleted;
      var notCompleteStatus = Resources.ExchangeOrderReportNotCompleted;
      
      // Двусторонний документ. Есть отказ в подписании.
      var isRejected = documentInfo.ExchangeState == Exchange.ExchangeDocumentInfo.ExchangeState.Rejected;
      if (isRejected)
        return completeStatus;
      
      // Документ аннулирован.
      var isRevoked = documentInfo.RevocationStatus == Exchange.ExchangeDocumentInfo.RevocationStatus.Revoked;
      if (isRevoked)
        return completeStatus;
      
      // Документ без требования подписи. Есть ИОП, если требуется.
      var sellerHasReceipts = documentInfo.DeliveryConfirmationStatus == Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.NotRequired ||
        documentInfo.DeliveryConfirmationStatus == Exchange.ExchangeDocumentInfo.DeliveryConfirmationStatus.Processed;
      if (documentInfo.NeedSign != true && sellerHasReceipts)
        return completeStatus;
      
      // Двусторонний документ. Подписан обеими сторонами. Есть ИОПы, если требуются.
      var isSigned = documentInfo.SenderSignId.HasValue && documentInfo.ReceiverSignId.HasValue;
      var buyerHasReceipts =  !documentInfo.BuyerDeliveryConfirmationStatus.HasValue ||
        documentInfo.BuyerDeliveryConfirmationStatus == Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.NotRequired ||
        documentInfo.BuyerDeliveryConfirmationStatus == Exchange.ExchangeDocumentInfo.BuyerDeliveryConfirmationStatus.Processed;
      if (isSigned && buyerHasReceipts && sellerHasReceipts)
        return completeStatus;

      return notCompleteStatus;
    }
    
    /// <summary>
    /// Получить статус для отчета по соглашению об аннулировании.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Статус для отчета по соглашению об аннулировании.</returns>
    [Public]
    public virtual string GetCancellationStatusReportRow(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var cancellationStatusReport = string.Empty;
      var isRevoked = documentInfo.RevocationStatus == Exchange.ExchangeDocumentInfo.RevocationStatus.Revoked;
      if (isRevoked)
        return Resources.ExchangeOrderReportCancellationApproved;

      var isCancellationAwaited = documentInfo.RevocationStatus == Exchange.ExchangeDocumentInfo.RevocationStatus.Waiting;
      if (isCancellationAwaited)
        return Resources.ExchangeOrderReportCancellationAwaited;
      
      var lastCancellationAgreementId = documentInfo.ServiceDocuments
        .Where(d => d.DocumentType == ExchDocumentType.Annulment && d.Date.HasValue)
        .OrderByDescending(d => d.Date)
        .Select(s => s.DocumentId)
        .FirstOrDefault();
      var isCancellationCanceled = documentInfo.ServiceDocuments
        .Where(d => d.DocumentType == ExchDocumentType.Reject && d.ParentDocumentId == lastCancellationAgreementId)
        .Any();
      if (isCancellationCanceled)
        return Resources.ExchangeOrderReportCancellationCancel;
      
      return cancellationStatusReport;
    }
    
    /// <summary>
    /// Получить строку отчета для соглашения об аннулировании.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <param name="cancellation">Служебное соглашение об аннулировании.</param>
    /// <param name="cancellationSenderName">Имя отправителя соглашения об аннулировании.</param>
    /// <returns>Строка отчета для соглашения об аннулировании.</returns>
    public virtual Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo GetCancellationReportRow(Exchange.IExchangeDocumentInfo documentInfo,
                                                                                                              Exchange.IExchangeDocumentInfoServiceDocuments cancellation,
                                                                                                              string cancellationSenderName)
    {
      if (cancellation == null)
        return null;
      
      var cancellationTypeName = CancellationAgreements.Info.LocalizedName;
      var cancellationGroupName = Resources.ExchangeOrderReportCancellationGroupNameFormat(cancellationTypeName, this.DateReportFormat(cancellation.Date));
      var cancellationMessageType = Resources.ExchangeOrderReportMessageCreated;
      
      var cancellationReportRow = this.GetServiceDocumentReportRow(cancellation, cancellationSenderName,
                                                                   cancellationMessageType, cancellation.Sign,
                                                                   cancellationGroupName,
                                                                   cancellationTypeName.ToLower());
      
      return cancellationReportRow;
    }
    
    /// <summary>
    /// Получить строку отчета для ответа на соглашение об аннулировании.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <param name="cancellation">Служебное соглашение об аннулировании.</param>
    /// <param name="сancellationReplySenderName">Имя отправителя соглашения об аннулировании.</param>
    /// <returns>Строка отчета для ответа на соглашение об аннулировании.</returns>
    public virtual Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo GetCancellationReplyReportRow(Exchange.IExchangeDocumentInfo documentInfo,
                                                                                                                   Exchange.IExchangeDocumentInfoServiceDocuments cancellation,
                                                                                                                   string сancellationReplySenderName)
    {
      if (cancellation == null || Equals(cancellation.DocumentType, ExchDocumentType.Cancellation))
        return null;
      
      var cancellationTypeName = CancellationAgreements.Info.LocalizedName;
      var cancellationGroupName = Resources.ExchangeOrderReportCancellationGroupNameFormat(cancellationTypeName, this.DateReportFormat(cancellation.Date));
      
      var rejectCancellationReportRow = this.GetRejectCancellationReportRow(documentInfo, cancellation,
                                                                            сancellationReplySenderName,
                                                                            cancellationGroupName);
      
      if (rejectCancellationReportRow != null)
        return rejectCancellationReportRow;
      else
        return this.GetSecondSignCancellationReportRow(documentInfo, cancellation,
                                                       сancellationReplySenderName,
                                                       cancellationGroupName);
    }
    
    /// <summary>
    /// Получить строку отчета для отказа на соглашение об аннулировании.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <param name="cancellation">Служебное соглашение об аннулировании.</param>
    /// <param name="сancellationReplySenderName">Имя отправителя ответа на соглашение об аннулировании.</param>
    /// <param name="cancellationGroupName">Имя группы для соглашения об аннулировании.</param>
    /// <returns>Строка отчета для отказа на соглашение об аннулировании.</returns>
    public virtual Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo GetRejectCancellationReportRow(Exchange.IExchangeDocumentInfo documentInfo,
                                                                                                                    Exchange.IExchangeDocumentInfoServiceDocuments cancellation,
                                                                                                                    string сancellationReplySenderName,
                                                                                                                    string cancellationGroupName)
    {
      if (cancellation == null)
        return null;
      
      var rejectCancellation = documentInfo.ServiceDocuments.Where(s => string.Equals(s.ParentDocumentId, cancellation.DocumentId) &&
                                                                   s.DocumentType == ExchDocumentType.Reject &&
                                                                   s.Date != null)
        .FirstOrDefault();
      
      if (rejectCancellation == null)
        return null;
      
      var rejectMessageType = Resources.ExchangeOrderReportRejectForCancellation;
      var rejectName = Sungero.Exchange.Resources.ExchangeOrderReportAccusativeCancellation;
      
      var rejectCancellationReportRow = this.GetServiceDocumentReportRow(rejectCancellation, сancellationReplySenderName,
                                                                         rejectMessageType, rejectCancellation.Sign,
                                                                         cancellationGroupName,
                                                                         rejectName);
      return rejectCancellationReportRow;
    }
    
    /// <summary>
    /// Получить строку отчета для второй подписи на соглашение об аннулировании.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <param name="cancellation">Служебное соглашение об аннулировании.</param>
    /// <param name="сancellationReplySenderName">Имя отправителя ответа на соглашение об аннулировании.</param>
    /// <param name="cancellationGroupName">Имя группы для соглашения об аннулировании.</param>
    /// <returns>Строка отчета для второй подписи на соглашение об аннулировании.</returns>
    public virtual Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo GetSecondSignCancellationReportRow(Exchange.IExchangeDocumentInfo documentInfo,
                                                                                                                        Exchange.IExchangeDocumentInfoServiceDocuments cancellation,
                                                                                                                        string сancellationReplySenderName,
                                                                                                                        string cancellationGroupName)
    {
      if (cancellation == null)
        return null;
      
      var secondSignCancellationReportRow = Sungero.Docflow.Structures.ExchangeOrderReport.ExchangeOrderInfo.Create();
      
      if (cancellation.SecondSign != null)
      {
        var secondSignCancellationMessageType = Resources.ExchangeOrderReportCancellationSigned;
        var secondSignName = CancellationAgreements.Info.LocalizedName.ToLower();
        secondSignCancellationReportRow = this.GetServiceDocumentReportRow(cancellation, сancellationReplySenderName,
                                                                           secondSignCancellationMessageType, cancellation.SecondSign,
                                                                           cancellationGroupName,
                                                                           secondSignName);
      }
      else
      {
        secondSignCancellationReportRow.GroupName = cancellationGroupName;
        secondSignCancellationReportRow.MessageType = Resources.ExchangeOrderReportCancellationWait;
        secondSignCancellationReportRow.DocumentName = Resources.ExchangeOrderReportCancellationSign;
      }

      return secondSignCancellationReportRow;
    }
    
    /// <summary>
    /// Получить названия организаций из тела соглашения об аннулировании.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <param name="cancellation">Служебный документ - соглашение об аннулировании.</param>
    /// <returns>Структура с названиями отправителя и получателя соглашения об аннулировании.</returns>
    public Sungero.Exchange.Structures.Module.IAnnulmentCounterpartyNames GetCancellationCounterpartyNamesFromBody(Sungero.Exchange.IExchangeDocumentInfo documentInfo,
                                                                                                                   Sungero.Exchange.IExchangeDocumentInfoServiceDocuments cancellation)
    {
      if (documentInfo == null || cancellation == null)
        return Sungero.Exchange.Structures.Module.AnnulmentCounterpartyNames.Create();
      
      var cancellationSentByBusinessUnit = false;
      
      try
      {
        var encoding = Encoding.GetEncoding(1251);
        var cancellationBody = System.Xml.Linq.XDocument.Parse(encoding.GetString(cancellation.Body));
        var cancellationSenderFtsId = cancellationBody.Element("Файл").Element("Документ").Element("УчастЭДО").Attribute("ИдУчастЭДО").Value;
        var senderBoxes = ExchangeCore.BusinessUnitBoxes.GetAll().Where(x => string.Equals(x.FtsId, cancellationSenderFtsId, StringComparison.InvariantCultureIgnoreCase));
        cancellationSentByBusinessUnit = senderBoxes.Any() && senderBoxes.Contains(documentInfo.RootBox);
      }
      catch (Exception ex)
      {
        Logger.DebugFormat("GetExchangeOrderInfo. Cannot get sender from annulment body, ExchangeDocumentInfo Id({0})," +
                           " get annulment counterparty names from signatures. Error text: {1}",
                           documentInfo.Id, ex.Message);
      }
      
      var fullCounterpartyName = this.GetFullCounterpartyNameForReport(documentInfo);
      
      var cancellationSenderName = cancellationSentByBusinessUnit ?
        documentInfo.RootBox.BusinessUnit.Name :
        fullCounterpartyName;
      var cancellationReceiverName = cancellationSentByBusinessUnit ?
        fullCounterpartyName :
        documentInfo.RootBox.BusinessUnit.Name;
      
      return Sungero.Exchange.Structures.Module.AnnulmentCounterpartyNames.Create(cancellationSenderName, cancellationReceiverName);
    }
    
    /// <summary>
    /// Получить имя отправителя документа для отчета.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Имя отправителя документа для отчета.</returns>
    public virtual string GetDocumentReceiverNameForReport(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var isIncomingDocument = documentInfo.MessageType == Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      return isIncomingDocument ? documentInfo.RootBox.BusinessUnit.Name :
        this.GetFullCounterpartyNameForReport(documentInfo);
    }
    
    /// <summary>
    /// Получить имя получателя документа для отчета.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Имя получателя документа для отчета.</returns>
    public virtual string GetDocumentSenderNameForReport(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var isIncomingDocument = documentInfo.MessageType == Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      
      return isIncomingDocument ? this.GetFullCounterpartyNameForReport(documentInfo) :
        documentInfo.RootBox.BusinessUnit.Name;
    }
    
    /// <summary>
    /// Получить имя контрагента для отчета.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Имя контрагента для отчета.</returns>
    public virtual string GetFullCounterpartyNameForReport(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var counterpartyName = documentInfo.Counterparty.Name;
      return documentInfo.CounterpartyDepartmentBox == null
        ? counterpartyName
        : Exchange.PublicFunctions.ExchangeDocumentInfo.GetCompanyNameWithDepartment(documentInfo, counterpartyName);
    }
    
    /// <summary>
    /// Получить тип направления сообщения служебного документа для строчки отчета.
    /// </summary>
    /// <param name="documentType">Тип документа.</param>
    /// <param name="messageType">Тип сообщения для документа, на которое отправлен служебный документ.</param>
    /// <returns>Тип направления сообщения служебного документа для строчки отчета.</returns>
    public string GetServiceDocumentMessageType(Sungero.Core.Enumeration documentType,
                                                Sungero.Core.Enumeration messageType)
    {
      // Подтверждение даты отправки отправляет сервис обмена, поэтому мы его получаем.
      if (Equals(documentType, ExchDocumentType.IConfirmation) ||
          Equals(documentType, ExchDocumentType.IRConfirmation) ||
          Equals(documentType, ExchDocumentType.IRjConfirmation) ||
          Equals(documentType, ExchDocumentType.BTConfirmation))
        return Resources.ExchangeOrderReportMessageAccepted;
      
      // Тип направления сообщения для извещение о получении зависит от докумета, на которое оно отправлено.
      if ((Equals(documentType, ExchDocumentType.IReceipt) ||
           Equals(documentType, ExchDocumentType.IRReceipt) ||
           Equals(documentType, ExchDocumentType.Receipt)) &&
          Equals(messageType, Exchange.ExchangeDocumentInfo.MessageType.Outgoing))
        return Resources.ExchangeOrderReportMessageAccepted;
      
      return Resources.ExchangeOrderReportMessageSended;
    }
    
    /// <summary>
    ///  Получить строку отчета для служебного документа.
    /// </summary>
    /// <param name="serviceDocument">Служебный документ.</param>
    /// <param name="documentSenderName">Имя отправителя служебного документа.</param>
    /// <param name="messageType">Тип сообщения.</param>
    /// <param name="signature">Подпись на служебный документ.</param>
    /// <param name="groupName">Имя группы.</param>
    /// <param name="documentName">Имя служебного документа.</param>
    /// <returns>Строка отчета для служебного документа.</returns>
    public virtual Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo GetServiceDocumentReportRow(Exchange.IExchangeDocumentInfoServiceDocuments serviceDocument,
                                                                                                                 string documentSenderName,
                                                                                                                 string messageType,
                                                                                                                 byte[] signature,
                                                                                                                 string groupName,
                                                                                                                 string documentName = null)
    {
      var signatureInfo = ExternalSignatures.GetSignatureInfo(signature);
      
      if (signatureInfo.SignatureFormat == SignatureFormat.Hash)
        throw AppliedCodeException.Create(Docflow.Resources.IncorrectSignatureFormat);
      
      var serviceDocumentRow = Sungero.Docflow.Structures.ExchangeOrderReport.ExchangeOrderInfo.Create();
      var certificateInfo = Sungero.Docflow.PublicFunctions.Module.GetSignatureCertificateInfo(signature);
      var parsedSubject = Sungero.Docflow.PublicFunctions.Module.ParseCertificateSubject(certificateInfo.SubjectInfo);
      var cadesBesSignatureInfo = signatureInfo.AsCadesBesSignatureInfo();
      var signDate = cadesBesSignatureInfo.SignDate;
      var documentType = serviceDocument.DocumentType;
      var isConfirmation = Equals(documentType, ExchDocumentType.IConfirmation) ||
        Equals(documentType, ExchDocumentType.IRConfirmation) ||
        Equals(documentType, ExchDocumentType.IRjConfirmation) ||
        Equals(documentType, ExchDocumentType.BTConfirmation);
      var senderName = isConfirmation ? Resources.ExchangeOrderReportOperator : documentSenderName;
      
      serviceDocumentRow.SendedFrom = this.SendedFrom(senderName, Sungero.Docflow.Server.ModuleFunctions.GetCertificateOwnerShortName(parsedSubject));
      serviceDocumentRow.Date = signDate != null ? this.DateWithTimeReportFormat(signDate) : this.DateWithTimeReportFormat(serviceDocument.Date);
      serviceDocumentRow.DocumentName = documentName ?? serviceDocument.Info.Properties.DocumentType.GetLocalizedValue(documentType).ToLower();
      serviceDocumentRow.MessageType = messageType;
      serviceDocumentRow.GroupName = groupName;

      return serviceDocumentRow;
    }
    
    /// <summary>
    /// Получить строку отчета для титула покупателя или информации о второй подписи для документа.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строка отчета для титула покупателя или информации о второй подписи для документа.</returns>
    public Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo GetBuyerTitleOrSecondSignatureReportRow(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var accounting = AccountingDocumentBases.As(documentInfo.Document);
      var doesNotNeedSignSentDocuments = documentInfo.NeedSign != true;
      if (accounting != null && accounting.IsFormalized == true)
      {
        var isTaxInvoice = accounting.FormalizedFunction == Docflow.AccountingDocumentBase.FormalizedFunction.Schf;
        doesNotNeedSignSentDocuments = documentInfo.NeedSign == true ? isTaxInvoice : true;
      }
      
      if (doesNotNeedSignSentDocuments && (accounting == null || accounting.IsFormalized != true || accounting.BuyerTitleId == null))
        return null;
      
      var buyerTitleOrSecondSignatureReportRow = Sungero.Docflow.Structures.ExchangeOrderReport.ExchangeOrderInfo.Create();
      if (accounting != null && accounting.IsFormalized == true)
        buyerTitleOrSecondSignatureReportRow = this.GetBuyerTitleReportRow(documentInfo);
      else
        buyerTitleOrSecondSignatureReportRow = this.GetSecondSignatureReportRow(documentInfo);

      return buyerTitleOrSecondSignatureReportRow;
    }
    
    /// <summary>
    /// Получить строку отчета для титула покупателя.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строка отчета для титула покупателя.</returns>
    public Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo GetBuyerTitleReportRow(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var buyerTitleReportRow = Sungero.Docflow.Structures.ExchangeOrderReport.ExchangeOrderInfo.Create();
      var accounting = AccountingDocumentBases.As(documentInfo.Document);
      var isIncoming = documentInfo.MessageType == Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      buyerTitleReportRow.GroupName = documentInfo.Document.Name;
      
      if (documentInfo.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.PartiallyAccepted)
        buyerTitleReportRow.DocumentName = Resources.ExchangeOrderReportBuyerTitlePartiallyAccepted;
      else if (documentInfo.BuyerAcceptanceStatus == Exchange.ExchangeDocumentInfo.BuyerAcceptanceStatus.Rejected)
        buyerTitleReportRow.DocumentName = Resources.ExchangeOrderReportBuyerTitleRejected;
      else
        buyerTitleReportRow.DocumentName = Resources.ExchangeOrderReportBuyerTitle;
      
      var sign = Signatures.Get(accounting.Versions.SingleOrDefault(x => x.Id == accounting.BuyerTitleId))
        .SingleOrDefault(x => x.Id == accounting.BuyerSignatureId);
      if (accounting.BuyerTitleId == null || sign == null)
      {
        if (documentInfo.ExchangeState == Exchange.ExchangeDocumentInfo.ExchangeState.Rejected)
          return null;
        
        if (accounting.FormalizedServiceType != Sungero.Docflow.AccountingDocumentBase.FormalizedServiceType.Invoice)
        {
          buyerTitleReportRow.MessageType = isIncoming ?
            Resources.ExchangeOrderReportTitleNotSended :
            Resources.ExchangeOrderReportTitleNotAccepted;
          return buyerTitleReportRow;
        }
      }
      
      buyerTitleReportRow.MessageType = isIncoming ?
        Resources.ExchangeOrderReportTitleSended :
        Resources.ExchangeOrderReportTitleAccepted;
      
      if (sign == null)
        return buyerTitleReportRow;
      
      var certificateInfo = Docflow.PublicFunctions.Module.GetSignatureCertificateInfo(sign.GetDataSignature());
      var parsedSubject = Docflow.PublicFunctions.Module.ParseCertificateSubject(certificateInfo.SubjectInfo);
      var organizationName = Exchange.PublicFunctions.ExchangeDocumentInfo.GetSigningOrganizationInfo(documentInfo, sign).Name;
      buyerTitleReportRow.SendedFrom = this.SendedFrom(organizationName, Sungero.Docflow.Server.ModuleFunctions.GetCertificateOwnerShortName(parsedSubject));
      buyerTitleReportRow.Date = this.DateWithTimeReportFormat(sign.SigningDate);
      
      return buyerTitleReportRow;
    }
    
    /// <summary>
    /// Получить строку отчета для второй подписи на документ.
    /// </summary>
    /// <param name="documentInfo">Сведения о документе обмена.</param>
    /// <returns>Строка отчета для второй подписи на документ.</returns>
    public Sungero.Docflow.Structures.ExchangeOrderReport.IExchangeOrderInfo GetSecondSignatureReportRow(Exchange.IExchangeDocumentInfo documentInfo)
    {
      var secondSignatureReportRow = Sungero.Docflow.Structures.ExchangeOrderReport.ExchangeOrderInfo.Create();
      
      var isIncoming = documentInfo.MessageType == Exchange.ExchangeDocumentInfo.MessageType.Incoming;
      secondSignatureReportRow.GroupName = documentInfo.Document.Name;
      secondSignatureReportRow.DocumentName = Resources.ExchangeOrderReportSignature;
      
      var sign = Signatures.Get(documentInfo.Document).SingleOrDefault(x => x.Id == documentInfo.ReceiverSignId);
      if (!documentInfo.ReceiverSignId.HasValue || sign == null)
      {
        if (documentInfo.ExchangeState == Exchange.ExchangeDocumentInfo.ExchangeState.Rejected)
          return null;
        
        secondSignatureReportRow.MessageType = isIncoming ?
          Resources.ExchangeOrderReportSignatureNotSended :
          Resources.ExchangeOrderReportSignatureNotAccepted;
        return secondSignatureReportRow;
      }
      
      secondSignatureReportRow.MessageType = isIncoming ?
        Resources.ExchangeOrderReportSignatureSended :
        Resources.ExchangeOrderReportSignatureAccepted;
      
      if (sign == null)
        return secondSignatureReportRow;
      
      var certificateInfo = Docflow.PublicFunctions.Module.GetSignatureCertificateInfo(sign.GetDataSignature());
      var parsedSubject = Docflow.PublicFunctions.Module.ParseCertificateSubject(certificateInfo.SubjectInfo);
      var organizationName = Exchange.PublicFunctions.ExchangeDocumentInfo.GetSigningOrganizationInfo(documentInfo, sign).Name;
      secondSignatureReportRow.SendedFrom = this.SendedFrom(organizationName, Sungero.Docflow.Server.ModuleFunctions.GetCertificateOwnerShortName(parsedSubject));
      secondSignatureReportRow.Date = this.DateWithTimeReportFormat(sign.SigningDate);

      return secondSignatureReportRow;
    }
    
    /// <summary>
    /// Формирование строки отправителя.
    /// </summary>
    /// <param name="organizationName">Название организации.</param>
    /// <param name="signedName">ФИО подписавшего.</param>
    /// <returns>Строка с отправителем документа.</returns>
    [Public]
    public string SendedFrom(string organizationName, string signedName)
    {
      if (string.IsNullOrWhiteSpace(organizationName) && string.IsNullOrEmpty(signedName))
        return string.Empty;
      
      var signedBy = string.Format("{0} {1}", Resources.ExchangeOrderReportSignedBy, signedName);
      return string.IsNullOrWhiteSpace(organizationName) ?
        signedBy :
        string.Format("{0} <b>{1}</b>, {2}", Resources.ExchangeOrderReportSendedBy,
                      organizationName, signedBy);
    }
    
    /// <summary>
    /// Получить строковое представление даты со временем, приведенной ко времени текущего пользователя.
    /// </summary>
    /// <param name="datetime">Дата со временем.</param>
    /// <returns>Строковое представление даты во времени пользователя.</returns>
    [Public]
    public virtual string DateWithTimeReportFormat(DateTime? datetime)
    {
      if (datetime == null)
        return null;
      
      return Docflow.PublicFunctions.Module.ToTenantTime(datetime.Value).ToUserTime().ToString("g");
    }
    
    /// <summary>
    /// Получить строковое представление даты, приведенной ко времени текущего пользователя.
    /// </summary>
    /// <param name="datetime">Дата.</param>
    /// <returns>Строковое представление даты.</returns>
    [Public]
    public virtual string DateReportFormat(DateTime? datetime)
    {
      if (datetime == null)
        return null;
      
      return Docflow.PublicFunctions.Module.ToTenantTime(datetime.Value).ToUserTime().ToString("d");
    }
    
    #endregion
    
    #region Конвертация в pdf
    
    /// <summary>
    /// Запуск асинхронного обработчика конвертации в pdf.
    /// </summary>
    /// <param name="queueItem">Элемент очереди конвертации pdf.</param>
    public virtual void ExecuteConvertDocumentToPdfAsyncHandler(IBodyConverterQueueItem queueItem)
    {
      var asyncHandlerId = Guid.NewGuid().ToString();
      queueItem.AsyncHandlerId = asyncHandlerId;
      queueItem.Save();
      
      var convertDocumentToPdfHandler = Sungero.Exchange.AsyncHandlers.ConvertExchangeDocumentToPdf.Create();
      convertDocumentToPdfHandler.QueueItemId = queueItem.Id;
      convertDocumentToPdfHandler.AsyncHandlerId = asyncHandlerId;
      convertDocumentToPdfHandler.ExecuteAsync();
      this.LogDebugFormat(string.Format("ExecuteConvertDocumentToPdfAsyncHandler. QueueItemId: '{0}' AsyncHandlerId: '{1}'.", queueItem.Id, asyncHandlerId));
    }
    #endregion
    
    #region Управление загрузкой исторических сообщений.
    
    /// <summary>
    /// Запустить загрузку исторических сообщений.
    /// </summary>
    /// <param name="businessUnitBoxId">ИД абонентского ящика.</param>
    /// <param name="periodBegin">Начало периода.</param>
    /// <param name="periodEnd">Конец периода.</param>
    /// <returns>Информация о созданной сессии загрузки исторических сообщений.</returns>
    [Public(WebApiRequestType = RequestType.Post)]
    public virtual string RunHistoricalDownload(int businessUnitBoxId, DateTime periodBegin, DateTime periodEnd)
    {
      this.ValidateCreateHistoricalDownloadSessionData(businessUnitBoxId, periodBegin, periodEnd);
      
      var downloadSession = this.CreateHistoricalDownloadSession(businessUnitBoxId, periodBegin, periodEnd);
      
      return ExchangeCore.PublicFunctions.HistoricalMessagesDownloadSession.GetMainInfo(downloadSession);
    }
    
    /// <summary>
    /// Прекратить загрузку исторических сообщений.
    /// </summary>
    /// <param name="businessUnitBoxId">ИД абонентского ящика.</param>
    /// <returns>Информация о прекращенной сессии загрузки исторических сообщений.</returns>
    [Public(WebApiRequestType = RequestType.Post)]
    public virtual string AbortHistoricalDownload(int businessUnitBoxId)
    {
      this.ValidateAbortHistoricalDownloadSessionData(businessUnitBoxId);
      
      var downloadSession = this.AbortHistoricalDownloadSession(businessUnitBoxId);
      
      return downloadSession != null ? this.GetHistoricalDownloadSessionInfo(downloadSession.Id) : string.Empty;
    }

    /// <summary>
    /// Проверить данные для создания сессии загрузки исторических сообщений.
    /// </summary>
    /// <param name="businessUnitBoxId">ИД абонентского ящика.</param>
    /// <param name="periodBegin">Начало периода.</param>
    /// <param name="periodEnd">Конец периода.</param>
    public virtual void ValidateCreateHistoricalDownloadSessionData(int businessUnitBoxId, DateTime periodBegin, DateTime periodEnd)
    {
      if (!BusinessUnitBoxes.GetAll().Where(b => b.Id == businessUnitBoxId).Any())
        throw AppliedCodeException.Create(Sungero.Exchange.Resources.BusinessUnitBoxNotFoundFormat(businessUnitBoxId));
      
      var businessUnitBox = BusinessUnitBoxes.Get(businessUnitBoxId);
      
      if (businessUnitBox.Status != CoreEntities.DatabookEntry.Status.Active)
        throw AppliedCodeException.Create(Sungero.Exchange.Resources.BusinessUnitBoxClosedFormat(businessUnitBoxId));
      
      var downloadSessionInWork = ExchangeCore.PublicFunctions.BusinessUnitBox.GetHistoricalMessagesDownloadSessionInWork(businessUnitBox);
      
      if (downloadSessionInWork != null)
        throw AppliedCodeException.Create(Sungero.Exchange.Resources.HistoricalDownloadSessionAlreadyExistFormat(businessUnitBoxId));
      
      if (periodBegin > periodEnd || periodEnd > Calendar.Today || periodBegin > Calendar.Today)
        throw AppliedCodeException.Create(Sungero.Exchange.Resources.InvalidDatePeriodHistoricalDownloading);
    }
    
    /// <summary>
    /// Проверить данные для прекращения сессии загрузки исторических сообщений.
    /// </summary>
    /// <param name="businessUnitBoxId">ИД абонентского ящика.</param>
    public virtual void ValidateAbortHistoricalDownloadSessionData(int businessUnitBoxId)
    {
      if (!BusinessUnitBoxes.GetAll().Where(b => b.Id == businessUnitBoxId).Any())
        throw AppliedCodeException.Create(Sungero.Exchange.Resources.BusinessUnitBoxNotFoundFormat(businessUnitBoxId));
      
      var businessUnitBox = BusinessUnitBoxes.Get(businessUnitBoxId);
      var downloadSessionInWork = ExchangeCore.PublicFunctions.BusinessUnitBox.GetHistoricalMessagesDownloadSessionInWork(businessUnitBox);
      
      if (downloadSessionInWork == null)
        throw AppliedCodeException.Create(Sungero.Exchange.Resources.NotFoundActiveHistoricalMessagesDownloadSessionFormat(businessUnitBoxId));
    }

    /// <summary>
    /// Создать сессию загрузки исторических сообщений.
    /// </summary>
    /// <param name="businessUnitBoxId">ИД абонентского ящика.</param>
    /// <param name="periodBegin">Начало периода.</param>
    /// <param name="periodEnd">Конец периода.</param>
    /// <returns>Созданная сессия загрузки исторических сообщений.</returns>
    public virtual IHistoricalMessagesDownloadSession CreateHistoricalDownloadSession(int businessUnitBoxId, DateTime periodBegin, DateTime periodEnd)
    {
      var businessUnitBox = BusinessUnitBoxes.Get(businessUnitBoxId);
      var downloadSessionInWork = ExchangeCore.PublicFunctions.BusinessUnitBox.GetHistoricalMessagesDownloadSessionInWork(businessUnitBox);
      
      if (downloadSessionInWork != null)
        throw AppliedCodeException.Create(Sungero.Exchange.Resources.HistoricalDownloadSessionAlreadyExistFormat(businessUnitBoxId));
      
      var downloadSession = HistoricalMessagesDownloadSessions.Create();
      downloadSession.BusinessUnitBox = businessUnitBox;
      downloadSession.PeriodBegin = periodBegin;
      downloadSession.PeriodEnd = periodEnd;
      downloadSession.DownloadingState = ExchangeCore.HistoricalMessagesDownloadSession.DownloadingState.InWork;
      downloadSession.Save();
      
      return downloadSession;
    }
    
    /// <summary>
    /// Прекратить сессию загрузки исторических сообщений.
    /// </summary>
    /// <param name="businessUnitBoxId">ИД абонентского ящика.</param>
    /// <returns>Прекращенная сессия загрузки исторических сообщений.</returns>
    public virtual IHistoricalMessagesDownloadSession AbortHistoricalDownloadSession(int businessUnitBoxId)
    {
      var businessUnitBox = BusinessUnitBoxes.Get(businessUnitBoxId);
      var downloadSession = ExchangeCore.PublicFunctions.BusinessUnitBox.GetHistoricalMessagesDownloadSessionInWork(businessUnitBox);
      
      if (downloadSession != null)
      {
        downloadSession.DownloadingState = ExchangeCore.HistoricalMessagesDownloadSession.DownloadingState.Aborted;
        downloadSession.Save();
      }
      
      return downloadSession;
    }

    /// <summary>
    /// Получить сессии загрузки исторических сообщений по абонентскому ящику.
    /// </summary>
    /// <param name="businessUnitBoxId">Ид абонентского ящика.</param>
    /// <returns>Список Ид сессий загрузки исторических сообщений по абонентскому ящику.</returns>
    [Public(WebApiRequestType = RequestType.Get)]
    public virtual List<int> GetHistoricalDownloadSessions(int businessUnitBoxId)
    {
      return HistoricalMessagesDownloadSessions.GetAll(s => s.BusinessUnitBox.Id == businessUnitBoxId)
        .Select(s => s.Id)
        .OrderBy(s => s)
        .ToList();
    }

    /// <summary>
    /// Получить активные сессии загрузки исторических сообщений.
    /// </summary>
    /// <returns>Список Ид активных сессий загрузки исторических сообщений.</returns>
    [Public(WebApiRequestType = RequestType.Get)]
    public virtual List<int> GetActiveHistoricalDownloadSessions()
    {
      return HistoricalMessagesDownloadSessions.GetAll(s => s.DownloadingState == ExchangeCore.HistoricalMessagesDownloadSession.DownloadingState.InWork)
        .Select(s => s.Id)
        .OrderBy(s => s)
        .ToList();
    }
    
    /// <summary>
    /// Получить информацию о сессии загрузки исторических сообщений.
    /// </summary>
    /// <param name="downloadSessionId">ИД сессии загрузки исторических сообщений.</param>
    /// <returns>Информация о сессии загрузки исторических сообщений.</returns>
    [Public(WebApiRequestType = RequestType.Get)]
    public virtual string GetHistoricalDownloadSessionInfo(int downloadSessionId)
    {
      var downloadSession = HistoricalMessagesDownloadSessions.GetAll().Where(s => s.Id == downloadSessionId).SingleOrDefault();
      
      if (downloadSession == null)
        return string.Empty;
      
      var sessionMainInfo = ExchangeCore.PublicFunctions.HistoricalMessagesDownloadSession.GetMainInfo(downloadSession);
      var sessionAdditionalInfo = ExchangeCore.PublicFunctions.HistoricalMessagesDownloadSession.GetAdditionalInfo(downloadSession);

      return string.Format("{0} {1}", sessionMainInfo, sessionAdditionalInfo);
    }
    
    #endregion

    #region Логирование

    /// <summary>
    /// Записать в лог полную информацию о содержимом сообщения из сервиса обмена.
    /// </summary>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    public virtual void LogFullMessage(NpoComputer.DCX.Common.IMessage message)
    {
      this.LogMessage(message);
      this.LogMessagePrimaryDocuments(message);
      this.LogMessageReglamentDocuments(message);
      this.LogMessageSignatures(message);
    }
    
    /// <summary>
    /// Записать в лог общую информацию о сообщении из сервиса обмена.
    /// </summary>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    public virtual void LogMessage(NpoComputer.DCX.Common.IMessage message)
    {
      this.LogDebugFormat(message, "Service message: IsReply: '{0}', IsIncoming: '{1}', Sender: '{2}', Receiver: '{3}', ParentServiceMessageId: '{4}'.",
                          message.IsReply, message.IsIncome, message.Sender == null ? string.Empty : message.Sender.BoxId,
                          message.Receiver == null ? string.Empty : message.Receiver.BoxId, message.ParentServiceMessageId);
      
      var fromDepartment = message.FromDepartment;
      if (fromDepartment == null)
        this.LogDebugFormat(message, "Service message: property FromDepartment is null.");
      else
        this.LogDebugFormat(message, "Service message: FromDepartment: Name:  '{0}', Id:  '{1}', TRRC:  '{2}', ParentDepartmentId:  '{3}'.",
                            fromDepartment.Name, fromDepartment.Id, fromDepartment.Kpp, fromDepartment.ParentDepartmentId);
      
      var intoDepartment = message.ToDepartment;
      if (intoDepartment == null)
        this.LogDebugFormat(message, "Service message: property ToDepartment is null.");
      else
        this.LogDebugFormat(message, "Service message: ToDepartment: Name:  '{0}', Id:  '{1}', TRRC:  '{2}', ParentDepartmentId:  '{3}'.",
                            intoDepartment.Name, intoDepartment.Id, intoDepartment.Kpp, intoDepartment.ParentDepartmentId);
      
    }
    
    /// <summary>
    /// Записать в лог информацию о документах сообщения из сервиса обмена.
    /// </summary>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    public virtual void LogMessagePrimaryDocuments(NpoComputer.DCX.Common.IMessage message)
    {
      foreach (var primaryDocument in message.PrimaryDocuments)
      {
        this.LogDebugFormat(message, primaryDocument, "Primary document: NeedSign: '{0}', SignStatus: '{1}', NeedReceipt: '{2}', RevocationStatus: '{3}', ParentServiceEntityId: '{4}'.",
                            primaryDocument.NeedSign, primaryDocument.SignStatus, primaryDocument.NeedReceipt, primaryDocument.RevocationStatus, primaryDocument.ParentServiceEntityId);
        
        this.LogDebugFormat(message, primaryDocument, "Primary document: NonformalizedKind: '{0}', IsUnknownDocumentType: '{1}', Comment: '{2}', Date: '{3}', IsLegitimate: '{4}', Card: '{5}',  GlobalDocumentId: '{6}'.",
                            primaryDocument.NonformalizedKind, primaryDocument.IsUnknownDocumentType, primaryDocument.Comment, primaryDocument.Date, primaryDocument.IsLegitimate, primaryDocument.Card,
                            primaryDocument.GlobalDocumentId);
        
        if (primaryDocument.DocumentType != DocumentType.Nonformalized)
          this.LogDebugFormat(message, primaryDocument, "Primary document: FileName: '{0}'.", primaryDocument.FileName);
        
        this.LogDebugFormat(message, primaryDocument, "Primary document: BoundDocuments: '{0}'.", string.Join(", ",  primaryDocument.BoundDocuments.Select(x => x.DocumentId).ToList()));
        
        // Метаданные.
        foreach (var item in primaryDocument.Metadata)
        {
          this.LogDebugFormat(message, primaryDocument, "Metadata: Key = '{0}', Value = '{1}'.", item.Key, item.Value);
        }
      }
    }
    
    /// <summary>
    /// Записать в лог информацию о служебных документах сообщения из сервиса.
    /// </summary>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    public virtual void LogMessageReglamentDocuments(NpoComputer.DCX.Common.IMessage message)
    {
      foreach (var reglamentDocument in message.ReglamentDocuments)
      {
        this.LogDebugFormat(message, reglamentDocument, "Reglament document:  Type: '{0}', FileName: '{1}', DateTime: '{2}'.", reglamentDocument.DocumentType, reglamentDocument.FileName, reglamentDocument.DateTime);
        
        this.LogDebugFormat(message, reglamentDocument, "Reglament document:  Type: '{0}', RootServiceEntityId: '{1}', ParentServiceEntityId: '{2}'.",
                            reglamentDocument.DocumentType, reglamentDocument.RootServiceEntityId, reglamentDocument.ParentServiceEntityId);
      }
    }
    
    /// <summary>
    /// Записать в лог информацию о подписи из сообщения.
    /// </summary>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    public virtual void LogMessageSignatures(NpoComputer.DCX.Common.IMessage message)
    {
      foreach (var signature in message.Signatures)
      {
        this.LogDebugFormat(message, "Signature:  DocumentId: '{0}', SignerBoxId: '{1}'.", signature.DocumentId, signature.SignerBoxId);
      }
    }
    
    /// <summary>
    /// Получить строку с префиксом Exchange.
    /// </summary>
    /// <param name="text">Сообщение.</param>
    /// <param name="paramsInformation">Строка с параметрами.</param>
    /// <returns>Строка с префиксом Exchange.</returns>
    public virtual string ExchangeLogPattern(string text, string paramsInformation)
    {
      if (string.IsNullOrEmpty(paramsInformation))
        return string.Format("Exchange. {0}", text);
      
      return string.Format("Exchange. {0} {1}", text, paramsInformation);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="text">Сообщение.</param>
    [Public]
    public virtual void LogDebugFormat(string text)
    {
      var format = this.ExchangeLogPattern(text, null);
      Logger.Debug(format);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="documentId">Ид документа.</param>
    /// <param name="versionId">Ид версии.</param>
    /// <param name="text">Сообщение.</param>
    [Public]
    public virtual void LogDebugFormat(int documentId, int versionId, string text)
    {
      var documentInformation = string.Format("DocumentId: '{0}', VersionId: '{1}'.", documentId, versionId);
      var format = this.ExchangeLogPattern(text, documentInformation);
      Logger.Debug(format);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="text">Сообщение.</param>
    [Public]
    public virtual void LogDebugFormat(ExchangeCore.IBoxBase box, string text)
    {
      this.LogDebugFormat(box, text, string.Empty);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="documentInfo">Информация о документе обмена.</param>
    /// <param name="text">Сообщение.</param>
    [Public]
    public virtual void LogDebugFormat(IExchangeDocumentInfo documentInfo, string text)
    {
      this.LogDebugFormat(documentInfo, text, string.Empty);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="queueItem">Элемент очереди конвертации тел документов.</param>
    /// <param name="text">Сообщение.</param>
    [Public]
    public virtual void LogDebugFormat(ExchangeCore.IBodyConverterQueueItem queueItem, string text)
    {
      this.LogDebugFormat(queueItem, text, string.Empty);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="queueItem">Элемент очереди синхронизации контрагентов.</param>
    /// <param name="text">Сообщение.</param>
    [Public]
    public virtual void LogDebugFormat(ExchangeCore.ICounterpartyQueueItem queueItem, string text)
    {
      this.LogDebugFormat(queueItem, text, string.Empty);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="queueItem">Элемент очереди синхронизации сообщений.</param>
    /// <param name="text">Сообщение.</param>
    [Public]
    public virtual void LogDebugFormat(ExchangeCore.IMessageQueueItem queueItem, string text)
    {
      this.LogDebugFormat(queueItem, text, string.Empty);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="counterpartyDepartmentBox">Абонентский ящик подразделения контрагента.</param>
    /// <param name="text">Сообщение.</param>
    [Public]
    public virtual void LogDebugFormat(ExchangeCore.ICounterpartyDepartmentBox counterpartyDepartmentBox, string text)
    {
      this.LogDebugFormat(counterpartyDepartmentBox, text, string.Empty);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogDebugFormat(ExchangeCore.IBoxBase box, string logFormat, params object[] args)
    {
      var boxInformation = string.Format("Box: DisplayValue: '{0}', Id: '{1}'.", box?.DisplayValue, box?.Id);
      var format = this.ExchangeLogPattern(logFormat, boxInformation);
      Logger.DebugFormat(format, args);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogDebugFormat(NpoComputer.DCX.Common.IMessage message, string logFormat, params object[] args)
    {
      var messageInformation = string.Format("ServiceMessageId: '{0}'.", message?.ServiceMessageId);
      var format = this.ExchangeLogPattern(logFormat, messageInformation);
      Logger.DebugFormat(format, args);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    /// <param name="primaryDocument">Документ сообщения.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogDebugFormat(NpoComputer.DCX.Common.IMessage message, NpoComputer.DCX.Common.IDocument primaryDocument, string logFormat, params object[] args)
    {
      var primaryDocumentInformation = string.Format("ServiceMessageId: '{0}'. Primary document: Type: '{1}', ServiceEntityId: '{2}'.",
                                                     message?.ServiceMessageId, primaryDocument?.DocumentType, primaryDocument?.ServiceEntityId);
      var format = this.ExchangeLogPattern(logFormat, primaryDocumentInformation);
      Logger.DebugFormat(format, args);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    /// <param name="reglamentDocument">Служебный документ сообщения.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogDebugFormat(NpoComputer.DCX.Common.IMessage message, NpoComputer.DCX.Common.IReglamentDocument reglamentDocument, string logFormat, params object[] args)
    {
      var reglamentDocumentInformation = string.Format("ServiceMessageId: '{0}'. Reglament document: Type: '{1}, ServiceEntityId: '{2}'.",
                                                       message?.ServiceMessageId, reglamentDocument?.DocumentType, reglamentDocument?.ServiceEntityId);
      var format = this.ExchangeLogPattern(logFormat, reglamentDocumentInformation);
      Logger.DebugFormat(format, args);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="queueItem">Элемент очереди синхронизации сообщений.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogDebugFormat(ExchangeCore.IMessageQueueItem queueItem, string logFormat, params object[] args)
    {
      var queueItemInformation = string.Format("MessageQueueItem: Id: '{0}', ExternalId: '{1}', RootMessageId: '{2}',  IsRootMessage: '{3}', AsyncHandlerId: '{4}', SessionId: '{5}'.",
                                               queueItem?.Id, queueItem?.ExternalId, queueItem?.RootMessageId, queueItem?.IsRootMessage, queueItem?.AsyncHandlerId, queueItem?.DownloadSession?.Id);
      var format = this.ExchangeLogPattern(logFormat, queueItemInformation);
      Logger.DebugFormat(format, args);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    /// <param name="queueItem">Элемент очереди синхронизации сообщений.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogDebugFormat(NpoComputer.DCX.Common.IMessage message, IMessageQueueItem queueItem, ExchangeCore.IBoxBase box, string logFormat, params object[] args)
    {
      var paramsInformation = string.Format("ServiceMessageId: '{0}'. ParentServiceMessageId: '{1}'. MessageQueueItem: Id: '{2}', IsRootMessage: '{3}', AsyncHandlerId: '{4}', SessionId: '{5}'. BoxId: '{6}'.",
                                            message?.ServiceMessageId, message?.ParentServiceMessageId, queueItem?.Id, queueItem?.IsRootMessage, queueItem?.AsyncHandlerId, queueItem?.DownloadSession?.Id, box?.Id);
      
      var format = this.ExchangeLogPattern(logFormat, paramsInformation);
      Logger.DebugFormat(format, args);
    }

    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="message">Сообщение из сервиса обмена.</param>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogDebugFormat(NpoComputer.DCX.Common.IMessage message, ExchangeCore.IBoxBase box, string logFormat, params object[] args)
    {
      var paramsInformation = string.Format("ServiceMessageId: '{0}'. BoxId: '{1}'.",
                                            message?.ServiceMessageId, box?.Id);
      
      var format = this.ExchangeLogPattern(logFormat, paramsInformation);
      Logger.DebugFormat(format, args);
    }

    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="documentInfo">Информация о документе обмена.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogDebugFormat(IExchangeDocumentInfo documentInfo, string logFormat, params object[] args)
    {
      var serviceMessageId = documentInfo?.ServiceMessageId;
      var serviceDocumentId = documentInfo?.ServiceDocumentId;
      var exchangeDocumentInfoId = documentInfo?.Id;
      var documentId = documentInfo?.Document?.Id;
      
      var paramsInformation = string.Format("ExchangeDocumentInfo: Id: '{0}', ServiceMessageId: '{1}', ServiceDocumentId: '{2}', DocumentId: '{3}'.",
                                            exchangeDocumentInfoId, serviceMessageId, serviceDocumentId, documentId);

      var format = this.ExchangeLogPattern(logFormat, paramsInformation);
      
      Logger.DebugFormat(format, args);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="queueItem">Элемент очереди конвертации тел документов.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogDebugFormat(ExchangeCore.IBodyConverterQueueItem queueItem, string logFormat, params object[] args)
    {
      var queueItemInformation = string.Format("BodyConverterQueueItem: Id: '{0}', DocumentId: '{1}'.", queueItem?.Id, queueItem?.Document?.Id);
      var format = this.ExchangeLogPattern(logFormat, queueItemInformation);
      Logger.DebugFormat(format, args);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="queueItem">Элемент очереди синхронизации контрагентов.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogDebugFormat(ExchangeCore.ICounterpartyQueueItem queueItem, string logFormat, params object[] args)
    {
      var queueItemInformation = string.Format("CounterpartyQueueItem: Id: '{0}', ExternalId: '{1}', RootBoxId: '{2}'.", queueItem?.Id, queueItem?.ExternalId, queueItem?.RootBox?.Id);
      var format = this.ExchangeLogPattern(logFormat, queueItemInformation);
      Logger.DebugFormat(format, args);
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="documentInfo">Информация о документе обмена.</param>
    /// <param name="reglamentDocuments">Служебные документы сообщения.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogDebugFormat(IExchangeDocumentInfo documentInfo, List<NpoComputer.DCX.Common.IReglamentDocument> reglamentDocuments, string logFormat, params object[] args)
    {
      foreach (var reglamentDocument in reglamentDocuments)
      {
        var serviceMessageId = documentInfo?.ServiceMessageId;
        var serviceDocumentId = documentInfo?.ServiceDocumentId;
        var exchangeDocumentInfoId = documentInfo?.Id;
        var documentId = documentInfo?.Document?.Id;
        
        var paramsInformation = string.Format("ExchangeDocumentInfo: Id: '{0}', ServiceMessageId: '{1}', ServiceDocumentId: '{2}', DocumentId: '{3}'. " +
                                              "Reglament document:  DocumentType: '{4}', RootServiceEntityId: '{5}', ParentServiceEntityId: '{6}'.",
                                              exchangeDocumentInfoId, serviceMessageId, serviceDocumentId, documentId,
                                              reglamentDocument.DocumentType, reglamentDocument.RootServiceEntityId, reglamentDocument.ParentServiceEntityId);

        var format = this.ExchangeLogPattern(logFormat, paramsInformation);
        Logger.DebugFormat(format, args);
      }
    }
    
    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="counterpartyDepartmentBox">Абонентский ящик подразделения контрагента.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogDebugFormat(ExchangeCore.ICounterpartyDepartmentBox counterpartyDepartmentBox, string logFormat, params object[] args)
    {
      var departmentInformation = string.Format("CounterpartyDepartmentBox: Id: '{0}', BoxId: '{1}', CounterpartyId: '{2}',  OrganizationId: '{3}', DepartmentId: '{4}', FtsId: '{5}'.",
                                                counterpartyDepartmentBox?.Id, counterpartyDepartmentBox?.Box?.Id, counterpartyDepartmentBox?.Counterparty?.Id,
                                                counterpartyDepartmentBox?.OrganizationId, counterpartyDepartmentBox?.DepartmentId, counterpartyDepartmentBox?.FtsId);
      var format = this.ExchangeLogPattern(logFormat, departmentInformation);
      Logger.DebugFormat(format, args);
    }
    
    /// <summary>
    /// Записать сообщение об ошибке в лог.
    /// </summary>
    /// <param name="text">Сообщение.</param>
    [Public]
    public virtual void LogErrorFormat(string text)
    {
      var format = this.ExchangeLogPattern(text, null);
      Logger.Error(format);
    }
    
    /// <summary>
    /// Записать сообщение об ошибке в лог.
    /// </summary>
    /// <param name="text">Сообщение.</param>
    /// <param name="ex">Исключение.</param>
    [Public]
    public virtual void LogErrorFormat(string text, System.Exception ex)
    {
      var format = this.ExchangeLogPattern(text, null);
      Logger.Error(format, ex);
    }
    
    /// <summary>
    /// Записать сообщение об ошибке в лог.
    /// </summary>
    /// <param name="documentId">Ид документа.</param>
    /// <param name="versionId">Ид версии.</param>
    /// <param name="text">Сообщение.</param>
    /// <param name="ex">Исключение.</param>
    [Public]
    public virtual void LogErrorFormat(int documentId, int versionId, string text, System.Exception ex)
    {
      var documentInformation = string.Format("DocumentId: '{0}', VersionId: '{1}'.", documentId, versionId);
      var format = this.ExchangeLogPattern(text, documentInformation);
      Logger.Error(format, ex);
    }
    
    /// <summary>
    ///  Записать сообщение об ошибке в лог.
    /// </summary>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="logFormat">Формат строки.</param>
    /// <param name="args">Аргументы.</param>
    public virtual void LogErrorFormat(ExchangeCore.IBoxBase box, string logFormat, params object[] args)
    {
      var paramsInformation = string.Format("BoxId: '{0}'.", box?.Id);
      
      var format = this.ExchangeLogPattern(logFormat, paramsInformation);
      Logger.ErrorFormat(format, args);
    }

    /// <summary>
    /// Записать сообщение об ошибке в лог.
    /// </summary>
    /// <param name="box">Абонентский ящик.</param>
    /// <param name="text">Сообщение.</param>
    /// <param name="ex">Исключение.</param>
    [Public]
    public virtual void LogErrorFormat(ExchangeCore.IBoxBase box, string text, System.Exception ex)
    {
      var boxInformation = string.Format("BoxId: '{0}'.", box?.Id);
      
      var format = this.ExchangeLogPattern(text, boxInformation);
      Logger.Error(format, ex);
    }
    
    /// <summary>
    /// Записать сообщение об ошибке в лог.
    /// </summary>
    /// <param name="queueItem">Элемент очереди синхронизации сообщений.</param>
    /// <param name="text">Сообщение.</param>
    /// <param name="ex">Исключение.</param>
    public virtual void LogErrorFormat(ExchangeCore.IMessageQueueItem queueItem, string text, System.Exception ex)
    {
      var queueItemInformation = string.Format("MessageQueueItem: Id: '{0}', ExternalId: '{1}', RootMessageId: '{2}', IsRootMessage: '{3}', AsyncHandlerId: '{4}', SessionId: '{5}'.",
                                               queueItem?.Id, queueItem?.ExternalId, queueItem?.RootMessageId, queueItem?.IsRootMessage, queueItem?.AsyncHandlerId, queueItem?.DownloadSession?.Id);
      var format = this.ExchangeLogPattern(text, queueItemInformation);
      Logger.Error(format, ex);
    }
    
    #endregion
  }
}