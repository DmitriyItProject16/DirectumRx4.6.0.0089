using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Content;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Domain.Shared;
using Sungero.Metadata;

namespace Sungero.SmartProcessing.Server
{
  public class ModuleAsyncHandlers
  {

    /// <summary>
    /// Асинхронный обработчик для простановки статуса обучения классификатора в результатах распознавания.
    /// </summary>
    /// <param name="args">RecognitionInfoId - ИД результата распознавания,
    /// TrainingStatus - статус обучения.</param>
    public virtual void SetClassifierTrainingStatus(Sungero.SmartProcessing.Server.AsyncHandlerInvokeArgs.SetClassifierTrainingStatusInvokeArgs args)
    {
      var info = Commons.EntityRecognitionInfos.Get(args.RecognitionInfoId);
      if (info == null)
        return;
      Logger.DebugFormat("ClassifierTraining. SetClassifierTrainingStatus. Changing status async to \"{0}\". Recognition Info Id={1}",
                         args.TrainingStatus,
                         args.RecognitionInfoId);
      info.ClassifierTrainingStatus = new Enumeration(args.TrainingStatus);
      info.Save();
    }
    
    /// <summary>
    /// Асинхронный обработчик для проверки статуса дообучения в Ario.
    /// </summary>
    /// <param name="args">ClassifierTrainingSessionId - ИД сессии обучения классификатора.</param>
    public virtual void TrainClassifier(Sungero.SmartProcessing.Server.AsyncHandlerInvokeArgs.TrainClassifierInvokeArgs args)
    {
      // Обработка ошибок.
      var classifierTrainingSessionId = args.ClassifierTrainingSessionId;
      Logger.DebugFormat("ClassifierTraining. TrainClassifier: start async handler, iteration: {0} (sessionId={1}).", args.RetryIteration, classifierTrainingSessionId);
      
      var classifierTrainingSession = Sungero.Commons.ClassifierTrainingSessions.GetAll(x => x.Id == classifierTrainingSessionId).FirstOrDefault();
      if (classifierTrainingSession == null)
      {
        Logger.ErrorFormat("ClassifierTraining. TrainClassifier: training session not found (sessionId={0}).", classifierTrainingSessionId);
        return;
      }
      
      var classifierTrainingSessionLockInfo = Locks.GetLockInfo(classifierTrainingSession);
      if (classifierTrainingSessionLockInfo.IsLocked)
      {
        Logger.DebugFormat("ClassifierTraining. TrainingSession (sessionId={0}) is locked by user Id {1}.", classifierTrainingSessionId, classifierTrainingSessionLockInfo.LoginId);
        args.Retry = true;
        return;
      }        
 
      var arioTaskInfo = SmartProcessing.Functions.Module.GetTrainingTaskInfo(classifierTrainingSession.ArioTaskId.Value);
      
      var arioTask = arioTaskInfo.Task;
      var arioTaskStatus = arioTask.State;
      
      if (arioTaskStatus == Constants.Module.ProcessingTaskStates.ErrorOccurred ||
          arioTaskStatus == Constants.Module.ProcessingTaskStates.Terminated)
      {
        SmartProcessing.Functions.Module.ResetTrainingSessionStatus(classifierTrainingSession);
        Logger.ErrorFormat("ClassifierTraining. TrainClassifier: Ario service error: {0} (sessionId={1}).", arioTaskInfo.Result, classifierTrainingSessionId);          
        return;
      }
      
      // Дообучение не завершено.
      if (arioTaskStatus != Constants.Module.ProcessingTaskStates.Completed)
        args.Retry = true;
      
      // Дообучение завершено.
      if (arioTaskStatus == Constants.Module.ProcessingTaskStates.Completed)
      {
        SmartProcessing.Functions.Module.FinalizeClassifierTraining(classifierTrainingSession, arioTask);
        return;
      } 
      
    }
    
