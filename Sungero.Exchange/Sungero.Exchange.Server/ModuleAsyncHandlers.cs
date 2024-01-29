using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.ExchangeCore.MessageQueueItem;

namespace Sungero.Exchange.Server
{
  public class ModuleAsyncHandlers
  {
    
    public virtual void ConvertExchangeDocumentToPdf(Sungero.Exchange.Server.AsyncHandlerInvokeArgs.ConvertExchangeDocumentToPdfInvokeArgs args)
    {
      Exchange.Functions.Module.LogDebugFormat(string.Format("Execute async handler ConvertExchangeDocumentToPdf. AsyncHandlerId: {0}, RetryIteration: {1}, QueueItemId: {2}.",
                                                             args.AsyncHandlerId, args.RetryIteration, args.QueueItemId));

      var startTime = Calendar.Now;
      var queueItem = ExchangeCore.BodyConverterQueueItems.GetAll().Where(x => x.Id == args.QueueItemId && string.Equals(x.AsyncHandlerId, args.AsyncHandlerId)).SingleOrDefault();
      
      if (queueItem == null)
      {
        Exchange.PublicFunctions.Module.LogDebugFormat(string.Format("ConvertExchangeDocumentToPdf. Queue item with id: {0} and async handler id: {1} not found.", args.QueueItemId, args.AsyncHandlerId));
        return;
      }
      
      if (queueItem.Document == null)
      {
        Exchange.PublicFunctions.Module.LogDebugFormat(queueItem, "ConvertExchangeDocumentToPdf. Queue item property Document is null.");
        ExchangeCore.BodyConverterQueueItems.Delete(queueItem);
        return;
      }
      
      if (queueItem.VersionId == null)
      {
        Exchange.PublicFunctions.Module.LogDebugFormat(queueItem, "ConvertExchangeDocumentToPdf. Queue item property VersionId is null.");
        ExchangeCore.BodyConverterQueueItems.Delete(queueItem);
        return;
      }
      
      if (!queueItem.Document.Versions.Any(v => Equals(v.Id, queueItem.VersionId)))
      {
        Exchange.PublicFunctions.Module.LogDebugFormat(queueItem, string.Format("ConvertExchangeDocumentToPdf. Document version id: {0} not found.", queueItem.VersionId));
        ExchangeCore.BodyConverterQueueItems.Delete(queueItem);
        return;
      }

      if (Locks.GetLockInfo(queueItem).IsLocked)
      {
        Exchange.PublicFunctions.Module.LogDebugFormat(queueItem, "ConvertExchangeDocumentToPdf. Queue item is locked.");
        args.Retry = true;
        return;
      }
      
      if (Locks.GetLockInfo(queueItem.Document).IsLocked)
      {
        Exchange.PublicFunctions.Module.LogDebugFormat(queueItem, "ConvertExchangeDocumentToPdf. Document is locked.");
        args.Retry = true;
        return;
      }
      
      var generated = false;

      var transactionSuccess = Transactions.Execute(
        () =>
        {
          // Dmitriev_IA:
          // Переполучение queueItem, т.к. при выполнении Transactions.Execute сбрасывается сессия NHibernate и полученные ранее сущности "забываются".
          // см. User Story 199135.
          queueItem = ExchangeCore.BodyConverterQueueItems.GetAll().Where(x => x.Id == args.QueueItemId).Single();
          var document = queueItem.Document;
          var exchangeState = document != null && document.ExchangeState != null ? document.ExchangeState : queueItem.ExchangeState;
          generated = Docflow.PublicFunctions.Module.GeneratePublicBodyForExchangeDocument(queueItem.Document, queueItem.VersionId.Value, exchangeState, startTime);
        });
      
      if (generated && transactionSuccess)
      {
        ExchangeCore.BodyConverterQueueItems.Delete(queueItem);
        Exchange.PublicFunctions.Module.LogDebugFormat(queueItem, "ConvertExchangeDocumentToPdf. Document success converted.");
      }
      else
      {
        Transactions.Execute(
          () =>
          {
            // Dmitriev_IA:
            // Переполучение queueItem, т.к. при выполнении Transactions.Execute сбрасывается сессия NHibernate и полученные ранее сущности "забываются".
            // см. User Story 199135.
            queueItem = ExchangeCore.BodyConverterQueueItems.GetAll().Where(x => x.Id == args.QueueItemId).Single();
            ExchangeCore.PublicFunctions.QueueItemBase.QueueItemOnError(queueItem, Resources.GeneratePublicBodyFailed);
          });

        var maxRetriesCount = Sungero.Docflow.PublicFunctions.Module.Remote.GetDocflowParamsIntegerValue(Sungero.Docflow.PublicConstants.Module.ConvertExchangeDocumentToPdfRetriesMaxCountParamName);
        
        if (maxRetriesCount <= 0)
          maxRetriesCount = Sungero.Docflow.PublicConstants.Module.ConvertExchangeDocumentToPdfRetriesMaxCount;
        
        if (queueItem.Retries >= maxRetriesCount)
        {
          ExchangeCore.BodyConverterQueueItems.Delete(queueItem);
          args.Retry = false;
          Exchange.PublicFunctions.Module.LogDebugFormat(queueItem, string.Format("Exceeded maximum count attempts to convert exchange document. Retries {0}.", queueItem.Retries));
        }
        else
        {
          args.Retry = true;
          Exchange.PublicFunctions.Module.LogDebugFormat(queueItem, "ConvertExchangeDocumentToPdf. An error occurred while generating the document body. Async handler will be retried.");
        }
      }
      Exchange.Functions.Module.LogDebugFormat(string.Format("Done async handler ConvertExchangeDocumentToPdf. AsyncHandlerId: {0}.", args.AsyncHandlerId));
    }
    
