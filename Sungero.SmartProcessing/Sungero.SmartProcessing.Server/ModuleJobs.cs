using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow;
using Sungero.Domain.Shared;
using Sungero.Metadata;
using Sungero.Workflow;

namespace Sungero.SmartProcessing.Server
{
  public class ModuleJobs
  {
    /// <summary>
    /// Запуск обучения классификатора по типам документов.
    /// </summary>
    public virtual void StartClassifierTraining()
    {
      // Обучение классификатора возможно при наличии лицензии на модуль "Интеллектуальные функции".
      if (!Docflow.PublicFunctions.Module.Remote.IsModuleAvailableByLicense(Sungero.Commons.PublicConstants.Module.IntelligenceGuid))
      {
        Logger.Debug("ClassifierTraining. StartClassifierTrainingJob. Module license \"Intelligence\" not found.");
        return;
      }
      
      // ИД классификатора.
      var smartProcessingSetting = Docflow.PublicFunctions.SmartProcessingSetting.GetSettings();
      var classifierId = smartProcessingSetting.TypeClassifierId.Value;
      
      // В один момент может быть запущено только одно обучение классификатора.
      if (Functions.Module.IsClassifierTrainingInProcess(classifierId))
      {
        Logger.Debug("ClassifierTraining. StartClassifierTrainingJob. Classifier is already being trained.");
        return;
      }
      
      // Отбор данных для обучения.
      var recognitionInfos = Functions.Module.GetRecognitionInfosForClassifierTraining();
      if (!recognitionInfos.Any())
      {
        Logger.Debug("ClassifierTraining. StartClassifierTrainingJob. Documents for training not found.");
        return;
      }
      
      var trainingSession = Commons.ClassifierTrainingSessions.Null;
      try
      {
        // Создать сессию обучения, связать с отобранными результатами распознавания.
        trainingSession = Functions.Module.CreateClassifierTrainingSession(classifierId);
        if (!Locks.TryLock(trainingSession))
        {
          Logger.DebugFormat("ClassifierTraining. StartClassifierTrainingJob. TrainingSession (sessionId={0}) is locked by user Id {1}.", trainingSession.Id, Locks.GetLockInfo(trainingSession).LoginId);
          return;
        }

        Functions.Module.LinkRecognitionInfosWithTrainingSession(trainingSession, recognitionInfos);
        
        // Сформировать из текста документов CSV-файл.
        var trainingDataset = Functions.Module.GetCsvClassifierTrainingSessionDataset(trainingSession);
        if (trainingDataset.Length == 0)
        {
          Logger.DebugFormat("ClassifierTraining. StartClassifierTrainingJob. Failed to get training dataset from documents (sessionId={0}).", trainingSession.Id);
          trainingSession.TrainingStatus = Sungero.Commons.ClassifierTrainingSession.TrainingStatus.Error;
          trainingSession.Save();
          return;
        } 
        
        // Отправить запрос на обучение в Ario. Запустить обработчик для отслеживания результатов.
        Functions.Module.TrainClassifierAsync(trainingSession, trainingDataset);
      }
      catch (Exception ex)
      {
        Logger.Error("ClassifierTraining. StartClassifierTrainingJob. Error while starting training of classifier.", ex);
                
        // В случае ошибки сбросить статус обучения у результатов распознавания.
        if (trainingSession != null)
          Functions.Module.ResetTrainingSessionStatus(trainingSession);
      }
      finally
      {
        if (trainingSession != null && Locks.GetLockInfo(trainingSession).IsLockedByMe)
          Locks.Unlock(trainingSession);
      }
    }
    
    /// <summary>
    /// Фоновый процесс для удаления пакетов бинарных образов документов, которые отправлены на верификацию.
    /// </summary>
    public virtual void DeleteBlobPackages()
    {
      // Удаление BlobPackage со статусом Processed.
      var processedBlobPackages = BlobPackages.GetAll().Where(x => x.ProcessState == SmartProcessing.BlobPackage.ProcessState.Processed);
      foreach (var blobPackage in processedBlobPackages)
      {
        var blobs = blobPackage.Blobs.Select(x => x.Blob);
        var mailBodyBlob = blobPackage.MailBodyBlob;
        BlobPackages.Delete(blobPackage);
        foreach (var blob in blobs)
          Blobs.Delete(blob);
        
        if (mailBodyBlob != null)
          Blobs.Delete(mailBodyBlob);
      }
    }

  }
}