    /// <summary>
    /// Асинхронный обработчик распознавания в Ario документов из пакета.
    /// </summary>
    /// <param name="args">BlobPackageId - ИД пакета бинарных образов документов.</param>
    public virtual void ProcessBlobPackage(Sungero.SmartProcessing.Server.AsyncHandlerInvokeArgs.ProcessBlobPackageInvokeArgs args)
    {
      // Проверка статусов задач в Ario.
      var arioError = false;
      var blobPackage = BlobPackages.Null;
      try
      {
        blobPackage = SmartProcessing.BlobPackages.GetAll().Where(r => r.Id == args.BlobPackageId).FirstOrDefault();
        var blobsSuccessfullyProcessed = this.CheckArioTasksStatus(blobPackage);
        if (!blobsSuccessfullyProcessed)
        {
          args.NextRetryTime = this.GetProcessBlobPackageNextRetryTime(args.RetryIteration);
          args.Retry = true;
          
          // Вызываем string.Format здесь, т.к. нельзя использовать params в public-функциях.
          var iterationLogMessage = string.Format("Smart processing. ProcessBlobPackage, iteration: {0}, next retry time: {1}",
                                                  args.RetryIteration,
                                                  args.NextRetryTime);
          Functions.Module.LogMessage(iterationLogMessage, blobPackage);
          return;
        }
      }
      catch (Exception ex)
      {
        Functions.Module.LogError("Smart processing. ProcessBlobPackage. Error while trying to check tasks statuses in Ario.", ex, blobPackage);
        arioError = true;
      }
      
      // Запуск обработки пакета документов.
      try
      {
        // Отправка уведомления, если были ошибки на Ario. Для почты отправить оригинальные тела задачей на верификацию.
        if ((!this.HasBlobsSuccessfullyProcessedByArio(blobPackage) || arioError) &&
            blobPackage.SourceType != SmartProcessing.BlobPackage.SourceType.Mail)
        {
          this.SendNotificationAboutArioErrors(blobPackage);
          Functions.Module.LogMessage("Smart processing. ProcessBlobPackage. Fatal error while processing captured package.", blobPackage);
          return;
        }
        Functions.Module.ProcessCapturedPackage(blobPackage);
      }
      catch (Exception ex)
      {
        Functions.Module.LogError("Smart processing. ProcessBlobPackage. Fatal error while processing captured package.", ex, blobPackage);
      }
    }
    
    /// <summary>
    /// Получить время следующей попытки интеллектуальной обработки документов.
    /// </summary>
    /// <param name="retryIteration">Номер итерации (начинается с нуля).</param>
    /// <returns>Время следующей попытки.</returns>
    public virtual DateTime GetProcessBlobPackageNextRetryTime(int retryIteration)
    {
      var retryTimeout = 60;
      switch (retryIteration)
      {
        case 0:
          retryTimeout = Constants.Module.ArioDocumentProcessingMinDuration;
          break;
        case 1:
          retryTimeout = Constants.Module.ArioAccessMinPeriod;
          break;
        case 2:
          retryTimeout = Constants.Module.ArioAccessMinPeriod;
          break;
        case 3:
          retryTimeout = Constants.Module.ArioAccessMinPeriod;
          break;
        case 4:
          retryTimeout = Constants.Module.ArioAccessMinPeriod;
          break;
      }
      
      return Calendar.Now.AddSeconds(retryTimeout);
    }
    