    public virtual void ProcessMessages(Sungero.Exchange.Server.AsyncHandlerInvokeArgs.ProcessMessagesInvokeArgs args)
    {
      Exchange.Functions.Module.LogDebugFormat(string.Format("Execute async handler ProcessMessages. AsyncHandlerId: {0}, RetryIteration: {1}, QueueItemIds: {2}",
                                                             args.AsyncHandlerId, args.RetryIteration, args.QueueItemIds));
      var queueItems = Sungero.ExchangeCore.PublicFunctions.Module.GetMessageQueueItems(args.QueueItemIds)
        .Where(q => q.ProcessingStatus != ExchangeCore.MessageQueueItem.ProcessingStatus.Suspended &&
               string.Equals(q.AsyncHandlerId, args.AsyncHandlerId))
        .OrderByDescending(q => q.IsRootMessage == true)
        .ThenBy(q => q.Created)
        .ThenBy(q => q.Id)
        .ToList();
      
      if (!queueItems.Any())
      {
        Exchange.Functions.Module.LogDebugFormat(string.Format("Message queue item list is empty. AsyncHandlerId: {0}.", args.AsyncHandlerId));
        args.Retry = false;
        return;
      }
      
      var firstQueueItem = queueItems.First();
      var client = ExchangeCore.PublicFunctions.BusinessUnitBox.GetPublicClient(queueItems.First().RootBox) as NpoComputer.DCX.ClientApi.Client;
      var rootMessage = Sungero.ExchangeCore.PublicFunctions.MessageQueueItem.GetRootMessageQueueItem(firstQueueItem);
      
      bool isHistoricalQueueItem = Sungero.ExchangeCore.MessageQueueItems.Is(firstQueueItem) && Sungero.ExchangeCore.MessageQueueItems.As(firstQueueItem).DownloadSession != null;
      
      var maxRetriesCount = isHistoricalQueueItem ? Sungero.Docflow.PublicFunctions.Module.Remote.GetDocflowParamsIntegerValue(Sungero.Docflow.PublicConstants.Module.ProcessHistoricalMessageRetriesMaxCountParamName) :
        Sungero.Docflow.PublicFunctions.Module.Remote.GetDocflowParamsIntegerValue(Sungero.Docflow.PublicConstants.Module.ProcessMessagesRetriesMaxCountParamName);
      
      if (maxRetriesCount <= 0)
        maxRetriesCount = isHistoricalQueueItem ? Sungero.Docflow.PublicConstants.Module.ProcessHistoricalMessageRetriesMaxCount :
          Sungero.Docflow.PublicConstants.Module.ProcessMessagesRetriesMaxCount;
      
      foreach (var queueItem in queueItems)
      {
        if (Sungero.ExchangeCore.PublicFunctions.MessageQueueItem.NeedAbortHistoricalQueueItem(queueItem))
        {
          Sungero.ExchangeCore.PublicFunctions.MessageQueueItem.AbortHistoricalQueueItem(queueItem);
          continue;
        }
        
        if (!Exchange.Functions.Module.IsRootMessageQueueItemProcessed(queueItem))
        {
          if (rootMessage != null && Sungero.ExchangeCore.PublicFunctions.MessageQueueItem.NeedIncrementRetries(rootMessage))
            Sungero.ExchangeCore.PublicFunctions.MessageQueueItem.IncrementRetries(queueItem, maxRetriesCount);
          
          Exchange.Functions.Module.LogDebugFormat(queueItem, string.Format("Root message not processed. Retries: {0}", queueItem.Retries));
        }
        else if (!Exchange.Functions.Module.ProcessMessageLiteQueueItem(queueItem, args.AsyncHandlerId, client))
        {
          var freshQueueItem = ExchangeCore.MessageQueueItems.GetAll(q => queueItem.Id == q.Id).SingleOrDefault();
          
          if (freshQueueItem != null)
          {
            if (Sungero.ExchangeCore.PublicFunctions.MessageQueueItem.NeedIncrementRetries(freshQueueItem))
              Sungero.ExchangeCore.PublicFunctions.MessageQueueItem.IncrementRetries(freshQueueItem, maxRetriesCount);
            
            Exchange.Functions.Module.LogDebugFormat(freshQueueItem, string.Format("Process message has errors. Retries: '{0}'.", freshQueueItem.Retries));
          }
        }
      }
      
      var notProcessedMessageIds = Sungero.ExchangeCore.PublicFunctions.Module.GetMessageQueueItems(args.QueueItemIds)
        .Where(q => q.ProcessingStatus != ExchangeCore.MessageQueueItem.ProcessingStatus.Processed &&
               q.ProcessingStatus != ExchangeCore.MessageQueueItem.ProcessingStatus.Suspended &&
               string.Equals(q.AsyncHandlerId, args.AsyncHandlerId))
        .Select(q => q.Id);
      
      if (notProcessedMessageIds.Any())
      {
        Exchange.Functions.Module.LogDebugFormat(string.Format("Has not processed message. Async handler sent to retry. AsyncHandlerId: {0}. NotProcessedMessageIds: {1}.",
                                                               args.AsyncHandlerId, string.Join(",", notProcessedMessageIds.ToList())));
        args.Retry = true;
        return;
      }
      
      Exchange.Functions.Module.LogDebugFormat(string.Format("Done async handler ProcessMessages. AsyncHandlerId: {0}.", args.AsyncHandlerId));
    }
  }

}