    /// <summary>
    /// Проверить статус задач по обработке в Ario.
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    /// <returns>Признак того, что все задачи по обработке успешно завершены.</returns>
    public virtual bool CheckArioTasksStatus(IBlobPackage blobPackage)
    {
      var unprocessedBlobs = blobPackage.Blobs.Select(x => x.Blob)
        .Where(b => b.ArioTaskStatus == SmartProcessing.Blob.ArioTaskStatus.InProcess && b.ArioTaskId != null);
      var blobPackageProcessed = true;
      
      foreach (var blob in unprocessedBlobs)
      {
        var processTaskInfo = Functions.Module.GetProcessTaskInfo(blob.ArioTaskId.Value);
        var processTask = processTaskInfo.Task;
        var arioTaskStatus = processTask.State;
        if (arioTaskStatus == Constants.Module.ProcessingTaskStates.Completed)
        {
          blob.ArioResultJson = processTaskInfo.GetArioResultJson();
          blob.ArioTaskStatus = SmartProcessing.Blob.ArioTaskStatus.Success;
          blob.Save();
        }
        
        if (arioTaskStatus == Constants.Module.ProcessingTaskStates.New ||
            arioTaskStatus == Constants.Module.ProcessingTaskStates.InWork)
        {
          blobPackageProcessed = false;
          continue;
        }
        
        if (arioTaskStatus == Constants.Module.ProcessingTaskStates.ErrorOccurred ||
            arioTaskStatus == Constants.Module.ProcessingTaskStates.TrainingCompleted ||
            arioTaskStatus == Constants.Module.ProcessingTaskStates.Terminated)
        {
          blob.ArioTaskStatus = SmartProcessing.Blob.ArioTaskStatus.ErrorOccurred;
          blob.Save();
        }
      }
      
      return blobPackageProcessed;
    }
    
    /// <summary>
    /// Проверить, есть ли успешно обработанные в Ario бинарные образы документов.
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    /// <returns>Признак того, что хотя бы один бинарный образ документов успешно обработан в Ario.</returns>
    [Public]
    public virtual bool HasBlobsSuccessfullyProcessedByArio(IBlobPackage blobPackage)
    {
      return blobPackage.Blobs.Select(x => x.Blob).Where(b => b.ArioTaskStatus == SmartProcessing.Blob.ArioTaskStatus.Success).Count() > 0;
    }
    
    /// <summary>
    /// Отправить уведомление ответственному за верификацию об ошибках в Ario.
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    [Public]
    public virtual void SendNotificationAboutArioErrors(IBlobPackage blobPackage)
    {
      var timeWithUtc = Docflow.PublicFunctions.Module.GetDateWithUTCLabel(blobPackage.DcsProcessingBeginDate.GetValueOrDefault());
      var title = Sungero.SmartProcessing.Resources.ArioErrorsNotificationTitleFormat(timeWithUtc);
      var text = Sungero.SmartProcessing.Resources.ArioErrorsNotificationTextFormat(blobPackage.SourceName,
                                                                                    blobPackage.PackageId,
                                                                                    timeWithUtc,
                                                                                    Resources.ArioErrorsNotificationPrefix);
      
      var responsible = Functions.Module.GetResponsible(blobPackage);
      if (responsible == null)
        return;
      
      var task = Workflow.SimpleTasks.CreateWithNotices(title, responsible);
      task.ActiveText = text;
      task.Save();
      task.Start();
    }

    /// <summary>
    /// Асинхронный обработчик удаления результатов распознавания сущности.
    /// </summary>
    /// <param name="args">Параметр - ИД сущности, результаты распознавания которой нужно удалить.</param>
    public virtual void DeleteEntityRecognitionInfo(Sungero.SmartProcessing.Server.AsyncHandlerInvokeArgs.DeleteEntityRecognitionInfoInvokeArgs args)
    {
      var electronicDocumentMetadata = typeof(IElectronicDocument).GetEntityMetadata();
      var documentRecognitionInfos = Commons.EntityRecognitionInfos.GetAll().Where(r => r.EntityId == args.EntityId);
      
      foreach (var recognitionInfo in documentRecognitionInfos)
      {
        if (electronicDocumentMetadata.IsAncestorFor(Sungero.Metadata.Services.MetadataSearcher.FindEntityMetadata(Guid.Parse(recognitionInfo.EntityType))))
          Commons.EntityRecognitionInfos.Delete(recognitionInfo);
      }
    }
    
  }
}