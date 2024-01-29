using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CommonLibrary;
using Sungero.Commons;
using Sungero.Commons.Structures.Module;
using Sungero.Company;
using Sungero.Contracts;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow;
using Sungero.Docflow.OfficialDocument;
using Sungero.Docflow.Structures.Module;
using Sungero.Domain.Shared;
using Sungero.Metadata;
using Sungero.Parties;
using Sungero.RecordManagement;
using Sungero.SmartProcessing.Constants;
using Sungero.SmartProcessing.Structures.Module;
using Sungero.Workflow;
using ArioGrammars = Sungero.SmartProcessing.Constants.Module.ArioGrammars;
using ElasticsearchTypes = Sungero.Commons.PublicConstants.Module.ElasticsearchType;
using ProcessingFunctionName = Sungero.Docflow.PublicConstants.Module.ProcessingFunctionName;

namespace Sungero.SmartProcessing.Server
{
  public class ModuleFunctions
  {
    /// <summary>
    /// Обработать пакет бинарных образов докуGetRecognizedLetterAddresseesFuzzyентов DCS.
    /// </summary>
    /// <param name="packageId">Идентификатор пакета.</param>
    [Public(WebApiRequestType = RequestType.Post)]
    public virtual void ProcessCapturedPackage(string packageId)
    {
      this.LogMessage("Smart processing. Processing captured package...", packageId);

      var blobPackage = BlobPackages.GetAll().Where(x => x.PackageId == packageId).FirstOrDefault();
      
      var asyncProcessBlobPackageHandler = SmartProcessing.AsyncHandlers.ProcessBlobPackage.Create();
      asyncProcessBlobPackageHandler.BlobPackageId = blobPackage.Id;
      asyncProcessBlobPackageHandler.ExecuteAsync();
      
      this.LogMessage("Smart processing. Processing captured package completed successfully.", packageId);
    }
    
    /// <summary>
    /// Обработать пакет документов со сканера или почты.
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    public virtual void ProcessCapturedPackage(IBlobPackage blobPackage)
    {
      var arioPackage = this.UnpackArioPackage(blobPackage);
      
      var documentPackage = this.BuildDocumentPackage(blobPackage, arioPackage);
      
      this.OrderAndLinkDocumentPackage(documentPackage);
      
      this.SendToResponsible(documentPackage);
      
      // Вызываем асинхронную выдачу прав, так как убрали ее при сохранении.
      this.EnqueueGrantAccessRightsJobs(documentPackage);

      this.FinalizeProcessing(blobPackage);
    }
    
    #region Обработка пакета документов через rxcmd
    
    /// <summary>
    /// Установка параметра AllIndicesExist в DocflowParams, при наличии всех индексов.
    /// </summary>
    [Public(WebApiRequestType = RequestType.Post)]
    public virtual void UpdateDocflowParamsIfAllIndicesExist()
    {
      // Проверка существования индексов.
      var indicesNotExist = new List<string>();
      
      if (!Commons.PublicFunctions.Module.IsIndexExist(Commons.PublicFunctions.Module.GetIndexName(BusinessUnits.Info.Name)))
        indicesNotExist.Add(BusinessUnits.Info.Name);
      
      if (!Commons.PublicFunctions.Module.IsIndexExist(Commons.PublicFunctions.Module.GetIndexName(Employees.Info.Name)))
        indicesNotExist.Add(Employees.Info.Name);
      
      if (!Commons.PublicFunctions.Module.IsIndexExist(Commons.PublicFunctions.Module.GetIndexName(CompanyBases.Info.Name)))
        indicesNotExist.Add(CompanyBases.Info.Name);
      
      if (!Commons.PublicFunctions.Module.IsIndexExist(Commons.PublicFunctions.Module.GetIndexName(Contacts.Info.Name)))
        indicesNotExist.Add(Contacts.Info.Name);
      
      if (indicesNotExist.Any())
        Logger.ErrorFormat("SmartProcessing. UpdateDocflowParamsIfAllIndicesExist. Indices not created: {0}.", string.Join(", ", indicesNotExist));
      else
        Docflow.PublicFunctions.Module.InsertOrUpdateDocflowParam(Commons.PublicConstants.Module.AllIndicesExistParamName, string.Empty);
    }
    
    /// <summary>
    /// Удаление параметра AllIndicesExist из DocflowParams.
    /// </summary>
    [Public(WebApiRequestType = RequestType.Post)]
    public virtual void RemoveAllIndicesExistFromDocflowParams()
    {
      Docflow.PublicFunctions.Module.ExecuteSQLCommandFormat(Queries.Module.RemoveDocflowParamsValue, new[] { Commons.PublicConstants.Module.AllIndicesExistParamName });
    }
    
    /// <summary>
    /// Валидация настроек интеллектуальной обработки.
    /// </summary>
    /// <param name="senderLineName">Наименование линии.</param>
    [Public(WebApiRequestType = RequestType.Post)]
    public virtual void ValidateSettings(string senderLineName)
    {
      var smartProcessingSettings = Sungero.Docflow.PublicFunctions.SmartProcessingSetting.GetSettings();
      Sungero.Docflow.PublicFunctions.SmartProcessingSetting.ValidateSettings(smartProcessingSettings, senderLineName);
    }
    
    /// <summary>
    /// Сформировать пакет бинарных образов документов на основе пакета документов из DCS.
    /// </summary>
    /// <param name="dcsPackage">Пакет документов из DCS.</param>
    [Public(WebApiRequestType = RequestType.Post)]
    public virtual void PrepareBlobPackage(Structures.Module.IDcsPackage dcsPackage)
    {
      dcsPackage.Blobs = this.ExcludeUnnecessaryDcsBlobs(dcsPackage.Blobs);
      this.CreateBlobPackage(dcsPackage);
    }

    /// <summary>
    /// Исключить лишние бинарные образы документов DCS из пакета, пришедшего с почты, которые не нужно заносить в систему.
    /// </summary>
    /// <param name="dcsBlobs">Бинарные образы документов DCS.</param>
    /// <returns>Актуальные бинарные образы документов DCS.</returns>
    [Public]
    public virtual List<Structures.Module.IDcsBlob> ExcludeUnnecessaryDcsBlobs(List<Structures.Module.IDcsBlob> dcsBlobs)
    {
      // Исключить из пакета тело письма (body.html или body.txt).
      var mailBodyDcsBlobs = this.GetMailBodyBlobs(dcsBlobs);
      if (mailBodyDcsBlobs != null)
      {
        foreach (var mailBodyDcsBlob in mailBodyDcsBlobs)
        {
          dcsBlobs = dcsBlobs
            .Where(b => !string.Equals(b.OriginalFileName, mailBodyDcsBlob.OriginalFileName, StringComparison.InvariantCultureIgnoreCase))
            .ToList();
        }
      }
      
      // Исключить из пакета картинки тела письма.
      dcsBlobs = dcsBlobs.Where(b => !b.IsInlineMailContent).ToList();
      
      return dcsBlobs;
    }
    
    /// <summary>
    /// Получить тела письма из всех бинарных образов документов DCS.
    /// </summary>
    /// <param name="blobs">Бинарные образы документов DCS.</param>
    /// <returns>Тела письма.</returns>
    [Public]
    public virtual List<Structures.Module.IDcsBlob> GetMailBodyBlobs(List<Structures.Module.IDcsBlob> blobs)
    {
      var mailBodyHtmlName = ArioExtensions.DcsFileParser.Constants.DcsMailBodyName.Html;
      var mailBodyTxtName = ArioExtensions.DcsFileParser.Constants.DcsMailBodyName.Txt;

      var mailBodyBlobs = blobs.Where(b => string.Equals(b.OriginalFileName, mailBodyHtmlName, StringComparison.InvariantCultureIgnoreCase) ||
                                      string.Equals(b.OriginalFileName, mailBodyTxtName, StringComparison.InvariantCultureIgnoreCase)).ToList();
      return mailBodyBlobs;
    }
    
    /// <summary>
    /// Исключить из пакета бинарных образов документов DCS изображения, содержащиеся в html-теле письма.
    /// </summary>
    /// <param name="htmlMailBodyDcsBlob">Html-тело письма.</param>
    /// <param name="dcsBlobs">Бинарные образы документов DCS.</param>
    /// <returns>Бинарные образы документов DCS за исключением изображений в теле письма.</returns>
    [Public, Obsolete("Теперь картинки тела письма исключаются из пакета в ExcludeUnnecessaryDcsBlobs.")]
    public virtual List<Structures.Module.IDcsBlob> ExcludeEmailBodyInlineImages(Structures.Module.IDcsBlob htmlMailBodyDcsBlob,
                                                                                 List<Structures.Module.IDcsBlob> dcsBlobs)
    {
      // Метод находится здесь, потому что в нем есть привязка к AsposeExtensions.
      // ArioExtensions не ссылается на AsposeExtensions, поэтому метод перенести в ArioExtensions нельзя.
      // Получить количество ссылок на файлы-изображения в html-теле письма.
      var inlineImagesCount = AsposeExtensions.HtmlTagReader.GetInlineImagesCount(htmlMailBodyDcsBlob.Body);
      // Убрать из пакета файлы этих изображений (убираются первые n документов пакета, где n - количество картинок).
      return dcsBlobs.Skip(inlineImagesCount).ToList();
    }
    
    /// <summary>
    /// Создать пакет бинарных образов документов.
    /// </summary>
    /// <param name="dcsPackage">Пакет бинарных образов документов DCS.</param>
    /// <returns>Пакет бинарных образов документов.</returns>
    [Public]
    public virtual IBlobPackage CreateBlobPackage(Structures.Module.IDcsPackage dcsPackage)
    {
      // Заполнить основную информацию.
      var blobPackage = Functions.BlobPackage.CreateBlobPackage();
      blobPackage.SenderLine = dcsPackage.SenderLine;
      blobPackage.PackageId = dcsPackage.PackageId;
      if (dcsPackage.SourceType == ArioExtensions.DcsFileParser.Constants.CaptureSourceType.Mail)
      {
        blobPackage.SourceType = SmartProcessing.BlobPackage.SourceType.Mail;
        Functions.BlobPackage.FillMailInfoFromDcsPackage(blobPackage, dcsPackage);
      }
      else
      {
        blobPackage.SourceType = SmartProcessing.BlobPackage.SourceType.Folder;
      }
      blobPackage.PackageFolderPath = dcsPackage.PackageFolderPath;
      blobPackage.SourceName = dcsPackage.SourceName;
      blobPackage.DcsProcessingBeginDate = dcsPackage.DcsProcessingBeginDate.FromUtcTime();

      if (!dcsPackage.Blobs.Any() && blobPackage.SourceType == SmartProcessing.BlobPackage.SourceType.Folder)
        throw AppliedCodeException.Create(Resources.EmptyScanPackage);

      // Заполнить информацию о документах пакета.
      foreach (var dcsBlob in dcsPackage.Blobs)
      {
        var blob = Functions.Blob.CreateBlob(dcsBlob);
        blobPackage.Blobs.AddNew().Blob = blob;
      }
      
      // Заполнить тело письма.
      var mailBodyDcsBlob = dcsPackage.MailBodyBlob;
      if (mailBodyDcsBlob != null)
      {
        var bodyBase64 = Convert.ToBase64String(mailBodyDcsBlob.Body);
        if (!this.IsEmptyMailBody(bodyBase64, mailBodyDcsBlob.OriginalFileName))
          blobPackage.MailBodyBlob = Functions.Blob.CreateBlob(mailBodyDcsBlob);
      }
      blobPackage.Save();
      
      return blobPackage;
    }
    
    /// <summary>
    /// Проверить, пустое ли тело письма.
    /// </summary>
    /// <param name="mailBody">Тело письма, закодированное в Base64.</param>
    /// <param name="mailBodyFileName">Имя файла тела письма.</param>
    /// <returns>True - тело письма пустое, иначе - false.</returns>
    /// <remarks>Тело письма передается в формате Base64, так как в параметрах метода с атрибутами Remote и/или Public
    /// нельзя использовать сторонние библиотеки, а также массив байт.</remarks>
    [Remote(IsPure = true), Public]
    public virtual bool IsEmptyMailBody(string mailBody, string mailBodyFileName)
    {
      // Метод находится здесь, потому что в нем есть привязка к AsposeExtensions.
      // ArioExtensions не ссылается на AsposeExtensions, поэтому метод перенести в ArioExtensions нельзя.
      var mailBodyHtmlName = Constants.Module.DcsMailBodyName.Html;
      var mailBodyTxtName = Constants.Module.DcsMailBodyName.Txt;

      // Получить текст из тела письма.
      var bodyText = string.Empty;
      var convertedMailBody = Convert.FromBase64String(mailBody);
      if (string.Equals(mailBodyFileName, mailBodyHtmlName, StringComparison.InvariantCultureIgnoreCase))
      {
        using (var bodyStream = new MemoryStream(convertedMailBody))
          bodyText = IsolatedFunctions.HtmlDocumentParser.GetText(bodyStream);
      }
      else if (string.Equals(mailBodyFileName, mailBodyTxtName, StringComparison.InvariantCultureIgnoreCase))
      {
        using (var bodyStream = new MemoryStream(convertedMailBody))
          using (var streamReader = new StreamReader(bodyStream))
        {
          bodyText = streamReader.ReadToEnd();
        }
      }
      // Очистить текст из тела письма от спецсимволов, чтобы определить, пуст ли он.
      var clearBodyText = bodyText.Trim(new[] { ' ', '\r', '\n', '\0' });
      
      return string.IsNullOrWhiteSpace(clearBodyText);
    }
    
    /// <summary>
    /// Получить статус задачи на обработку файла.
    /// </summary>
    /// <param name="taskId">ИД задачи на обработку.</param>
    /// <returns>Статус и результат задачи.</returns>
    /// <remarks>Значения статусов Task.State: 0 - Новая, 1 - В работе, 2 - Завершена,
    /// 3 - Произошла ошибка, 4 - Обучение завершено, 5 - Прекращена.</remarks>
    public virtual ArioExtensions.Models.ProcessTaskInfo GetProcessTaskInfo(int taskId)
    {
      var arioConnector = this.GetArioConnector();
      return arioConnector.GetProcessTaskInfo(taskId);
    }
    
    /// <summary>
    /// Получить коннектор к Ario.
    /// </summary>
    /// <returns>Коннектор к Ario.</returns>
    /// <remarks> Функция по получению коннектора к Ario уже есть в Docflow,
    /// но ее нельзя использовать здесь, так как возвращаемый тип - Sungero.ArioExtensions.ArioConnector,
    /// а сторонние библиотеки не могут быть в качестве возвращаемого результата Public/Remote функций (ограничение платформы).
    /// Поэтому приходится дублировать функцию GetArioConnector в модуле SmartProcessing.</remarks>
    public virtual Sungero.ArioExtensions.ArioConnector GetArioConnector()
    {
      var timeoutInSeconds = Docflow.PublicFunctions.SmartProcessingSetting.Remote.GetArioConnectionTimeoutInSeconds();
      var timeout = new TimeSpan(0, 0, timeoutInSeconds);
      var smartProcessingSettings = Docflow.PublicFunctions.SmartProcessingSetting.GetSettings();
      var password = string.IsNullOrEmpty(smartProcessingSettings.Password)
        ? string.Empty
        : Encryption.Decrypt(smartProcessingSettings.Password);
      return ArioExtensions.ArioConnector.Get(smartProcessingSettings.ArioUrl, timeout, smartProcessingSettings.Login, password);
    }
    
    #endregion
    
    #region Распаковка и заполнение фактов
    
    /// <summary>
    /// Десериализовать результат классификации комплекта документов в Ario.
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    /// <returns>Десериализованный результат классификации комплекта документов в Ario.</returns>
    [Public]
    public virtual IArioPackage UnpackArioPackage(IBlobPackage blobPackage)
    {
      var packageId = blobPackage.PackageId;
      this.LogMessage("Smart processing. UnpackArioPackage...", packageId);
      
      var arioPackage = ArioPackage.Create();
      arioPackage.Documents = new List<IArioDocument>();
      if (blobPackage.Blobs.Count == 0)
        return arioPackage;
      
      var blobs = blobPackage.Blobs.Select(x => x.Blob);
      foreach (var blob in blobs)
      {
        var arioDocuments = this.UnpackArioDocuments(blob);
        arioPackage.Documents.AddRange(arioDocuments);
      }
      
      this.LogMessage("Smart processing. UnpackArioPackage.", packageId);
      return arioPackage;
    }
    
    /// <summary>
    /// Десериализовать результат классификации документа в Ario.
    /// </summary>
    /// <param name="blob">Бинарный образ документа.</param>
    /// <returns>Список документов, распознанных в Ario.</returns>
    [Public]
    public virtual List<IArioDocument> UnpackArioDocuments(IBlob blob)
    {
      var arioDocuments = new List<IArioDocument>();
      
      // Документ не был обработан в Ario.
      if (blob.ArioResultJson == null)
      {
        arioDocuments.Add(this.UnpackFailedProcessArioDocument(blob));
        return arioDocuments;
      }
      
      var packageProcessResults = ArioExtensions.ArioConnector.DeserializeClassifyAndExtractFactsResultString(blob.ArioResultJson);
      foreach (var packageProcessResult in packageProcessResults)
      {
        if (packageProcessResult.ClassificationResult == null)
          arioDocuments.Add(this.UnpackUnprocessedArioDocument(blob, packageProcessResult));
        else
          arioDocuments.Add(this.UnpackArioDocument(blob, packageProcessResult));
      }
      
      return arioDocuments;
    }
    
    /// <summary>
    /// Распаковать обработанный в Ario документ.
    /// </summary>
    /// <param name="blob">Бинарный образ документа.</param>
    /// <param name="packageProcessResult">Результат обработки.</param>
    /// <returns>Документ, обработанный в Ario.</returns>
    public virtual IArioDocument UnpackArioDocument(IBlob blob, ArioExtensions.Models.PackageProcessResult packageProcessResult)
    {
      var arioDocument = ArioDocument.Create();
      arioDocument.IsProcessedByArio = true;
      arioDocument.BodyFromArio = new byte[0];
      
      // Класс и гуид тела документа.
      var clsResult = packageProcessResult.ClassificationResult;
      arioDocument.BodyGuid = packageProcessResult.Guid;
      arioDocument.IsRecognized = clsResult.PredictedClass != null;
      arioDocument.OriginalBlob = blob;
      if (!arioDocument.IsRecognized)
      {
        var message = "Capture Service cannot classify the document.";
        if (blob != null)
          message = string.Format("{0} Original file name: {1}", message, blob.OriginalFileName);
        Logger.DebugFormat(message);
      }
      
      // Создать результат распознавания.
      var recognitionInfo = EntityRecognitionInfos.Create();
      recognitionInfo.RecognizedClass = arioDocument.IsRecognized ? clsResult.PredictedClass.Name : string.Empty;
      recognitionInfo.Name = recognitionInfo.RecognizedClass ?? string.Empty;
      if (clsResult.PredictedProbability != null)
        recognitionInfo.ClassProbability = (double)clsResult.PredictedProbability;
      
      // Доп. классификаторы.
      if (packageProcessResult.AdditionalClassificationResults != null)
      {
        foreach (var additionalClassificationResult in packageProcessResult.AdditionalClassificationResults.Results)
        {
          var additionalClassifier = recognitionInfo.AdditionalClassifiers.AddNew();
          additionalClassifier.ClassifierID = additionalClassificationResult.ClassifierId;
          var additionalPredictedClass = additionalClassificationResult.PredictedClass;
          additionalClassifier.PredictedClass = additionalPredictedClass != null ? additionalPredictedClass.Name : string.Empty;
          additionalClassifier.Probability = additionalClassificationResult.PredictedProbability;
        }
      }
      
      // Факты и поля фактов.
      this.FillAllFacts(packageProcessResult, arioDocument, recognitionInfo);
      recognitionInfo.Save();
      arioDocument.RecognitionInfo = recognitionInfo;
      
      // Печати и подписи.
      this.FillStamps(packageProcessResult, arioDocument);
      this.FillSignatures(packageProcessResult, arioDocument);
      
      return arioDocument;
    }
    
    /// <summary>
    /// Распаковать необработанный в Ario документ.
    /// </summary>
    /// <param name="blob">Бинарный образ документа.</param>
    /// <param name="packageProcessResult">Результат обработки.</param>
    /// <returns>Документ, необработанный в Ario.</returns>
    public virtual IArioDocument UnpackUnprocessedArioDocument(IBlob blob, ArioExtensions.Models.PackageProcessResult packageProcessResult)
    {
      var arioDocument = ArioDocument.Create();
      arioDocument.IsProcessedByArio = true;
      arioDocument.BodyFromArio = new byte[0];
      arioDocument.OriginalBlob = blob;
      arioDocument.IsProcessedByArio = true;
      arioDocument.IsRecognized = false;
      arioDocument.BodyGuid = packageProcessResult.Guid;
      
      var recognitionInfo = EntityRecognitionInfos.Create();
      recognitionInfo.RecognizedClass = string.Empty;
      recognitionInfo.Name = string.Empty;
      recognitionInfo.Save();
      arioDocument.RecognitionInfo = recognitionInfo;
      return arioDocument;
    }
    
    /// <summary>
    /// Распаковать документ обработанный в Ario с ошибкой.
    /// </summary>
    /// <param name="blob">Бинарный образ документа.</param>
    /// <returns>Документ, необработанный в Ario.</returns>
    public virtual IArioDocument UnpackFailedProcessArioDocument(IBlob blob)
    {
      var arioDocument = ArioDocument.Create();
      arioDocument.OriginalBlob = blob;
      arioDocument.IsProcessedByArio = false;
      arioDocument.IsRecognized = false;
      arioDocument.FailedArioProcessDocument = blob.ArioTaskStatus != SmartProcessing.Blob.ArioTaskStatus.Success && blob.ArioTaskId != null;
      arioDocument.BodyFromArio = new byte[0];
      return arioDocument;
    }
    
    /// <summary>
    /// Заполнить все факты и поля фактов.
    /// </summary>
    /// <param name="packageProcessResult">Результат обработки.</param>
    /// <param name="arioDocument">Распознанный в Ario документ.</param>
    /// <param name="docInfo">Справочник с результатами распознавания документа.</param>
    public virtual void FillAllFacts(ArioExtensions.Models.PackageProcessResult packageProcessResult,
                                     IArioDocument arioDocument,
                                     IEntityRecognitionInfo docInfo)
    {
      arioDocument.Facts = new List<IArioFact>();
      var smartProcessingSettings = Docflow.PublicFunctions.SmartProcessingSetting.GetSettings();
      if (packageProcessResult.ExtractionResult.Facts != null)
      {
        var pages = packageProcessResult.ExtractionResult.DocumentPages;
        var facts = packageProcessResult.ExtractionResult.Facts
          .Where(f => !string.IsNullOrWhiteSpace(f.Name))
          .Where(f => f.Fields.Any())
          .ToList();
        foreach (var fact in facts)
        {
          var fields = fact.Fields.Where(f => f != null)
            .Where(f => f.Probability >= smartProcessingSettings.LowerConfidenceLimit)
            .Select(f => ArioFactField.Create(f.Id, f.Name, f.Value, f.Probability));
          arioDocument.Facts.Add(ArioFact.Create(fact.Id, fact.Name, fields.ToList()));
          
          foreach (var factField in fact.Fields)
          {
            var fieldInfo = docInfo.Facts.AddNew();
            fieldInfo.FactId = fact.Id;
            fieldInfo.FieldId = factField.Id;
            fieldInfo.FactName = fact.Name;
            fieldInfo.FieldName = factField.Name;
            fieldInfo.FieldProbability = factField.Probability;
            fieldInfo.FieldConfidence = factField.Сonfidence;
            var fieldValue = factField.Value;
            if (fieldValue != null && fieldValue.Length > 1000)
            {
              fieldValue = fieldValue.Substring(0, 1000);
              Logger.DebugFormat("WARN. Value truncated. Length is over 1000 characters. GetRecognitionResults. FactID({0}). FieldID({1}).",
                                 fact.Id,
                                 factField.Id);
            }
            fieldInfo.FieldValue = fieldValue;
            
            // Позиция подсветки фактов в теле документа.
            if (factField.Positions != null)
            {
              var positions = factField.Positions
                .Where(p => p != null)
                .Select(p => this.CalculatePosition(p, pages));
              fieldInfo.Position = string.Join(Docflow.PublicConstants.Module.PositionsDelimiter.ToString(), positions);
            }
          }
        }
      }
    }
    
    /// <summary>
    /// Заполнить информацию о печатях.
    /// </summary>
    /// <param name="packageProcessResult">Результат обработки.</param>
    /// <param name="arioDocument">Распознанный в Ario документ.</param>
    public virtual void FillStamps(ArioExtensions.Models.PackageProcessResult packageProcessResult,
                                   IArioDocument arioDocument)
    {
      arioDocument.Stamps = new List<IArioStamp>();
      if (packageProcessResult.ExtractionResult == null)
        return;
      
      if (packageProcessResult.ExtractionResult.Stamps != null)
      {
        var pages = packageProcessResult.ExtractionResult.DocumentPages;
        foreach (var stampInfo in packageProcessResult.ExtractionResult.Stamps)
        {
          var stamp = ArioStamp.Create();
          
          stamp.Probability = stampInfo.Probability;
          stamp.Angle = stampInfo.Angle;
          var position = this.CalculatePosition(stampInfo.Position, pages);
          stamp.Position = string.Join(Docflow.PublicConstants.Module.PositionsDelimiter.ToString(), position);
          arioDocument.Stamps.Add(stamp);
        }
      }
    }
    
    /// <summary>
    /// Заполнить информацию о подписях.
    /// </summary>
    /// <param name="packageProcessResult">Результат обработки.</param>
    /// <param name="arioDocument">Распознанный в Ario документ.</param>
    public virtual void FillSignatures(ArioExtensions.Models.PackageProcessResult packageProcessResult,
                                       IArioDocument arioDocument)
    {
      arioDocument.Signatures = new List<IArioSignature>();
      if (packageProcessResult.ExtractionResult == null)
        return;
      
      if (packageProcessResult.ExtractionResult.Signatures != null)
      {
        var pages = packageProcessResult.ExtractionResult.DocumentPages;
        foreach (var signatureInfo in packageProcessResult.ExtractionResult.Signatures)
        {
          var signature = ArioSignature.Create();
          
          signature.Probability = signatureInfo.Probability;
          signature.Angle = signatureInfo.Angle;
          var position = this.CalculatePosition(signatureInfo.Position, pages);
          signature.Position = string.Join(Docflow.PublicConstants.Module.PositionsDelimiter.ToString(), position);
          arioDocument.Signatures.Add(signature);
        }
      }
    }
    
    /// <summary>
    /// Вычислить строку с информацией о позиции в документе.
    /// </summary>
    /// <param name="position">Позиция в документе.</param>
    /// <param name="pages">Информация о страницах.</param>
    /// <returns>Строка с информацией о позицией в документе.</returns>
    public virtual string CalculatePosition(ArioExtensions.Models.Position position, List<ArioExtensions.Models.PageInfo> pages)
    {
      return string.Format("{1}{0}{2}{0}{3}{0}{4}{0}{5}{0}{6}{0}{7}",
                           Docflow.PublicConstants.Module.PositionElementDelimiter,
                           position.Page,
                           (int)Math.Round(position.Top),
                           (int)Math.Round(position.Left),
                           (int)Math.Round(position.Width),
                           (int)Math.Round(position.Height),
                           (int)Math.Round(pages.Where(x => x.Number == position.Page).Select(x => x.Width).FirstOrDefault()),
                           (int)Math.Round(pages.Where(x => x.Number == position.Page).Select(x => x.Height).FirstOrDefault()));
    }
    
    #endregion
    
    #region Создание и заполнение пакета
    
    /// <summary>
    /// Сформировать пакет документов.
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    /// <param name="arioPackage">Пакет результатов обработки документов в Ario.</param>
    /// <returns>Пакет созданных документов.</returns>
    [Public]
    public virtual IDocumentPackage BuildDocumentPackage(IBlobPackage blobPackage, IArioPackage arioPackage)
    {
      var packageId = blobPackage.PackageId;
      this.LogMessage("Smart processing. BuildDocumentPackage...", packageId);
      
      var documentPackage = this.PrepareDocumentPackage(blobPackage, arioPackage);
      
      documentPackage.Responsible = this.GetResponsible(blobPackage);
      
      foreach (var documentInfo in documentPackage.DocumentInfos)
      {
        try
        {
          var document = this.CreateDocument(documentInfo, documentPackage);
          this.CreateVersion(document, documentInfo);
          
          if (!documentInfo.FailedCreateVersion)
          {
            this.FillDeliveryMethod(document, blobPackage.SourceType);
            this.FillVerificationState(document);
          }
          
          this.SaveDocument(document, documentInfo);
        }
        catch (Exception ex)
        {
          this.LogError("Smart processing. BuildDocumentPackage. Error while trying to create the document.", ex, packageId);
          documentInfo.FailedCreateDocument = true;
          this.CreateSimpleDocument(documentInfo, documentPackage.Responsible, blobPackage.SourceType);
        }
      }
      this.CreateDocumentFromEmailBody(documentPackage);
      
      this.LogMessage("Smart processing. BuildDocumentPackage.", packageId);
      return documentPackage;
    }
    
    /// <summary>
    /// Создать незаполненный пакет документов.
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    /// <param name="arioPackage">Пакет результатов обработки документов в Ario.</param>
    /// <returns>Заготовка пакета документов.</returns>
    [Public]
    public virtual IDocumentPackage PrepareDocumentPackage(IBlobPackage blobPackage, IArioPackage arioPackage)
    {
      var isFuzzySearchEnabled = this.IsFuzzySearchEnabled();
      if (!isFuzzySearchEnabled)
        this.LogMessage("Smart processing. PrepareDocumentPackage. Fuzzy search is not enabled.", blobPackage);
      
      var documentInfos = new List<IDocumentInfo>();
      foreach (var arioDocument in arioPackage.Documents)
      {
        try
        {
          this.FillArioDocumentBody(arioDocument);
        }
        catch (Exception ex)
        {
          arioDocument.IsProcessedByArio = false;
          arioDocument.IsRecognized = false;
          arioDocument.FailedArioProcessDocument = true;
          
          if (blobPackage.SourceType != SmartProcessing.BlobPackage.SourceType.Mail)
            throw ex;
          
          this.LogError("Smart processing. PrepareDocumentPackage. Error while filling Ario document body.", ex, blobPackage);
        }
        var documentInfo = this.CreateDocumentInfo(arioDocument);
        documentInfo.IsFuzzySearchEnabled = isFuzzySearchEnabled;
        documentInfos.Add(documentInfo);
      }

      var documentPackage = DocumentPackage.Create();
      documentPackage.DocumentInfos = documentInfos;
      documentPackage.BlobPackage = blobPackage;

      return documentPackage;
    }
    
    /// <summary>
    /// Заполнить тело документа, полученного из Ario.
    /// </summary>
    /// <param name="arioDocument">Распознанный в Ario документ.</param>
    [Public]
    public virtual void FillArioDocumentBody(IArioDocument arioDocument)
    {
      arioDocument.BodyFromArio = new byte[0];
      
      if (arioDocument.IsProcessedByArio)
        arioDocument.BodyFromArio = this.GetDocumentByGuidFromArio(arioDocument.BodyGuid);
    }
    
    /// <summary>
    /// Получить тело документа из Ario.
    /// </summary>
    /// <param name="bodyGuid">Guid тела документа в Ario.</param>
    /// <returns>Тело документа.</returns>
    [Public]
    public virtual byte[] GetDocumentByGuidFromArio(string bodyGuid)
    {
      var buffer = new byte[0];
      
      var smartProcessingSettings = Sungero.Docflow.PublicFunctions.SmartProcessingSetting.GetSettings();
      using (var bodyFromArio = Sungero.Docflow.PublicFunctions.SmartProcessingSetting
             .GetDocumentBody(smartProcessingSettings, bodyGuid))
      {
        var bufferLen = (int)bodyFromArio.Length;
        buffer = new byte[bufferLen];
        bodyFromArio.Read(buffer, 0, bufferLen);
      }
      
      return buffer;
    }
    
    /// <summary>
    /// Создать информацию о документе.
    /// </summary>
    /// <param name="arioDocument">Распознанный в Ario документ.</param>
    /// <returns>Информация о документе.</returns>
    [Public]
    public virtual IDocumentInfo CreateDocumentInfo(IArioDocument arioDocument)
    {
      var documentInfo = new DocumentInfo();
      documentInfo.ArioDocument = arioDocument;
      documentInfo.IsRecognized = arioDocument.IsRecognized;
      documentInfo.FailedArioProcessDocument = arioDocument.FailedArioProcessDocument;
      
      return documentInfo;
    }
    
    /// <summary>
    /// Получить ответственного за верификацию пакета документов.
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    /// <returns>Ответственный за верификацию пакета документов.</returns>
    [Public]
    public IEmployee GetResponsible(IBlobPackage blobPackage)
    {
      var smartProcessingSettings = Docflow.PublicFunctions.SmartProcessingSetting.GetSettings();
      var responsible = Docflow.PublicFunctions.SmartProcessingSetting
        .GetDocumentProcessingResponsible(smartProcessingSettings, blobPackage.SenderLine);
      
      return responsible;
    }
    
    /// <summary>
    /// Создать простой документ.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию пакета документов.</param>
    /// <param name="sourceType">Тип источника.</param>
    [Public]
    public virtual void CreateSimpleDocument(IDocumentInfo documentInfo, IEmployee responsible, Enumeration? sourceType)
    {
      var document = this.CreateSimpleDocument(documentInfo, responsible);
      this.CreateVersion(document, documentInfo);
      
      if (!documentInfo.FailedCreateVersion)
      {
        this.FillDeliveryMethod(document, sourceType);
        this.FillVerificationState(document);
      }
      
      this.SaveDocument(document, documentInfo);
      documentInfo.Document = document;
    }
    
    #endregion
    
    #region Создание документов
    
    #region Общий механизм создания документов
    
    /// <summary>
    /// Создать документ.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="documentPackage">Пакет документов.</param>
    /// <returns>Созданный документ.</returns>
    [Public]
    public virtual IOfficialDocument CreateDocument(IDocumentInfo documentInfo, IDocumentPackage documentPackage)
    {
      var document = OfficialDocuments.Null;
      var arioDocument = documentInfo.ArioDocument;
      if (!arioDocument.FailedArioProcessDocument)
      {
        var predictedClass = documentInfo.IsRecognized ? arioDocument.RecognitionInfo.RecognizedClass : string.Empty;
        document = arioDocument.IsProcessedByArio ? this.GetDocumentByBarcode(documentInfo) : OfficialDocuments.Null;
        if (document == null)
          document = this.CreateDocumentByFacts(predictedClass, documentInfo, documentPackage.Responsible);
      }
      else
      {
        document = this.CreateSimpleDocument(documentInfo, documentPackage.Responsible);
      }
      documentInfo.Document = document;

      return document;
    }
    
    /// <summary>
    /// Создать документ по классу и фактам.
    /// </summary>
    /// <param name="className">Имя класса.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный.</param>
    /// <returns>Документ.</returns>
    [Public]
    public virtual IOfficialDocument CreateDocumentByFacts(string className,
                                                           IDocumentInfo documentInfo,
                                                           IEmployee responsible)
    {
      // Если не нашли правило для обработки по имени класса или класс не распознался,
      // то взять правило с пустым именем класса.
      var smartProcessingSettings = Docflow.PublicFunctions.SmartProcessingSetting.GetSettings();
      var processingRule = smartProcessingSettings.ProcessingRules
        .Where(x => string.Equals(x.ClassName, className, StringComparison.InvariantCultureIgnoreCase))
        .FirstOrDefault();
      
      if (processingRule == null)
      {
        if (!string.IsNullOrEmpty(className))
          Logger.DebugFormat("Smart processing. There is no processing rule for class '{0}'", className);
        processingRule = smartProcessingSettings.ProcessingRules
          .Where(x => string.IsNullOrWhiteSpace(x.ClassName))
          .FirstOrDefault();
      }
      
      var document = OfficialDocuments.Null;
      var parameters = new object[] { documentInfo, responsible };
      if (processingRule != null)
        document = (IOfficialDocument)ExecuteModuleServerFunction(processingRule.ModuleName, processingRule.FunctionName, parameters);
      
      return document;
    }
    
    /// <summary>
    /// Создать тело документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void CreateVersion(IOfficialDocument document,
                                      IDocumentInfo documentInfo)
    {
      var versionNote = string.Empty;
      if (documentInfo.FoundByBarcode)
      {
        var documentParams = ((Domain.Shared.IExtendedEntity)document).Params;
        var documentLockInfo = Locks.GetLockInfo(document);
        if (documentLockInfo.IsLocked)
        {
          documentInfo.FailedCreateVersion = true;
          return;
        }
        else
        {
          documentParams[Docflow.PublicConstants.OfficialDocument.FindByBarcodeParamName] = true;
          versionNote = OfficialDocuments.Resources.VersionCreatedByCaptureService;
        }
      }
      
      this.CreateVersion(document, documentInfo.ArioDocument, versionNote);
    }
    
    /// <summary>
    /// Создать тело документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="arioDocument">Результат обработки входящего документа в Ario.</param>
    /// <param name="versionNote">Примечание к версии.</param>
    [Public]
    public virtual void CreateVersion(IOfficialDocument document,
                                      IArioDocument arioDocument,
                                      string versionNote = "")
    {
      var needCreatePublicBody = arioDocument.OriginalBlob != null && arioDocument.OriginalBlob.Body.Size != 0;
      var isRecognized = arioDocument.RecognitionInfo != null && !arioDocument.FailedArioProcessDocument;
      var pdfApp = Content.AssociatedApplications.GetByExtension(Docflow.PublicConstants.OfficialDocument.PdfExtension);
      if (pdfApp == Content.AssociatedApplications.Null)
        pdfApp = Docflow.PublicFunctions.Module.GetAssociatedApplicationByFileName(arioDocument.OriginalBlob.FilePath);
      
      var originalFileApp = Content.AssociatedApplications.Null;
      if (needCreatePublicBody || !isRecognized)
        originalFileApp = Docflow.PublicFunctions.Module.GetAssociatedApplicationByFileName(arioDocument.OriginalBlob.FilePath);
      
      // При создании версии Subject не должен быть пустым, иначе задваивается имя документа.
      var subjectIsEmpty = string.IsNullOrEmpty(document.Subject);
      if (subjectIsEmpty)
        document.Subject = "tmp_Subject";
      
      // Выключить error-логирование при доступе к зашифрованной версии.
      AccessRights.SuppressSecurityEvents(
        () =>
        {
          document.CreateVersion();
          var version = document.LastVersion;
          
          if (!isRecognized)
          {
            using (var body = arioDocument.OriginalBlob.Body.Read())
              version.Body.Write(body);
            version.AssociatedApplication = originalFileApp;
          }
          else if (needCreatePublicBody)
          {
            using (var publicBody = new MemoryStream(arioDocument.BodyFromArio))
              version.PublicBody.Write(publicBody);
            using (var body = arioDocument.OriginalBlob.Body.Read())
              version.Body.Write(body);
            version.AssociatedApplication = pdfApp;
            version.BodyAssociatedApplication = originalFileApp;
          }
          else
          {
            using (var body = new MemoryStream(arioDocument.BodyFromArio))
              version.Body.Write(body);
            
            version.AssociatedApplication = pdfApp;
          }

          if (!string.IsNullOrEmpty(versionNote))
            version.Note = versionNote;
        });
      
      // Очистить Subject, если он был пуст до создания версии.
      if (subjectIsEmpty)
        document.Subject = string.Empty;
    }
    
    /// <summary>
    /// Сохранить документ.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void SaveDocument(IOfficialDocument document, IDocumentInfo documentInfo)
    {
      if (!documentInfo.FailedCreateVersion)
      {
        // Удаляем параметр, чтобы не вызывать асинхронный обработчик по выдаче прав на документ, так как это вызывает ошибку (Bug 199971, 202010).
        // Асинхронный обработчик запускается после выполнения всех операций по документу.
        var documentParams = ((Sungero.Domain.Shared.IExtendedEntity)document).Params;
        if (documentParams.ContainsKey(Sungero.Docflow.PublicConstants.OfficialDocument.GrantAccessRightsToDocumentAsync))
          documentParams.Remove(Sungero.Docflow.PublicConstants.OfficialDocument.GrantAccessRightsToDocumentAsync);
        document.Save();
      }

      var arioDocument = documentInfo.ArioDocument;
      if (arioDocument.IsProcessedByArio)
      {
        // Добавить ИД документа в запись справочника с результатами обработки Ario.
        arioDocument.RecognitionInfo.EntityId = document.Id;
        // Заполнить поле Тип сущности guid'ом конечного типа сущности.
        arioDocument.RecognitionInfo.EntityType = document.GetEntityMetadata().GetOriginal().NameGuid.ToString();
        arioDocument.RecognitionInfo.Save();
      }
    }
    
    /// <summary>
    /// Выполнить серверную функцию модуля.
    /// </summary>
    /// <param name="moduleName">Имя решения и модуля.</param>
    /// <param name="functionName">Имя функции.</param>
    /// <param name="parameters">Массив параметров.</param>
    /// <returns>Результат выполнения.</returns>
    /// <remarks>Нельзя исп. params в public-функциях.</remarks>
    [Public]
    public static object ExecuteModuleServerFunction(string moduleName, string functionName, object[] parameters)
    {
      var functionTypeName = string.Format("{0}.Functions.Module", moduleName);
      var sharedAssemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.FullName.Contains(string.Format("{0}.Server", moduleName)));
      if (sharedAssemblies.Count() == 0)
      {
        throw new Exception(string.Format("Smart processing. Module \"{0}\" does not exist.", moduleName));
      }
      var sharedAssembly = sharedAssemblies.First();
      var modulesFunctions = sharedAssembly.GetTypes().First(a => a.FullName == functionTypeName);
      
      var method = modulesFunctions.GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        .Where(x => x.Name == functionName && x.GetParameters().Length == parameters.Length).SingleOrDefault();
      if (method == null)
      {
        throw new Exception(string.Format("Smart processing. Function \"{0}\" of module \"{1}\" does not exist.", functionName, moduleName));
      }
      var document = method.Invoke(null, parameters);

      return document;
    }

    /// <summary>
    /// Проверить возможность использования нечеткого поиска при заполнении карточек документов.
    /// </summary>
    /// <returns>True - если нечеткий поиск включен, иначе - false.</returns>
    public virtual bool IsFuzzySearchEnabled()
    {
      return Commons.PublicFunctions.Module.IsIntelligenceEnabled() &&
        Commons.PublicFunctions.Module.IsElasticsearchConfigured() &&
        Commons.PublicFunctions.Module.IsElasticsearchEnabled();
    }

    #endregion
    
    #region Создание конкретных типов документов
    
    /// <summary>
    /// Создать входящее письмо.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    /// <returns>Входящее письмо.</returns>
    [Public]
    public virtual IOfficialDocument CreateIncomingLetter(IDocumentInfo documentInfo,
                                                          IEmployee responsible)
    {
      // Входящее письмо.
      var document = RecordManagement.IncomingLetters.Create();
      if (documentInfo.IsFuzzySearchEnabled)
        this.FillIncomingLetterPropertiesFuzzy(document, documentInfo, responsible);
      else
        this.FillIncomingLetterProperties(document, documentInfo, responsible);
      
      return document;
    }
    
    /// <summary>
    /// Создать акт выполненных работ.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    /// <returns>Акт выполненных работ.</returns>
    [Public]
    public virtual IOfficialDocument CreateContractStatement(IDocumentInfo documentInfo, IEmployee responsible)
    {
      // Акт выполненных работ.
      var document = FinancialArchive.ContractStatements.Create();
      if (documentInfo.IsFuzzySearchEnabled)
        this.FillContractStatementPropertiesFuzzy(document, documentInfo, responsible);
      else
        this.FillContractStatementProperties(document, documentInfo, responsible);
      
      return document;
    }
    
    /// <summary>
    /// Создать товарную накладную.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    /// <returns>Товарная накладная.</returns>
    [Public]
    public virtual IOfficialDocument CreateWaybill(IDocumentInfo documentInfo, IEmployee responsible)
    {
      // Товарная накладная.
      var document = FinancialArchive.Waybills.Create();
      if (documentInfo.IsFuzzySearchEnabled)
        this.FillWaybillPropertiesFuzzy(document, documentInfo, responsible);
      else
        this.FillWaybillProperties(document, documentInfo, responsible);
      
      return document;
    }
    
    /// <summary>
    /// Создать счет-фактуру.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    /// <returns>Счет-фактура.</returns>
    [Public]
    public virtual IOfficialDocument CreateTaxInvoice(IDocumentInfo documentInfo,
                                                      IEmployee responsible)
    {
      var documentParties = documentInfo.IsFuzzySearchEnabled ?
        this.GetRecognizedTaxInvoicePartiesFuzzy(documentInfo.ArioDocument.Facts, responsible) :
        this.GetRecognizedTaxInvoiceParties(documentInfo.ArioDocument.Facts, responsible);
      
      if (documentParties.IsDocumentOutgoing.Value == true)
      {
        var document = FinancialArchive.OutgoingTaxInvoices.Create();
        this.FillOutgoingTaxInvoiceProperties(document, documentInfo, responsible, documentParties);
        return document;
      }
      else
      {
        var document = FinancialArchive.IncomingTaxInvoices.Create();
        this.FillIncomingTaxInvoiceProperties(document, documentInfo, responsible, documentParties);
        return document;
      }
    }
    
    /// <summary>
    /// Создать корректировочный счет-фактуру.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    /// <returns>Корректировочный счет-фактура.</returns>
    [Public]
    public virtual IOfficialDocument CreateTaxInvoiceCorrection(IDocumentInfo documentInfo,
                                                                IEmployee responsible)
    {
      var documentParties = documentInfo.IsFuzzySearchEnabled ?
        this.GetRecognizedTaxInvoicePartiesFuzzy(documentInfo.ArioDocument.Facts, responsible) :
        this.GetRecognizedTaxInvoiceParties(documentInfo.ArioDocument.Facts, responsible);
      
      if (documentParties.IsDocumentOutgoing.Value == true)
      {
        var document = FinancialArchive.OutgoingTaxInvoices.Create();
        document.IsAdjustment = true;
        this.FillOutgoingTaxInvoiceProperties(document, documentInfo, responsible, documentParties);
        return document;
      }
      else
      {
        var document = FinancialArchive.IncomingTaxInvoices.Create();
        document.IsAdjustment = true;
        this.FillIncomingTaxInvoiceProperties(document, documentInfo, responsible, documentParties);
        return document;
      }
    }
    
    /// <summary>
    /// Создать универсальный передаточный документ.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    /// <returns>Универсальный передаточный документ.</returns>
    [Public]
    public virtual IOfficialDocument CreateUniversalTransferDocument(IDocumentInfo documentInfo,
                                                                     IEmployee responsible)
    {
      // УПД.
      var document = FinancialArchive.UniversalTransferDocuments.Create();
      if (documentInfo.IsFuzzySearchEnabled)
        this.FillUniversalTransferDocumentPropertiesFuzzy(document, documentInfo, responsible);
      else
        this.FillUniversalTransferDocumentProperties(document, documentInfo, responsible);
      return document;
    }
    
    /// <summary>
    /// Создать универсальный корректировочный документ.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    /// <returns>Универсальный корректировочный документ.</returns>
    [Public]
    public virtual IOfficialDocument CreateUniversalTransferCorrectionDocument(IDocumentInfo documentInfo,
                                                                               IEmployee responsible)
    {
      // УКД.
      var document = FinancialArchive.UniversalTransferDocuments.Create();
      document.IsAdjustment = true;
      if (documentInfo.IsFuzzySearchEnabled)
        this.FillUniversalTransferDocumentPropertiesFuzzy(document, documentInfo, responsible);
      else
        this.FillUniversalTransferDocumentProperties(document, documentInfo, responsible);
      return document;
    }
    
    /// <summary>
    /// Создать входящий счет на оплату.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    /// <returns>Входящий счет на оплату.</returns>
    [Public]
    public virtual IOfficialDocument CreateIncomingInvoice(IDocumentInfo documentInfo,
                                                           IEmployee responsible)
    {
      // Счет на оплату.
      var document = Contracts.IncomingInvoices.Create();
      if (documentInfo.IsFuzzySearchEnabled)
        this.FillIncomingInvoicePropertiesFuzzy(document, documentInfo, responsible);
      else
        this.FillIncomingInvoiceProperties(document, documentInfo, responsible);
      return document;
    }
    
    /// <summary>
    /// Создать договор.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    /// <returns>Договор.</returns>
    [Public]
    public virtual IOfficialDocument CreateContract(IDocumentInfo documentInfo,
                                                    IEmployee responsible)
    {
      // Договор.
      var document = Contracts.Contracts.Create();
      if (documentInfo.IsFuzzySearchEnabled)
        this.FillContractPropertiesFuzzy(document, documentInfo, responsible);
      else
        this.FillContractProperties(document, documentInfo, responsible);
      
      return document;
    }
    
    /// <summary>
    /// Создать доп. соглашение.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    /// <returns>Доп. соглашение.</returns>
    [Public]
    public virtual IOfficialDocument CreateSupAgreement(IDocumentInfo documentInfo,
                                                        IEmployee responsible)
    {
      // Доп.соглашение.
      var document = Contracts.SupAgreements.Create();
      if (documentInfo.IsFuzzySearchEnabled)
        this.FillSupAgreementPropertiesFuzzy(document, documentInfo, responsible);
      else
        this.FillSupAgreementProperties(document, documentInfo, responsible);
      
      return document;
    }
    
    /// <summary>
    /// Создать простой документ.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    /// <returns>Простой документ.</returns>
    [Public]
    public virtual IOfficialDocument CreateSimpleDocument(IDocumentInfo documentInfo,
                                                          IEmployee responsible)
    {
      // Все нераспознанные документы создать простыми.
      var document = Docflow.SimpleDocuments.Create();
      
      // Имя документа сделать шаблонным, чтобы не падало сохранение, т.к. это свойство обязательное у документа.
      // Заполнение нужным значением будет выполнено в RenameNotClassifiedDocuments.
      var documentName = Resources.SimpleDocumentName;
      
      this.FillSimpleDocumentProperties(document, documentInfo, responsible, documentName);
      
      return document;
    }
    
    #endregion
    
    #endregion
    
    #region Заполнение свойств документов
    
    #region Заполнение общих свойств
    
    /// <summary>
    /// Заполнить способ доставки.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="sourceType">Тип источника.</param>
    [Public]
    public virtual void FillDeliveryMethod(IOfficialDocument document, Sungero.Core.Enumeration? sourceType)
    {
      var methodName = sourceType == SmartProcessing.BlobPackage.SourceType.Folder
        ? MailDeliveryMethods.Resources.MailMethod
        : MailDeliveryMethods.Resources.EmailMethod;
      
      document.DeliveryMethod = MailDeliveryMethods.GetAll()
        .Where(m => m.Name.Equals(methodName, StringComparison.InvariantCultureIgnoreCase))
        .FirstOrDefault();
    }
    
    /// <summary>
    /// Заполнить статус верификации для документов, в которых поддерживается режим верификации.
    /// </summary>
    /// <param name="document">Документ.</param>
    [Public]
    public virtual void FillVerificationState(IOfficialDocument document)
    {
      document.VerificationState = Docflow.OfficialDocument.VerificationState.InProcess;
    }
    
    /// <summary>
    /// Заполнить вид документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <remarks>Заполняется видом документа по умолчанию.
    /// Если вид документа по умолчанию не указан, то формируется список всех доступных видов документа
    /// и берется первый элемент из этого списка.</remarks>
    [Public]
    public virtual void FillDocumentKind(Docflow.IOfficialDocument document)
    {
      var documentKind = Docflow.PublicFunctions.OfficialDocument.GetDefaultDocumentKind(document);
      if (documentKind == null)
      {
        documentKind = Docflow.PublicFunctions.DocumentKind.GetAvailableDocumentKinds(document).FirstOrDefault();
        if (documentKind == null)
        {
          Logger.Error(string.Format("Cannot fill document kind for document type {0}.", Commons.PublicFunctions.Module.GetFinalTypeName(document)));
          return;
        }
        Logger.Debug(string.Format("Cannot find default document kind for document type {0}", Commons.PublicFunctions.Module.GetFinalTypeName(document)));
      }
      document.DocumentKind = documentKind;
    }
    
    #endregion
    
    #region Заполнение регистрационных данных
    
    /// <summary>
    /// Заполнить регистрационные данные документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="factName">Наименование факта с датой и номером документа.</param>
    /// <param name="withoutNumberLabel">Замещающий текст для номера, если он не распознан или отсутствует.</param>
    /// <remarks>Нумеруемым типам присваиваем номер, у остальных просто заполняем поля.</remarks>
    [Public]
    public virtual void FillDocumentRegistrationData(IOfficialDocument document,
                                                     IDocumentInfo documentInfo,
                                                     string factName,
                                                     string withoutNumberLabel)
    {
      // Для ненумеруемых документов регистрации нет.
      if (document.DocumentKind == null || document.DocumentKind.NumberingType == Docflow.DocumentKind.NumberingType.NotNumerable)
        return;
      
      if (document.DocumentKind.NumberingType == Docflow.DocumentKind.NumberingType.Numerable)
        this.NumberDocument(document, documentInfo, factName, withoutNumberLabel);
      
      // Если не получилось пронумеровать, то заполнить дату и номер по логике регистрируемых документов.
      if (document.DocumentKind.NumberingType == Docflow.DocumentKind.NumberingType.Registrable || documentInfo.RegistrationFailed)
        this.FillDocumentNumberAndDate(document, documentInfo, factName, withoutNumberLabel);
    }
    
    /// <summary>
    /// Пронумеровать документ.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="factName">Наименование факта с датой и номером документа.</param>
    /// <param name="withoutNumberLabel">Замещающий текст для номера, если он не распознан или отсутствует. По умолчанию "???".</param>
    [Public]
    public virtual void NumberDocument(Docflow.IOfficialDocument document,
                                       IDocumentInfo documentInfo,
                                       string factName,
                                       string withoutNumberLabel)
    {
      // Присвоить номер, если вид документа - нумеруемый.
      if (document.DocumentKind == null || document.DocumentKind.NumberingType != Docflow.DocumentKind.NumberingType.Numerable)
        return;
      
      var arioDocument = documentInfo.ArioDocument;
      
      // Проверить конфигурацию DirectumRX на возможность нумерации документа.
      // Можем нумеровать только тогда, когда однозначно подобран журнал.
      var registersIds = Docflow.PublicFunctions.OfficialDocument.GetDocumentRegistersIdsByDocument(document, Docflow.RegistrationSetting.SettingType.Numeration);
      
      // Если не смогли пронумеровать, то передать параметр с результатом в задачу на обработку документа.
      if (registersIds.Count != 1)
      {
        documentInfo.RegistrationFailed = true;
        return;
      }
      var register = DocumentRegisters.Get(registersIds.First());

      var props = document.Info.Properties;
      
      // Дата.
      var recognizedDate = this.GetRecognizedDate(arioDocument.Facts, factName, ArioGrammars.DocumentFact.DateField);
      // Если дата не распозналась или меньше минимальной, то подставить минимальную дату с минимальной вероятностью.
      if (!recognizedDate.Date.HasValue || recognizedDate.Date < Calendar.SqlMinValue)
      {
        recognizedDate.Date = Calendar.SqlMinValue;
        recognizedDate.Probability = Module.PropertyProbabilityLevels.Min;
      }
      
      // Номер.
      var recognizedNumber = this.GetRecognizedNumber(arioDocument.Facts, factName, ArioGrammars.DocumentFact.NumberField,
                                                      props.RegistrationNumber);
      // Если номер не распознался, то подставить заданное значение с минимальной вероятностью.
      if (string.IsNullOrWhiteSpace(recognizedNumber.Number))
      {
        recognizedNumber.Number = string.IsNullOrEmpty(withoutNumberLabel) ? Docflow.Resources.UnknownNumber : withoutNumberLabel;
        recognizedNumber.Probability = Module.PropertyProbabilityLevels.Min;
      }
      
      // Не нумеровать, если номер не уникален.
      if (recognizedDate.Date.HasValue)
      {
        var depCode = document.Department != null ? document.Department.Code : string.Empty;
        var bunitCode = document.BusinessUnit != null ? document.BusinessUnit.Code : string.Empty;
        var caseIndex = document.CaseFile != null ? document.CaseFile.Index : string.Empty;
        var kindCode = document.DocumentKind != null ? document.DocumentKind.Code : string.Empty;
        var counterpartyCode = Docflow.PublicFunctions.OfficialDocument.GetCounterpartyCode(document);
        var leadingDocumentId = document.LeadingDocument != null ? document.LeadingDocument.Id : 0;
        if (!Docflow.PublicFunctions.DocumentRegister.Remote.IsRegistrationNumberUnique(register, document,
                                                                                        recognizedNumber.Number, 0, recognizedDate.Date.Value,
                                                                                        depCode, bunitCode,
                                                                                        caseIndex, kindCode,
                                                                                        counterpartyCode, leadingDocumentId))
        {
          documentInfo.RegistrationFailed = true;
          return;
        }
      }
      
      // Не сохранять документ при нумерации, чтобы не потерять параметр DocumentNumberingBySmartCaptureResult.
      Docflow.PublicFunctions.OfficialDocument.RegisterDocument(document, register, recognizedDate.Date, recognizedNumber.Number, false, false);
      
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                        recognizedDate.Fact,
                                                                        ArioGrammars.DocumentFact.DateField,
                                                                        props.RegistrationDate.Name,
                                                                        document.RegistrationDate,
                                                                        recognizedDate.Probability);
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                        recognizedNumber.Fact,
                                                                        ArioGrammars.DocumentFact.NumberField,
                                                                        props.RegistrationNumber.Name,
                                                                        document.RegistrationNumber,
                                                                        recognizedNumber.Probability);
    }
    
    /// <summary>
    /// Заполнить дату и номер документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="factName">Наименование факта с датой и номером документа.</param>
    /// <param name="withoutNumberLabel">Замещающий текст для номера, если он не распознан или отсутствует. По умолчанию "б/н".</param>
    [Public]
    public virtual void FillDocumentNumberAndDate(IOfficialDocument document,
                                                  IDocumentInfo documentInfo,
                                                  string factName,
                                                  string withoutNumberLabel)
    {
      var arioDocument = documentInfo.ArioDocument;
      var props = document.Info.Properties;

      // Дата.
      var recognizedDate = this.GetRecognizedDate(arioDocument.Facts, factName,
                                                  ArioGrammars.DocumentFact.DateField);

      if (recognizedDate.Fact != null)
      {
        document.RegistrationDate = recognizedDate.Date;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          recognizedDate.Fact,
                                                                          ArioGrammars.DocumentFact.DateField,
                                                                          props.RegistrationDate.Name,
                                                                          recognizedDate.Date,
                                                                          recognizedDate.Probability);
      }

      // Номер.
      var regNumberPropertyInfo = props.RegistrationNumber;
      var recognizedNumber = this.GetRecognizedNumber(arioDocument.Facts, factName,
                                                      ArioGrammars.DocumentFact.NumberField,
                                                      regNumberPropertyInfo);
      
      // Если номер не распознан или пришло пустое значение, то вероятность минимальная.
      if (string.IsNullOrWhiteSpace(recognizedNumber.Number) || recognizedNumber.Number == string.Empty)
      {
        recognizedNumber.Number = string.Empty;
        recognizedNumber.Probability = Module.PropertyProbabilityLevels.Min;
      }
      
      // Список обозначений "без номера" аналогичен синхронизации с 1С.
      var emptyNumberSymbols = new List<string>
      {
        Resources.WithoutNumberWithSlash,
        Resources.WithoutNumber,
        Resources.WithoutNumberWithDash,
        string.Empty
      };

      // Если номер не распознан или документ пришел без номера, то подставить заданное значение.
      if (emptyNumberSymbols.Contains(recognizedNumber.Number.ToLower()))
        recognizedNumber.Number = string.IsNullOrEmpty(withoutNumberLabel) ? Docflow.Resources.DocumentWithoutNumber : withoutNumberLabel;

      document.RegistrationNumber = recognizedNumber.Number;
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                        recognizedNumber.Fact,
                                                                        ArioGrammars.DocumentFact.NumberField,
                                                                        props.RegistrationNumber.Name,
                                                                        document.RegistrationNumber,
                                                                        recognizedNumber.Probability);
    }
    
    #endregion
    
    #region Делопроизводство
    
    /// <summary>
    /// Заполнить свойства входящего письма по результатам обработки Ario.
    /// </summary>
    /// <param name="letter">Входящее письмо.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    [Public]
    public virtual void FillIncomingLetterProperties(RecordManagement.IIncomingLetter letter,
                                                     IDocumentInfo documentInfo,
                                                     Sungero.Company.IEmployee responsible)
    {
      var props = letter.Info.Properties;
      var arioDocument = documentInfo.ArioDocument;

      // Вид документа.
      this.FillDocumentKind(letter);
      
      // Содержание.
      this.FillIncomingLetterSubject(letter, arioDocument);
      
      // Дата.
      var recognizedDate = this.GetRecognizedDate(arioDocument.Facts, ArioGrammars.LetterFact.Name, ArioGrammars.LetterFact.DateField);
      if (recognizedDate.Fact != null)
      {
        letter.Dated = recognizedDate.Date;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          recognizedDate.Fact,
                                                                          ArioGrammars.LetterFact.DateField,
                                                                          props.Dated.Name,
                                                                          recognizedDate.Date,
                                                                          recognizedDate.Probability);
      }
      
      // Номер.
      var numberPropertyInfo = props.InNumber;
      var recognizedNumber = this.GetRecognizedNumber(arioDocument.Facts,
                                                      ArioGrammars.LetterFact.Name,
                                                      ArioGrammars.LetterFact.NumberField,
                                                      numberPropertyInfo);
      if (recognizedNumber.Fact != null)
      {
        letter.InNumber = recognizedNumber.Number;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          recognizedNumber.Fact,
                                                                          ArioGrammars.LetterFact.NumberField,
                                                                          props.InNumber.Name,
                                                                          letter.InNumber,
                                                                          recognizedNumber.Probability);
      }
      
      // Заполнить данные корреспондента.
      var correspondent = this.GetRecognizedCounterparty(arioDocument.Facts,
                                                         props.Correspondent.Name,
                                                         ArioGrammars.LetterFact.Name,
                                                         ArioGrammars.LetterFact.CorrespondentNameField,
                                                         ArioGrammars.LetterFact.CorrespondentLegalFormField);

      if (correspondent != null)
      {
        letter.Correspondent = correspondent.Counterparty;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          correspondent.Fact,
                                                                          null,
                                                                          props.Correspondent.Name,
                                                                          letter.Correspondent,
                                                                          correspondent.CounterpartyProbability);
      }
      
      // Заполнить данные нашей стороны.
      // Убираем уже использованный факт для подбора контрагента,
      // чтобы организация и адресат не искались по тем же реквизитам, что и контрагент.
      if (correspondent != null)
        arioDocument.Facts.Remove(correspondent.Fact);
      
      this.FillIncomingLetterToProperties(letter, documentInfo, responsible);
      
      var personFacts = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                       ArioGrammars.LetterPersonFact.Name,
                                                                       ArioGrammars.LetterPersonFact.NameField);
      // Заполнить подписанта.
      this.FillIncomingLetterSignedBy(letter, documentInfo, personFacts);
      
      // Заполнить контакт.
      this.FillIncomingLetterContact(letter, documentInfo, personFacts);
    }
    
    /// <summary>
    /// Заполнить контакт входящего письма.
    /// </summary>
    /// <param name="document">Входящее письмо.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="facts">Факты, содержащие информацию о персонах.</param>
    [Public]
    public virtual void FillIncomingLetterContact(IIncomingLetter document,
                                                  IDocumentInfo documentInfo,
                                                  List<IArioFact> facts)
    {
      var recognitionInfo = documentInfo.ArioDocument.RecognitionInfo;
      var props = document.Info.Properties;
      var responsibleFact = facts
        .Where(x => Commons.PublicFunctions.Module.GetFieldValue(x, ArioGrammars.LetterPersonFact.TypeField) == ArioGrammars.LetterPersonFact.PersonTypes.Responsible)
        .FirstOrDefault();

      var recognizedResponsibleNaming = this.GetRecognizedPersonNaming(responsibleFact,
                                                                       ArioGrammars.LetterPersonFact.SurnameField,
                                                                       ArioGrammars.LetterPersonFact.NameField,
                                                                       ArioGrammars.LetterPersonFact.PatrnField);
      
      var contact = this.GetRecognizedContact(responsibleFact, props.Contact.Name, document.Correspondent,
                                              props.Correspondent.Name, recognizedResponsibleNaming);
      
      // При заполнении полей подписал и контакт, если контрагент не заполнен, он подставляется из подписанта/контакта.
      if (document.Correspondent == null && contact.Contact != null)
      {
        // Если вероятность определения подписанта больше уровня "выше среднего", то установить вероятность определения КА "выше среднего",
        // иначе установить минимальную вероятность.
        var recognizedContactProbability = contact.Probability >= Module.PropertyProbabilityLevels.UpperMiddle ?
          Module.PropertyProbabilityLevels.UpperMiddle :
          Module.PropertyProbabilityLevels.Min;
        
        // Если запись контрагента - закрытая, то установить минимальную вероятность. bug 104160
        if (contact.Contact.Company.Status == Sungero.CoreEntities.DatabookEntry.Status.Closed)
          recognizedContactProbability = Module.PropertyProbabilityLevels.Min;
        
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(recognitionInfo, null,
                                                                          null, props.Correspondent.Name,
                                                                          contact.Contact.Company,
                                                                          recognizedContactProbability);
      }
      
      document.Contact = contact.Contact;
      
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(recognitionInfo, responsibleFact,
                                                                        null, props.Contact.Name,
                                                                        document.Contact,
                                                                        contact.Probability);
    }
    
    /// <summary>
    /// Заполнить подписанта входящего письма.
    /// </summary>
    /// <param name="document">Входящее письмо.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="facts">Факты, содержащие информацию о персонах.</param>
    [Public]
    public virtual void FillIncomingLetterSignedBy(IIncomingLetter document,
                                                   IDocumentInfo documentInfo,
                                                   List<IArioFact> facts)
    {
      var recognitionInfo = documentInfo.ArioDocument.RecognitionInfo;
      var props = document.Info.Properties;
      var signatoryFact = facts
        .Where(x => Commons.PublicFunctions.Module.GetFieldValue(x, ArioGrammars.LetterPersonFact.TypeField) == ArioGrammars.LetterPersonFact.PersonTypes.Signatory)
        .FirstOrDefault();
      
      var recognizedSignatoryNaming = this.GetRecognizedPersonNaming(signatoryFact,
                                                                     ArioGrammars.LetterPersonFact.SurnameField,
                                                                     ArioGrammars.LetterPersonFact.NameField,
                                                                     ArioGrammars.LetterPersonFact.PatrnField);
      
      var signedBy = this.GetRecognizedContact(signatoryFact, props.SignedBy.Name, document.Correspondent,
                                               props.Correspondent.Name, recognizedSignatoryNaming);
      
      // При заполнении полей подписал и контакт, если контрагент не заполнен, он подставляется из подписанта/контакта.
      if (document.Correspondent == null && signedBy.Contact != null)
      {
        // Если вероятность определения подписанта больше уровня "выше среднего", то установить вероятность определения КА "выше среднего",
        // иначе установить минимальную вероятность.
        var recognizedCorrespondentProbability = signedBy.Probability >= Module.PropertyProbabilityLevels.UpperMiddle ?
          Module.PropertyProbabilityLevels.UpperMiddle :
          Module.PropertyProbabilityLevels.Min;
        
        // Если запись контрагента - закрытая, то установить минимальную вероятность. bug 104160
        if (signedBy.Contact.Company.Status == Sungero.CoreEntities.DatabookEntry.Status.Closed)
          recognizedCorrespondentProbability = Module.PropertyProbabilityLevels.Min;
        
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(recognitionInfo, null, null,
                                                                          props.Correspondent.Name,
                                                                          signedBy.Contact.Company,
                                                                          recognizedCorrespondentProbability);
      }
      
      document.SignedBy = signedBy.Contact;

      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(recognitionInfo, signatoryFact,
                                                                        null, props.SignedBy.Name,
                                                                        document.SignedBy,
                                                                        signedBy.Probability);
    }
    
    /// <summary>
    /// Заполнить данные нашей стороны (НОР, подразделение, адресата).
    /// </summary>
    /// <param name="document">Входящее письмо.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    [Public]
    public virtual void FillIncomingLetterToProperties(IIncomingLetter document,
                                                       IDocumentInfo documentInfo,
                                                       IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      var recognitionInfo = arioDocument.RecognitionInfo;
      var props = document.Info.Properties;
      
      // Заполнить адресата.
      this.FillIncomingLetterAddressee(document, documentInfo);
      
      // Заполнить НОР.
      var recognizedBusinessUnit = RecognizedCounterparty.Create();
      var recognizedBusinessUnits = this.GetRecognizedBusinessUnits(arioDocument.Facts,
                                                                    ArioGrammars.LetterFact.Name,
                                                                    ArioGrammars.LetterFact.CorrespondentNameField,
                                                                    ArioGrammars.LetterFact.CorrespondentLegalFormField);
      
      // Если для свойства businessUnitPropertyName по факту существует верифицированное ранее значение, то вернуть его.
      foreach (var fact in recognizedBusinessUnits.Select(x => x.Fact))
      {
        var previousRecognizedBusinessUnit = this.GetPreviousBusinessUnitRecognitionResults(fact, props.BusinessUnit.Name);
        if (previousRecognizedBusinessUnit != null && previousRecognizedBusinessUnit.BusinessUnit != null)
        {
          recognizedBusinessUnit = previousRecognizedBusinessUnit;
          break;
        }
      }
      
      if (recognizedBusinessUnit.BusinessUnit == null)
      {
        // Если по фактам нашлась одна НОР, то ее и подставляем в документ.
        if (recognizedBusinessUnits.Count == 1)
          recognizedBusinessUnit = recognizedBusinessUnits.FirstOrDefault();
        
        // Если по фактам нашлось несколько НОР, то берем наиболее вероятную.
        if (recognizedBusinessUnits.Count > 1)
          recognizedBusinessUnit = recognizedBusinessUnits.OrderByDescending(x => x.BusinessUnitProbability).FirstOrDefault();
      }
      
      // Если не удалось найти НОР по фактам, то попытаться определить НОР от адресата.
      if (recognizedBusinessUnit.BusinessUnit == null)
      {
        // Получить НОР адресата.
        var businessUnitByAddressee = Company.PublicFunctions.BusinessUnit.Remote.GetBusinessUnit(document.Addressee);
        // Вероятность распознавания адресата.
        var addresseeProbability = recognitionInfo.Facts.Where(x => x.PropertyName == props.Addressee.Name)
          .Select(x => x.Probability)
          .FirstOrDefault();
        if (businessUnitByAddressee != null)
        {
          recognizedBusinessUnit.BusinessUnit = businessUnitByAddressee;
          recognizedBusinessUnit.Fact = null;
          // Если вероятность определения подписанта больше уровня "выше среднего", то установить вероятность определения НОР "выше среднего",
          // иначе установить минимальную вероятность.
          recognizedBusinessUnit.BusinessUnitProbability = addresseeProbability >= Module.PropertyProbabilityLevels.UpperMiddle ?
            Module.PropertyProbabilityLevels.UpperMiddle :
            Module.PropertyProbabilityLevels.Min;
        }
      }
      
      // Если и по адресату НОР не найдена, то вернуть НОР из персональных настроек или карточки ответственного.
      if (recognizedBusinessUnit.BusinessUnit == null)
      {
        recognizedBusinessUnit.BusinessUnit = Docflow.PublicFunctions.Module.GetDefaultBusinessUnit(responsible);
        recognizedBusinessUnit.Fact = null;
        recognizedBusinessUnit.BusinessUnitProbability = Module.PropertyProbabilityLevels.Min;
      }
      
      document.BusinessUnit = recognizedBusinessUnit.BusinessUnit;
      
      // Если запись НОР - закрытая, то установить минимальную вероятность. bug 104160
      if (recognizedBusinessUnit.BusinessUnit != null && recognizedBusinessUnit.BusinessUnit.Status == Sungero.CoreEntities.DatabookEntry.Status.Closed)
        recognizedBusinessUnit.BusinessUnitProbability = Module.PropertyProbabilityLevels.Min;
      
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(recognitionInfo, recognizedBusinessUnit.Fact,
                                                                        null, props.BusinessUnit.Name,
                                                                        document.BusinessUnit, recognizedBusinessUnit.BusinessUnitProbability);
      // Заполнить подразделение.
      document.Department = document.Addressee != null
        ? Company.PublicFunctions.Department.GetDepartment(document.Addressee)
        : Company.PublicFunctions.Department.GetDepartment(responsible);
    }
    
    /// <summary>
    /// Заполнить адресата входящего письма.
    /// </summary>
    /// <param name="document">Входящее письмо.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    public virtual void FillIncomingLetterAddressee(IIncomingLetter document,
                                                    IDocumentInfo documentInfo)
    {
      var addresseeFact = Commons.PublicFunctions.Module.GetOrderedFacts(documentInfo.ArioDocument.Facts,
                                                                         ArioGrammars.LetterFact.Name,
                                                                         ArioGrammars.LetterFact.AddresseeField)
        .FirstOrDefault();
      
      var addresseeName = Commons.PublicFunctions.Module.GetFieldValue(addresseeFact,
                                                                       ArioGrammars.LetterFact.AddresseeField);
      if (string.IsNullOrEmpty(addresseeName))
        return;
      
      var employees = this.GetAddresseesByName(addresseeName);
      if (!employees.Any())
        return;
      
      var fieldProbability = Commons.PublicFunctions.Module.GetFieldProbability(addresseeFact, ArioGrammars.LetterFact.AddresseeField);
      var probability = fieldProbability / employees.Count();
      
      document.Addressee = employees.FirstOrDefault();
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(documentInfo.ArioDocument.RecognitionInfo,
                                                                        addresseeFact,
                                                                        ArioGrammars.LetterFact.AddresseeField,
                                                                        document.Info.Properties.Addressee.Name,
                                                                        document.Addressee,
                                                                        probability);
    }

    /// <summary>
    /// Заполнить содержание входящего письма.
    /// </summary>
    /// <param name="letter">Входящее письмо.</param>
    /// <param name="arioDocument">Информация о документе Ario.</param>
    [Public]
    public virtual void FillIncomingLetterSubject(IIncomingLetter letter, IArioDocument arioDocument)
    {
      var subjectFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                       ArioGrammars.LetterFact.Name,
                                                                       ArioGrammars.LetterFact.SubjectField)
        .FirstOrDefault();
      
      var subject = Commons.PublicFunctions.Module.GetFieldValue(subjectFact, ArioGrammars.LetterFact.SubjectField);
      if (!string.IsNullOrEmpty(subject))
      {
        letter.Subject = subject;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          subjectFact,
                                                                          ArioGrammars.LetterFact.SubjectField,
                                                                          letter.Info.Properties.Subject.Name,
                                                                          letter.Subject);
      }
    }
    #endregion
    
    #region Договорные документы и счет на оплату

    /// <summary>
    /// Заполнить свойства договора по результатам обработки Ario.
    /// </summary>
    /// <param name="contract">Договор.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    [Public]
    public virtual void FillContractProperties(Contracts.IContract contract,
                                               IDocumentInfo documentInfo,
                                               Sungero.Company.IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(contract);
      
      this.FillContractualDocumentAmountAndCurrency(contract, documentInfo);
      
      // Заполнить данные нашей стороны и корреспондента.
      this.FillContractualDocumentParties(contract, documentInfo, responsible);
      
      // Заполнить ответственного после заполнения НОР и КА, чтобы вычислялась НОР из фактов, а не по отв.
      // Если так не сделать, то НОР заполнится по ответственному и вычисления не будут выполняться.
      contract.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
      contract.ResponsibleEmployee = responsible;
      
      // Дата и номер.
      this.FillDocumentRegistrationData(contract, documentInfo, ArioGrammars.DocumentFact.Name, Docflow.Resources.DocumentWithoutNumber);
    }
    
    /// <summary>
    /// Заполнить свойства доп. соглашения по результатам обработки Ario.
    /// </summary>
    /// <param name="supAgreement">Доп. соглашение.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    [Public]
    public virtual void FillSupAgreementProperties(Contracts.ISupAgreement supAgreement,
                                                   IDocumentInfo documentInfo,
                                                   Sungero.Company.IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(supAgreement);

      this.FillContractualDocumentAmountAndCurrency(supAgreement, documentInfo);
      
      // Заполнить данные нашей стороны и корреспондента.
      this.FillContractualDocumentParties(supAgreement, documentInfo, responsible);
      
      // Заполнить ответственного после заполнения НОР и КА, чтобы вычислялась НОР из фактов, а не по отв.
      // Если так не сделать, то НОР заполнится по ответственному и вычисления не будут выполняться.
      supAgreement.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
      supAgreement.ResponsibleEmployee = responsible;
      
      // Дата и номер.
      this.FillDocumentRegistrationData(supAgreement, documentInfo, ArioGrammars.SupAgreementFact.Name, Docflow.Resources.DocumentWithoutNumber);
    }
    
    /// <summary>
    /// Заполнить сумму и валюту в договорных документах.
    /// </summary>
    /// <param name="contractualDocument">Договорной документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillContractualDocumentAmountAndCurrency(Docflow.IContractualDocumentBase contractualDocument,
                                                                 IDocumentInfo documentInfo)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      var recognizedAmount = this.GetRecognizedAmount(arioDocument.Facts);
      if (recognizedAmount.HasValue)
      {
        contractualDocument.TotalAmount = recognizedAmount.Amount;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          recognizedAmount.Fact,
                                                                          ArioGrammars.DocumentAmountFact.AmountField,
                                                                          contractualDocument.Info.Properties.TotalAmount.Name,
                                                                          recognizedAmount.Amount,
                                                                          recognizedAmount.Probability);
      }

      // В факте с суммой документа может быть не указана валюта, поэтому факт с валютой ищем отдельно,
      // так как на данный момент функция используется для обработки бухгалтерских и договорных документов,
      // а в них все расчеты ведутся в одной валюте.
      var recognizedCurrency = this.GetRecognizedCurrency(arioDocument.Facts);
      
      if (recognizedAmount.HasValue && !recognizedCurrency.HasValue)
      {
        recognizedCurrency.HasValue = true;
        recognizedCurrency.Currency = Commons.Currencies.GetAllCached(c => c.IsDefault == true).FirstOrDefault();
        recognizedCurrency.Probability = Module.PropertyProbabilityLevels.Min;
      }
      
      if (recognizedCurrency.HasValue)
      {
        contractualDocument.Currency = recognizedCurrency.Currency;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          recognizedCurrency.Fact,
                                                                          ArioGrammars.DocumentAmountFact.CurrencyField,
                                                                          contractualDocument.Info.Properties.Currency.Name,
                                                                          recognizedCurrency.Currency,
                                                                          recognizedCurrency.Probability);
      }
    }

    /// <summary>
    /// Заполнить стороны договорного документа.
    /// </summary>
    /// <param name="contractualDocument">Договорной документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    [Public]
    public virtual void FillContractualDocumentParties(Contracts.IContractualDocument contractualDocument,
                                                       IDocumentInfo documentInfo,
                                                       IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      var props = contractualDocument.Info.Properties;
      var businessUnitPropertyName = props.BusinessUnit.Name;
      var counterpartyPropertyName = props.Counterparty.Name;
      
      var signatoryFieldNames = new List<string>
      {
        ArioGrammars.CounterpartyFact.SignatorySurnameField,
        ArioGrammars.CounterpartyFact.SignatoryNameField,
        ArioGrammars.CounterpartyFact.SignatoryPatrnField
      };
      
      var counterpartyFieldNames = new List<string>
      {
        ArioGrammars.CounterpartyFact.NameField,
        ArioGrammars.CounterpartyFact.LegalFormField,
        ArioGrammars.CounterpartyFact.CounterpartyTypeField,
        ArioGrammars.CounterpartyFact.TinField,
        ArioGrammars.CounterpartyFact.TinIsValidField,
        ArioGrammars.CounterpartyFact.TrrcField
      };
      
      // Заполнить данные нашей стороны.
      // Наша организация по фактам из Ario.
      var recognizedBusinessUnit = this.GetRecognizedBusinessUnitForContractualDocument(arioDocument.Facts, responsible);
      if (recognizedBusinessUnit.BusinessUnit != null)
      {
        contractualDocument.BusinessUnit = recognizedBusinessUnit.BusinessUnit;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                                recognizedBusinessUnit.Fact,
                                                                                counterpartyFieldNames,
                                                                                businessUnitPropertyName,
                                                                                contractualDocument.BusinessUnit,
                                                                                recognizedBusinessUnit.BusinessUnitProbability,
                                                                                null);
      }
      
      // Заполнить подписанта.
      var ourSignatory = this.GetSignatoryForContractualDocument(contractualDocument,
                                                                 arioDocument.Facts,
                                                                 recognizedBusinessUnit,
                                                                 signatoryFieldNames,
                                                                 true);
      
      // При заполнении поля подписал, если НОР не заполнена, она подставляется из подписанта.
      if (ourSignatory.Employee != null)
        this.FillOurSignatoryForContractualDocument(contractualDocument, documentInfo, ourSignatory, signatoryFieldNames);
      
      // Если НОР по фактам не нашли, то взять ее из персональных настроек, или от ответственного.
      if (contractualDocument.BusinessUnit == null)
      {
        var responsibleEmployeeBusinessUnit = Company.PublicFunctions.BusinessUnit.Remote.GetBusinessUnit(responsible);
        var responsibleEmployeePersonalSettings = Docflow.PublicFunctions.PersonalSetting.GetPersonalSettings(responsible);
        var responsibleEmployeePersonalSettingsBusinessUnit = responsibleEmployeePersonalSettings != null
          ? responsibleEmployeePersonalSettings.BusinessUnit
          : Company.BusinessUnits.Null;

        // Если в персональных настройках ответственного указана НОР.
        if (responsibleEmployeePersonalSettingsBusinessUnit != null)
          contractualDocument.BusinessUnit = responsibleEmployeePersonalSettingsBusinessUnit;
        else
          contractualDocument.BusinessUnit = responsibleEmployeeBusinessUnit;
        
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(documentInfo.ArioDocument.RecognitionInfo,
                                                                                null,
                                                                                null,
                                                                                contractualDocument.Info.Properties.BusinessUnit.Name,
                                                                                contractualDocument.BusinessUnit,
                                                                                Module.PropertyProbabilityLevels.Min,
                                                                                null);
      }

      // Убрать использованные факты подбора НОР и подписывающего с нашей стороны.
      if (recognizedBusinessUnit.Fact != null)
        arioDocument.Facts.Remove(recognizedBusinessUnit.Fact);
      if (ourSignatory.Fact != null &&
          (recognizedBusinessUnit.Fact == null || ourSignatory.Fact.Id != recognizedBusinessUnit.Fact.Id))
        arioDocument.Facts.Remove(ourSignatory.Fact);

      // Заполнить данные контрагента.
      var counterparty = this.GetRecognizedCounterparty(arioDocument.Facts,
                                                        counterpartyPropertyName,
                                                        ArioGrammars.CounterpartyFact.Name,
                                                        ArioGrammars.CounterpartyFact.NameField,
                                                        ArioGrammars.CounterpartyFact.LegalFormField);
      
      if (counterparty != null)
      {
        contractualDocument.Counterparty = counterparty.Counterparty;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                                counterparty.Fact,
                                                                                counterpartyFieldNames,
                                                                                counterpartyPropertyName,
                                                                                contractualDocument.Counterparty,
                                                                                counterparty.CounterpartyProbability,
                                                                                null);
      }
      
      // Заполнить подписанта от КА.
      var signedBy = this.GetSignatoryForContractualDocument(contractualDocument,
                                                             arioDocument.Facts,
                                                             counterparty,
                                                             signatoryFieldNames,
                                                             false);
      
      // При заполнении поля подписал, если контрагент не заполнен, он подставляется из подписанта.
      if (signedBy.Contact != null)
        this.FillCounterpartySignatoryForContractualDocument(contractualDocument, documentInfo, signedBy, signatoryFieldNames);
      
    }

    /// <summary>
    /// Заполнить подписанта НОР.
    /// </summary>
    /// <param name="contractualDocument">Договорной документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="ourSignatory">Информация о подписанте.</param>
    /// <param name="signatoryFieldNames">Список полей факта о подписанте.</param>
    public virtual void FillOurSignatoryForContractualDocument(Contracts.IContractualDocument contractualDocument,
                                                               IDocumentInfo documentInfo,
                                                               IRecognizedOfficial ourSignatory,
                                                               List<string> signatoryFieldNames)
    {
      if (contractualDocument.BusinessUnit == null &&
          ourSignatory.Employee.Department != null &&
          ourSignatory.Employee.Department.BusinessUnit != null)
      {
        // Если вероятность определения подписанта больше уровня "выше среднего", то установить вероятность определения НОР "выше среднего",
        // иначе установить минимальную вероятность.
        var recognizedBusinessUnitProbability = ourSignatory.Probability >= Module.PropertyProbabilityLevels.UpperMiddle ?
          Module.PropertyProbabilityLevels.UpperMiddle :
          Module.PropertyProbabilityLevels.Min;
        
        // Если запись НОР - закрытая, то установить минимальную вероятность. bug 104069
        if (ourSignatory.Employee.Department.BusinessUnit.Status == Sungero.CoreEntities.DatabookEntry.Status.Closed)
          recognizedBusinessUnitProbability = Module.PropertyProbabilityLevels.Min;
        contractualDocument.BusinessUnit = ourSignatory.Employee.Department.BusinessUnit;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(documentInfo.ArioDocument.RecognitionInfo,
                                                                          null,
                                                                          null,
                                                                          contractualDocument.Info.Properties.BusinessUnit.Name,
                                                                          ourSignatory.Employee.Department.BusinessUnit,
                                                                          recognizedBusinessUnitProbability);
      }
      
      contractualDocument.OurSignatory = ourSignatory.Employee;
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(documentInfo.ArioDocument.RecognitionInfo,
                                                                              ourSignatory.Fact,
                                                                              signatoryFieldNames,
                                                                              contractualDocument.Info.Properties.OurSignatory.Name,
                                                                              contractualDocument.OurSignatory,
                                                                              ourSignatory.Probability,
                                                                              null);
    }
    
    /// <summary>
    /// Заполнить подписанта контрагента.
    /// </summary>
    /// <param name="contractualDocument">Договорной документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="signedBy">Информация о подписанте.</param>
    /// <param name="signatoryFieldNames">Список полей факта о подписанте.</param>
    public virtual void FillCounterpartySignatoryForContractualDocument(Contracts.IContractualDocument contractualDocument,
                                                                        IDocumentInfo documentInfo,
                                                                        IRecognizedOfficial signedBy,
                                                                        List<string> signatoryFieldNames)
    {
      var arioDocument = documentInfo.ArioDocument;
      var props = contractualDocument.Info.Properties;
      // Если контрагент не заполнен, взять его из подписанта.
      if (contractualDocument.Counterparty == null && signedBy.Contact.Company != null)
      {
        // Если вероятность определения подписанта больше уровня "выше среднего", то установить вероятность определения КА "выше среднего",
        // иначе установить минимальную вероятность.
        var recognizedCounterpartyProbability = signedBy.Probability >= Module.PropertyProbabilityLevels.UpperMiddle ?
          Module.PropertyProbabilityLevels.UpperMiddle :
          Module.PropertyProbabilityLevels.Min;
        
        // Если запись контрагента - закрытая, то установить минимальную вероятность. bug 104069
        if (signedBy.Contact.Company.Status == Sungero.CoreEntities.DatabookEntry.Status.Closed)
          recognizedCounterpartyProbability = Module.PropertyProbabilityLevels.Min;
        
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                                null,
                                                                                null,
                                                                                props.Counterparty.Name,
                                                                                signedBy.Contact.Company,
                                                                                recognizedCounterpartyProbability,
                                                                                null);
      }
      
      contractualDocument.CounterpartySignatory = signedBy.Contact;
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                              signedBy.Fact,
                                                                              signatoryFieldNames,
                                                                              props.CounterpartySignatory.Name,
                                                                              contractualDocument.CounterpartySignatory,
                                                                              signedBy.Probability,
                                                                              null);
    }

    /// <summary>
    /// Получить подписанта нашей стороны/подписанта контрагента для договорного документа по фактам и НОР.
    /// </summary>
    /// <param name="document">Договорной документ.</param>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <param name="recognizedOrganization">Структура с НОР, КА, фактом и признаком доверия.</param>
    /// <param name="signatoryFieldNames">Список наименований полей с ФИО подписанта.</param>
    /// <param name="isOurSignatory">Признак поиска нашего подписанта (true) или подписанта КА (false).</param>
    /// <returns>Структура, содержащая сотрудника или контакт, факт и вероятность.</returns>
    [Public]
    public virtual IRecognizedOfficial GetSignatoryForContractualDocument(Contracts.IContractualDocument document,
                                                                          List<IArioFact> facts,
                                                                          IRecognizedCounterparty recognizedOrganization,
                                                                          List<string> signatoryFieldNames,
                                                                          bool isOurSignatory = false)
    {
      var props = document.Info.Properties;
      var signatoryFacts = Commons.PublicFunctions.Module.GetFacts(facts, ArioGrammars.CounterpartyFact.Name);
      var signedBy = RecognizedOfficial.Create(null, null, null, Module.PropertyProbabilityLevels.Min);
      
      if (!signatoryFacts.Any())
        return signedBy;
      
      if (recognizedOrganization != null)
      {
        var organizationFact = recognizedOrganization.Fact;
        if (organizationFact != null)
        {
          signedBy.Fact = organizationFact;
          var isOrganizationFactWithSignatory = Commons.PublicFunctions.Module.GetFields(organizationFact, signatoryFieldNames).Any();
          
          var recognizedOrganizationNaming = this.GetRecognizedPersonNaming(organizationFact,
                                                                            ArioGrammars.CounterpartyFact.SignatorySurnameField,
                                                                            ArioGrammars.CounterpartyFact.SignatoryNameField,
                                                                            ArioGrammars.CounterpartyFact.SignatoryPatrnField);
          if (isOrganizationFactWithSignatory)
            return isOurSignatory
              ? this.GetRecognizedOurSignatoryForContractualDocument(document, organizationFact, recognizedOrganizationNaming)
              : this.GetRecognizedContact(organizationFact, props.CounterpartySignatory.Name, document.Counterparty,
                                          props.Counterparty.Name, recognizedOrganizationNaming);
        }
        
        if (recognizedOrganization.BusinessUnit != null || recognizedOrganization.Counterparty != null)
          signatoryFacts = this.GetSpecifiedSignatoryByNameAndLegalForm(signatoryFacts, recognizedOrganization, isOurSignatory);
      }
      
      signatoryFacts = signatoryFacts
        .Where(f => Commons.PublicFunctions.Module.GetFields(f, signatoryFieldNames).Any()).ToList();
      
      var organizationSignatory = new List<IRecognizedOfficial>();
      foreach (var signatoryFact in signatoryFacts)
      {
        IRecognizedOfficial signatory = null;
        
        var recognizedSignatoryNaming = this.GetRecognizedPersonNaming(signatoryFact,
                                                                       ArioGrammars.CounterpartyFact.SignatorySurnameField,
                                                                       ArioGrammars.CounterpartyFact.SignatoryNameField,
                                                                       ArioGrammars.CounterpartyFact.SignatoryPatrnField);
        if (isOurSignatory)
        {
          signatory = this.GetRecognizedOurSignatoryForContractualDocument(document, signatoryFact, recognizedSignatoryNaming);
          if (signatory.Employee != null)
            organizationSignatory.Add(signatory);
        }
        else
        {
          signatory = this.GetRecognizedContact(signatoryFact, props.CounterpartySignatory.Name, document.Counterparty,
                                                props.Counterparty.Name, recognizedSignatoryNaming);
          if (signatory.Contact != null)
            organizationSignatory.Add(signatory);
        }
      }
      
      if (!organizationSignatory.Any())
        return signedBy;
      
      return organizationSignatory.OrderByDescending(x => x.Probability).FirstOrDefault();
    }

    /// <summary>
    /// Получить список фактов о подписанте, уточненный по наименованию организации и ОПФ.
    /// </summary>
    /// <param name="signatoryFacts">Список фактов.</param>
    /// <param name="recognizedOrganization">Структура с НОР, КА, фактом и признаком доверия.</param>
    /// <param name="isOurSignatory">Признак поиска нашего подписанта (true) или подписанта КА (false).</param>
    /// <returns>Уточненный список фактов.</returns>
    public virtual List<IArioFact> GetSpecifiedSignatoryByNameAndLegalForm(List<IArioFact> signatoryFacts, IRecognizedCounterparty recognizedOrganization, bool isOurSignatory)
    {
      var organizationName = isOurSignatory ?
        recognizedOrganization.BusinessUnit.Name :
        recognizedOrganization.Counterparty.Name;
      
      // Ожидаемое наименование НОР или организации в формате {Название}, {ОПФ}.
      var organizationNameAndLegalForm = organizationName.Split(new string[] { ", " }, StringSplitOptions.None);
      
      // Уточнить по наименованию.
      var specifiedSignatoryFacts = signatoryFacts
        .Where(f => f.Fields.Any(fl => fl.Name == ArioGrammars.CounterpartyFact.NameField &&
                                 fl.Value.Equals(organizationNameAndLegalForm.FirstOrDefault(), StringComparison.InvariantCultureIgnoreCase)))
        .ToList();
      // Уточнить по ОПФ.
      if (organizationNameAndLegalForm.Length > 1)
      {
        specifiedSignatoryFacts = specifiedSignatoryFacts
          .Where(f => f.Fields.Any(fl => fl.Name == ArioGrammars.CounterpartyFact.LegalFormField &&
                                   fl.Value.Equals(organizationNameAndLegalForm.LastOrDefault(), StringComparison.InvariantCultureIgnoreCase)))
          .ToList();
      }
      return specifiedSignatoryFacts;
    }

    /// <summary>
    /// Получить подписанта нашей стороны для договорного документа по факту.
    /// </summary>
    /// <param name="document">Договорной документ.</param>
    /// <param name="ourSignatoryFact">Факт, содержащий сведения о подписанте нашей стороны.</param>
    /// <param name="recognizedOurSignatoryNaming">Полное и краткое ФИО подписанта нашей стороны.</param>
    /// <returns>Структура, содержащая сотрудника, факт и вероятность.</returns>
    [Public]
    public virtual IRecognizedOfficial GetRecognizedOurSignatoryForContractualDocument(Contracts.IContractualDocument document,
                                                                                       IArioFact ourSignatoryFact,
                                                                                       IRecognizedPersonNaming recognizedOurSignatoryNaming)
    {
      var signedBy = RecognizedOfficial.Create(null, null, ourSignatoryFact, Module.PropertyProbabilityLevels.Min);
      var businessUnit = document.BusinessUnit;

      if (ourSignatoryFact == null)
        return signedBy;

      // Если для свойства Подписал по факту существует верифицированное ранее значение, то вернуть его.
      signedBy = this.GetPreviousOurSignatoryRecognitionResults(ourSignatoryFact,
                                                                document.Info.Properties.OurSignatory.Name,
                                                                businessUnit,
                                                                document.Info.Properties.BusinessUnit.Name);
      if (signedBy.Employee != null)
        return signedBy;
      
      var fullName = recognizedOurSignatoryNaming.FullName;
      var shortName = recognizedOurSignatoryNaming.ShortName;
      var filteredEmployees = Company.PublicFunctions.Employee.Remote.GetEmployeesByName(fullName);
      
      if (businessUnit != null)
        filteredEmployees = filteredEmployees.Where(e => e.Department.BusinessUnit.Equals(businessUnit)).ToList();
      
      // Проверить у найденного сотрудника право подписи на документ.
      filteredEmployees = filteredEmployees.Where(x => Docflow.PublicFunctions.OfficialDocument.Remote.CanSignByEmployee(document, x)).ToList();
      
      if (!filteredEmployees.Any())
        return signedBy;
      
      signedBy.Employee = filteredEmployees.FirstOrDefault();
      
      // Если сотрудник подобран по полному имени персоны и полное имя не эквивалентно короткому,
      // то считаем, что сотрудник определен с максимальной вероятностью,
      // иначе с вероятностью ниже среднего.
      signedBy.Probability = string.Equals(signedBy.Employee.Name, fullName, StringComparison.InvariantCultureIgnoreCase) &&
        !string.Equals(fullName, shortName, StringComparison.InvariantCultureIgnoreCase) ?
        Module.PropertyProbabilityLevels.Max / filteredEmployees.Count() :
        Module.PropertyProbabilityLevels.LowerMiddle / filteredEmployees.Count();
      
      return signedBy;
    }
    
    /// <summary>
    /// Поиск НОР для договорных документов по фактам Ario.
    /// </summary>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    /// <returns>НОР и соответствующий ей факт.</returns>
    [Public]
    public virtual IRecognizedCounterparty GetRecognizedBusinessUnitForContractualDocument(List<IArioFact> facts,
                                                                                           IEmployee responsible)
    {
      var businessUnitPropertyName = Sungero.Contracts.ContractualDocuments.Info.Properties.BusinessUnit.Name;
      var recognizedBusinessUnits = this.GetRecognizedBusinessUnits(facts,
                                                                    ArioGrammars.CounterpartyFact.Name,
                                                                    ArioGrammars.CounterpartyFact.NameField,
                                                                    ArioGrammars.CounterpartyFact.LegalFormField);
      
      var businessUnit = RecognizedCounterparty.Create();
      
      // Если для свойства businessUnitPropertyName по факту существует верифицированное ранее значение, то вернуть его.
      foreach (var fact in recognizedBusinessUnits.Select(x => x.Fact))
      {
        businessUnit = this.GetPreviousBusinessUnitRecognitionResults(fact, businessUnitPropertyName);
        if (businessUnit != null && businessUnit.BusinessUnit != null)
          return businessUnit;
      }
      
      // Если найдено несколько НОР, попытаться уточнить по ответственному за верификацию
      if (recognizedBusinessUnits.Count > 1)
      {
        var responsibleEmployeeBusinessUnit = Company.PublicFunctions.BusinessUnit.Remote.GetBusinessUnit(responsible);
        var responsibleEmployeePersonalSettings = Docflow.PublicFunctions.PersonalSetting.GetPersonalSettings(responsible);
        var responsibleEmployeePersonalSettingsBusinessUnit = responsibleEmployeePersonalSettings != null
          ? responsibleEmployeePersonalSettings.BusinessUnit
          : Company.BusinessUnits.Null;
        
        // Если в персональных настройках ответственного указана НОР, то отфильтровать по ней НОР, подобранные по фактам.
        if (responsibleEmployeePersonalSettingsBusinessUnit != null &&
            recognizedBusinessUnits.Any(x => Equals(x.BusinessUnit, responsibleEmployeePersonalSettingsBusinessUnit)))
          recognizedBusinessUnits = recognizedBusinessUnits.Where(x => Equals(x.BusinessUnit, responsibleEmployeePersonalSettingsBusinessUnit)).ToList();
        
        // Из НОР, подобранных по фактам, отфильтровать несоответствующие НОР ответственного.
        if (responsibleEmployeeBusinessUnit != null &&
            recognizedBusinessUnits.Any(x => Equals(x.BusinessUnit, responsibleEmployeeBusinessUnit)))
          recognizedBusinessUnits = recognizedBusinessUnits.Where(x => Equals(x.BusinessUnit, responsibleEmployeeBusinessUnit)).ToList();
      }
      
      // Если по фактам НОР не найдена.
      if (recognizedBusinessUnits.Count() == 0)
        return businessUnit;
      
      // Вернуть НОР с наибольшей вероятностью.
      return recognizedBusinessUnits.OrderByDescending(x => x.BusinessUnitProbability)
        .FirstOrDefault();
    }
    
    /// <summary>
    /// Заполнить свойства во входящем счете на оплату.
    /// </summary>
    /// <param name="incomingInvoice">Входящий счет.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    [Public]
    public virtual void FillIncomingInvoiceProperties(Contracts.IIncomingInvoice incomingInvoice,
                                                      IDocumentInfo documentInfo,
                                                      IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(incomingInvoice);
      
      // Подразделение.
      incomingInvoice.Department = Company.PublicFunctions.Department.GetDepartment(responsible);

      // Сумма и валюта.
      this.FillAccountingDocumentAmountAndCurrency(incomingInvoice, documentInfo);
      
      // НОР и КА.
      var arioCounterpartyTypes = new List<string>();
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer);
      arioCounterpartyTypes.Add(string.Empty);
      
      var recognizedCounterparties = this.GetRecognizedAccountingDocumentCounterparties(arioDocument.Facts, arioCounterpartyTypes);
      var seller = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller).FirstOrDefault();
      var buyer = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer).FirstOrDefault();
      var nonType = recognizedCounterparties.Where(m => m.Type == string.Empty).ToList();
      
      var recognizedDocumentParties = this.GetDocumentParties(buyer, seller, nonType, responsible);
      
      this.FillAccountingDocumentParties(incomingInvoice, documentInfo, recognizedDocumentParties);
      
      // Договор.
      var contractFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                        ArioGrammars.FinancialDocumentFact.Name,
                                                                        ArioGrammars.FinancialDocumentFact.DocumentBaseNameField)
        .FirstOrDefault();
      
      var contract = this.GetLeadingContractualDocument(contractFact,
                                                        incomingInvoice.Info.Properties.Contract.Name,
                                                        incomingInvoice.Counterparty,
                                                        incomingInvoice.Info.Properties.Counterparty.Name);
      incomingInvoice.Contract = contract.Contract;
      
      var props = incomingInvoice.Info.Properties;
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                        contractFact,
                                                                        null,
                                                                        props.Contract.Name,
                                                                        incomingInvoice.Contract,
                                                                        contract.Probability);
      
      // Дата.
      var recognizedDate = this.GetRecognizedDate(arioDocument.Facts,
                                                  ArioGrammars.FinancialDocumentFact.Name,
                                                  ArioGrammars.FinancialDocumentFact.DateField);
      if (recognizedDate.Fact != null)
      {
        incomingInvoice.Date = recognizedDate.Date;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          recognizedDate.Fact,
                                                                          ArioGrammars.FinancialDocumentFact.DateField,
                                                                          props.Date.Name,
                                                                          recognizedDate.Date,
                                                                          recognizedDate.Probability);
      }
      
      // Номер.
      var numberPropertyInfo = props.Number;
      var recognizedNumber = this.GetRecognizedNumber(arioDocument.Facts,
                                                      ArioGrammars.FinancialDocumentFact.Name,
                                                      ArioGrammars.FinancialDocumentFact.NumberField,
                                                      numberPropertyInfo);
      if (recognizedNumber.Fact != null)
      {
        incomingInvoice.Number = recognizedNumber.Number;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          recognizedNumber.Fact,
                                                                          ArioGrammars.FinancialDocumentFact.NumberField,
                                                                          props.Number.Name,
                                                                          incomingInvoice.Number,
                                                                          recognizedNumber.Probability);
      }
    }
    
    #endregion
    
    #region Первичка

    /// <summary>
    /// Заполнить свойства в акте.
    /// </summary>
    /// <param name="contractStatement">Акт.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    [Public]
    public virtual void FillContractStatementProperties(FinancialArchive.IContractStatement contractStatement,
                                                        IDocumentInfo documentInfo,
                                                        IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(contractStatement);
      
      // Подразделение и ответственный.
      contractStatement.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
      contractStatement.ResponsibleEmployee = responsible;
      
      // Сумма и валюта.
      this.FillAccountingDocumentAmountAndCurrency(contractStatement, documentInfo);
      
      var props = contractStatement.Info.Properties;

      // НОР и КА.
      var arioCounterpartyTypes = new List<string>();
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer);
      arioCounterpartyTypes.Add(string.Empty);
      
      var recognizedCounterparties = this.GetRecognizedAccountingDocumentCounterparties(arioDocument.Facts, arioCounterpartyTypes);
      var seller = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller).FirstOrDefault();
      var buyer = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer).FirstOrDefault();
      var nonType = recognizedCounterparties.Where(m => m.Type == string.Empty).ToList();
      
      var recognizedDocumentParties = this.GetDocumentParties(buyer, seller, nonType, responsible);
      
      this.FillAccountingDocumentParties(contractStatement, documentInfo, recognizedDocumentParties);
      
      // Дата, номер и регистрация.
      this.FillDocumentRegistrationData(contractStatement, documentInfo, ArioGrammars.DocumentFact.Name, Docflow.Resources.UnknownNumber);
      
      // Договор.
      var leadingDocFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                          ArioGrammars.FinancialDocumentFact.Name,
                                                                          ArioGrammars.FinancialDocumentFact.DocumentBaseNameField)
        .FirstOrDefault();
      
      var leadingDocument = this.GetLeadingContractualDocument(leadingDocFact,
                                                               props.LeadingDocument.Name,
                                                               contractStatement.Counterparty, props.Counterparty.Name);
      contractStatement.LeadingDocument = leadingDocument.Contract;
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                        leadingDocFact,
                                                                        null,
                                                                        props.LeadingDocument.Name,
                                                                        contractStatement.LeadingDocument,
                                                                        leadingDocument.Probability);
    }
    
    /// <summary>
    /// Заполнить свойства выставленного счёта-фактуры по результатам обработки Ario.
    /// </summary>
    /// <param name="outgoingTaxInvoice">Выставленный счёт-фактура.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    /// <param name="recognizedDocumentParties">Результат подбора сторон сделки для документа.</param>
    [Public]
    public virtual void FillOutgoingTaxInvoiceProperties(FinancialArchive.IOutgoingTaxInvoice outgoingTaxInvoice,
                                                         IDocumentInfo documentInfo,
                                                         Sungero.Company.IEmployee responsible,
                                                         IRecognizedDocumentParties recognizedDocumentParties)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(outgoingTaxInvoice);
      
      // Подразделение.
      outgoingTaxInvoice.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
      
      // Сумма и валюта.
      this.FillAccountingDocumentAmountAndCurrency(outgoingTaxInvoice, documentInfo);
      
      // НОР и КА.
      this.FillAccountingDocumentParties(outgoingTaxInvoice, documentInfo, recognizedDocumentParties);
      
      // Дата, номер и регистрация.
      this.FillDocumentRegistrationData(outgoingTaxInvoice, documentInfo, ArioGrammars.FinancialDocumentFact.Name, Docflow.Resources.UnknownNumber);
      
      // Корректировочный документ.
      if (outgoingTaxInvoice.IsAdjustment.HasValue && outgoingTaxInvoice.IsAdjustment.Value == true)
        this.FillOutgoingTaxInvoiceCorrectedDocument(outgoingTaxInvoice, documentInfo);
      else
        this.FillOutgoingTaxInvoiceRevisionInfo(outgoingTaxInvoice, documentInfo);
    }
    
    /// <summary>
    /// Заполнить свойства полученного счёта-фактуры по результатам обработки Ario.
    /// </summary>
    /// <param name="incomingTaxInvoice">Полученный счёт-фактура.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    /// <param name="recognizedDocumentParties">Результат подбора сторон сделки для документа.</param>
    [Public]
    public virtual void FillIncomingTaxInvoiceProperties(FinancialArchive.IIncomingTaxInvoice incomingTaxInvoice,
                                                         IDocumentInfo documentInfo,
                                                         Sungero.Company.IEmployee responsible,
                                                         IRecognizedDocumentParties recognizedDocumentParties)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(incomingTaxInvoice);
      
      // Подразделение.
      incomingTaxInvoice.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
      
      // Сумма и валюта.
      this.FillAccountingDocumentAmountAndCurrency(incomingTaxInvoice, documentInfo);
      
      // НОР и КА.
      this.FillAccountingDocumentParties(incomingTaxInvoice, documentInfo, recognizedDocumentParties);
      
      // Дата, номер и регистрация.
      this.FillDocumentRegistrationData(incomingTaxInvoice, documentInfo, ArioGrammars.FinancialDocumentFact.Name, Docflow.Resources.UnknownNumber);
      
      // Корректировочный документ.
      if (incomingTaxInvoice.IsAdjustment.HasValue && incomingTaxInvoice.IsAdjustment.Value == true)
        this.FillIncomingTaxInvoiceCorrectedDocument(incomingTaxInvoice, documentInfo);
      else
        this.FillIncomingTaxInvoiceRevisionInfo(incomingTaxInvoice, documentInfo);
    }
    
    /// <summary>
    /// Заполнить свойства накладной по результатам обработки Ario.
    /// </summary>
    /// <param name="waybill">Товарная накладная.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    [Public]
    public virtual void FillWaybillProperties(FinancialArchive.IWaybill waybill,
                                              IDocumentInfo documentInfo,
                                              Sungero.Company.IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(waybill);
      
      // Подразделение и ответственный.
      waybill.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
      waybill.ResponsibleEmployee = responsible;
      
      // Сумма и валюта.
      this.FillAccountingDocumentAmountAndCurrency(waybill, documentInfo);
      
      // НОР и КА.
      var arioCounterpartyTypes = new List<string>();
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Supplier);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Payer);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Shipper);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Consignee);
      
      var recognizedCounterparties = this.GetRecognizedAccountingDocumentCounterparties(arioDocument.Facts, arioCounterpartyTypes);
      var seller = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Supplier).FirstOrDefault() ??
        recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Shipper).FirstOrDefault();
      var buyer = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Payer).FirstOrDefault() ??
        recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Consignee).FirstOrDefault();
      
      var recognizedDocumentParties = this.GetDocumentParties(buyer, seller, responsible);
      
      this.FillAccountingDocumentParties(waybill, documentInfo, recognizedDocumentParties);
      
      // Дата, номер и регистрация.
      this.FillDocumentRegistrationData(waybill, documentInfo, ArioGrammars.FinancialDocumentFact.Name, Docflow.Resources.UnknownNumber);
      
      // Документ-основание.
      var leadingDocFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                          ArioGrammars.FinancialDocumentFact.Name,
                                                                          ArioGrammars.FinancialDocumentFact.DocumentBaseNameField)
        .FirstOrDefault();
      
      var leadingDocument = this.GetLeadingContractualDocument(leadingDocFact,
                                                               waybill.Info.Properties.LeadingDocument.Name,
                                                               waybill.Counterparty, waybill.Info.Properties.Counterparty.Name);
      waybill.LeadingDocument = leadingDocument.Contract;
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo, leadingDocFact, null, waybill.Info.Properties.LeadingDocument.Name, waybill.LeadingDocument, leadingDocument.Probability);
    }
    
    /// <summary>
    /// Заполнить свойства универсального передаточного документа по результатам обработки Ario.
    /// </summary>
    /// <param name="universalTransferDocument">Универсальный передаточный документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    [Public]
    public virtual void FillUniversalTransferDocumentProperties(FinancialArchive.IUniversalTransferDocument universalTransferDocument,
                                                                IDocumentInfo documentInfo,
                                                                Sungero.Company.IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(universalTransferDocument);
      
      // Сумма и валюта.
      this.FillAccountingDocumentAmountAndCurrency(universalTransferDocument, documentInfo);
      
      // Подразделение и ответственный.
      universalTransferDocument.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
      universalTransferDocument.ResponsibleEmployee = responsible;
      
      var props = universalTransferDocument.Info.Properties;
      
      // НОР и КА.
      var arioCounterpartyTypes = new List<string>();
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Shipper);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Consignee);

      var recognizedCounterparties = this.GetRecognizedAccountingDocumentCounterparties(arioDocument.Facts, arioCounterpartyTypes);
      var seller = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller).FirstOrDefault() ??
        recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Shipper).FirstOrDefault();
      var buyer = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer).FirstOrDefault() ??
        recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Consignee).FirstOrDefault();
      
      var recognizedDocumentParties = this.GetDocumentParties(buyer, seller, responsible);
      
      this.FillAccountingDocumentParties(universalTransferDocument, documentInfo, recognizedDocumentParties);
      
      // Дата, номер и регистрация.
      this.FillDocumentRegistrationData(universalTransferDocument, documentInfo, ArioGrammars.FinancialDocumentFact.Name, Docflow.Resources.UnknownNumber);
      
      // Корректировочный документ.
      if (universalTransferDocument.IsAdjustment.HasValue && universalTransferDocument.IsAdjustment.Value == true)
        this.FillUniversalTransferDocumentCorrectedDocument(universalTransferDocument, documentInfo);
      else
        this.FillUniversalTransferDocumentRevisionInfo(universalTransferDocument, documentInfo);
    }

    /// <summary>
    /// Заполнить сумму и валюту в финансовом документе.
    /// </summary>
    /// <param name="accountingDocument">Финансовый документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillAccountingDocumentAmountAndCurrency(Docflow.IAccountingDocumentBase accountingDocument,
                                                                IDocumentInfo documentInfo)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      var recognizedAmount = this.GetRecognizedAmount(arioDocument.Facts);
      if (recognizedAmount.HasValue)
      {
        accountingDocument.TotalAmount = recognizedAmount.Amount;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          recognizedAmount.Fact,
                                                                          ArioGrammars.DocumentAmountFact.AmountField,
                                                                          accountingDocument.Info.Properties.TotalAmount.Name,
                                                                          recognizedAmount.Amount,
                                                                          recognizedAmount.Probability);
      }

      // В факте с суммой документа может быть не указана валюта, поэтому факт с валютой ищем отдельно,
      // так как на данный момент функция используется для обработки бухгалтерских и договорных документов,
      // а в них все расчеты ведутся в одной валюте.
      var recognizedCurrency = this.GetRecognizedCurrency(arioDocument.Facts);
      
      if (recognizedAmount.HasValue && !recognizedCurrency.HasValue)
      {
        recognizedCurrency.HasValue = true;
        recognizedCurrency.Currency = Commons.Currencies.GetAllCached(c => c.IsDefault == true).FirstOrDefault();
        recognizedCurrency.Probability = Module.PropertyProbabilityLevels.Min;
      }
      
      if (recognizedCurrency.HasValue)
      {
        accountingDocument.Currency = recognizedCurrency.Currency;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          recognizedCurrency.Fact,
                                                                          ArioGrammars.DocumentAmountFact.CurrencyField,
                                                                          accountingDocument.Info.Properties.Currency.Name,
                                                                          recognizedCurrency.Currency,
                                                                          recognizedCurrency.Probability);
      }
    }

    /// <summary>
    /// Заполнить корректируемый документ в полученном СФ.
    /// </summary>
    /// <param name="incomingTaxInvoice">Полученный СФ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillIncomingTaxInvoiceCorrectedDocument(FinancialArchive.IIncomingTaxInvoice incomingTaxInvoice,
                                                                IDocumentInfo documentInfo)
    {
      this.FillIncomingTaxInvoicelCorrectedDocumentRevisionInfo(incomingTaxInvoice, documentInfo);
      
      var arioDocument = documentInfo.ArioDocument;
      var props = incomingTaxInvoice.Info.Properties;
      
      var correctionDateFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                              ArioGrammars.FinancialDocumentFact.Name,
                                                                              ArioGrammars.FinancialDocumentFact.CorrectionDateField)
        .FirstOrDefault();
      
      var correctionDate = Commons.PublicFunctions.Module.GetFieldDateTimeValue(correctionDateFact, ArioGrammars.FinancialDocumentFact.CorrectionDateField);
      
      var correctionNumberFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                                ArioGrammars.FinancialDocumentFact.Name,
                                                                                ArioGrammars.FinancialDocumentFact.CorrectionNumberField)
        .FirstOrDefault();
      
      var correctionNumber = Commons.PublicFunctions.Module.GetFieldValue(correctionNumberFact, ArioGrammars.FinancialDocumentFact.CorrectionNumberField);
      
      var documents = FinancialArchive.IncomingTaxInvoices.GetAll()
        .Where(d => d.Id != incomingTaxInvoice.Id && d.IsAdjustment != true)
        .Where(d => d.RegistrationNumber.Equals(correctionNumber, StringComparison.InvariantCultureIgnoreCase) &&
               d.RegistrationDate == correctionDate &&
               incomingTaxInvoice.Counterparty != null &&
               Equals(d.Counterparty, incomingTaxInvoice.Counterparty));
      
      if (!documents.Any())
      {
        var correctionDateString = correctionDate == null ? string.Empty : correctionDate.Value.Date.ToString("d");
        var correctionString = Resources.TaxInvoiceCorrectsFormat(correctionNumber, correctionDateString);
        incomingTaxInvoice.Subject = string.IsNullOrEmpty(incomingTaxInvoice.Subject) ?
          correctionString :
          string.Format("{0}{1}{2}", correctionString, Environment.NewLine, incomingTaxInvoice.Subject);
        return;
      }
      
      incomingTaxInvoice.Corrected = documents.FirstOrDefault();
      
      var probability = Module.PropertyProbabilityLevels.Max / documents.Count();
      
      // Если Контрагент определен с уровнем вероятности "Ниже среднего" и ниже,
      // то для корректировочного документа использовать минимальный уровень вероятности.
      var counterpartyProbability = Commons.PublicFunctions.EntityRecognitionInfo.GetProbabilityByPropertyName(arioDocument.RecognitionInfo,
                                                                                                               props.Counterparty.Name);
      if (!counterpartyProbability.HasValue)
        return;
      if (counterpartyProbability.Value <= Module.PropertyProbabilityLevels.LowerMiddle)
        probability = Module.PropertyProbabilityLevels.Min;
      
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                        correctionDateFact,
                                                                        ArioGrammars.FinancialDocumentFact.CorrectionDateField,
                                                                        props.Corrected.Name,
                                                                        incomingTaxInvoice.Corrected,
                                                                        probability);
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                        correctionNumberFact,
                                                                        ArioGrammars.FinancialDocumentFact.CorrectionNumberField,
                                                                        props.Corrected.Name,
                                                                        incomingTaxInvoice.Corrected,
                                                                        probability);
    }
    
    /// <summary>
    /// Заполнить корректируемый документ в выставленном СФ.
    /// </summary>
    /// <param name="outgoingTaxInvoice">Выставленный СФ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillOutgoingTaxInvoiceCorrectedDocument(FinancialArchive.IOutgoingTaxInvoice outgoingTaxInvoice,
                                                                IDocumentInfo documentInfo)
    {
      this.FillOutgoingTaxInvoicelCorrectedDocumentRevisionInfo(outgoingTaxInvoice, documentInfo);
      
      var arioDocument = documentInfo.ArioDocument;
      var props = outgoingTaxInvoice.Info.Properties;
      
      var correctionDateFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                              ArioGrammars.FinancialDocumentFact.Name,
                                                                              ArioGrammars.FinancialDocumentFact.CorrectionDateField)
        .FirstOrDefault();
      var correctionDate = Commons.PublicFunctions.Module.GetFieldDateTimeValue(correctionDateFact,
                                                                                ArioGrammars.FinancialDocumentFact.CorrectionDateField);
      
      var correctionNumberFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                                ArioGrammars.FinancialDocumentFact.Name,
                                                                                ArioGrammars.FinancialDocumentFact.CorrectionNumberField)
        .FirstOrDefault();
      var correctionNumber = Commons.PublicFunctions.Module.GetFieldValue(correctionNumberFact,
                                                                          ArioGrammars.FinancialDocumentFact.CorrectionNumberField);
      
      var documents = FinancialArchive.OutgoingTaxInvoices.GetAll()
        .Where(d => d.Id != outgoingTaxInvoice.Id && d.IsAdjustment != true)
        .Where(d => d.RegistrationNumber.Equals(correctionNumber, StringComparison.InvariantCultureIgnoreCase) &&
               d.RegistrationDate == correctionDate &&
               outgoingTaxInvoice.Counterparty != null &&
               Equals(d.Counterparty, outgoingTaxInvoice.Counterparty));
      
      if (!documents.Any())
      {
        var correctionDateString = correctionDate == null ? string.Empty : correctionDate.Value.Date.ToString("d");
        var correctionString = Resources.TaxInvoiceCorrectsFormat(correctionNumber, correctionDateString);
        outgoingTaxInvoice.Subject = string.IsNullOrEmpty(outgoingTaxInvoice.Subject) ?
          correctionString :
          string.Format("{0}{1}{2}", correctionString, Environment.NewLine, outgoingTaxInvoice.Subject);
        return;
      }
      
      outgoingTaxInvoice.Corrected = documents.FirstOrDefault();
      
      var probability = Module.PropertyProbabilityLevels.Max / documents.Count();
      
      // Если Контрагент определен с уровнем вероятности "Ниже среднего" и ниже,
      // то для корректировочного документа использовать минимальный уровень вероятности.
      var counterpartyPropbability = Commons.PublicFunctions.EntityRecognitionInfo.GetProbabilityByPropertyName(arioDocument.RecognitionInfo,
                                                                                                                props.Counterparty.Name);
      if (!counterpartyPropbability.HasValue)
        return;
      if (counterpartyPropbability.Value <= Module.PropertyProbabilityLevels.LowerMiddle)
        probability = Module.PropertyProbabilityLevels.Min;
      
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                        correctionDateFact,
                                                                        ArioGrammars.FinancialDocumentFact.CorrectionDateField,
                                                                        props.Corrected.Name,
                                                                        outgoingTaxInvoice.Corrected,
                                                                        probability);
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                        correctionNumberFact,
                                                                        ArioGrammars.FinancialDocumentFact.CorrectionNumberField,
                                                                        props.Corrected.Name,
                                                                        outgoingTaxInvoice.Corrected,
                                                                        probability);
    }
    
    /// <summary>
    /// Заполнить корректируемый УПД.
    /// </summary>
    /// <param name="universalTransferDocument">Корректирующий УПД.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillUniversalTransferDocumentCorrectedDocument(FinancialArchive.IUniversalTransferDocument universalTransferDocument,
                                                                       IDocumentInfo documentInfo)
    {
      this.FillUniversalCorrectedDocumentRevisionInfo(universalTransferDocument, documentInfo);
      
      var arioDocument = documentInfo.ArioDocument;
      var props = universalTransferDocument.Info.Properties;
      
      var correctionDateFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                              ArioGrammars.FinancialDocumentFact.Name,
                                                                              ArioGrammars.FinancialDocumentFact.CorrectionDateField)
        .FirstOrDefault();
      var correctionDate = Commons.PublicFunctions.Module.GetFieldDateTimeValue(correctionDateFact, ArioGrammars.FinancialDocumentFact.CorrectionDateField);
      
      var correctionNumberFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                                ArioGrammars.FinancialDocumentFact.Name,
                                                                                ArioGrammars.FinancialDocumentFact.CorrectionNumberField)
        .FirstOrDefault();
      
      var correctionNumber = Commons.PublicFunctions.Module.GetFieldValue(correctionNumberFact, ArioGrammars.FinancialDocumentFact.CorrectionNumberField);
      
      var documents = FinancialArchive.UniversalTransferDocuments.GetAll()
        .Where(d => d.Id != universalTransferDocument.Id && d.IsAdjustment != true)
        .Where(d => d.RegistrationNumber.Equals(correctionNumber, StringComparison.InvariantCultureIgnoreCase) &&
               d.RegistrationDate == correctionDate &&
               universalTransferDocument.Counterparty != null &&
               Equals(d.Counterparty, universalTransferDocument.Counterparty));
      
      if (!documents.Any())
      {
        var correctionDateString = correctionDate == null ? string.Empty : correctionDate.Value.Date.ToString("d");
        var correctionString = Resources.UTDCorrectsFormat(correctionNumber, correctionDateString);
        universalTransferDocument.Subject = string.IsNullOrEmpty(universalTransferDocument.Subject) ?
          correctionString :
          string.Format("{0}{1}{2}", correctionString, Environment.NewLine, universalTransferDocument.Subject);
        return;
      }
      
      universalTransferDocument.Corrected = documents.FirstOrDefault();
      
      var probability = Module.PropertyProbabilityLevels.Max / documents.Count();
      
      // Если Контрагент определен с уровнем вероятности "Ниже среднего" и ниже,
      // то для корректировочного документа использовать минимальный уровень вероятности.
      var counterpartyPropbability = Commons.PublicFunctions.EntityRecognitionInfo.GetProbabilityByPropertyName(arioDocument.RecognitionInfo,
                                                                                                                props.Counterparty.Name);
      if (!counterpartyPropbability.HasValue)
        return;
      if (counterpartyPropbability.Value <= Module.PropertyProbabilityLevels.LowerMiddle)
        probability = Module.PropertyProbabilityLevels.Min;
      
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                        correctionDateFact,
                                                                        ArioGrammars.FinancialDocumentFact.CorrectionDateField,
                                                                        props.Corrected.Name,
                                                                        universalTransferDocument.Corrected,
                                                                        probability);
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                        correctionNumberFact,
                                                                        ArioGrammars.FinancialDocumentFact.CorrectionNumberField,
                                                                        props.Corrected.Name,
                                                                        universalTransferDocument.Corrected,
                                                                        probability);
    }
    
    /// <summary>
    /// Для исправленного полученного СФ заполнить признак исправленного СФ, в поле Содержание № и дату исправления.
    /// </summary>
    /// <param name="incomingTaxInvoice">Полученный СФ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillIncomingTaxInvoiceRevisionInfo(FinancialArchive.IIncomingTaxInvoice incomingTaxInvoice,
                                                           IDocumentInfo documentInfo)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      var revisionNumberFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                              ArioGrammars.FinancialDocumentFact.Name,
                                                                              ArioGrammars.FinancialDocumentFact.RevisionNumberField).FirstOrDefault();
      
      var revisionDateFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                            ArioGrammars.FinancialDocumentFact.Name,
                                                                            ArioGrammars.FinancialDocumentFact.RevisionDateField).FirstOrDefault();
      
      if (revisionNumberFact != null || revisionDateFact != null)
      {
        var revisionNumber = Commons.PublicFunctions.Module.GetFieldValue(revisionNumberFact,
                                                                          ArioGrammars.FinancialDocumentFact.RevisionNumberField);
        
        var revisionDate = Commons.PublicFunctions.Module.GetFieldDateTimeValue(revisionDateFact,
                                                                                ArioGrammars.FinancialDocumentFact.RevisionDateField);
        var revisionDateString = revisionDate == null ? string.Empty : revisionDate.Value.Date.ToString("d");
        
        var revisionString = string.Format(Exchange.Resources.TaxInvoiceRevision, revisionNumber, revisionDateString);
        
        incomingTaxInvoice.Subject = string.IsNullOrEmpty(incomingTaxInvoice.Subject) ?
          revisionString :
          string.Format("{0}{1}{2}", incomingTaxInvoice.Subject, Environment.NewLine, revisionString);
        
        incomingTaxInvoice.IsRevision = true;
        
        // Поиск исправляемого документа для создания связи
        var originalIncomingTaxInvoice = FinancialArchive.IncomingTaxInvoices.GetAll()
          .Where(d => d.Id != incomingTaxInvoice.Id && d.IsRevision != true)
          .Where(d => d.RegistrationNumber.Equals(incomingTaxInvoice.RegistrationNumber, StringComparison.InvariantCultureIgnoreCase) &&
                 d.RegistrationDate == incomingTaxInvoice.RegistrationDate &&
                 Equals(d.Counterparty, incomingTaxInvoice.Counterparty)).FirstOrDefault();
        
        if (originalIncomingTaxInvoice != null)
        {
          incomingTaxInvoice.Relations.Add(Exchange.PublicConstants.Module.SimpleRelationRelationName, originalIncomingTaxInvoice);
        }
      }
    }
    
    /// <summary>
    /// Для исправленного корректировочного полученного СФ заполнить признак исправленного СФ, в поле Содержание № и дату исправления.
    /// </summary>
    /// <param name="incomingTaxInvoice">Корректировочный полученный СФ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillIncomingTaxInvoicelCorrectedDocumentRevisionInfo(FinancialArchive.IIncomingTaxInvoice incomingTaxInvoice,
                                                                             IDocumentInfo documentInfo)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      var revisionNumberFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                              ArioGrammars.FinancialDocumentFact.Name,
                                                                              ArioGrammars.FinancialDocumentFact.CorrectionRevisionNumberField).FirstOrDefault();
      
      var revisionDateFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                            ArioGrammars.FinancialDocumentFact.Name,
                                                                            ArioGrammars.FinancialDocumentFact.CorrectionRevisionDateField).FirstOrDefault();
      if (revisionNumberFact != null || revisionDateFact != null)
      {
        var revisionNumber = Commons.PublicFunctions.Module.GetFieldValue(revisionNumberFact,
                                                                          ArioGrammars.FinancialDocumentFact.CorrectionRevisionNumberField);
        
        var revisionDate = Commons.PublicFunctions.Module.GetFieldDateTimeValue(revisionDateFact,
                                                                                ArioGrammars.FinancialDocumentFact.CorrectionRevisionDateField);
        var revisionDateString = revisionDate == null ? string.Empty : revisionDate.Value.Date.ToString("d");
        
        var revisionString = string.Format(Exchange.Resources.TaxInvoiceRevision, revisionNumber, revisionDateString);
        
        incomingTaxInvoice.Subject = string.IsNullOrEmpty(incomingTaxInvoice.Subject) ?
          revisionString :
          string.Format("{0}{1}{2}", incomingTaxInvoice.Subject, Environment.NewLine, revisionString);
        
        incomingTaxInvoice.IsRevision = true;
        
        // Поиск исправляемого корректировочного выставленного СФ для создания связи
        var originalIncomingTaxInvoice = FinancialArchive.IncomingTaxInvoices.GetAll()
          .Where(d => d.Id != incomingTaxInvoice.Id && d.IsRevision != true && d.IsAdjustment == true)
          .Where(d => d.RegistrationNumber.Equals(incomingTaxInvoice.RegistrationNumber, StringComparison.InvariantCultureIgnoreCase) &&
                 d.RegistrationDate == incomingTaxInvoice.RegistrationDate &&
                 Equals(d.Counterparty, incomingTaxInvoice.Counterparty)).FirstOrDefault();
        
        if (originalIncomingTaxInvoice != null)
        {
          incomingTaxInvoice.Relations.Add(Exchange.PublicConstants.Module.SimpleRelationRelationName, originalIncomingTaxInvoice);
        }
      }
    }
    
    /// <summary>
    /// Для исправленного выставленного СФ заполнить признак исправленного СФ, в поле Содержание № и дату исправления.
    /// </summary>
    /// <param name="outgoingTaxInvoice">Выставленный СФ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillOutgoingTaxInvoiceRevisionInfo(FinancialArchive.IOutgoingTaxInvoice outgoingTaxInvoice,
                                                           IDocumentInfo documentInfo)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      var revisionNumberFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                              ArioGrammars.FinancialDocumentFact.Name,
                                                                              ArioGrammars.FinancialDocumentFact.RevisionNumberField).FirstOrDefault();
      
      var revisionDateFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                            ArioGrammars.FinancialDocumentFact.Name,
                                                                            ArioGrammars.FinancialDocumentFact.RevisionDateField).FirstOrDefault();
      
      if (revisionNumberFact != null || revisionDateFact != null)
      {
        var revisionNumber = Commons.PublicFunctions.Module.GetFieldValue(revisionNumberFact,
                                                                          ArioGrammars.FinancialDocumentFact.RevisionNumberField);
        
        var revisionDate = Commons.PublicFunctions.Module.GetFieldDateTimeValue(revisionDateFact,
                                                                                ArioGrammars.FinancialDocumentFact.RevisionDateField);
        var revisionDateString = revisionDate == null ? string.Empty : revisionDate.Value.Date.ToString("d");
        
        var revisionString = string.Format(Exchange.Resources.TaxInvoiceRevision, revisionNumber, revisionDateString);
        
        outgoingTaxInvoice.Subject = string.IsNullOrEmpty(outgoingTaxInvoice.Subject) ?
          revisionString :
          string.Format("{0}{1}{2}", outgoingTaxInvoice.Subject, Environment.NewLine, revisionString);
        
        outgoingTaxInvoice.IsRevision = true;
        
        // Поиск исправляемого документа для создания связи
        var originalOutgoingTaxInvoice = FinancialArchive.OutgoingTaxInvoices.GetAll()
          .Where(d => d.Id != outgoingTaxInvoice.Id && d.IsRevision != true)
          .Where(d => d.RegistrationNumber.Equals(outgoingTaxInvoice.RegistrationNumber, StringComparison.InvariantCultureIgnoreCase) &&
                 d.RegistrationDate == outgoingTaxInvoice.RegistrationDate &&
                 Equals(d.Counterparty, outgoingTaxInvoice.Counterparty)).FirstOrDefault();
        
        if (originalOutgoingTaxInvoice != null)
        {
          outgoingTaxInvoice.Relations.Add(Exchange.PublicConstants.Module.SimpleRelationRelationName, originalOutgoingTaxInvoice);
        }
      }
    }
    
    /// <summary>
    /// Для исправленного корректировочного выставленного СФ заполнить признак исправленного СФ, в поле Содержание № и дату исправления.
    /// </summary>
    /// <param name="outgoingTaxInvoice">Корректировочный выставленный СФ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillOutgoingTaxInvoicelCorrectedDocumentRevisionInfo(FinancialArchive.IOutgoingTaxInvoice outgoingTaxInvoice,
                                                                             IDocumentInfo documentInfo)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      var revisionNumberFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                              ArioGrammars.FinancialDocumentFact.Name,
                                                                              ArioGrammars.FinancialDocumentFact.CorrectionRevisionNumberField).FirstOrDefault();
      
      var revisionDateFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                            ArioGrammars.FinancialDocumentFact.Name,
                                                                            ArioGrammars.FinancialDocumentFact.CorrectionRevisionDateField).FirstOrDefault();
      if (revisionNumberFact != null || revisionDateFact != null)
      {
        var revisionNumber = Commons.PublicFunctions.Module.GetFieldValue(revisionNumberFact,
                                                                          ArioGrammars.FinancialDocumentFact.CorrectionRevisionNumberField);
        
        var revisionDate = Commons.PublicFunctions.Module.GetFieldDateTimeValue(revisionDateFact,
                                                                                ArioGrammars.FinancialDocumentFact.CorrectionRevisionDateField);
        var revisionDateString = revisionDate == null ? string.Empty : revisionDate.Value.Date.ToString("d");
        
        var revisionString = string.Format(Exchange.Resources.TaxInvoiceRevision, revisionNumber, revisionDateString);
        
        outgoingTaxInvoice.Subject = string.IsNullOrEmpty(outgoingTaxInvoice.Subject) ?
          revisionString :
          string.Format("{0}{1}{2}", outgoingTaxInvoice.Subject, Environment.NewLine, revisionString);
        
        outgoingTaxInvoice.IsRevision = true;
        
        // Поиск исправляемого корректировочного выставленного СФ для создания связи
        var originalOutgoingTaxInvoice = FinancialArchive.OutgoingTaxInvoices.GetAll()
          .Where(d => d.Id != outgoingTaxInvoice.Id && d.IsRevision != true && d.IsAdjustment == true)
          .Where(d => d.RegistrationNumber.Equals(outgoingTaxInvoice.RegistrationNumber, StringComparison.InvariantCultureIgnoreCase) &&
                 d.RegistrationDate == outgoingTaxInvoice.RegistrationDate &&
                 Equals(d.Counterparty, outgoingTaxInvoice.Counterparty)).FirstOrDefault();
        
        if (originalOutgoingTaxInvoice != null)
        {
          outgoingTaxInvoice.Relations.Add(Exchange.PublicConstants.Module.SimpleRelationRelationName, originalOutgoingTaxInvoice);
        }
      }
    }
    
    /// <summary>
    /// Для исправленного УПД заполнить признак исправленного УПД, в поле Содержание № и дату исправления.
    /// </summary>
    /// <param name="universalTransferDocument">УПД.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillUniversalTransferDocumentRevisionInfo(FinancialArchive.IUniversalTransferDocument universalTransferDocument,
                                                                  IDocumentInfo documentInfo)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      var revisionNumberFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                              ArioGrammars.FinancialDocumentFact.Name,
                                                                              ArioGrammars.FinancialDocumentFact.RevisionNumberField).FirstOrDefault();
      
      var revisionDateFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                            ArioGrammars.FinancialDocumentFact.Name,
                                                                            ArioGrammars.FinancialDocumentFact.RevisionDateField).FirstOrDefault();
      if (revisionNumberFact != null || revisionDateFact != null)
      {
        var revisionNumber = Commons.PublicFunctions.Module.GetFieldValue(revisionNumberFact,
                                                                          ArioGrammars.FinancialDocumentFact.RevisionNumberField);
        
        var revisionDate = Commons.PublicFunctions.Module.GetFieldDateTimeValue(revisionDateFact,
                                                                                ArioGrammars.FinancialDocumentFact.RevisionDateField);
        var revisionDateString = revisionDate == null ? string.Empty : revisionDate.Value.Date.ToString("d");
        
        var revisionString = string.Format(Exchange.Resources.TaxInvoiceRevision, revisionNumber, revisionDateString);
        
        universalTransferDocument.Subject = string.IsNullOrEmpty(universalTransferDocument.Subject) ?
          revisionString :
          string.Format("{0}{1}{2}", universalTransferDocument.Subject, Environment.NewLine, revisionString);
        
        universalTransferDocument.IsRevision = true;
        
        // Поиск исправляемого документа для создания связи
        var originalUniversalTransferDocument = FinancialArchive.UniversalTransferDocuments.GetAll()
          .Where(d => d.Id != universalTransferDocument.Id && d.IsRevision != true)
          .Where(d => d.RegistrationNumber.Equals(universalTransferDocument.RegistrationNumber, StringComparison.InvariantCultureIgnoreCase) &&
                 d.RegistrationDate == universalTransferDocument.RegistrationDate &&
                 Equals(d.Counterparty, universalTransferDocument.Counterparty)).FirstOrDefault();
        
        if (originalUniversalTransferDocument != null)
        {
          universalTransferDocument.Relations.Add(Exchange.PublicConstants.Module.SimpleRelationRelationName, originalUniversalTransferDocument);
        }
      }
    }
    
    /// <summary>
    /// Для исправленного УКД заполнить признак исправленного УКД, в поле Содержание № и дату исправления.
    /// </summary>
    /// <param name="universalTransferDocument">УКД.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillUniversalCorrectedDocumentRevisionInfo(FinancialArchive.IUniversalTransferDocument universalTransferDocument,
                                                                   IDocumentInfo documentInfo)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      var revisionNumberFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                              ArioGrammars.FinancialDocumentFact.Name,
                                                                              ArioGrammars.FinancialDocumentFact.CorrectionRevisionNumberField).FirstOrDefault();
      
      var revisionDateFact = Commons.PublicFunctions.Module.GetOrderedFacts(arioDocument.Facts,
                                                                            ArioGrammars.FinancialDocumentFact.Name,
                                                                            ArioGrammars.FinancialDocumentFact.CorrectionRevisionDateField).FirstOrDefault();
      if (revisionNumberFact != null || revisionDateFact != null)
      {
        var revisionNumber = Commons.PublicFunctions.Module.GetFieldValue(revisionNumberFact,
                                                                          ArioGrammars.FinancialDocumentFact.CorrectionRevisionNumberField);
        
        var revisionDate = Commons.PublicFunctions.Module.GetFieldDateTimeValue(revisionDateFact,
                                                                                ArioGrammars.FinancialDocumentFact.CorrectionRevisionDateField);
        var revisionDateString = revisionDate == null ? string.Empty : revisionDate.Value.Date.ToString("d");
        
        var revisionString = string.Format(Exchange.Resources.TaxInvoiceRevision, revisionNumber, revisionDateString);
        
        universalTransferDocument.Subject = string.IsNullOrEmpty(universalTransferDocument.Subject) ?
          revisionString :
          string.Format("{0}{1}{2}", universalTransferDocument.Subject, Environment.NewLine, revisionString);
        
        universalTransferDocument.IsRevision = true;
        
        // Поиск исправляемого УКД для создания связи
        var originalUniversalTransferDocument = FinancialArchive.UniversalTransferDocuments.GetAll()
          .Where(d => d.Id != universalTransferDocument.Id && d.IsRevision != true && d.IsAdjustment == true)
          .Where(d => d.RegistrationNumber.Equals(universalTransferDocument.RegistrationNumber, StringComparison.InvariantCultureIgnoreCase) &&
                 d.RegistrationDate == universalTransferDocument.RegistrationDate &&
                 Equals(d.Counterparty, universalTransferDocument.Counterparty)).FirstOrDefault();
        
        if (originalUniversalTransferDocument != null)
        {
          universalTransferDocument.Relations.Add(Exchange.PublicConstants.Module.SimpleRelationRelationName, originalUniversalTransferDocument);
        }
      }
    }
    
    /// <summary>
    /// Заполнить НОР и контрагента в финансовом документе.
    /// </summary>
    /// <param name="accountingDocument">Финансовый документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="recognizedDocumentParties">НОР и контрагент.</param>
    [Public]
    public virtual void FillAccountingDocumentParties(Docflow.IAccountingDocumentBase accountingDocument,
                                                      IDocumentInfo documentInfo,
                                                      IRecognizedDocumentParties recognizedDocumentParties)
    {
      var recognitionInfo = documentInfo.ArioDocument.RecognitionInfo;
      var props = accountingDocument.Info.Properties;
      var counterpartyPropertyName = props.Counterparty.Name;
      var businessUnitPropertyName = props.BusinessUnit.Name;
      var counterparty = recognizedDocumentParties.Counterparty;
      var businessUnit = recognizedDocumentParties.BusinessUnit;
      
      var businessUnitMatched = businessUnit != null && businessUnit.BusinessUnit != null;
      var counterpartyMatched = recognizedDocumentParties.Counterparty != null &&
        recognizedDocumentParties.Counterparty.Counterparty != null;

      accountingDocument.Counterparty = counterparty != null ? counterparty.Counterparty : null;
      accountingDocument.BusinessUnit = businessUnitMatched ? businessUnit.BusinessUnit : recognizedDocumentParties.ResponsibleEmployeeBusinessUnit;

      if (counterpartyMatched)
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(recognitionInfo, recognizedDocumentParties.Counterparty.Fact, null,
                                                                          counterpartyPropertyName, recognizedDocumentParties.Counterparty.Counterparty,
                                                                          recognizedDocumentParties.Counterparty.CounterpartyProbability);

      if (businessUnitMatched)
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(recognitionInfo, recognizedDocumentParties.BusinessUnit.Fact, null,
                                                                          businessUnitPropertyName, recognizedDocumentParties.BusinessUnit.BusinessUnit,
                                                                          recognizedDocumentParties.BusinessUnit.BusinessUnitProbability);
      else
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(recognitionInfo, null, null,
                                                                          businessUnitPropertyName, recognizedDocumentParties.ResponsibleEmployeeBusinessUnit,
                                                                          Module.PropertyProbabilityLevels.Min);
    }
    #endregion
    
    #region Документооборот

    /// <summary>
    /// Заполнить свойства по результатам обработки Ario.
    /// </summary>
    /// <param name="simpleDocument">Простой документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    /// <param name="documentName">Имя документа.</param>
    [Public]
    public virtual void FillSimpleDocumentProperties(Docflow.ISimpleDocument simpleDocument,
                                                     IDocumentInfo documentInfo,
                                                     Sungero.Company.IEmployee responsible,
                                                     string documentName)
    {
      // Вид документа.
      this.FillDocumentKind(simpleDocument);
      
      simpleDocument.Name = documentName;
      simpleDocument.PreparedBy = responsible;
      simpleDocument.BusinessUnit = Docflow.PublicFunctions.Module.GetDefaultBusinessUnit(responsible);
      simpleDocument.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
    }
    
    #endregion
    
    #region Получение даты и номера
    
    /// <summary>
    /// Распознать дату документа.
    /// </summary>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <param name="factName">Наименование факта.</param>
    /// <param name="fieldName">Наименование поля.</param>
    /// <returns>Результаты распознавания даты.</returns>
    [Public]
    public virtual IRecognizedDocumentDate GetRecognizedDate(List<IArioFact> facts,
                                                             string factName,
                                                             string fieldName)
    {
      var defaultProbability = Module.PropertyProbabilityLevels.Min;
      var defaultDate = Calendar.SqlMinValue;
      var dateFact = Commons.PublicFunctions.Module.GetOrderedFacts(facts, factName, fieldName).FirstOrDefault();
      var recognizedDate = RecognizedDocumentDate.Create(null, null, null);
      
      if (dateFact == null)
        return recognizedDate;
      
      recognizedDate.Fact = dateFact;
      recognizedDate.Date = defaultDate;
      recognizedDate.Probability = defaultProbability;
      var date = Commons.PublicFunctions.Module.GetFieldDateTimeValue(dateFact, fieldName);
      var isDateValid = date == null ||
        date != null && date.HasValue && date >= Calendar.SqlMinValue;
      
      if (isDateValid)
      {
        recognizedDate.Date = date;
        recognizedDate.Probability = Commons.PublicFunctions.Module.GetFieldProbability(dateFact, fieldName);
      }
      
      return recognizedDate;
    }
    
    /// <summary>
    /// Распознать номер документа.
    /// </summary>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <param name="factName">Наименование факта.</param>
    /// <param name="fieldName">Наименование поля.</param>
    /// <param name="numberPropertyInfo">Информация о свойстве с номером.</param>
    /// <returns>Результаты распознавания номера.</returns>
    [Public]
    public virtual IRecognizedDocumentNumber GetRecognizedNumber(List<IArioFact> facts,
                                                                 string factName, string fieldName,
                                                                 Sungero.Domain.Shared.IStringPropertyInfo numberPropertyInfo)
    {
      var recognizedNumber = RecognizedDocumentNumber.Create();
      var numberFact = Commons.PublicFunctions.Module.GetOrderedFacts(facts, factName, fieldName).FirstOrDefault();
      
      if (numberFact == null)
        return recognizedNumber;
      
      recognizedNumber.Fact = numberFact;
      
      var number = Commons.PublicFunctions.Module.GetFieldValue(numberFact, fieldName);
      if (string.IsNullOrWhiteSpace(number))
        return recognizedNumber;
      
      recognizedNumber.Number = number;
      recognizedNumber.Probability = Commons.PublicFunctions.Module.GetFieldProbability(numberFact, fieldName);
      if (number.Length > numberPropertyInfo.Length)
      {
        recognizedNumber.Number = number.Substring(0, numberPropertyInfo.Length);
        recognizedNumber.Probability = Module.PropertyProbabilityLevels.LowerMiddle;
      }
      
      return recognizedNumber;
    }
    
    #endregion
    
    #region Получение суммы и валюты
    
    /// <summary>
    /// Распознать сумму.
    /// </summary>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <returns>Результаты распознавания суммы.</returns>
    [Public]
    public virtual IRecognizedAmount GetRecognizedAmount(List<IArioFact> facts)
    {
      var recognizedAmount = RecognizedAmount.Create();
      var amountFacts = Commons.PublicFunctions.Module.GetOrderedFacts(facts,
                                                                       ArioGrammars.DocumentAmountFact.Name,
                                                                       ArioGrammars.DocumentAmountFact.AmountField);
      
      var amountFact = amountFacts.FirstOrDefault();
      if (amountFact == null)
        return recognizedAmount;
      
      recognizedAmount.Fact = amountFact;
      
      var totalAmount = Commons.PublicFunctions.Module.GetFieldNumericalValue(amountFact,
                                                                              ArioGrammars.DocumentAmountFact.AmountField);
      if (!totalAmount.HasValue || totalAmount.Value <= 0)
        return recognizedAmount;
      
      recognizedAmount.HasValue = true;
      recognizedAmount.Amount = totalAmount.Value;
      recognizedAmount.Probability = Commons.PublicFunctions.Module.GetFieldProbability(amountFact,
                                                                                        ArioGrammars.DocumentAmountFact.AmountField);
      
      // Если в сумме больше 2 знаков после запятой, то обрезать лишние разряды,
      // иначе пометить, что результату извлечения можно доверять.
      var roundedAmount = Math.Round(totalAmount.Value, 2);
      var amountClipping = roundedAmount - totalAmount.Value != 0.0;
      if (amountClipping)
      {
        recognizedAmount.Amount = roundedAmount;
        recognizedAmount.Probability = Module.PropertyProbabilityLevels.UpperMiddle;
      }
      
      return recognizedAmount;
    }
    
    /// <summary>
    /// Распознать валюту.
    /// </summary>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <returns>Результаты распознавания валюты.</returns>
    [Public]
    public virtual IRecognizedCurrency GetRecognizedCurrency(List<IArioFact> facts)
    {
      var recognizedCurrency = RecognizedCurrency.Create();
      
      var currencyFact = Commons.PublicFunctions.Module.GetOrderedFacts(facts,
                                                                        ArioGrammars.DocumentAmountFact.Name,
                                                                        ArioGrammars.DocumentAmountFact.CurrencyField)
        .FirstOrDefault();
      if (currencyFact == null)
        return recognizedCurrency;
      
      var currencyCode = Commons.PublicFunctions.Module.GetFieldValue(currencyFact,
                                                                      ArioGrammars.DocumentAmountFact.CurrencyField);
      int resCurrencyCode;
      if (!int.TryParse(currencyCode, out resCurrencyCode))
        return recognizedCurrency;
      
      var currency = Commons.Currencies.GetAll(x => x.NumericCode == currencyCode).OrderBy(x => x.Status).FirstOrDefault();
      if ((currency == null) || (currency.Status == CoreEntities.DatabookEntry.Status.Closed))
        return recognizedCurrency;
      
      recognizedCurrency.HasValue = true;
      recognizedCurrency.Probability = Commons.PublicFunctions.Module.GetFieldProbability(currencyFact,
                                                                                          ArioGrammars.DocumentAmountFact.CurrencyField);
      recognizedCurrency.Currency = currency;
      recognizedCurrency.Fact = currencyFact;
      
      return recognizedCurrency;
    }
    
    #endregion
    
    #region Получение ведущего документа
    
    /// <summary>
    /// Получить ведущий договорной документ по номеру и дате из факта.
    /// </summary>
    /// <param name="fact">Факт.</param>
    /// <param name="leadingDocPropertyName">Имя связанного свойства.</param>
    /// <param name="counterparty">Контрагент.</param>
    /// <param name="counterpartyPropertyName">Имя свойства, связанного с контрагентом.</param>
    /// <returns>Структура, содержащая ведущий договорной документ, факт и признак доверия.</returns>
    [Public]
    public virtual IRecognizedContract GetLeadingContractualDocument(IArioFact fact,
                                                                     string leadingDocPropertyName,
                                                                     Parties.ICounterparty counterparty,
                                                                     string counterpartyPropertyName)
    {
      var result = RecognizedContract.Create(Contracts.ContractualDocuments.Null, fact, null);
      if (fact == null)
        return result;

      if (!string.IsNullOrEmpty(leadingDocPropertyName) && counterparty != null)
      {
        result = this.GetPreviousContractRecognitionResults(fact, leadingDocPropertyName, counterparty.Id.ToString(),
                                                            counterpartyPropertyName);
        if (result.Contract != null)
          return result;
      }
      
      if (fact == null)
        return result;
      
      var docDate = Commons.PublicFunctions.Module.GetFieldDateTimeValue(fact, ArioGrammars.FinancialDocumentFact.DocumentBaseDateField);
      var number = Commons.PublicFunctions.Module.GetFieldValue(fact, ArioGrammars.FinancialDocumentFact.DocumentBaseNumberField);
      
      if (string.IsNullOrWhiteSpace(number))
        return result;
      
      var contracts = Contracts.ContractualDocuments.GetAll(x => x.RegistrationNumber == number &&
                                                            x.RegistrationDate == docDate &&
                                                            (counterparty == null || x.Counterparty.Equals(counterparty)));
      
      result.Contract = contracts.FirstOrDefault();
      if (contracts.Count() == 1)
        result.Probability = Module.PropertyProbabilityLevels.Max;
      
      if (contracts.Count() > 1)
        result.Probability = Module.PropertyProbabilityLevels.LowerMiddle;
      
      return result;
    }
    
    /// <summary>
    /// Получить результаты предшествующего распознавания договорного документа по факту, идентичному переданному,
    /// с фильтрацией по контрагенту.
    /// </summary>
    /// <param name="fact">Факт Ario.</param>
    /// <param name="propertyName">Имя связанного свойства.</param>
    /// <param name="filterPropertyValue">Значение свойства для дополнительной фильтрации результатов распознавания сущности.</param>
    /// <param name="filterPropertyName">Имя свойства для дополнительной фильтрации результатов распознавания сущности.</param>
    /// <returns>Структура, содержащая договорной документ, подтвержденный пользователем, факт и вероятность.</returns>
    [Public]
    public virtual IRecognizedContract GetPreviousContractRecognitionResults(IArioFact fact,
                                                                             string propertyName,
                                                                             string filterPropertyValue,
                                                                             string filterPropertyName)
    {
      var result = RecognizedContract.Create(Contracts.ContractualDocuments.Null, fact, null);
      var contractRecognitionInfo = Commons.PublicFunctions.Module.GetPreviousPropertyRecognitionResults(fact, propertyName,
                                                                                                         filterPropertyValue,
                                                                                                         filterPropertyName);
      if (contractRecognitionInfo == null)
        return result;
      
      int docId;
      if (!int.TryParse(contractRecognitionInfo.VerifiedValue, out docId))
        return result;
      
      var contract = Contracts.ContractualDocuments.GetAll(x => x.Id == docId).FirstOrDefault();
      if (contract != null)
      {
        result.Contract = contract;
        result.Probability = contractRecognitionInfo.Probability;
      }
      return result;
    }

    #endregion
    
    #region Получение контрагентов, контактов, подписантов, НОР
    
    /// <summary>
    /// Определить направление документа, НОР и КА у счет-фактуры.
    /// </summary>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <param name="responsible">Ответственный.</param>
    /// <returns>Результат подбора сторон сделки для документа.</returns>
    /// <remarks>Если НОР выступает продавцом, то счет-фактура - исходящая, иначе - входящая.</remarks>
    [Public]
    public virtual IRecognizedDocumentParties GetRecognizedTaxInvoiceParties(List<IArioFact> facts,
                                                                             IEmployee responsible)
    {
      var responsibleEmployeeBusinessUnit = Company.PublicFunctions.BusinessUnit.Remote.GetBusinessUnit(responsible);
      var responsibleEmployeePersonalSettings = Docflow.PublicFunctions.PersonalSetting.GetPersonalSettings(responsible);
      var responsibleEmployeePersonalSettingsBusinessUnit = responsibleEmployeePersonalSettings != null
        ? responsibleEmployeePersonalSettings.BusinessUnit
        : Company.BusinessUnits.Null;
      var document = AccountingDocumentBases.Null;
      var props = AccountingDocumentBases.Info.Properties;

      // Определить направление документа, НОР и КА.
      // Если НОР выступает продавцом, то создаем исходящую счет-фактуру, иначе - входящую.
      var arioCounterpartyTypes = new List<string>();
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Shipper);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Consignee);

      var recognizedCounterparties = this.GetRecognizedAccountingDocumentCounterparties(facts, arioCounterpartyTypes);
      var seller = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller).FirstOrDefault() ??
        recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Shipper).FirstOrDefault();
      var buyer = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer).FirstOrDefault() ??
        recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Consignee).FirstOrDefault();

      var buyerIsBusinessUnit = buyer != null && buyer.BusinessUnit != null;
      var sellerIsBusinessUnit = seller != null && seller.BusinessUnit != null;
      var recognizedDocumentParties = RecognizedDocumentParties.Create();
      if (buyerIsBusinessUnit && sellerIsBusinessUnit)
      {
        // Мультинорность. Уточнить НОР по ответственному.
        if (Equals(seller.BusinessUnit, responsibleEmployeePersonalSettingsBusinessUnit) ||
            Equals(seller.BusinessUnit, responsibleEmployeeBusinessUnit))
        {
          // Исходящий документ.
          recognizedDocumentParties.IsDocumentOutgoing = true;
          recognizedDocumentParties.Counterparty = buyer;
          recognizedDocumentParties.BusinessUnit = seller;
        }
        else
        {
          // Входящий документ.
          recognizedDocumentParties.IsDocumentOutgoing = false;
          recognizedDocumentParties.Counterparty = seller;
          recognizedDocumentParties.BusinessUnit = buyer;
        }
      }
      else if (buyerIsBusinessUnit)
      {
        // Входящий документ.
        recognizedDocumentParties.IsDocumentOutgoing = false;
        recognizedDocumentParties.Counterparty = seller;
        recognizedDocumentParties.BusinessUnit = buyer;
      }
      else if (sellerIsBusinessUnit)
      {
        // Исходящий документ.
        recognizedDocumentParties.IsDocumentOutgoing = true;
        recognizedDocumentParties.Counterparty = buyer;
        recognizedDocumentParties.BusinessUnit = seller;
      }
      else
      {
        // НОР не найдена по фактам - НОР будет взята по ответственному.
        if (buyer != null && buyer.Counterparty != null && (seller == null || seller.Counterparty == null))
        {
          // Исходящий документ, потому что buyer - контрагент, а другой информации нет.
          recognizedDocumentParties.IsDocumentOutgoing = true;
          recognizedDocumentParties.Counterparty = buyer;
        }
        else
        {
          // Входящий документ.
          recognizedDocumentParties.IsDocumentOutgoing = false;
          recognizedDocumentParties.Counterparty = seller;
        }
      }

      recognizedDocumentParties.ResponsibleEmployeeBusinessUnit = Docflow.PublicFunctions.Module.GetDefaultBusinessUnit(responsible);

      return recognizedDocumentParties;
    }

    /// <summary>
    /// Подобрать участников сделки (НОР и контрагент).
    /// </summary>
    /// <param name="buyer">Список фактов с данными о контрагенте. Тип контрагента - покупатель.</param>
    /// <param name="seller">Список фактов с данными о контрагенте. Тип контрагента - продавец.</param>
    /// <param name="responsibleEmployee">Ответственный сотрудник.</param>
    /// <returns>НОР и контрагент.</returns>
    [Public]
    public virtual IRecognizedDocumentParties GetDocumentParties(IRecognizedCounterparty buyer,
                                                                 IRecognizedCounterparty seller,
                                                                 Company.IEmployee responsibleEmployee)
    {
      IRecognizedCounterparty counterparty = null;
      IRecognizedCounterparty businessUnit = null;

      // НОР.
      if (buyer != null)
        businessUnit = buyer;

      // Контрагент.
      if (seller != null && seller.Counterparty != null)
        counterparty = seller;

      var responsibleEmployeeBusinessUnit = Docflow.PublicFunctions.Module.GetDefaultBusinessUnit(responsibleEmployee);

      return RecognizedDocumentParties.Create(businessUnit, counterparty, responsibleEmployeeBusinessUnit, null);
    }

    /// <summary>
    /// Подобрать участников сделки (НОР и контрагент).
    /// </summary>
    /// <param name="buyer">Список фактов с данными о контрагенте. Тип контрагента - покупатель.</param>
    /// <param name="seller">Список фактов с данными о контрагенте. Тип контрагента - продавец.</param>
    /// <param name="nonType">Список фактов с данными о контрагенте. Тип контрагента не заполнен.</param>
    /// <param name="responsibleEmployee">Ответственный сотрудник.</param>
    /// <returns>НОР и контрагент.</returns>
    [Public]
    public virtual IRecognizedDocumentParties GetDocumentParties(IRecognizedCounterparty buyer,
                                                                 IRecognizedCounterparty seller,
                                                                 List<IRecognizedCounterparty> nonType,
                                                                 Company.IEmployee responsibleEmployee)
    {
      IRecognizedCounterparty counterparty = null;
      IRecognizedCounterparty businessUnit = null;
      var responsibleEmployeeBusinessUnit = Company.PublicFunctions.BusinessUnit.Remote.GetBusinessUnit(responsibleEmployee);
      var responsibleEmployeePersonalSettings = Docflow.PublicFunctions.PersonalSetting.GetPersonalSettings(responsibleEmployee);
      var responsibleEmployeePersonalSettingsBusinessUnit = responsibleEmployeePersonalSettings != null
        ? responsibleEmployeePersonalSettings.BusinessUnit
        : Company.BusinessUnits.Null;

      // НОР.
      if (buyer != null)
      {
        // НОР по факту с типом BUYER.
        businessUnit = buyer;
      }
      else
      {
        // НОР по факту без типа.
        var nonTypeBusinessUnits = nonType.Where(m => m.BusinessUnit != null);

        // Уточнить НОР по ответственному.
        if (nonTypeBusinessUnits.Count() > 1)
        {
          // Если в персональных настройках ответственного указана НОР.
          if (responsibleEmployeePersonalSettingsBusinessUnit != null)
            businessUnit = nonTypeBusinessUnits.Where(m => Equals(m.BusinessUnit, responsibleEmployeePersonalSettingsBusinessUnit)).FirstOrDefault();

          // НОР не определилась по персональным настройкам ответственного.
          if (businessUnit == null)
            businessUnit = nonTypeBusinessUnits.Where(m => Equals(m.BusinessUnit, responsibleEmployeeBusinessUnit)).FirstOrDefault();
        }
        // Если не удалось уточнить НОР по ответственному, то берем первую наиболее вероятную.
        if (businessUnit == null)
          businessUnit = nonTypeBusinessUnits.OrderByDescending(x => x.BusinessUnitProbability).FirstOrDefault();
      }

      // Контрагент по типу.
      if (seller != null && seller.Counterparty != null)
      {
        // Контрагент по факту с типом SELLER.
        counterparty = seller;
      }
      else
      {
        // Берем наиболее вероятного контрагента по факту без типа. Исключить факт, по которому нашли НОР.
        var nonTypeCounterparties = nonType
          .Where(m => m.Counterparty != null)
          .Where(m => !Equals(m, businessUnit));
        counterparty = nonTypeCounterparties.OrderByDescending(x => x.CounterpartyProbability).FirstOrDefault();
      }

      // В качестве НОР ответственного вернуть НОР из персональных настроек, если она указана.
      return responsibleEmployeePersonalSettingsBusinessUnit != null
        ? RecognizedDocumentParties.Create(businessUnit, counterparty, responsibleEmployeePersonalSettingsBusinessUnit, null)
        : RecognizedDocumentParties.Create(businessUnit, counterparty, responsibleEmployeeBusinessUnit, null);
    }

    /// <summary>
    /// Подобрать по факту контрагента и НОР.
    /// </summary>
    /// <param name="allFacts">Факты.</param>
    /// <param name="arioCounterpartyTypes">Типы фактов контрагентов.</param>
    /// <returns>Наши организации и контрагенты, найденные по фактам.</returns>
    [Public]
    public virtual List<IRecognizedCounterparty> GetRecognizedAccountingDocumentCounterparties(List<IArioFact> allFacts,
                                                                                               List<string> arioCounterpartyTypes)
    {
      var props = AccountingDocumentBases.Info.Properties;
      var recognizedCounterparties = new List<IRecognizedCounterparty>();
      var facts = allFacts.Where(f => f.Name == ArioGrammars.CounterpartyFact.Name);
      
      foreach (var fact in facts)
      {
        var businessUnit = Company.BusinessUnits.Null;
        var counterparty = Parties.Counterparties.Null;
        double? businessUnitProbability = 0.0;
        double? counterpartyProbability = 0.0;

        // Если для свойства BusinessUnit по факту существует верифицированное ранее значение, то вернуть его.
        var verifiedBusinessUnit = this.GetPreviousBusinessUnitRecognitionResults(fact, props.BusinessUnit.Name);
        if (verifiedBusinessUnit != null)
        {
          businessUnit = verifiedBusinessUnit.BusinessUnit;
          businessUnitProbability = verifiedBusinessUnit.BusinessUnitProbability;
        }

        // Если для свойства Counterparty по факту существует верифицированное ранее значение, то вернуть его.
        var verifiedCounterparty = this.GetPreviousCounterpartyRecognitionResults(fact, props.Counterparty.Name);
        if (verifiedCounterparty != null)
        {
          counterparty = verifiedCounterparty.Counterparty;
          counterpartyProbability = verifiedCounterparty.CounterpartyProbability;
        }

        // Поиск по ИНН/КПП.
        var tin = Commons.PublicFunctions.Module.GetFieldValue(fact, ArioGrammars.CounterpartyFact.TinField);
        var trrc = Commons.PublicFunctions.Module.GetFieldValue(fact, ArioGrammars.CounterpartyFact.TrrcField);
        
        // Получить обобщенную вероятность по полям ИНН, КПП.
        var tinTrrcFieldsProbability = this.GetTinTrrcFieldsProbability(fact,
                                                                        ArioGrammars.CounterpartyFact.TinField,
                                                                        ArioGrammars.CounterpartyFact.TrrcField);
        if (businessUnit == null)
        {
          var businessUnits = Company.PublicFunctions.BusinessUnit.GetBusinessUnits(tin, trrc);
          
          // Если подобрано несколько организаций, то вероятность равномерно делится между всеми.
          businessUnitProbability = businessUnits.Any() ? tinTrrcFieldsProbability / businessUnits.Count() : 0.0;
          businessUnit = businessUnits.FirstOrDefault();
        }
        if (counterparty == null)
        {
          var counterparties = Parties.PublicFunctions.Counterparty.GetDuplicateCounterparties(tin, trrc, string.Empty, true);
          
          // Если подобрано несколько организаций, то вероятность равномерно делится между всеми.
          counterpartyProbability = counterparties.Any() ? tinTrrcFieldsProbability / counterparties.Count() : 0.0;

          // Получить запись по точному совпадению по ИНН/КПП.
          if (!string.IsNullOrWhiteSpace(trrc))
            counterparty = counterparties.FirstOrDefault(x => Parties.CompanyBases.Is(x) && Parties.CompanyBases.As(x).TRRC == trrc);
          
          // Получить запись с совпадением по ИНН, если не найдено по точному совпадению ИНН/КПП.
          if (counterparty == null)
            counterparty = counterparties.FirstOrDefault();
        }

        if (counterparty != null || businessUnit != null)
        {
          var recognizedCounterparty = RecognizedCounterparty.Create(businessUnit, counterparty, fact,
                                                                     Commons.PublicFunctions.Module.GetFieldValue(fact, ArioGrammars.CounterpartyFact.CounterpartyTypeField),
                                                                     businessUnitProbability, counterpartyProbability);
          recognizedCounterparties.Add(recognizedCounterparty);
          continue;
        }

        // Если не нашли по ИНН/КПП, то ищем по наименованию.
        var name = GetCounterpartyName(fact, ArioGrammars.CounterpartyFact.NameField,
                                       ArioGrammars.CounterpartyFact.LegalFormField);
        
        var counterpartiesByName = Parties.Counterparties.GetAll()
          .Where(x => x.Status != Sungero.CoreEntities.DatabookEntry.Status.Closed &&
                 x.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
        
        var businessUnitsByName = Company.BusinessUnits.GetAll()
          .Where(x => x.Status != Sungero.CoreEntities.DatabookEntry.Status.Closed &&
                 x.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
        
        if (counterpartiesByName.Any() || businessUnitsByName.Any())
        {
          // Получить обобщенную вероятность по полям Наименование и ОПФ.
          var nameFieldsProbability = this.GetNameFieldsProbability(fact, ArioGrammars.CounterpartyFact.NameField,
                                                                    ArioGrammars.CounterpartyFact.LegalFormField);
          
          // Если для одного факта подобрано несколько организаций,
          // то вероятность того, что организация подобрана корректно, равномерно делится между всеми.
          businessUnitProbability = businessUnitsByName.Any() ? nameFieldsProbability / businessUnitsByName.Count() : 0.0;
          counterpartyProbability = counterpartiesByName.Any() ? nameFieldsProbability / counterpartiesByName.Count() : 0.0;
          
          var recognizedCounterparty = RecognizedCounterparty.Create(businessUnitsByName.FirstOrDefault(),
                                                                     counterpartiesByName.FirstOrDefault(), fact,
                                                                     Commons.PublicFunctions.Module.GetFieldValue(fact, ArioGrammars.CounterpartyFact.CounterpartyTypeField),
                                                                     businessUnitProbability, counterpartyProbability);
          recognizedCounterparties.Add(recognizedCounterparty);
        }
      }

      return recognizedCounterparties;
    }

    /// <summary>
    /// Поиск контрагента по извлеченным фактам.
    /// </summary>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <param name="propertyName">Имя свойства.</param>
    /// <param name="counterpartyFactName">Наименование факта с контрагентом.</param>
    /// <param name="counterpartyNameField">Поле с наименованием контрагента.</param>
    /// <param name="counterpartyLegalFormField">Поле с юридической формой контрагента.</param>
    /// <returns>Контрагент.</returns>
    /// <remarks>Метод возвращает первое найденное по фактам верифицированное значение.
    /// Метод возвращает первого найденного по наименованию контрагента, если нет фактов, содержащих поле ИНН.
    /// Метод возвращает контрагента, подобранного по ИНН/КПП, если он подобран один.
    /// Метод возвращает первого из подобранных по имени с пустыми ИНН и КПП, если по ИНН/КПП не подобралось ни одного контрагента.
    /// Результат уточняется по наименованию, если по ИНН/КПП подобралось более одного контрагента.
    /// - Если ни один не подходит по имени, то возвращается первый подходящий по ИНН/КПП с минимальной вероятностью.
    /// - Если по имени подходит один, то он и возвращается.
    /// - Если по имени подходит несколько, то возвращается первый с минимальной вероятностью.</remarks>
    [Public]
    public virtual IRecognizedCounterparty GetRecognizedCounterparty(List<IArioFact> facts,
                                                                     string propertyName,
                                                                     string counterpartyFactName,
                                                                     string counterpartyNameField,
                                                                     string counterpartyLegalFormField)
    {
      var actualCounterparties = Sungero.Parties.Counterparties.GetAll()
        .Where(x => x.Status != Sungero.CoreEntities.DatabookEntry.Status.Closed);

      // Получить все факты контрагентов, содержащие наименование (counterpartyNameField),
      // отсортированные по уменьшению Probability поля факта.
      var foundByName = new List<IRecognizedCounterparty>();
      var counterpartyNames = new List<string>();
      var counterpartyNameFacts = Commons.PublicFunctions.Module.GetFacts(facts,
                                                                          counterpartyFactName,
                                                                          counterpartyNameField)
        .OrderByDescending(x => x.Fields.First(f => f.Name == counterpartyNameField).Probability);
      
      // Подобрать контрагентов, подходящих по имени для переданных фактов.
      var nameFieldsProbability = 0.0;
      foreach (var fact in counterpartyNameFacts)
      {
        // Если для свойства propertyName по факту существует верифицированное ранее значение, то вернуть его.
        // Вероятность соответствует probability верифицированного ранее значения.
        var verifiedCounterparty = this.GetPreviousCounterpartyRecognitionResults(fact, propertyName);
        if (verifiedCounterparty != null)
          return verifiedCounterparty;
        
        var name = GetCounterpartyName(fact, counterpartyNameField, counterpartyLegalFormField);
        counterpartyNames.Add(name);
        
        // Получить обобщенную вероятность по полям Наименование и ОПФ.
        nameFieldsProbability = this.GetNameFieldsProbability(fact, counterpartyNameField, counterpartyLegalFormField);
        
        var counterparties = actualCounterparties.Where(x => x.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
        foreach (var counterparty in counterparties)
        {
          var recognizedCounterparty = RecognizedCounterparty.Create();
          recognizedCounterparty.Counterparty = counterparty;
          recognizedCounterparty.Fact = fact;
          // Если для одного факта подобрано несколько контрагентов,
          // то вероятность того, что контрагент подобран корректно, равномерно делится между всеми.
          recognizedCounterparty.CounterpartyProbability = nameFieldsProbability / counterparties.Count();
          foundByName.Add(recognizedCounterparty);
        }
      }

      // Поиск по ИНН/КПП.
      // Отфильтровать факты, у которых TinIsValid = "false".
      var counterpartyTINFacts = Commons.PublicFunctions.Module.GetFacts(facts,
                                                                         ArioGrammars.CounterpartyFact.Name,
                                                                         ArioGrammars.CounterpartyFact.TinField)
        .Where(x => Commons.PublicFunctions.Module.GetFieldValue(x, ArioGrammars.CounterpartyFact.TinIsValidField) == "true")
        .OrderByDescending(x => x.Fields.First(f => f.Name == ArioGrammars.CounterpartyFact.TinField).Probability);

      // Если нет фактов, содержащих поле ИНН, то вернуть первого контрагента по наименованию.
      if (!counterpartyTINFacts.Any())
        return foundByName.FirstOrDefault();
      
      var foundByTin = new List<IRecognizedCounterparty>();
      foreach (var fact in counterpartyTINFacts)
      {
        // Если для свойства propertyName по факту существует верифицированное ранее значение, то вернуть его.
        // Вероятность соответствует probability верифицированного ранее значения.
        var verifiedCounterparty = this.GetPreviousCounterpartyRecognitionResults(fact, propertyName);
        if (verifiedCounterparty != null)
          return verifiedCounterparty;
        
        var tinFieldValue = Commons.PublicFunctions.Module.GetFieldValue(fact, ArioGrammars.CounterpartyFact.TinField);
        var trrcFieldValue = Commons.PublicFunctions.Module.GetFieldValue(fact, ArioGrammars.CounterpartyFact.TrrcField);
        var counterparties = Parties.PublicFunctions.Counterparty.GetDuplicateCounterparties(tinFieldValue,
                                                                                             trrcFieldValue,
                                                                                             string.Empty,
                                                                                             true);
        var tinTrrcFieldsProbability = this.GetTinTrrcFieldsProbability(fact,
                                                                        ArioGrammars.CounterpartyFact.TinField,
                                                                        ArioGrammars.CounterpartyFact.TrrcField);
        foreach (var counterparty in counterparties)
        {
          var recognizedCounterparty = RecognizedCounterparty.Create();
          recognizedCounterparty.Counterparty = counterparty;
          recognizedCounterparty.Fact = fact;
          // Если для одного факта подобрано несколько контрагентов,
          // то вероятность того, что контрагент подобран корректно, равномерно делится между всеми.
          recognizedCounterparty.CounterpartyProbability = tinTrrcFieldsProbability / counterparties.Count();
          foundByTin.Add(recognizedCounterparty);
        }
      }

      // Не найдены контрагенты по ИНН\КПП. Искать по наименованию в контрагентах с пустыми ИНН/КПП.
      if (foundByTin.Count == 0)
        return foundByName
          .Where(x => string.IsNullOrEmpty(x.Counterparty.TIN))
          .Where(x => !Sungero.Parties.CompanyBases.Is(x.Counterparty) ||
                 string.IsNullOrEmpty(Sungero.Parties.CompanyBases.As(x.Counterparty).TRRC))
          .FirstOrDefault();

      // Найден один контрагент по ИНН\КПП.
      if (foundByTin.Count == 1)
        return foundByTin.First();

      // Найдено несколько контрагентов по ИНН\КПП. Уточнить поиск по наименованию.
      var specifiedByName = foundByTin
        .Where(x => counterpartyNames.Any(name => x.Counterparty.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase)))
        .ToList();
      
      if (specifiedByName.Count == 0)
        return foundByTin.FirstOrDefault();
      
      // Если при дополнительном поиске по наименованию подобран один или более контрагентов,
      var resultCounterparty = specifiedByName.FirstOrDefault();
      var tinTrrcFact = resultCounterparty.Fact;
      // Найти факт с наименованием и ОПФ, если эти поля не содержит факт с ИНН и КПП.
      IArioFact nameLegalFormFact = null;
      if (Commons.PublicFunctions.Module.GetField(tinTrrcFact, counterpartyNameField) == null)
      {
        var recognizedByName = foundByName.FirstOrDefault(x => Equals(x.Counterparty, resultCounterparty.Counterparty));
        if (recognizedByName != null)
          nameLegalFormFact = recognizedByName.Fact;
      }
      else
      {
        nameLegalFormFact = tinTrrcFact;
      }
      
      var aggregateFieldsProbability = this.GetNameTinTrrcFieldsProbability(tinTrrcFact,
                                                                            nameLegalFormFact,
                                                                            ArioGrammars.CounterpartyFact.TinField,
                                                                            ArioGrammars.CounterpartyFact.TrrcField,
                                                                            counterpartyNameField,
                                                                            counterpartyLegalFormField);
      
      // Если для одного факта подобрано несколько контрагентов,
      // то вероятность того, что контрагент подобран корректно, равномерно делится между всеми.
      resultCounterparty.CounterpartyProbability = aggregateFieldsProbability / specifiedByName.Count();
      return resultCounterparty;
    }

    /// <summary>
    /// Получение списка НОР по извлеченным фактам.
    /// </summary>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <param name="counterpartyFactName">Наименование факта с контрагентом.</param>
    /// <param name="counterpartyNameField">Поле с наименованием контрагента.</param>
    /// <param name="counterpartyLegalFormField">Поле с юридической формой контрагента.</param>
    /// <returns>Список НОР и соответствующих им фактов.</returns>
    [Public]
    public virtual List<IRecognizedCounterparty> GetRecognizedBusinessUnits(List<IArioFact> facts,
                                                                            string counterpartyFactName,
                                                                            string counterpartyNameField,
                                                                            string counterpartyLegalFormField)
    {
      // Получить факты с наименованиями организаций.
      var businessUnitsByName = new List<IRecognizedCounterparty>();
      var nameFieldsProbability = 0.0;
      var correspondentNameFacts = Commons.PublicFunctions.Module.GetFacts(facts, counterpartyFactName, counterpartyNameField)
        .OrderByDescending(x => x.Fields.First(f => f.Name == counterpartyNameField).Probability);
      
      foreach (var fact in correspondentNameFacts)
      {
        var name = GetCounterpartyName(fact, counterpartyNameField, counterpartyLegalFormField);
        
        // Получить обобщенную вероятность по полям Наименование и ОПФ.
        nameFieldsProbability = this.GetNameFieldsProbability(fact,
                                                              counterpartyNameField,
                                                              counterpartyLegalFormField);
        
        var businessUnits = BusinessUnits.GetAll()
          .Where(x => x.Status != CoreEntities.DatabookEntry.Status.Closed)
          .Where(x => x.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
        
        foreach (var businessUnit in businessUnits)
        {
          var recognizedBusinessUnit = RecognizedCounterparty.Create();
          recognizedBusinessUnit.BusinessUnit = businessUnit;
          recognizedBusinessUnit.Fact = fact;
          recognizedBusinessUnit.BusinessUnitProbability = nameFieldsProbability / businessUnits.Count();
          
          businessUnitsByName.Add(recognizedBusinessUnit);
        }
      }
      
      // Если факты с ИНН/КПП не найдены, то вернуть факты с наименованиями организаций.
      // Отфильтровать факты, у которых TinIsValid = "false".
      var correspondentTinFacts = Commons.PublicFunctions.Module.GetFacts(facts,
                                                                          ArioGrammars.CounterpartyFact.Name,
                                                                          ArioGrammars.CounterpartyFact.TinField)
        .Where(x => Commons.PublicFunctions.Module.GetFieldValue(x, ArioGrammars.CounterpartyFact.TinIsValidField) == "true")
        .OrderByDescending(x => x.Fields.First(f => f.Name == ArioGrammars.CounterpartyFact.TinField).Probability);
      
      if (!correspondentTinFacts.Any())
        return businessUnitsByName;

      // Поиск по ИНН/КПП.
      var businessUnitsByTin = new List<IRecognizedCounterparty>();
      foreach (var fact in correspondentTinFacts)
      {
        var tinFieldValue = Commons.PublicFunctions.Module.GetFieldValue(fact, ArioGrammars.CounterpartyFact.TinField);
        var trrcFieldValue = Commons.PublicFunctions.Module.GetFieldValue(fact, ArioGrammars.CounterpartyFact.TrrcField);
        var businessUnits = Company.PublicFunctions.BusinessUnit.GetBusinessUnits(tinFieldValue, trrcFieldValue);
        var tinTrrcFieldsProbability = this.GetTinTrrcFieldsProbability(fact,
                                                                        ArioGrammars.CounterpartyFact.TinField,
                                                                        ArioGrammars.CounterpartyFact.TrrcField);
        
        foreach (var businessUnit in businessUnits)
        {
          var recognizedBusinessUnit = RecognizedCounterparty.Create();
          recognizedBusinessUnit.BusinessUnit = businessUnit;
          recognizedBusinessUnit.Fact = fact;
          recognizedBusinessUnit.BusinessUnitProbability = tinTrrcFieldsProbability / businessUnits.Count();
          
          businessUnitsByTin.Add(recognizedBusinessUnit);
        }
      }
      
      // Найдено по ИНН/КПП.
      if (businessUnitsByTin.Any())
        return businessUnitsByTin;
      
      // Если по ИНН/КПП не найдено НОР, то искать по наименованию в НОР с пустыми ИНН/КПП.
      if (businessUnitsByName.Any())
        return businessUnitsByName.Where(x => string.IsNullOrEmpty(x.BusinessUnit.TIN) && string.IsNullOrEmpty(x.BusinessUnit.TRRC)).ToList();
      
      return businessUnitsByName;
    }
    
    /// <summary>
    /// Получить обобщенную вероятность по полям Наименование и ОПФ.
    /// </summary>
    /// <param name="fact">Факт с наименованием и ОПФ организации.</param>
    /// <param name="counterpartyNameField">Наименование поля с наименованием организации.</param>
    /// <param name="counterpartyLegalFormField">Наименование поля с ОПФ организации.</param>
    /// <returns>Вероятность.</returns>
    [Public]
    public virtual double GetNameFieldsProbability(IArioFact fact,
                                                   string counterpartyNameField,
                                                   string counterpartyLegalFormField)
    {
      // Получить обобщенную вероятность по полям Наименование и ОПФ.
      // Вес вероятности наименования - 0.8, вес вероятности ОПФ - 0.2.
      var weightedFields = new Dictionary<IArioFactField, double>();
      var nameField = Commons.PublicFunctions.Module.GetField(fact, counterpartyNameField);
      if (nameField != null)
        weightedFields.Add(nameField, 0.8);
      var legalFormField = Commons.PublicFunctions.Module.GetField(fact, counterpartyLegalFormField);
      if (legalFormField != null)
        weightedFields.Add(legalFormField, 0.2);
      
      return Commons.PublicFunctions.Module.GetAggregateFieldsProbability(weightedFields);
    }
    
    /// <summary>
    /// Получить обобщенную вероятность по полям ИНН и КПП.
    /// </summary>
    /// <param name="fact">Факт с ИНН и КПП организации.</param>
    /// <param name="tinNameField">Наименование поля с ИНН организации.</param>
    /// <param name="trrcNameField">Наименование поля с КПП организации.</param>
    /// <returns>Вероятность.</returns>
    [Public]
    public virtual double GetTinTrrcFieldsProbability(IArioFact fact,
                                                      string tinNameField,
                                                      string trrcNameField)
    {
      // Получить обобщенную вероятность по полям ИНН и КПП.
      // Вес вероятности ИНН - 0.65, вес вероятности КПП - 0.35.
      var weightedFields = new Dictionary<IArioFactField, double>();
      var tinField = Commons.PublicFunctions.Module.GetField(fact, tinNameField);
      if (tinField != null)
        weightedFields.Add(tinField, 0.65);
      var trrcField = Commons.PublicFunctions.Module.GetField(fact, trrcNameField);
      if (trrcField != null)
        weightedFields.Add(trrcField, 0.35);
      
      return Commons.PublicFunctions.Module.GetAggregateFieldsProbability(weightedFields);
    }
    
    /// <summary>
    /// Получить обобщенную вероятность по полям Наименование, ОПФ, ИНН, КПП.
    /// </summary>
    /// <param name="tinTrrcFact">Факт с ИНН, КПП организации.</param>
    /// <param name="nameLegalFormFact">Факт с наименованием, ОПФ организации.</param>
    /// <param name="tinNameField">Наименование поля с ИНН организации.</param>
    /// <param name="trrcNameField">Наименование поля с КПП организации.</param>
    /// <param name="counterpartyNameField">Наименование поля с наименованием организации.</param>
    /// <param name="counterpartyLegalFormField">Наименование поля с ОПФ организации.</param>
    /// <returns>Вероятность.</returns>
    [Public]
    public virtual double GetNameTinTrrcFieldsProbability(IArioFact tinTrrcFact,
                                                          IArioFact nameLegalFormFact,
                                                          string tinNameField,
                                                          string trrcNameField,
                                                          string counterpartyNameField,
                                                          string counterpartyLegalFormField)
    {
      // Получить обобщенную вероятность по полям ИНН, КПП, Наименование, ОПФ.
      // Вес вероятности Наименования - 0.4,
      // вес вероятности ОПФ - 0.1,
      // вес вероятности ИНН - 0.4,
      // вес вероятности КПП - 0.1.
      var weightedFields = new Dictionary<IArioFactField, double>();
      var tinField = Commons.PublicFunctions.Module.GetField(tinTrrcFact, tinNameField);
      if (tinField != null)
        weightedFields.Add(tinField, 0.4);
      var trrcField = Commons.PublicFunctions.Module.GetField(tinTrrcFact, trrcNameField);
      if (trrcField != null)
        weightedFields.Add(trrcField, 0.1);
      var nameField = Commons.PublicFunctions.Module.GetField(nameLegalFormFact, counterpartyNameField);
      if (nameField != null)
        weightedFields.Add(nameField, 0.4);
      var legalFormField = Commons.PublicFunctions.Module.GetField(nameLegalFormFact, counterpartyLegalFormField);
      if (legalFormField != null)
        weightedFields.Add(legalFormField, 0.1);
      
      return Commons.PublicFunctions.Module.GetAggregateFieldsProbability(weightedFields);
    }
    
    /// <summary>
    /// Получить результаты предшествующего распознавания подписанта нашей стороны по факту, идентичному переданному.
    /// </summary>
    /// <param name="fact">Факт Ario.</param>
    /// <param name="propertyName">Имя свойства, связанного с подписантом.</param>
    /// <param name="businessUnit">НОР для фильтрации сотрудников.</param>
    /// <param name="businessUnitPropertyName">Имя связанного свойства НОР.</param>
    /// <returns>Структура, содержащая сотрудника, факт и вероятность.</returns>
    [Public]
    public virtual IRecognizedOfficial GetPreviousOurSignatoryRecognitionResults(IArioFact fact,
                                                                                 string propertyName,
                                                                                 IBusinessUnit businessUnit,
                                                                                 string businessUnitPropertyName)
    {
      var result = RecognizedOfficial.Create(null, null, fact,
                                             Module.PropertyProbabilityLevels.Min);
      
      var employeeRecognitionInfo = businessUnit != null
        ? Commons.PublicFunctions.Module.GetPreviousPropertyRecognitionResults(fact, propertyName, businessUnit.Id.ToString(),
                                                                               businessUnitPropertyName)
        : Commons.PublicFunctions.Module.GetPreviousPropertyRecognitionResults(fact, propertyName);
      
      if (employeeRecognitionInfo == null)
        return result;
      
      int employeeId;
      if (!int.TryParse(employeeRecognitionInfo.VerifiedValue, out employeeId))
        return result;
      
      var employee = Employees.GetAll(x => x.Id == employeeId)
        .Where(x => x.Status == CoreEntities.DatabookEntry.Status.Active).FirstOrDefault();
      
      if (employee != null)
      {
        result.Employee = employee;
        result.Probability = employeeRecognitionInfo.Probability;
      }
      return result;
    }

    /// <summary>
    /// Получить контактное лицо по данным из факта и контрагента.
    /// </summary>
    /// <param name="contactFact">Факт, содержащий сведения о контакте.</param>
    /// <param name="propertyName">Имя связанного свойства.</param>
    /// <param name="counterparty">Контрагент.</param>
    /// <param name="counterpartyPropertyName">Имя связанного свойства контрагента.</param>
    /// <param name="recognizedContactNaming">Полное и краткое ФИО персоны.</param>
    /// <returns>Структура, содержащая контактное лицо, факт и вероятность.</returns>
    [Public]
    public virtual IRecognizedOfficial GetRecognizedContact(IArioFact contactFact,
                                                            string propertyName,
                                                            ICounterparty counterparty,
                                                            string counterpartyPropertyName,
                                                            IRecognizedPersonNaming recognizedContactNaming)
    {
      var signedBy = RecognizedOfficial.Create(null, Sungero.Parties.Contacts.Null, contactFact, Module.PropertyProbabilityLevels.Min);
      
      if (contactFact == null)
        return signedBy;

      // Если для свойства propertyName по факту существует верифицированное ранее значение, то вернуть его.
      signedBy = this.GetPreviousContactRecognitionResults(contactFact, propertyName, counterparty, counterpartyPropertyName);
      if (signedBy.Contact != null)
        return signedBy;
      
      var contacts = new List<IContact>().AsQueryable();
      
      var fullName = recognizedContactNaming.FullName;
      var shortName = recognizedContactNaming.ShortName;
      
      if (!string.IsNullOrWhiteSpace(fullName) || !string.IsNullOrWhiteSpace(shortName))
      {
        contacts = Parties.PublicFunctions.Contact.GetContactsByName(fullName, shortName, counterparty);
        
        if (!contacts.Any())
          contacts = Parties.PublicFunctions.Contact.GetContactsByName(shortName, shortName, counterparty);
      }
      
      contacts = contacts.Where(x => x.Status == CoreEntities.DatabookEntry.Status.Active);
      
      if (!contacts.Any())
        return signedBy;
      
      signedBy.Contact = contacts.FirstOrDefault();
      
      // Если нашли контакты по полному ФИО (Фамилия Имя Отчество), то вероятность максимальная. Иначе ниже среднего.
      // И вероятность зависит от количества найденных контактов.
      signedBy.Probability = string.Equals(signedBy.Contact.Name, fullName, StringComparison.InvariantCultureIgnoreCase) &&
        !string.Equals(fullName, shortName, StringComparison.InvariantCultureIgnoreCase) ?
        Module.PropertyProbabilityLevels.Max / contacts.Count() :
        Module.PropertyProbabilityLevels.LowerMiddle / contacts.Count();
      
      return signedBy;
    }
    
    /// <summary>
    /// Получить полное и краткое ФИО персоны из факта.
    /// </summary>
    /// <param name="personFact">Факт, содержащий сведения о персоне.</param>
    /// <param name="surnameField">Наименование поля с фамилией персоны.</param>
    /// <param name="nameField">Наименование поля с именем персоны.</param>
    /// <param name="patronymicField">Наименование поля с отчеством персоны.</param>
    /// <returns>Полное и краткое ФИО персоны.</returns>
    [Public]
    public virtual IRecognizedPersonNaming GetRecognizedPersonNaming(IArioFact personFact,
                                                                     string surnameField, string nameField,
                                                                     string patronymicField)
    {
      var recognizedPersonNaming = RecognizedPersonNaming.Create();
      
      if (personFact == null)
        return recognizedPersonNaming;
      
      var surname = Commons.PublicFunctions.Module.GetFieldValue(personFact, surnameField);
      var name = Commons.PublicFunctions.Module.GetFieldValue(personFact, nameField);
      var patronymic = Commons.PublicFunctions.Module.GetFieldValue(personFact, patronymicField);
      
      // Собрать полные ФИО из фамилии, имени и отчества.
      var parts = new List<string>();
      
      if (!string.IsNullOrWhiteSpace(surname))
        parts.Add(surname);
      if (!string.IsNullOrWhiteSpace(name))
        parts.Add(name);
      if (!string.IsNullOrWhiteSpace(patronymic))
        parts.Add(patronymic);
      
      recognizedPersonNaming.FullName = string.Join(" ", parts);
      
      // Собрать краткое ФИО.
      var shortName = string.Empty;
      
      // Если 2 из 3 полей пустые, то скорее всего сервис Ario вернул Фамилию И.О. в третье поле.
      if (string.IsNullOrWhiteSpace(surname) && string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(patronymic))
        shortName = patronymic;
      
      if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(patronymic) && !string.IsNullOrWhiteSpace(surname))
        shortName = surname;
      
      if (string.IsNullOrWhiteSpace(surname) && string.IsNullOrWhiteSpace(patronymic) && !string.IsNullOrWhiteSpace(name))
        shortName = name;
      
      if (string.IsNullOrEmpty(shortName) && (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(surname) ||
                                              !string.IsNullOrWhiteSpace(patronymic)))
        shortName = Parties.PublicFunctions.Module.GetSurnameAndInitialsInTenantCulture(name, patronymic, surname);
      
      if (!string.IsNullOrEmpty(shortName))
      {
        var nonBreakingSpace = new string('\u00A0', 1);
        var space = new string('\u0020', 1);
        
        // Короткое имя персоны содержит неразрывный пробел.
        shortName = shortName.Replace(". ", ".").Replace(space, nonBreakingSpace);
      }
      
      recognizedPersonNaming.ShortName = shortName;
      
      return recognizedPersonNaming;
    }
    
    /// <summary>
    /// Получить результаты предшествующего распознавания контактного лица контрагента по факту, идентичному переданному,
    /// с фильтрацией по контрагенту.
    /// </summary>
    /// <param name="fact">Факт Ario.</param>
    /// <param name="propertyName">Имя свойства, связанного с контактом.</param>
    /// <param name="counterparty">Контрагент для дополнительной фильтрации контактных лиц.</param>
    /// <param name="counterpartyPropertyName">Имя связанного свойства контрагента.</param>
    /// <returns>Структура, содержащая контактное лицо, факт и вероятность.</returns>
    [Public]
    public virtual IRecognizedOfficial GetPreviousContactRecognitionResults(IArioFact fact,
                                                                            string propertyName,
                                                                            Parties.ICounterparty counterparty,
                                                                            string counterpartyPropertyName)
    {
      var result = RecognizedOfficial.Create(null, Parties.Contacts.Null, fact,
                                             Module.PropertyProbabilityLevels.Min);
      
      var contactRecognitionInfo = counterparty != null
        ? Commons.PublicFunctions.Module.GetPreviousPropertyRecognitionResults(fact, propertyName, counterparty.Id.ToString(),
                                                                               counterpartyPropertyName)
        : Commons.PublicFunctions.Module.GetPreviousPropertyRecognitionResults(fact, propertyName);
      
      if (contactRecognitionInfo == null)
        return result;
      
      int contactId;
      if (!int.TryParse(contactRecognitionInfo.VerifiedValue, out contactId))
        return result;
      
      var contact = Parties.Contacts.GetAll(x => x.Id == contactId)
        .Where(x => x.Status == CoreEntities.DatabookEntry.Status.Active).FirstOrDefault();
      if (contact != null)
      {
        result.Contact = contact;
        result.Probability = contactRecognitionInfo.Probability;
      }
      return result;
    }
    
    /// <summary>
    /// Получить результаты предшествующего распознавания контрагента по факту, идентичному переданному.
    /// </summary>
    /// <param name="fact">Факт Ario.</param>
    /// <param name="propertyName">Имя свойства, связанного контрагентом.</param>
    /// <returns>Структура, содержащая контрагента, подтвержденного пользователем, факт и вероятность.</returns>
    [Public]
    public virtual IRecognizedCounterparty GetPreviousCounterpartyRecognitionResults(IArioFact fact,
                                                                                     string propertyName)
    {
      var counterpartyRecognitionInfo = Commons.PublicFunctions.Module.GetPreviousPropertyRecognitionResults(fact, propertyName);
      if (counterpartyRecognitionInfo == null)
        return null;
      
      int counterpartyId;
      if (!int.TryParse(counterpartyRecognitionInfo.VerifiedValue, out counterpartyId))
        return null;
      
      var counterparty = Parties.Counterparties.GetAll(x => x.Id == counterpartyId).FirstOrDefault();
      if (counterparty == null)
        return null;
      
      var result = RecognizedCounterparty.Create();
      result.Counterparty = counterparty;
      result.Fact = fact;
      result.CounterpartyProbability = counterpartyRecognitionInfo.Probability.HasValue ?
        counterpartyRecognitionInfo.Probability.Value :
        Module.PropertyProbabilityLevels.Min;
      return result;
    }
    
    /// <summary>
    /// Получить результаты предшествующего распознавания НОР по факту, идентичному переданному.
    /// </summary>
    /// <param name="fact">Факт Ario.</param>
    /// <param name="propertyName">Имя свойства, связанного с НОР.</param>
    /// <returns>Структура с НОР, подтвержденной пользователем, фактом и вероятностью.</returns>
    [Public]
    public virtual IRecognizedCounterparty GetPreviousBusinessUnitRecognitionResults(IArioFact fact,
                                                                                     string propertyName)
    {
      var businessUnitRecognitionInfo = Commons.PublicFunctions.Module.GetPreviousPropertyRecognitionResults(fact, propertyName);
      if (businessUnitRecognitionInfo == null)
        return null;
      
      int businessUnitId;
      if (!int.TryParse(businessUnitRecognitionInfo.VerifiedValue, out businessUnitId))
        return null;
      
      var businessUnit = BusinessUnits.GetAll(x => x.Id == businessUnitId).FirstOrDefault();
      if (businessUnit == null)
        return null;
      
      var result = RecognizedCounterparty.Create();
      result.BusinessUnit = businessUnit;
      result.Fact = fact;
      result.BusinessUnitProbability = businessUnitRecognitionInfo.Probability;
      return result;
    }
    
    /// <summary>
    /// Получить наименование контрагента.
    /// </summary>
    /// <param name="fact">Исходный факт, содержащий наименование контрагента.</param>
    /// <param name="nameFieldName">Наименование поля с наименованием контрагента.</param>
    /// <param name="legalFormFieldName">Наименование поля с организационно-правовой формой контрагента.</param>
    /// <returns>Наименование контрагента.</returns>
    [Public]
    public static string GetCounterpartyName(IArioFact fact, string nameFieldName, string legalFormFieldName)
    {
      if (fact == null)
        return string.Empty;
      
      var name = Commons.PublicFunctions.Module.GetFieldValue(fact, nameFieldName);
      var legalForm = Commons.PublicFunctions.Module.GetFieldValue(fact, legalFormFieldName);
      return string.IsNullOrEmpty(legalForm) ? name : string.Format("{0}, {1}", name, legalForm);
    }
    
    #endregion
    
    #region Получение адресатов
    
    /// <summary>
    /// Получить адресатов по имени.
    /// </summary>
    /// <param name="name">Имя.</param>
    /// <returns>Список адресатов.</returns>
    [Public]
    public virtual List<IEmployee> GetAddresseesByName(string name)
    {
      if (string.IsNullOrWhiteSpace(name))
        return null;
      
      var oldChar = "ё";
      var newChar = "е";
      name = name.ToLower().Replace(oldChar, newChar);
      
      var employees = new List<IEmployee>();
      var activeEmployees = Company.Employees.GetAll(e => e.Status == Company.Employee.Status.Active);
      // Прямое совпадение по 'name'.
      employees = activeEmployees.Where(e => e.Name.ToLower().Replace(oldChar, newChar) == name).ToList();
      if (employees.Any())
        return employees;
      
      // Поиск только по фамилии персоны, если передана только фамилия - старое поведение.
      employees = activeEmployees.Where(e => e.Person.LastName.ToLower().Replace(oldChar, newChar) == name).ToList();
      if (employees.Any())
        return employees;
      
      // Поискать без преобразования в именительный падеж.
      var nameVariants = this.GetNameVariants(name);
      foreach (var nameVariant in nameVariants)
        employees.AddRange(this.GetEmployeesByName(nameVariant).Where(x => x != null));
      if (employees.Any())
        return employees;
      
      // Поискать с предварительным преобразованием в именительный падеж с мужским фейковым отчеством.
      employees = this.GetEmployeesByDativeDeclensionName(nameVariants, CommonLibrary.Gender.Masculine);
      if (employees.Any())
        return employees;
      
      // Поискать с предварительным преобразованием в именительный падеж с женским фейковым отчеством.
      employees = this.GetEmployeesByDativeDeclensionName(nameVariants, CommonLibrary.Gender.Feminine);
      return employees;
    }
    
    /// <summary>
    /// Получить возможные варианты ФИО из строки.
    /// </summary>
    /// <param name="name">Строка с ФИО.</param>
    /// <returns>Персональные данные.</returns>
    /// <remarks>Реализован парсинг следующих форматов ФИО:
    /// <para>- Иванов  Иван;</para>
    /// <para>- Иванов  Иван  Иванович;</para>
    /// <para>- Иван  Иванов;</para>
    /// <para>- Иван  Иванович  Иванов;</para>
    /// <para>- Иванов  И.  И.;</para>
    /// <para>- Иванов  И.;</para>
    /// <para>- Иванов  И.И.;</para>
    /// <para>- Иванов  И  И;</para>
    /// <para>- Иванов  И;</para>
    /// <para>- Иванов  ИИ;</para>
    /// <para>- И. И. Иванов;</para>
    /// <para>- И. Иванов;</para>
    /// <para>- И.И. Иванов;</para>
    /// <para>- И И  Иванов;</para>
    /// <para>- И Иванов;</para>
    /// <para>- ИИ Иванов.</para></remarks>
    [Public]
    public virtual List<CommonLibrary.PersonFullName> GetNameVariants(string name)
    {
      var variants = new List<CommonLibrary.PersonFullName>();
      
      if (string.IsNullOrWhiteSpace(name))
        return variants;
      
      variants.AddRange(this.GetLongNameVariants(name));
      variants.AddRange(this.GetShortNameVariants(name));
      
      return variants;
    }
    
    /// <summary>
    /// Получить возможные варианты полных ФИО из строки.
    /// </summary>
    /// <param name="name">Строка с ФИО.</param>
    /// <returns>Персональные данные.</returns>
    /// <remarks>ФИО может быть без отчества.</remarks>
    [Public]
    public virtual List<CommonLibrary.PersonFullName> GetLongNameVariants(string name)
    {
      var variants = new List<CommonLibrary.PersonFullName>();
      if (string.IsNullOrWhiteSpace(name))
        return variants;
      
      var nameMatching = System.Text.RegularExpressions.Regex.Match(name, @"^(\S+)(?<!\.)\s*(\S+)(?<!\.)\s*(\S*)(?<!\.)$");
      if (!nameMatching.Success)
        return variants;
      
      /* Иванов Иван Иванович
       * Иванов Иван
       * PersonFullName ctor params: LastName, FirstName, MiddleName.
       */
      variants.Add(CommonLibrary.PersonFullName.Create(nameMatching.Groups[1].Value, nameMatching.Groups[2].Value, nameMatching.Groups[3].Value));
      
      /* Иван Иванович Иванов
       * Иван Иванов
       * PersonFullName ctor params: LastName, FirstName, MiddleName.
       */
      variants.Add(CommonLibrary.PersonFullName.Create(nameMatching.Groups[3].Value, nameMatching.Groups[1].Value, nameMatching.Groups[2].Value));
      
      return variants;
    }
    
    /// <summary>
    /// Получить возможные варианты коротких ФИО из строки.
    /// </summary>
    /// <param name="name">Строка с ФИО.</param>
    /// <returns>Персональные данные.</returns>
    /// <remarks>ФИО может быть без отчества.</remarks>
    [Public]
    public virtual List<CommonLibrary.PersonFullName> GetShortNameVariants(string name)
    {
      var variants = new List<CommonLibrary.PersonFullName>();
      if (string.IsNullOrWhiteSpace(name))
        return variants;
      
      /* Иванов  И.  И.
       * Иванов  И.
       * Иванов  И.И.
       * Иванов  И  И
       * Иванов  И
       * Иванов  ИИ
       * PersonFullName ctor params: LastName, FirstName, MiddleName.
       */
      var nameMatching = System.Text.RegularExpressions.Regex.Match(name, @"^(\S+)\s*(\S)\.?\s*(\S?)(?<!\.)\.?$");
      if (nameMatching.Success)
        variants.Add(CommonLibrary.PersonFullName.Create(nameMatching.Groups[1].Value, nameMatching.Groups[2].Value, nameMatching.Groups[3].Value));
      
      /* И. И. Иванов
       * И. Иванов
       * И.И. Иванов
       * И И  Иванов
       * И Иванов
       * ИИ Иванов
       * PersonFullName ctor params: LastName, FirstName, MiddleName.
       */
      nameMatching = System.Text.RegularExpressions.Regex.Match(name, @"^(\S)\.?\s*(\S?)(?<!\.)\.?\s+(\S+)$");
      if (nameMatching.Success)
        variants.Add(CommonLibrary.PersonFullName.Create(nameMatching.Groups[3].Value, nameMatching.Groups[1].Value, nameMatching.Groups[2].Value));
      
      return variants;
    }
    
    /// <summary>
    /// Получить сотрудников по ФИО.
    /// </summary>
    /// <param name="name">Персональные данные.</param>
    /// <returns>Список сотрудников.</returns>
    [Public]
    public virtual List<IEmployee> GetEmployeesByName(CommonLibrary.PersonFullName name)
    {
      if (name == null)
        return new List<IEmployee>();
      
      var oldChar = "ё";
      var newChar = "е";
      var activeEmployees = Company.Employees.GetAll(e => e.Status == Company.Employee.Status.Active);
      var employees = activeEmployees.Where(x => x.Person.LastName.ToLower().Replace(oldChar, newChar) == name.LastName.ToLower()).ToList();
      
      // Уточнить по имени.
      if (!string.IsNullOrWhiteSpace(name.FirstName))
      {
        employees = name.FirstName.Length != 1
          ? employees.Where(x => x.Person.FirstName.ToLower().Replace(oldChar, newChar) == name.FirstName.ToLower()).ToList()
          : employees.Where(x => x.Person.FirstName.ToLower().Replace(oldChar, newChar)[0] == name.FirstName.ToLower()[0]).ToList();
      }
      
      // Уточнить по отчеству.
      if (!string.IsNullOrWhiteSpace(name.MiddleName))
      {
        employees = name.MiddleName.Length != 1
          ? employees.Where(x => x.Person.MiddleName.ToLower().Replace(oldChar, newChar) == name.MiddleName.ToLower()).ToList()
          : employees.Where(x => x.Person.MiddleName.ToLower().Replace(oldChar, newChar)[0] == name.MiddleName.ToLower()[0]).ToList();
      }
      
      return employees;
    }
    
    /// <summary>
    /// Получить сотрудников по ФИО в дательном падеже.
    /// </summary>
    /// <param name="nameVariants">Персональные данные.</param>
    /// <param name="gender">Пол.</param>
    /// <returns>Список сотрудников.</returns>
    [Public]
    public virtual List<IEmployee> GetEmployeesByDativeDeclensionName(List<CommonLibrary.PersonFullName> nameVariants, CommonLibrary.Gender gender)
    {
      var employees = new List<IEmployee>();
      foreach (var nameVariant in nameVariants)
      {
        var fullNameInDeclension = this.FromDativeToNominativeDeclension(nameVariant, gender);
        if (fullNameInDeclension != null)
          employees.AddRange(this.GetEmployeesByName(fullNameInDeclension).Where(x => x != null));
      }
      return employees;
    }
    
    /// <summary>
    /// Преобразовать ФИО из дательного падежа в именительный.
    /// </summary>
    /// <param name="name">Персональные данные.</param>
    /// <param name="gender">Пол.</param>
    /// <returns>ФИО в именительном падеже.</returns>
    /// <remarks>Для корректного преобразования из дательного падежа в именительный используется фейковое отчество.
    /// После преобразования к именительному падежу восстанавливается исходное отчество.</remarks>
    [Public]
    public virtual PersonFullName FromDativeToNominativeDeclension(PersonFullName name, CommonLibrary.Gender gender)
    {
      var maleTempMiddleName = "Александровичу";
      var femaleTempMiddleName = "Александровне";
      var replaceOriginalMiddleName =
        gender != CommonLibrary.Gender.NotDefined &&
        (string.IsNullOrWhiteSpace(name.MiddleName) || name.MiddleName.Length == 1);
      var nameInDeclensionStr = string.Empty;
      if (replaceOriginalMiddleName)
      {
        nameInDeclensionStr = gender == CommonLibrary.Gender.Masculine
          ? Padeg.ToNominativeDeclension(PersonFullName.Create(name.LastName, name.FirstName, maleTempMiddleName))
          : Padeg.ToNominativeDeclension(PersonFullName.Create(name.LastName, name.FirstName, femaleTempMiddleName));
      }
      else
      {
        nameInDeclensionStr = Padeg.ToNominativeDeclension(name);
      }
      
      PersonFullName nameInDeclension;
      if (PersonFullName.TryParse(nameInDeclensionStr, out nameInDeclension) && replaceOriginalMiddleName)
        nameInDeclension = PersonFullName.Create(nameInDeclension.LastName, nameInDeclension.FirstName, name.MiddleName);
      
      return nameInDeclension;
    }
    
    #endregion
    
    #endregion
    
    #region Заполнение свойств документов с нечетким поиском
    
    #region Входящее письмо
    
    /// <summary>
    /// Заполнить свойства входящего письма по результатам обработки Ario с использованием нечеткого поиска.
    /// </summary>
    /// <param name="letter">Входящее письмо.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    [Public]
    public virtual void FillIncomingLetterPropertiesFuzzy(RecordManagement.IIncomingLetter letter,
                                                          IDocumentInfo documentInfo,
                                                          Sungero.Company.IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      var props = letter.Info.Properties;
      
      // Вид документа.
      this.FillDocumentKind(letter);
      
      // Содержание.
      this.FillIncomingLetterSubject(letter, arioDocument);
      
      // Номер и дата.
      this.FillDocumentNumberAndDateFuzzy(letter, arioDocument, ArioGrammars.LetterFact.Name,
                                          props.InNumber, ArioGrammars.LetterFact.NumberField,
                                          props.Dated, ArioGrammars.LetterFact.DateField);

      // НОР, адресат, подразделение.
      this.FillIncomingLetterRecipientFuzzy(letter, arioDocument, responsible);

      // Корреспондент, подписант, контакт.
      this.FillIncomingLetterOfficialsFuzzy(letter, arioDocument);

      // Реквизиты искодящего письма ("в ответ на").
      this.FillIncomingLetterResponseToFuzzy(letter, arioDocument);
    }

    /// <summary>
    /// Заполнить корреспондента входящего письма.
    /// </summary>
    /// <param name="letter">Входящее письмо.</param>
    /// <param name="arioDocument">Информация о документе Ario.</param>
    /// <remarks>С использованием нечеткого поиска.</remarks>
    [Public, Obsolete("Заполнение корреспондента перенесено в функцию FillIncomingLetterOfficialsFuzzy.")]
    public virtual void FillIncomingLetterCorrespondentFuzzy(IIncomingLetter letter, IArioDocument arioDocument)
    {
      // Задать приоритет полей для поиска корреспондента. Вес определяет как учитывается вероятность каждого поля.
      var weightedFields = new Dictionary<string, double>()
      {
        { ArioGrammars.LetterFact.TinField, 0.35 },
        { ArioGrammars.LetterFact.TrrcField, 0.05 },
        { ArioGrammars.LetterFact.PsrnField, 0.25 },
        { ArioGrammars.LetterFact.CorrespondentNameField, 0.15 },
        { ArioGrammars.LetterFact.HeadCompanyNameField, 0.05 },
        { ArioGrammars.LetterFact.WebsiteField, 0.05 },
        { ArioGrammars.LetterFact.PhoneField, 0.05 },
        { ArioGrammars.LetterFact.EmailField, 0.05 }
      };

      // Подобрать факт Letter с полем типа Correspondent. Игнорировать факты Counterparty, содержащие данные о прочих организациях.
      var counterpartyFacts = arioDocument.Facts
        .Where(f => f.Name == ArioGrammars.LetterFact.Name &&
               f.Fields.Any(fl => fl.Name == ArioGrammars.LetterFact.TypeField &&
                            fl.Value == ArioGrammars.LetterFact.CorrespondentTypes.Correspondent) &&
               f.Fields.Any(fl => weightedFields.ContainsKey(fl.Name) && !string.IsNullOrEmpty(fl.Value)))
        .ToList();
      
      if (!counterpartyFacts.Any())
        return;

      // Отсортировать факты по количеству и приоритету полей.
      var orderedCounterpartyFacts = Commons.PublicFunctions.Module.GetOrderedFactsByFieldPriorities(counterpartyFacts, ArioGrammars.LetterFact.Name, weightedFields);
      var propertyName = letter.Info.Properties.Correspondent.Name;

      foreach (var fact in orderedCounterpartyFacts)
      {
        // Получить корреспондента по значениям полей факта с использованием Elasticsearch.
        var searchResult = this.SearchLetterCorrespondentFuzzy(fact);

        if (searchResult.EntityIds.Count != 1)
          continue;
        
        // Если корреспондент найден, рассчитать вероятность факта. Заполнить свойство в карточке документа и связь в результатах распознавания.
        var counterparty = Parties.Counterparties.GetAll(x => Equals(x.Id, searchResult.EntityIds.First())).FirstOrDefault();
        if (counterparty == null)
          continue;
        
        letter.Correspondent = counterparty;
        
        var factProbability = Commons.PublicFunctions.Module.GetAggregateFieldsProbability(fact, weightedFields);
        var fieldNames = fact.Fields
          .Where(fl => weightedFields.ContainsKey(fl.Name))
          .Select(fl => fl.Name)
          .Distinct()
          .ToList();
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                                fact,
                                                                                fieldNames,
                                                                                propertyName,
                                                                                counterparty,
                                                                                factProbability,
                                                                                null);
        return;
      }
    }
    
    /// <summary>
    /// Заполнить нашу организацию, адресата и подразделение с использованием нечеткого поиска.
    /// </summary>
    /// <param name="letter">Входящее письмо.</param>
    /// <param name="arioDocument">Информация о документе Ario.</param>
    /// <param name="responsible">Ответственный за обработку документов.</param>
    [Public]
    public virtual void FillIncomingLetterRecipientFuzzy(IIncomingLetter letter, IArioDocument arioDocument, IEmployee responsible)
    {
      var allBusinessUnits = Sungero.Company.BusinessUnits.GetAll();
      if (!allBusinessUnits.Any())
        return;

      var recognizedBusinessUnit = RecognizedCounterparty.Create();
      var recognizedAddressees = new List<IRecognizedOfficial>();

      // Получить список распознанных НОР и адресатов из фактов.
      var recognizedRecipients = this.GetRecognizedRecipientsFuzzy(arioDocument.Facts);
      
      // Получить НОР из карточки ответственного за обработку. Считать ее приоритетной при выборе из нескольких НОР.
      var defaultbusinessUnit = Docflow.PublicFunctions.Module.GetDefaultBusinessUnit(responsible);
      if (defaultbusinessUnit == null && allBusinessUnits.Count() == 1)
        defaultbusinessUnit = allBusinessUnits.Single();

      if (recognizedRecipients.Any())
      {
        // Если распознано несколько НОР, приоритетнее организация с адресатом.
        var recognizedRecipient = recognizedRecipients.First();
        if (defaultbusinessUnit != null && recognizedRecipients.Any(x => Equals(x.BusinessUnit.BusinessUnit, defaultbusinessUnit)))
          recognizedRecipient = recognizedRecipients.First(x => Equals(x.BusinessUnit.BusinessUnit, defaultbusinessUnit));
        else if (recognizedRecipients.Any(x => x.Addressees.Any()))
          recognizedRecipient = recognizedRecipients.First(x => x.Addressees.Any());
        
        recognizedBusinessUnit = recognizedRecipient.BusinessUnit;
        recognizedAddressees = recognizedRecipient.Addressees;
      }
      else if (defaultbusinessUnit != null)
      {
        // Если НОР не найдена по фактам - взять НОР, полученную по ответственному за обработку.
        recognizedBusinessUnit.Fact = null;
        recognizedBusinessUnit.BusinessUnit = defaultbusinessUnit;
        recognizedBusinessUnit.BusinessUnitProbability = Module.PropertyProbabilityLevels.Min;
      }

      // Заполнить карточку.
      if (recognizedBusinessUnit.BusinessUnit != null)
      {
        letter.BusinessUnit = recognizedBusinessUnit.BusinessUnit;
        var fieldNames = recognizedBusinessUnit.Fact != null ? recognizedBusinessUnit.Fact.Fields.Select(x => x.Name).ToList() : null;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                                recognizedBusinessUnit.Fact,
                                                                                fieldNames,
                                                                                letter.Info.Properties.BusinessUnit.Name,
                                                                                recognizedBusinessUnit.BusinessUnit,
                                                                                recognizedBusinessUnit.BusinessUnitProbability,
                                                                                null);
      }
      
      if (recognizedAddressees.Count == 1)
      {
        var recognizedAddressee = recognizedAddressees.Single();
        letter.Addressee = recognizedAddressee.Employee;

        Sungero.Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                                  recognizedAddressee.Fact,
                                                                                  ArioGrammars.LetterFact.AddresseeField,
                                                                                  letter.Info.Properties.Addressee.Name,
                                                                                  letter.Addressee,
                                                                                  recognizedAddressee.Probability);
      }
      else if (recognizedAddressees.Count > 1)
      {
        letter.IsManyAddressees = true;
        foreach (var addressee in recognizedAddressees)
        {
          var row = letter.Addressees.AddNew();
          row.Addressee = addressee.Employee;
          Sungero.Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                                          addressee.Fact,
                                                                                          new List<string> { ArioGrammars.LetterFact.AddresseeField },
                                                                                          row.Info.Properties.Addressee.Name,
                                                                                          row.Addressee,
                                                                                          addressee.Probability,
                                                                                          row.Id);
        }
      }
      
      // Заполнить подразделение.
      letter.Department = letter.Addressee != null
        ? Company.PublicFunctions.Department.GetDepartment(letter.Addressee)
        : Company.PublicFunctions.Department.GetDepartment(responsible);
    }

    /// <summary>
    /// Заполнить корреспондента, подписанта и контакт.
    /// </summary>
    /// <param name="letter">Входящее письмо.</param>
    /// <param name="arioDocument">Информация о документе Ario.</param>
    /// <remarks>С использованием нечеткого поиска.</remarks>
    [Public]
    public virtual void FillIncomingLetterOfficialsFuzzy(IIncomingLetter letter, IArioDocument arioDocument)
    {
      var recognizedCorrespondent = this.GetRecognizedCorrespondentFuzzy(arioDocument.Facts);
      if (recognizedCorrespondent.Correspondent == null)
        return;

      // Корреспондент.
      letter.Correspondent = recognizedCorrespondent.Correspondent.Counterparty;
      var correspondentFact = recognizedCorrespondent.Correspondent.Fact;
      var fieldNames = correspondentFact != null ? correspondentFact.Fields.Select(fl => fl.Name).Distinct().ToList() : null;
      Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                              correspondentFact,
                                                                              fieldNames,
                                                                              letter.Info.Properties.Correspondent.Name,
                                                                              letter.Correspondent,
                                                                              recognizedCorrespondent.Correspondent.CounterpartyProbability,
                                                                              null);

      // Подписант.
      if (recognizedCorrespondent.Signatory != null)
      {
        letter.SignedBy = recognizedCorrespondent.Signatory.Contact;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          recognizedCorrespondent.Signatory.Fact,
                                                                          null,
                                                                          letter.Info.Properties.SignedBy.Name,
                                                                          letter.SignedBy,
                                                                          recognizedCorrespondent.Signatory.Probability);
      }
      
      // Контакт.
      if (recognizedCorrespondent.Responsible != null)
      {
        letter.Contact = recognizedCorrespondent.Responsible.Contact;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          recognizedCorrespondent.Responsible.Fact,
                                                                          null,
                                                                          letter.Info.Properties.Contact.Name,
                                                                          letter.Contact,
                                                                          recognizedCorrespondent.Responsible.Probability);
      }
    }
    
    /// <summary>
    /// Заполнить подписанта и контакт входящего письма.
    /// </summary>
    /// <param name="letter">Входящее письмо.</param>
    /// <param name="arioDocument">Информация о документе Ario.</param>
    /// <remarks>С использованием нечеткого поиска.</remarks>
    [Public, Obsolete("Заполнение контактов перенесено в функцию FillIncomingLetterOfficialsFuzzy.")]
    public virtual void FillIncomingLetterContactsFuzzy(IIncomingLetter letter, IArioDocument arioDocument)
    {
      var probability = 0d;
      var counterpartyIds = new List<int>();
      if (letter.Correspondent != null)
        counterpartyIds.Add(letter.Correspondent.Id);
      
      // Подписант.
      var signatoryFacts = Commons.PublicFunctions.Module.GetOrderedFactsByType(arioDocument.Facts,
                                                                                ArioGrammars.LetterPersonFact.Name,
                                                                                ArioGrammars.LetterPersonFact.PersonTypes.Signatory,
                                                                                ArioGrammars.LetterPersonFact.TypeField,
                                                                                ArioGrammars.LetterPersonFact.NameField);
      var signatory = this.GetLetterContactFuzzy(signatoryFacts, counterpartyIds);
      if (signatory.Contact != null)
      {
        letter.SignedBy = signatory.Contact;
        probability += signatory.Probability.GetValueOrDefault();
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo, signatory.Fact,
                                                                          null, letter.Info.Properties.SignedBy.Name,
                                                                          letter.SignedBy,
                                                                          signatory.Probability);
        
        // Если удалось найти подписанта - искать исполнителя в той же компании.
        if (letter.SignedBy.Company != null && !counterpartyIds.Any())
          counterpartyIds.Add(letter.SignedBy.Company.Id);
      }
      
      // Контакт (исполнитель, ответственный).
      var responsibleFacts = Commons.PublicFunctions.Module.GetOrderedFactsByType(arioDocument.Facts,
                                                                                  ArioGrammars.LetterPersonFact.Name,
                                                                                  ArioGrammars.LetterPersonFact.PersonTypes.Responsible,
                                                                                  ArioGrammars.LetterPersonFact.TypeField,
                                                                                  ArioGrammars.LetterPersonFact.NameField);
      
      var responsible = this.GetLetterContactFuzzy(responsibleFacts, counterpartyIds);
      if (responsible.Contact != null)
      {
        letter.Contact = responsible.Contact;
        probability += responsible.Probability.GetValueOrDefault();
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo, responsible.Fact,
                                                                          null, letter.Info.Properties.Contact.Name,
                                                                          letter.Contact,
                                                                          responsible.Probability);
      }
      
      // Установить корреспондента по контактам.
      if (letter.Correspondent == null && (letter.SignedBy != null || letter.Contact != null))
      {
        letter.Correspondent = letter.SignedBy != null ? letter.SignedBy.Company : letter.Contact.Company;
        
        // Вероятность для подсветки определить по средней вероятности подписанта и исполнителя.
        var correspondentProbability = Module.PropertyProbabilityLevels.Min;
        if (letter.Correspondent.Status == Sungero.CoreEntities.DatabookEntry.Status.Active &&
            (probability / 2) >= Module.PropertyProbabilityLevels.UpperMiddle)
          correspondentProbability = Module.PropertyProbabilityLevels.UpperMiddle;

        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo, null, null,
                                                                          letter.Info.Properties.Correspondent.Name,
                                                                          letter.Correspondent,
                                                                          correspondentProbability);
      }
    }
    
    /// <summary>
    /// Заполнить реквизиты исходящего письма (свойство "в ответ на").
    /// </summary>
    /// <param name="letter">Входящее письмо.</param>
    /// <param name="arioDocument">Информация о документе Ario.</param>
    [Public]
    public virtual void FillIncomingLetterResponseToFuzzy(IIncomingLetter letter, IArioDocument arioDocument)
    {
      if (letter.BusinessUnit == null)
        return;

      // Получить факты с номером и/или датой исходящего документа. Приоритетнее факты, содержащие оба поля.
      var weightedFields = new Dictionary<string, double>()
      {
        { ArioGrammars.LetterFact.ResponseToNumberField, 0.5 },
        { ArioGrammars.LetterFact.ResponseToDateField, 0.5 }
      };
      var facts = Commons.PublicFunctions.Module.GetOrderedFactsByFieldPriorities(arioDocument.Facts, ArioGrammars.LetterFact.Name, weightedFields);

      if (!facts.Any())
        return;
      
      // Подготовить список исходящих писем для поиска.
      var outgoingLetters = RecordManagement.OutgoingLetters.GetAll(d => Equals(d.BusinessUnit, letter.BusinessUnit));
      if (letter.Correspondent != null)
        outgoingLetters = outgoingLetters.Where(d => Equals(d.Correspondent, letter.Correspondent));
      
      // Найти документ по номеру и дате.
      var recognizedDocument = this.GetRecognizedDocumentFuzzy(facts,
                                                               outgoingLetters,
                                                               ArioGrammars.LetterFact.ResponseToNumberField,
                                                               ArioGrammars.LetterFact.ResponseToDateField);
      if (recognizedDocument.Document != null)
      {
        letter.InResponseTo = OutgoingDocumentBases.As(recognizedDocument.Document);
        Sungero.Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                                        recognizedDocument.Fact,
                                                                                        weightedFields.Select(x => x.Key).ToList(),
                                                                                        letter.Info.Properties.InResponseTo.Name,
                                                                                        letter.InResponseTo,
                                                                                        recognizedDocument.Probability,
                                                                                        null);
      }
    }

    /// <summary>
    /// Получить список распознанных НОР и связанных с ними адресатов из фактов Ario.
    /// </summary>
    /// <param name="facts">Список фактов Ario.</param>
    /// <returns>Список структур с распознанными НОР и адресатами.</returns>
    [Public]
    public virtual List<IRecognizedLetterRecipient> GetRecognizedRecipientsFuzzy(List<IArioFact> facts)
    {
      var recognizedRecipients = new List<IRecognizedLetterRecipient>();
      if (!facts.Any())
        return recognizedRecipients;
      
      // Получить все распознанные НОР, связанные с фактами.
      var recognizedBusinessUnits = this.GetRecognizedLetterBusinessUnitsFuzzy(facts);

      // Получить адресатов.
      var recognizedAddressees = this.GetRecognizedLetterAddresseesFuzzy(facts, recognizedBusinessUnits);

      // Добавить НОР из адресатов в общий список распознанных.
      var addresseeBusinessUnits = recognizedAddressees
        .Where(x => x.Employee?.Department?.BusinessUnit != null)
        .Select(x => x.Employee.Department.BusinessUnit)
        .Distinct()
        .ToList();

      foreach (var businessUnit in addresseeBusinessUnits
               .Where(b => !recognizedBusinessUnits.Any(r => Equals(b, r.BusinessUnit))))
      {
        var recognizedBusinessUnit = RecognizedCounterparty.Create();
        recognizedBusinessUnit.BusinessUnit = businessUnit;
        recognizedBusinessUnit.BusinessUnitProbability = Constants.Module.PropertyProbabilityLevels.Min;
        recognizedBusinessUnits.Add(recognizedBusinessUnit);
      }

      // Для каждой НОР создать запись в результирующем списке.
      foreach (var recognizedBusinessUnit in recognizedBusinessUnits)
      {
        var businessUnit = recognizedBusinessUnit.BusinessUnit;
        if (recognizedRecipients.Any(x => Equals(x.BusinessUnit.BusinessUnit, businessUnit)))
          continue;
        
        var recognizedRecipient = RecognizedLetterRecipient.Create(recognizedBusinessUnit, new List<IRecognizedOfficial>());
        
        // Собрать всех адресатов. Исключить дубли.
        var employees = recognizedAddressees
          .Where(x => Equals(x.Employee?.Department?.BusinessUnit, businessUnit))
          .Select(x => x.Employee)
          .Distinct()
          .ToList();
        
        foreach (var employee in employees)
        {
          // Если НОР определена по адресату, сбросить вероятность до минимальной.
          var recognizedAddressee = recognizedAddressees.First(x => Equals(x.Employee, employee));
          if (recognizedBusinessUnit.Fact == null)
            recognizedAddressee.Probability = Constants.Module.PropertyProbabilityLevels.Min;
          recognizedRecipient.Addressees.Add(recognizedAddressee);
        }
        
        recognizedRecipients.Add(recognizedRecipient);
      }
      return recognizedRecipients;
    }
    
    /// <summary>
    /// Получить распознанного корреспондента и связанные с ним контакты из фактов Ario.
    /// </summary>
    /// <param name="facts">Список фактов Ario.</param>
    /// <returns>Распознанный корреспондент.</returns>
    [Public]
    public virtual IRecognizedLetterCorrespondent GetRecognizedCorrespondentFuzzy(List<IArioFact> facts)
    {
      var recognizedCorrespondent = RecognizedLetterCorrespondent.Create();
      
      // Получить факты Letter с типом "Корреспондент". Отсортировать по приоритету полей.
      var weightedFields = new Dictionary<string, double>()
      {
        { ArioGrammars.LetterFact.TinField, 0.35 },
        { ArioGrammars.LetterFact.TrrcField, 0.05 },
        { ArioGrammars.LetterFact.PsrnField, 0.25 },
        { ArioGrammars.LetterFact.CorrespondentNameField, 0.15 },
        { ArioGrammars.LetterFact.HeadCompanyNameField, 0.05 },
        { ArioGrammars.LetterFact.WebsiteField, 0.05 },
        { ArioGrammars.LetterFact.PhoneField, 0.05 },
        { ArioGrammars.LetterFact.EmailField, 0.05 }
      };
      
      var correspondentFacts = facts
        .Where(f => f.Name == ArioGrammars.LetterFact.Name &&
               f.Fields.Any(fl => fl.Name == ArioGrammars.LetterFact.TypeField &&
                            fl.Value == ArioGrammars.LetterFact.CorrespondentTypes.Correspondent) &&
               f.Fields.Any(fl => weightedFields.ContainsKey(fl.Name) && !string.IsNullOrEmpty(fl.Value)))
        .ToList();
      
      if (!correspondentFacts.Any())
        return recognizedCorrespondent;
      
      correspondentFacts = Commons.PublicFunctions.Module.GetOrderedFactsByFieldPriorities(correspondentFacts,
                                                                                           ArioGrammars.LetterFact.Name,
                                                                                           weightedFields);
      
      // Получить факты LetterPerson с типом "Подписант" и "Исполнитель/ответственный". Отсортировать по вероятности ФИО.
      var signatoryFacts = Commons.PublicFunctions.Module.GetOrderedFactsByType(facts, ArioGrammars.LetterPersonFact.Name,
                                                                                ArioGrammars.LetterPersonFact.PersonTypes.Signatory,
                                                                                ArioGrammars.LetterPersonFact.TypeField,
                                                                                ArioGrammars.LetterPersonFact.NameField);
      var responsibleFacts = Commons.PublicFunctions.Module.GetOrderedFactsByType(facts, ArioGrammars.LetterPersonFact.Name,
                                                                                  ArioGrammars.LetterPersonFact.PersonTypes.Responsible,
                                                                                  ArioGrammars.LetterPersonFact.TypeField,
                                                                                  ArioGrammars.LetterPersonFact.NameField);
      var foundCorrespondents = new List<IRecognizedLetterCorrespondent>();
      foreach (var fact in correspondentFacts)
      {
        // Получить корреспондента по факту с использованием нечеткого поиска.
        var searchCorrespondentResult = this.SearchLetterCorrespondentFuzzy(fact);
        if (!searchCorrespondentResult.EntityIds.Any())
          continue;

        // Получить подписанта.
        var foundCorrespondentsIds = searchCorrespondentResult.EntityIds;
        var recognizedSignatory = this.GetLetterContactFuzzy(signatoryFacts, foundCorrespondentsIds);
        // Если подписант найден, искать контакт в той же организации. В противном случае искать контакт во всех найденных.
        if (recognizedSignatory.Contact != null)
          foundCorrespondentsIds = new List<int>() { recognizedSignatory.Contact.Company.Id };
        var recognizedResponsible = this.GetLetterContactFuzzy(responsibleFacts, foundCorrespondentsIds);
        
        // Если по факту было найдено несколько дублей КА и не удалось уточнить единственного по ФИО, перейти к следующему факту.
        if (searchCorrespondentResult.EntityIds.Count > 1 && recognizedSignatory.Contact == null && recognizedResponsible.Contact == null)
          continue;

        var counterparty = Counterparties.Null;
        if (recognizedSignatory.Contact != null)
          counterparty = recognizedSignatory.Contact.Company;
        else if (recognizedResponsible.Contact != null)
          counterparty = recognizedResponsible.Contact.Company;
        else
          counterparty = Parties.Counterparties.GetAll(x => Equals(x.Id, searchCorrespondentResult.EntityIds.First())).FirstOrDefault();
        if (counterparty == null)
          continue;

        var recognizedCounterparty = RecognizedCounterparty.Create();
        
        recognizedCounterparty.Counterparty = counterparty;
        recognizedCounterparty.Fact = fact;
        recognizedCounterparty.Type = ArioGrammars.LetterFact.CorrespondentTypes.Correspondent;
        recognizedCounterparty.CounterpartyProbability = Commons.PublicFunctions.Module.GetAggregateFieldsProbability(fact, weightedFields);

        var foundCorrespondent = RecognizedLetterCorrespondent.Create(recognizedCounterparty, recognizedSignatory, recognizedResponsible);
        foundCorrespondents.Add(foundCorrespondent);
        
        // Если нашли и корреспондента, и оба контакта, то завершить поиск.
        if (foundCorrespondent.Signatory.Contact != null && foundCorrespondent.Responsible.Contact != null)
          return foundCorrespondent;
      }

      // Если по фактам корреспондент не найден - попробовать получить его из контактов.
      // Если найдено несколько контактов одного типа (из разных фактов), то не подставлять никого.
      if (!foundCorrespondents.Any())
      {
        // Искать по всему справочнику контактов.
        // Вероятность правильного подбора контакта по всему справочнику контактов считать ниже средней (для желтой подсветки).
        var counterpartyIds = new List<int>();
        recognizedCorrespondent.Signatory = this.GetLetterContactFuzzy(signatoryFacts, counterpartyIds);
        if (recognizedCorrespondent.Signatory.Contact != null)
        {
          recognizedCorrespondent.Signatory.Probability = Module.PropertyProbabilityLevels.LowerMiddle;
          // Ответственного искать в организации подписанта.
          counterpartyIds.Add(recognizedCorrespondent.Signatory.Contact.Company.Id);
        }
        recognizedCorrespondent.Responsible = this.GetLetterContactFuzzy(responsibleFacts, counterpartyIds);
        if (recognizedCorrespondent.Responsible.Contact != null)
          recognizedCorrespondent.Responsible.Probability = Module.PropertyProbabilityLevels.LowerMiddle;

        if (recognizedCorrespondent.Signatory.Contact != null || recognizedCorrespondent.Responsible.Contact != null)
        {
          var recognizedCounterparty = RecognizedCounterparty.Create();
          recognizedCounterparty.Counterparty = recognizedCorrespondent.Signatory.Contact != null ?
            recognizedCorrespondent.Signatory.Contact.Company :
            recognizedCorrespondent.Responsible.Contact.Company;
          
          // Вероятность подбора корреспондента выставить средней, если найдены оба контакта. Либо минимальной - в противном случае.
          recognizedCounterparty.CounterpartyProbability = Module.PropertyProbabilityLevels.Min;
          if (recognizedCorrespondent.Signatory.Contact != null && recognizedCorrespondent.Responsible.Contact != null)
            recognizedCounterparty.CounterpartyProbability = Module.PropertyProbabilityLevels.Middle;
          
          recognizedCorrespondent.Correspondent = recognizedCounterparty;
        }
      }
      
      // Для заполнения в карточке выбрать корреспондента, для которого найден и подписант и исполнитель.
      // Если такого нет, выбрать корреспондента, для которого найден хотя бы один из контактов.
      // Иначе взять первого найденного корреспондента.
      if (foundCorrespondents.Any())
      {
        recognizedCorrespondent = foundCorrespondents.FirstOrDefault(x => x.Signatory.Contact != null && x.Responsible.Contact != null);
        if (recognizedCorrespondent == null)
          recognizedCorrespondent = foundCorrespondents.FirstOrDefault(x => x.Signatory.Contact != null);
        if (recognizedCorrespondent == null)
          recognizedCorrespondent = foundCorrespondents.FirstOrDefault(x => x.Responsible.Contact != null);
        if (recognizedCorrespondent == null)
          recognizedCorrespondent = foundCorrespondents.First();
      }

      return recognizedCorrespondent;
    }
    
    /// <summary>
    /// Получить список всех распознанных НОР во входящем письме с использованием нечеткого поиска.
    /// </summary>
    /// <param name="facts">Список фактов Ario.</param>
    /// <returns>Список распознанных НОР.</returns>
    [Public]
    public virtual List<IRecognizedCounterparty> GetRecognizedLetterBusinessUnitsFuzzy(List<IArioFact> facts)
    {
      var recognizedBusinessUnits = new List<IRecognizedCounterparty>();
      
      var weightedFields = new Dictionary<string, double>()
      {
        { ArioGrammars.LetterFact.TinField, 0.35 },
        { ArioGrammars.LetterFact.TrrcField, 0.05 },
        { ArioGrammars.LetterFact.PsrnField, 0.25 },
        { ArioGrammars.LetterFact.CorrespondentNameField, 0.20 },
        { ArioGrammars.LetterFact.AddresseeField, 0.15 }
      };
      
      var recipientFacts = facts
        .Where(f => f.Name == ArioGrammars.LetterFact.Name &&
               f.Fields.Any(fl => fl.Name == ArioGrammars.LetterFact.TypeField &&
                            fl.Value == ArioGrammars.LetterFact.CorrespondentTypes.Recipient) &&
               f.Fields.Any(fl => weightedFields.ContainsKey(fl.Name) && !string.IsNullOrEmpty(fl.Value)))
        .ToList();
      
      if (!recipientFacts.Any())
        return recognizedBusinessUnits;
      
      var orderedFacts = Commons.PublicFunctions.Module.GetOrderedFactsByFieldPriorities(recipientFacts,
                                                                                         ArioGrammars.LetterFact.Name,
                                                                                         weightedFields);
      // Для каждого извлеченного факта получить НОР с использованием нечеткого поиска.
      foreach (var fact in orderedFacts)
      {
        var searchBusinessUnitResult = this.SearchLetterBusinessUnitFuzzy(fact);
        
        if (!searchBusinessUnitResult.EntityIds.Any())
          continue;
        
        var factRecognizedBusinessUnits = new List<IRecognizedCounterparty>();
        var factProbability = Commons.PublicFunctions.Module.GetAggregateFieldsProbability(fact, weightedFields);
        foreach (var entityId in searchBusinessUnitResult.EntityIds)
        {
          var businessUnit = Company.BusinessUnits.GetAll(x => x.Id == entityId).FirstOrDefault();
          if (businessUnit != null)
          {
            var recognizedBusinessUnit = RecognizedCounterparty.Create();
            recognizedBusinessUnit.BusinessUnit = businessUnit;
            recognizedBusinessUnit.Fact = fact;
            recognizedBusinessUnit.Type = ArioGrammars.LetterFact.CorrespondentTypes.Recipient;
            recognizedBusinessUnit.BusinessUnitProbability = factProbability;
            factRecognizedBusinessUnits.Add(recognizedBusinessUnit);
          }
        }
        
        // Если нечетким поиском найдена одна НОР, добавить ее в список распознанных.
        // Если найдено несколько дублей, уточнить НОР по адресату.
        // Если уточнить не удалось, или если НОР не найдены нечетким поиском, перейти к следующему факту.
        IRecognizedCounterparty foundRecognizedBusinessUnit = factRecognizedBusinessUnits.Count == 1 ?
          factRecognizedBusinessUnits.Single() :
          null;

        if (factRecognizedBusinessUnits.Count > 1)
        {
          var recognizedAddressees = this.GetRecognizedLetterAddresseesFuzzy(recipientFacts, factRecognizedBusinessUnits);
          // Если адресаты не найдены или найдены из разных НОР, исключить организации из списка распознанных.
          if (recognizedAddressees.Any())
          {
            var addresseeBusinessUnits = recognizedAddressees
              .Where(x => x.Employee.Department != null && x.Employee.Department.BusinessUnit != null)
              .Select(x => x.Employee.Department.BusinessUnit)
              .Distinct()
              .ToList();
            
            if (addresseeBusinessUnits.Count == 1)
              foundRecognizedBusinessUnit = factRecognizedBusinessUnits.First(x => Equals(x.BusinessUnit, addresseeBusinessUnits.First()));
          }
        }
        
        // Добавить НОР в список распознанных, если она не была найдена по другому факту.
        if (foundRecognizedBusinessUnit != null &&
            !recognizedBusinessUnits.Any(x => Equals(x.BusinessUnit, foundRecognizedBusinessUnit.BusinessUnit)))
          recognizedBusinessUnits.Add(foundRecognizedBusinessUnit);
      }
      
      return recognizedBusinessUnits;
    }

    /// <summary>
    /// Получить адресатов входящего письма по фактам Ario с использованием нечеткого поиска.
    /// </summary>
    /// <param name="facts">Список фактов.</param>
    /// <param name="recognizedBusinessUnits">Список распознанных НОР.</param>
    /// <returns>Список найденных адресатов.</returns>
    [Public]
    public virtual List<IRecognizedOfficial> GetRecognizedLetterAddresseesFuzzy(List<IArioFact> facts, List<IRecognizedCounterparty> recognizedBusinessUnits)
    {
      var recognizedAddressees = new List<IRecognizedOfficial>();
      
      // Получить список фактов, содержащих ФИО адресатов.
      var addresseeFacts = Commons.PublicFunctions.Module.GetOrderedFactsByType(facts, ArioGrammars.LetterFact.Name,
                                                                                ArioGrammars.LetterFact.CorrespondentTypes.Recipient,
                                                                                ArioGrammars.LetterFact.TypeField,
                                                                                ArioGrammars.LetterFact.AddresseeField);
      if (!addresseeFacts.Any())
        return recognizedAddressees;
      
      // Если передан список распознанных НОР, получить их ИД для поиска адресатов.
      var recognizedBusinessUnitIds = recognizedBusinessUnits
        .Where(x => x.BusinessUnit != null)
        .Select(x => x.BusinessUnit.Id)
        .Distinct()
        .ToList();
      
      // Искать адресатов в справочнике сотрудников с использованием нечеткого поиска.
      foreach (var fact in addresseeFacts)
      {
        var foundBusinessUnitIds = new List<int>();
        
        if (recognizedBusinessUnitIds.Any())
        {
          // Если подписант извлечен из того же факта, что и наша организация, искать ФИО только внутри этой организации.
          var recognizedAddresseeBusinessUnitIds = recognizedBusinessUnits
            .Where(x => x.BusinessUnit != null && Equals(x.Fact?.Id, fact.Id))
            .Select(x => x.BusinessUnit.Id)
            .ToList();

          if (recognizedAddresseeBusinessUnitIds.Any())
            foundBusinessUnitIds.AddRange(recognizedAddresseeBusinessUnitIds);
          else
            foundBusinessUnitIds.AddRange(recognizedBusinessUnitIds);
        }
        
        // В факте может быть несколько полей с адресатами - обработать все ФИО.
        var addresseeFields = fact.Fields
          .Where(fl => fl.Name == ArioGrammars.LetterFact.AddresseeField && !string.IsNullOrEmpty(fl.Value))
          .OrderByDescending(fl => fl.Probability);
        foreach (var field in addresseeFields)
        {
          var foundEmployees = Company.PublicFunctions.Employee.GetEmployeesByNameFuzzy(field.Value, foundBusinessUnitIds);
          
          // Если в нашей организации найдено несколько однофамильцев, оставить из них только руководителя.
          // Если и среди руководителей есть однофамильцы, то считать результаты слишком неточными и не возвращать никого.
          if (foundEmployees.Count > 1)
            foundEmployees = foundEmployees.Where(x => Company.PublicFunctions.Employee.IsManager(x)).ToList();
          
          if (foundEmployees.Count == 1)
          {
            // Добавить сотрудника в число распознанных адресатов, если он еще не был найден по другим фактам.
            var employee = foundEmployees.Single();
            if (!recognizedAddressees.Any(x => Equals(x.Employee, employee)))
              recognizedAddressees.Add(RecognizedOfficial.Create(employee, null, fact, field.Probability));
          }
        }
      }
      
      return recognizedAddressees;
    }
    
    /// <summary>
    /// Получить контакт входящего письма по фактам Арио с использованием нечеткого поиска.
    /// </summary>
    /// <param name="contactFacts">Список фактов Ario, содержащих ФИО подписанта или испольнителя.</param>
    /// <param name="counterpartyIds">Список ИД организации для поиска контактов.</param>
    /// <returns>Найденный контакт.</returns>
    [Public]
    public virtual IRecognizedOfficial GetLetterContactFuzzy(List<IArioFact> contactFacts, List<int> counterpartyIds)
    {
      var recognizedContacts = new List<IRecognizedOfficial>();
      // Искать ФИО из каждого факта во всех переданных организациях.
      foreach (var fact in contactFacts)
      {
        var contactNameFields = fact.Fields
          .Where(fl => fl.Name == ArioGrammars.LetterPersonFact.NameField && !string.IsNullOrEmpty(fl.Value))
          .OrderByDescending(fl => fl.Probability);
        foreach (var field in contactNameFields)
        {
          var contact = Sungero.Parties.PublicFunctions.Contact.GetContactsByNameFuzzy(field.Value, counterpartyIds);
          if (contact != null && !recognizedContacts.Any(x => Equals(x.Contact, contact)))
          {
            var recognizedContact = RecognizedOfficial.Create();
            recognizedContact.Contact = contact;
            recognizedContact.Fact = fact;
            recognizedContact.Probability = field.Probability;
            recognizedContacts.Add(recognizedContact);
          }
        }
      }

      if (recognizedContacts.Count == 1)
        return recognizedContacts.First();

      // Если найдено несколько контактов, вернуть первый, но только если все контакты найдены в единственной организации.
      if (recognizedContacts.Count > 1 && recognizedContacts.Select(x => x.Contact.Company).Distinct().Count() == 1)
      {
        // Если есть несколько однофамильцев, снизить вероятность для желтой подсветки.
        var recognizedContact = recognizedContacts.First();
        recognizedContact.Probability = Module.PropertyProbabilityLevels.LowerMiddle;
        return recognizedContact;
      }
      
      return RecognizedOfficial.Create();
    }
    
    #endregion

    #region Первичка

    /// <summary>
    /// Заполнить свойства акта выполненных работ по результатам обработки Ario с использованием нечеткого поиска.
    /// </summary>
    /// <param name="contractStatement">Акт выполненных работ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    [Public]
    public virtual void FillContractStatementPropertiesFuzzy(FinancialArchive.IContractStatement contractStatement,
                                                             IDocumentInfo documentInfo,
                                                             Sungero.Company.IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(contractStatement);
      
      // Подразделение и ответственный.
      contractStatement.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
      contractStatement.ResponsibleEmployee = responsible;
      
      // Сумма и валюта.
      this.FillAccountingDocumentAmountAndCurrency(contractStatement, documentInfo);
      
      // НОР и КА.
      var arioCounterpartyTypes = new List<string>()
      {
        ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller,
        ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer
      };
      
      var recognizedCounterparties = this.GetRecognizedAccountingDocumentCounterpartiesFuzzy(arioDocument.Facts, arioCounterpartyTypes);
      var seller = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller).FirstOrDefault();
      var buyer = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer).FirstOrDefault();
      var nonType = recognizedCounterparties.Where(m => m.Type == string.Empty).ToList();
      
      var recognizedDocumentParties = this.GetDocumentParties(buyer, seller, nonType, responsible);
      
      this.FillAccountingDocumentParties(contractStatement, documentInfo, recognizedDocumentParties);
      
      // Подписант с нашей стороны.
      this.FillOurSignatoryForContractStatementFuzzy(contractStatement, documentInfo);

      // Подписант со стороны контрагента.
      this.FillCounterpartySignatoryForContractStatementFuzzy(contractStatement, documentInfo);
      
      // Дата, номер и регистрация.
      this.FillDocumentRegistrationData(contractStatement, documentInfo, ArioGrammars.DocumentFact.Name, Docflow.Resources.UnknownNumber);
      
      // Договор.
      this.FillAccountingDocumentLeadingDocumentFuzzy(contractStatement, arioDocument);
    }

    /// <summary>
    /// Заполнить свойства накладной по результатам обработки Ario с использованием нечеткого поиска.
    /// </summary>
    /// <param name="waybill">Товарная накладная.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    [Public]
    public virtual void FillWaybillPropertiesFuzzy(FinancialArchive.IWaybill waybill,
                                                   IDocumentInfo documentInfo,
                                                   Sungero.Company.IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(waybill);
      
      // Подразделение и ответственный.
      waybill.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
      waybill.ResponsibleEmployee = responsible;
      
      // Сумма и валюта.
      this.FillAccountingDocumentAmountAndCurrency(waybill, documentInfo);
      
      // НОР и КА.
      var arioCounterpartyTypes = new List<string>();
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Supplier);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Payer);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Shipper);
      arioCounterpartyTypes.Add(ArioGrammars.CounterpartyFact.CounterpartyTypes.Consignee);
      
      var recognizedCounterparties = this.GetRecognizedAccountingDocumentCounterpartiesFuzzy(arioDocument.Facts, arioCounterpartyTypes);
      var seller = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Supplier).FirstOrDefault() ??
        recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Shipper).FirstOrDefault();
      var buyer = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Payer).FirstOrDefault() ??
        recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Consignee).FirstOrDefault();
      
      var recognizedDocumentParties = this.GetDocumentParties(buyer, seller, responsible);
      
      this.FillAccountingDocumentParties(waybill, documentInfo, recognizedDocumentParties);
      
      // Дата, номер и регистрация.
      this.FillDocumentRegistrationData(waybill, documentInfo, ArioGrammars.FinancialDocumentFact.Name, Docflow.Resources.UnknownNumber);
      
      // Документ-основание.
      this.FillAccountingDocumentLeadingDocumentFuzzy(waybill, arioDocument);
    }

    /// <summary>
    /// Заполнить свойства универсального передаточного документа по результатам обработки Ario с использованием нечеткого поиска.
    /// </summary>
    /// <param name="universalTransferDocument">Универсальный передаточный документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    [Public]
    public virtual void FillUniversalTransferDocumentPropertiesFuzzy(FinancialArchive.IUniversalTransferDocument universalTransferDocument,
                                                                     IDocumentInfo documentInfo,
                                                                     Sungero.Company.IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(universalTransferDocument);
      
      // Сумма и валюта.
      this.FillAccountingDocumentAmountAndCurrency(universalTransferDocument, documentInfo);
      
      // Подразделение и ответственный.
      universalTransferDocument.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
      universalTransferDocument.ResponsibleEmployee = responsible;
      
      // НОР и КА.
      var arioCounterpartyTypes = new List<string>()
      {
        ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller,
        ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer,
        ArioGrammars.CounterpartyFact.CounterpartyTypes.Shipper,
        ArioGrammars.CounterpartyFact.CounterpartyTypes.Consignee
      };

      var recognizedCounterparties = this.GetRecognizedAccountingDocumentCounterpartiesFuzzy(arioDocument.Facts, arioCounterpartyTypes);
      var seller = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller).FirstOrDefault() ??
        recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Shipper).FirstOrDefault();
      var buyer = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer).FirstOrDefault() ??
        recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Consignee).FirstOrDefault();
      
      var recognizedDocumentParties = this.GetDocumentParties(buyer, seller, responsible);
      
      this.FillAccountingDocumentParties(universalTransferDocument, documentInfo, recognizedDocumentParties);
      
      // Дата, номер и регистрация.
      this.FillDocumentRegistrationData(universalTransferDocument, documentInfo, ArioGrammars.FinancialDocumentFact.Name, Docflow.Resources.UnknownNumber);
      
      // Договор (документ-основание).
      this.FillAccountingDocumentLeadingDocumentFuzzy(universalTransferDocument, arioDocument);
      
      // Корректировочный документ.
      if (universalTransferDocument.IsAdjustment.HasValue && universalTransferDocument.IsAdjustment.Value == true)
        this.FillUniversalTransferDocumentCorrectedDocument(universalTransferDocument, documentInfo);
      else
        this.FillUniversalTransferDocumentRevisionInfo(universalTransferDocument, documentInfo);
    }

    /// <summary>
    /// Подобрать по факту контрагента и НОР.
    /// </summary>
    /// <param name="allFacts">Факты.</param>
    /// <param name="arioCounterpartyTypes">Типы фактов контрагентов.</param>
    /// <returns>Наши организации и контрагенты, найденные по фактам.</returns>
    [Public]
    public virtual List<IRecognizedCounterparty> GetRecognizedAccountingDocumentCounterpartiesFuzzy(List<IArioFact> allFacts, List<string> arioCounterpartyTypes)
    {
      var recognizedCounterparties = new List<IRecognizedCounterparty>();
      
      foreach (var counterpartyType in arioCounterpartyTypes)
      {
        var recognizedBusinessUnit = this.GetRecognizedBusinessUnitFuzzy(allFacts, counterpartyType);
        var recognizedCounterparty = this.GetRecognizedCounterpartyFuzzy(allFacts, counterpartyType);
        
        // Если по одному факту найдены и НОР, и КА. Объединить результаты в одной структуре. Если найдены по разным фактам, то НОР приоритетнее.
        if (recognizedBusinessUnit.BusinessUnit != null && recognizedCounterparty.Counterparty != null && Equals(recognizedBusinessUnit.Fact, recognizedCounterparty.Fact))
        {
          recognizedCounterparty.BusinessUnit = recognizedBusinessUnit.BusinessUnit;
          recognizedCounterparty.BusinessUnitProbability = recognizedBusinessUnit.BusinessUnitProbability;
          recognizedCounterparties.Add(recognizedCounterparty);
        }
        else if (recognizedBusinessUnit.BusinessUnit != null)
          recognizedCounterparties.Add(recognizedBusinessUnit);
        else if (recognizedCounterparty.Counterparty != null)
          recognizedCounterparties.Add(recognizedCounterparty);
      }
      
      return recognizedCounterparties;
    }

    /// <summary>
    /// Заполнить подписанта нашей стороны в акте выполненных работ с использованием нечеткого поиска.
    /// </summary>
    /// <param name="contractStatement">Акт выполненных работ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillOurSignatoryForContractStatementFuzzy(FinancialArchive.IContractStatement contractStatement, IDocumentInfo documentInfo)
    {
      if (contractStatement.BusinessUnit == null)
        return;

      var recognizedSignatory = this.GetOurSignatoryFuzzy(documentInfo.ArioDocument.Facts,
                                                          ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer,
                                                          contractStatement);
      if (recognizedSignatory.Employee != null)
      {
        contractStatement.OurSignatory = recognizedSignatory.Employee;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(documentInfo.ArioDocument.RecognitionInfo,
                                                                          recognizedSignatory.Fact,
                                                                          ArioGrammars.CounterpartyFact.SignatoryField,
                                                                          contractStatement.Info.Properties.OurSignatory.Name,
                                                                          contractStatement.OurSignatory,
                                                                          recognizedSignatory.Probability);
      }
    }

    /// <summary>
    /// Заполнить подписанта со стороны контрагента в акте выполненных работ с использованием нечеткого поиска.
    /// </summary>
    /// <param name="contractStatement">Акт выполненных работ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    [Public]
    public virtual void FillCounterpartySignatoryForContractStatementFuzzy(FinancialArchive.IContractStatement contractStatement, IDocumentInfo documentInfo)
    {
      if (contractStatement.Counterparty == null)
        return;

      var recognizedSignatory = this.GetCounterpartySignatoryFuzzy(documentInfo.ArioDocument.Facts,
                                                                   ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller,
                                                                   contractStatement.Counterparty);
      if (recognizedSignatory.Contact != null)
      {
        contractStatement.CounterpartySignatory = recognizedSignatory.Contact;
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(documentInfo.ArioDocument.RecognitionInfo,
                                                                          recognizedSignatory.Fact,
                                                                          ArioGrammars.CounterpartyFact.SignatoryField,
                                                                          contractStatement.Info.Properties.CounterpartySignatory.Name,
                                                                          contractStatement.CounterpartySignatory,
                                                                          recognizedSignatory.Probability);
      }
    }

    #endregion

    #region Договорные документы, финансовые документы, счет на оплату

    /// <summary>
    /// Заполнить свойства во входящем счете на оплату с использованием нечеткого поиска.
    /// </summary>
    /// <param name="incomingInvoice">Входящий счет.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    [Public]
    public virtual void FillIncomingInvoicePropertiesFuzzy(Contracts.IIncomingInvoice incomingInvoice,
                                                           IDocumentInfo documentInfo,
                                                           IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(incomingInvoice);
      
      // Подразделение.
      incomingInvoice.Department = Company.PublicFunctions.Department.GetDepartment(responsible);

      // Сумма и валюта.
      this.FillAccountingDocumentAmountAndCurrency(incomingInvoice, documentInfo);
      
      // НОР и КА.
      var arioCounterpartyTypes = new List<string>()
      {
        ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller,
        ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer
      };
      
      var recognizedCounterparties = this.GetRecognizedAccountingDocumentCounterpartiesFuzzy(arioDocument.Facts, arioCounterpartyTypes);
      var seller = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller).FirstOrDefault();
      var buyer = recognizedCounterparties.Where(m => m.Type == ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer).FirstOrDefault();
      var nonType = recognizedCounterparties.Where(m => m.Type == string.Empty).ToList();
      
      var recognizedDocumentParties = this.GetDocumentParties(buyer, seller, nonType, responsible);
      
      this.FillAccountingDocumentParties(incomingInvoice, documentInfo, recognizedDocumentParties);
      
      // Договор.
      this.FillIncomingInvoiceContractFuzzy(incomingInvoice, arioDocument);
      
      // Номер и дата.
      var props = incomingInvoice.Info.Properties;
      this.FillDocumentNumberAndDateFuzzy(incomingInvoice, arioDocument, ArioGrammars.FinancialDocumentFact.Name,
                                          props.Number, ArioGrammars.FinancialDocumentFact.NumberField,
                                          props.Date, ArioGrammars.FinancialDocumentFact.DateField);
    }

    /// <summary>
    /// Заполнить свойства договора по результатам обработки Ario.
    /// </summary>
    /// <param name="contract">Договор.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    [Public]
    public virtual void FillContractPropertiesFuzzy(Contracts.IContract contract,
                                                    IDocumentInfo documentInfo,
                                                    Sungero.Company.IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(contract);
      
      this.FillContractualDocumentAmountAndCurrency(contract, documentInfo);
      
      // Заполнить данные нашей стороны и корреспондента.
      this.FillContractualDocumentPartiesFuzzy(contract, documentInfo, responsible);
      
      // Заполнить ответственного после заполнения НОР и КА, чтобы вычислялась НОР из фактов, а не по отв.
      // Если так не сделать, то НОР заполнится по ответственному и вычисления не будут выполняться.
      contract.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
      contract.ResponsibleEmployee = responsible;
      
      // Дата и номер.
      this.FillDocumentRegistrationData(contract, documentInfo, ArioGrammars.DocumentFact.Name, Docflow.Resources.DocumentWithoutNumber);
    }

    /// <summary>
    /// Заполнить свойства доп. соглашения по результатам обработки Ario с использованием нечеткого поиска.
    /// </summary>
    /// <param name="supAgreement">Доп. соглашение.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку поступивших документов.</param>
    [Public]
    public virtual void FillSupAgreementPropertiesFuzzy(Contracts.ISupAgreement supAgreement,
                                                        IDocumentInfo documentInfo,
                                                        Sungero.Company.IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      
      // Вид документа.
      this.FillDocumentKind(supAgreement);
      
      this.FillContractualDocumentAmountAndCurrency(supAgreement, documentInfo);
      
      // Заполнить данные нашей стороны и корреспондента.
      this.FillContractualDocumentPartiesFuzzy(supAgreement, documentInfo, responsible);
      
      // Договор (документ-основание).
      this.FillSupAgreementLeadingDocumentFuzzy(supAgreement, arioDocument);
      
      // Заполнить ответственного после заполнения НОР и КА, чтобы вычислялась НОР из фактов, а не по отв.
      // Если так не сделать, то НОР заполнится по ответственному и вычисления не будут выполняться.
      supAgreement.Department = Company.PublicFunctions.Department.GetDepartment(responsible);
      supAgreement.ResponsibleEmployee = responsible;
      
      // Дата и номер.
      this.FillDocumentRegistrationData(supAgreement, documentInfo, ArioGrammars.SupAgreementFact.Name, Docflow.Resources.DocumentWithoutNumber);
    }

    /// <summary>
    /// Заполнить стороны договорного документа с использованием нечеткого поиска.
    /// </summary>
    /// <param name="contractualDocument">Договорной документ.</param>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <param name="responsible">Ответственный за верификацию.</param>
    [Public]
    public virtual void FillContractualDocumentPartiesFuzzy(Contracts.IContractualDocument contractualDocument,
                                                            IDocumentInfo documentInfo,
                                                            IEmployee responsible)
    {
      var arioDocument = documentInfo.ArioDocument;
      var props = contractualDocument.Info.Properties;
      var businessUnitPropertyName = props.BusinessUnit.Name;
      var counterpartyPropertyName = props.Counterparty.Name;
      
      var signatoryFieldNames = new List<string>
      {
        ArioGrammars.CounterpartyFact.SignatorySurnameField,
        ArioGrammars.CounterpartyFact.SignatoryNameField,
        ArioGrammars.CounterpartyFact.SignatoryPatrnField
      };
      
      // Задать приоритет полей для поиска корреспондента. Вес определяет, как учитывается вероятность каждого поля.
      var weightedFields = new Dictionary<string, double>()
      {
        { ArioGrammars.CounterpartyFact.TinField, 0.30 },
        { ArioGrammars.CounterpartyFact.TrrcField, 0.05 },
        { ArioGrammars.CounterpartyFact.NameField, 0.25 },
        { ArioGrammars.CounterpartyFact.LegalFormField, 0.05 },
        { ArioGrammars.CounterpartyFact.SignatorySurnameField, 0.20 },
        { ArioGrammars.CounterpartyFact.SignatoryNameField, 0.10 },
        { ArioGrammars.CounterpartyFact.SignatoryPatrnField, 0.05 }
      };
      
      var defaultBusinessUnit = Docflow.PublicFunctions.Module.GetDefaultBusinessUnit(responsible);
      // Если НОР не указана в карточке ответственного, проверить, является ли НОР единственной в справочнике.
      var allBusinessUnits = Sungero.Company.BusinessUnits.GetAll();
      if (allBusinessUnits.Count() == 1)
        defaultBusinessUnit = allBusinessUnits.Single();
      var defaultBusinessUnitId = defaultBusinessUnit != null ? defaultBusinessUnit.Id : 0;
      
      var allCounterpartyFacts = Commons.PublicFunctions.Module.GetOrderedFactsByFieldPriorities(arioDocument.Facts, ArioGrammars.CounterpartyFact.Name, weightedFields);
      var resultsForBusinessUnit = new List<IArioFactElasticsearchData>();
      var resultsForCounterparty = new List<IArioFactElasticsearchData>();
      foreach (var fact in allCounterpartyFacts)
      {
        var searchResultForBusinessUnit = this.SearchBusinessUnitFuzzy(fact);
        if (searchResultForBusinessUnit.EntityIds.Count == 1 &&
            !resultsForBusinessUnit.Any(x => x.EntityIds.First() == searchResultForBusinessUnit.EntityIds.First()))
        {
          searchResultForBusinessUnit.AggregateFieldsProbability = Commons.PublicFunctions.Module.GetAggregateFieldsProbability(fact, weightedFields);
          resultsForBusinessUnit.Add(searchResultForBusinessUnit);
        }
        
        var searchResultForCounterparty = this.SearchCounterpartyFuzzy(fact);
        if (searchResultForCounterparty.EntityIds.Count == 1 &&
            !resultsForCounterparty.Any(x => x.EntityIds.First() == searchResultForCounterparty.EntityIds.First()))
        {
          searchResultForCounterparty.AggregateFieldsProbability = Commons.PublicFunctions.Module.GetAggregateFieldsProbability(fact, weightedFields);
          resultsForCounterparty.Add(searchResultForCounterparty);
        }
      }

      var recognizedBusinessUnitInfo = ArioFactElasticsearchData.Create();
      recognizedBusinessUnitInfo.EntityIds = new List<int>();
      // Если найденная НОР соответствует НОР по умолчанию.
      var foundDefaultBusinessUnit = resultsForBusinessUnit.Where(l => l.EntityIds.First() == defaultBusinessUnitId);
      if (foundDefaultBusinessUnit.Any())
        recognizedBusinessUnitInfo = foundDefaultBusinessUnit.FirstOrDefault();
      // Взять первую НОР из найденных, если нет НОР по умолчанию.
      if (resultsForBusinessUnit.Any() && !recognizedBusinessUnitInfo.EntityIds.Any())
        recognizedBusinessUnitInfo = resultsForBusinessUnit.FirstOrDefault();
      
      if (recognizedBusinessUnitInfo.EntityIds.Count == 1)
      {
        contractualDocument.BusinessUnit = Company.BusinessUnits.GetAll(x => x.Id == recognizedBusinessUnitInfo.EntityIds.First()).FirstOrDefault();
        
        var businessUnitFieldNames = recognizedBusinessUnitInfo.Fact.Fields
          .Where(fl => weightedFields.ContainsKey(fl.Name))
          .Select(fl => fl.Name)
          .Distinct()
          .ToList();
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                                recognizedBusinessUnitInfo.Fact,
                                                                                businessUnitFieldNames,
                                                                                businessUnitPropertyName,
                                                                                contractualDocument.BusinessUnit,
                                                                                recognizedBusinessUnitInfo.AggregateFieldsProbability,
                                                                                null);
      }
      
      // Заполнить подписанта.
      var recognizedBusinessUnit = RecognizedCounterparty.Create(contractualDocument.BusinessUnit,
                                                                 null,
                                                                 recognizedBusinessUnitInfo.Fact,
                                                                 string.Empty,
                                                                 recognizedBusinessUnitInfo.AggregateFieldsProbability,
                                                                 null);
      var ourSignatory = this.GetSignatoryForContractualDocumentFuzzy(contractualDocument,
                                                                      arioDocument.Facts,
                                                                      recognizedBusinessUnit,
                                                                      signatoryFieldNames,
                                                                      true);
      if (ourSignatory.Employee != null)
        this.FillOurSignatoryForContractualDocument(contractualDocument, documentInfo, ourSignatory, signatoryFieldNames);

      if (contractualDocument.BusinessUnit == null)
      {
        contractualDocument.BusinessUnit = Company.BusinessUnits.GetAll(x => x.Id == defaultBusinessUnitId).FirstOrDefault();
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                                null,
                                                                                null,
                                                                                businessUnitPropertyName,
                                                                                contractualDocument.BusinessUnit,
                                                                                Module.PropertyProbabilityLevels.Min,
                                                                                null);
      }

      // Заполнить данные контрагента.
      var recognizedCounterpartyInfo = resultsForCounterparty
        .Where(x => (contractualDocument.BusinessUnit == null || (contractualDocument.BusinessUnit != null &&
                                                                  x.EntityIds.First() != contractualDocument.BusinessUnit.Company.Id)))
        .FirstOrDefault();

      if (recognizedCounterpartyInfo != null)
      {
        contractualDocument.Counterparty = Parties.Counterparties.GetAll(x => x.Id == recognizedCounterpartyInfo.EntityIds.First()).FirstOrDefault();
        var counterpartyFieldNames = recognizedCounterpartyInfo.Fact.Fields
          .Where(fl => weightedFields.ContainsKey(fl.Name))
          .Select(fl => fl.Name)
          .Distinct()
          .ToList();
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                                recognizedCounterpartyInfo.Fact,
                                                                                counterpartyFieldNames,
                                                                                counterpartyPropertyName,
                                                                                contractualDocument.Counterparty,
                                                                                recognizedCounterpartyInfo.AggregateFieldsProbability,
                                                                                null);
      }
      // Заполнить подписанта от КА.
      var recognizedCounterparty = RecognizedCounterparty.Create(null,
                                                                 contractualDocument.Counterparty,
                                                                 recognizedCounterpartyInfo != null ? recognizedCounterpartyInfo.Fact : null,
                                                                 string.Empty,
                                                                 null,
                                                                 recognizedCounterpartyInfo != null ? recognizedCounterpartyInfo.AggregateFieldsProbability : 0);
      
      var signedBy = this.GetSignatoryForContractualDocumentFuzzy(contractualDocument,
                                                                  arioDocument.Facts,
                                                                  recognizedCounterparty,
                                                                  signatoryFieldNames,
                                                                  false);
      
      // При заполнении поля подписал, если контрагент не заполнен, он подставляется из подписанта.
      if (signedBy.Contact != null)
        this.FillCounterpartySignatoryForContractualDocument(contractualDocument, documentInfo, signedBy, signatoryFieldNames);
    }

    /// <summary>
    /// Заполнить ведущий договор финансового документа.
    /// </summary>
    /// <param name="document">Финансовый документ.</param>
    /// <param name="arioDocument">Информация о документе Ario.</param>
    [Public]
    public virtual void FillAccountingDocumentLeadingDocumentFuzzy(IAccountingDocumentBase document, IArioDocument arioDocument)
    {
      var recognizedDocument = this.GetLeadingContractualDocumentFuzzy(arioDocument.Facts, document.BusinessUnit, document.Counterparty);
      if (recognizedDocument.Contract != null)
      {
        document.LeadingDocument = recognizedDocument.Contract;
        Sungero.Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                                  recognizedDocument.Fact,
                                                                                  null,
                                                                                  document.Info.Properties.LeadingDocument.Name,
                                                                                  document.LeadingDocument,
                                                                                  recognizedDocument.Probability);
      }
    }

    /// <summary>
    /// Заполнить ведущий договор во входящем счете.
    /// </summary>
    /// <param name="incomingInvoice">Входящий счет.</param>
    /// <param name="arioDocument">Информация о документе Ario.</param>
    [Public]
    public virtual void FillIncomingInvoiceContractFuzzy(IIncomingInvoice incomingInvoice, IArioDocument arioDocument)
    {
      var recognizedDocument = this.GetLeadingContractualDocumentFuzzy(arioDocument.Facts, incomingInvoice.BusinessUnit, incomingInvoice.Counterparty);
      if (recognizedDocument.Contract != null)
      {
        incomingInvoice.Contract = recognizedDocument.Contract;
        Sungero.Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                                  recognizedDocument.Fact,
                                                                                  null,
                                                                                  incomingInvoice.Info.Properties.Contract.Name,
                                                                                  incomingInvoice.Contract,
                                                                                  recognizedDocument.Probability);
      }
    }

    /// <summary>
    /// Заполнить ведущий договор в доп. соглашении.
    /// </summary>
    /// <param name="supAgreement">Доп. соглашение.</param>
    /// <param name="arioDocument">Информация о документе Ario.</param>
    [Public]
    public virtual void FillSupAgreementLeadingDocumentFuzzy(Contracts.ISupAgreement supAgreement, IArioDocument arioDocument)
    {
      // Получить факты, содержащие номер и/или дату договора. Приоритетнее факты, содержащие оба поля.
      var weightedFields = new Dictionary<string, double>()
      {
        { ArioGrammars.SupAgreementFact.DocumentBaseDateField, 0.5 },
        { ArioGrammars.SupAgreementFact.DocumentBaseNumberField, 0.5 }
      };
      var facts = Commons.PublicFunctions.Module.GetOrderedFactsByFieldPriorities(arioDocument.Facts, ArioGrammars.SupAgreementFact.Name, weightedFields);

      if (!facts.Any())
        return;
      
      // Подготовить список договоров для поска. НОР обязательна, контрагент - нет.
      if (supAgreement.BusinessUnit == null)
        return;
      
      var contracts = Contracts.ContractBases.GetAll(d => Equals(d.BusinessUnit, supAgreement.BusinessUnit));
      if (supAgreement.Counterparty != null)
        contracts = contracts.Where(d => Equals(d.Counterparty, supAgreement.Counterparty));
      if (!contracts.Any())
        return;
      
      // Найти договор по номеру и дате.
      var recognizedDocument = this.GetRecognizedDocumentFuzzy(facts, contracts, ArioGrammars.SupAgreementFact.DocumentBaseNumberField,
                                                               ArioGrammars.SupAgreementFact.DocumentBaseDateField);
      
      if (recognizedDocument.Document != null)
      {
        supAgreement.LeadingDocument = Contracts.ContractBases.As(recognizedDocument.Document);
        Sungero.Commons.PublicFunctions.EntityRecognitionInfo.LinkFactFieldsAndProperty(arioDocument.RecognitionInfo,
                                                                                        recognizedDocument.Fact,
                                                                                        weightedFields.Select(fl => fl.Key).ToList(),
                                                                                        supAgreement.Info.Properties.LeadingDocument.Name,
                                                                                        supAgreement.LeadingDocument,
                                                                                        recognizedDocument.Probability,
                                                                                        null);
      }
    }

    /// <summary>
    /// Получить подписанта нашей стороны/подписанта контрагента для договорного документа по фактам и НОР с использованием нечеткого поиска.
    /// </summary>
    /// <param name="document">Договорной документ.</param>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <param name="recognizedOrganization">Структура с НОР, КА, фактом и признаком доверия.</param>
    /// <param name="signatoryFieldNames">Список наименований полей с ФИО подписанта.</param>
    /// <param name="isOurSignatory">Признак поиска нашего подписанта (true) или подписанта КА (false).</param>
    /// <returns>Структура, содержащая сотрудника или контакт, факт и вероятность.</returns>
    [Public]
    public virtual IRecognizedOfficial GetSignatoryForContractualDocumentFuzzy(Contracts.IContractualDocument document,
                                                                               List<IArioFact> facts,
                                                                               IRecognizedCounterparty recognizedOrganization,
                                                                               List<string> signatoryFieldNames,
                                                                               bool isOurSignatory = false)
    {
      var props = document.Info.Properties;
      var signatoryFacts = Commons.PublicFunctions.Module.GetFacts(facts, ArioGrammars.CounterpartyFact.Name);
      var signedBy = RecognizedOfficial.Create(null, null, null, Module.PropertyProbabilityLevels.Min);
      
      if (!signatoryFacts.Any())
        return signedBy;
      
      if (recognizedOrganization != null)
      {
        var organizationFact = recognizedOrganization.Fact;
        if (organizationFact != null)
        {
          signedBy.Fact = organizationFact;
          var isOrganizationFactWithSignatory = Commons.PublicFunctions.Module.GetFields(organizationFact, signatoryFieldNames).Any();
          
          var recognizedOrganizationNaming = this.GetRecognizedPersonNaming(organizationFact,
                                                                            ArioGrammars.CounterpartyFact.SignatorySurnameField,
                                                                            ArioGrammars.CounterpartyFact.SignatoryNameField,
                                                                            ArioGrammars.CounterpartyFact.SignatoryPatrnField);
          if (isOrganizationFactWithSignatory)
            return isOurSignatory
              ? this.GetRecognizedOurSignatoryForContractualDocumentFuzzy(document, organizationFact, recognizedOrganizationNaming)
              : this.GetRecognizedContactFuzzy(organizationFact, document.Counterparty, recognizedOrganizationNaming);
        }
      }
      
      signatoryFacts = signatoryFacts
        .Where(f => Commons.PublicFunctions.Module.GetFields(f, signatoryFieldNames).Any()).ToList();
      
      var organizationSignatory = new List<IRecognizedOfficial>();
      foreach (var signatoryFact in signatoryFacts)
      {
        IRecognizedOfficial signatory = null;
        
        var recognizedSignatoryNaming = this.GetRecognizedPersonNaming(signatoryFact,
                                                                       ArioGrammars.CounterpartyFact.SignatorySurnameField,
                                                                       ArioGrammars.CounterpartyFact.SignatoryNameField,
                                                                       ArioGrammars.CounterpartyFact.SignatoryPatrnField);
        if (isOurSignatory)
        {
          signatory = this.GetRecognizedOurSignatoryForContractualDocumentFuzzy(document, signatoryFact, recognizedSignatoryNaming);
          if (signatory.Employee != null)
            organizationSignatory.Add(signatory);
        }
        else
        {
          signatory = this.GetRecognizedContactFuzzy(signatoryFact, document.Counterparty, recognizedSignatoryNaming);
          if (signatory.Contact != null)
            organizationSignatory.Add(signatory);
        }
      }
      
      if (organizationSignatory.Any())
        signedBy = organizationSignatory.OrderByDescending(x => x.Probability).FirstOrDefault();

      return signedBy;
    }

    /// <summary>
    /// Получить контактное лицо по данным из факта и контрагента с использованием нечеткого поиска.
    /// </summary>
    /// <param name="contactFact">Факт, содержащий сведения о контакте.</param>
    /// <param name="counterparty">Контрагент.</param>
    /// <param name="recognizedContactNaming">Полное и краткое ФИО персоны.</param>
    /// <returns>Структура, содержащая контактное лицо, факт и вероятность.</returns>
    [Public]
    public virtual IRecognizedOfficial GetRecognizedContactFuzzy(IArioFact contactFact,
                                                                 ICounterparty counterparty,
                                                                 IRecognizedPersonNaming recognizedContactNaming)
    {
      var signedBy = RecognizedOfficial.Create(null, Sungero.Parties.Contacts.Null, contactFact, Module.PropertyProbabilityLevels.Min);
      
      if (contactFact == null)
        return signedBy;
      
      var contact = Contacts.Null;
      
      var fullName = recognizedContactNaming.FullName;
      var shortName = recognizedContactNaming.ShortName;
      var probability = Module.PropertyProbabilityLevels.Min;
      
      if (!string.IsNullOrWhiteSpace(fullName) || !string.IsNullOrWhiteSpace(shortName))
      {
        var counterpartyId = counterparty != null ? counterparty.Id : 0;
        contact = Parties.PublicFunctions.Contact.GetContactsByNameFuzzy(fullName, counterpartyId);
        
        if (contact == null)
          contact = Parties.PublicFunctions.Contact.GetContactsByNameFuzzy(shortName, counterpartyId);
        else
          probability = Module.PropertyProbabilityLevels.UpperMiddle;
      }
      
      if (contact == null)
        return signedBy;
      
      signedBy.Contact = contact;
      signedBy.Fact = contactFact;
      signedBy.Probability = probability;
      
      return signedBy;
    }

    /// <summary>
    /// Получить подписанта нашей стороны для договорного документа по факту с использованием нечеткого поиска.
    /// </summary>
    /// <param name="document">Договорной документ.</param>
    /// <param name="ourSignatoryFact">Факт, содержащий сведения о подписанте нашей стороны.</param>
    /// <param name="recognizedOurSignatoryNaming">Полное и краткое ФИО подписанта нашей стороны.</param>
    /// <returns>Структура, содержащая сотрудника, факт и вероятность.</returns>
    [Public]
    public virtual IRecognizedOfficial GetRecognizedOurSignatoryForContractualDocumentFuzzy(Contracts.IContractualDocument document,
                                                                                            IArioFact ourSignatoryFact,
                                                                                            IRecognizedPersonNaming recognizedOurSignatoryNaming)
    {
      var signedBy = RecognizedOfficial.Create(null, null, ourSignatoryFact, Module.PropertyProbabilityLevels.Min);
      var businessUnit = document.BusinessUnit;

      if (ourSignatoryFact == null)
        return signedBy;
      
      var fullName = recognizedOurSignatoryNaming.FullName;
      var shortName = recognizedOurSignatoryNaming.ShortName;
      var foundEmployees = Company.PublicFunctions.Employee.GetEmployeesByNameFuzzy(fullName, businessUnit != null ? businessUnit.Id : 0);
      
      // Проверить у найденного сотрудника право подписи на документ.
      foundEmployees = foundEmployees.Where(x => Docflow.PublicFunctions.OfficialDocument.Remote.CanSignByEmployee(document, x)).ToList();

      if (!foundEmployees.Any())
        return signedBy;
      
      if (foundEmployees.Count == 1)
      {
        signedBy.Employee = foundEmployees.FirstOrDefault();
        
        var employeeFoundByFullName = string.Equals(signedBy.Employee.Name, fullName, StringComparison.InvariantCultureIgnoreCase);
        var fullNameEqualsShortName = string.Equals(fullName, shortName, StringComparison.InvariantCultureIgnoreCase);
        if (employeeFoundByFullName && !fullNameEqualsShortName)
          signedBy.Probability = Constants.Module.PropertyProbabilityLevels.Max;
        else
          signedBy.Probability = Constants.Module.PropertyProbabilityLevels.LowerMiddle;
      }
      
      return signedBy;
    }

    #endregion

    #region Заполнение даты и номера

    /// <summary>
    /// Заполнить номер и дату документа по результатам обработки Ario.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="arioDocument">Информация о документе Ario.</param>
    /// <param name="factName">Наименование факта.</param>
    /// <param name="numberPropertyInfo">Информация о свойстве с номером.</param>
    /// <param name="numberFieldName">Наименование поля с номером.</param>
    /// <param name="datePropertyInfo">Информация о свойстве с датой.</param>
    /// <param name="dateFieldName">Наименование поля с датой.</param>
    [Public]
    public virtual void FillDocumentNumberAndDateFuzzy(IOfficialDocument document, IArioDocument arioDocument, string factName,
                                                       Sungero.Domain.Shared.IStringPropertyInfo numberPropertyInfo, string numberFieldName,
                                                       Sungero.Domain.Shared.IDateTimePropertyInfo datePropertyInfo, string dateFieldName)
    {
      // Получить факты с номером и/или датой документа. Приоритетнее факты, содержащие оба поля.
      var weightedFields = new Dictionary<string, double>()
      {
        { numberFieldName, 0.5 },
        { dateFieldName, 0.5 }
      };
      var facts = Commons.PublicFunctions.Module.GetOrderedFactsByFieldPriorities(arioDocument.Facts, factName, weightedFields);
      
      // Заполнить номер.
      var numberFact = facts.FirstOrDefault(f => f.Fields.Any(fl => fl.Name == numberFieldName));
      if (numberFact != null)
      {
        var recognizedNumber = this.GetRecognizedNumber(new List<IArioFact>() { numberFact }, factName, numberFieldName, numberPropertyInfo);
        document.SetPropertyValue(numberPropertyInfo.Name, recognizedNumber.Number);
        Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                          recognizedNumber.Fact,
                                                                          numberFieldName,
                                                                          numberPropertyInfo.Name,
                                                                          recognizedNumber.Number,
                                                                          recognizedNumber.Probability);
      }
      
      // Заполнить дату.
      var dateFact = facts.FirstOrDefault(f => f.Fields.Any(fl => fl.Name == dateFieldName));
      if (dateFact != null)
      {
        var recognizedDate = this.GetRecognizedDate(new List<IArioFact>() { dateFact }, factName, dateFieldName);
        if (recognizedDate.Fact != null)
        {
          document.SetPropertyValue(datePropertyInfo.Name, recognizedDate.Date);
          Commons.PublicFunctions.EntityRecognitionInfo.LinkFactAndProperty(arioDocument.RecognitionInfo,
                                                                            recognizedDate.Fact,
                                                                            dateFieldName,
                                                                            datePropertyInfo.Name,
                                                                            recognizedDate.Date,
                                                                            recognizedDate.Probability);
        }
      }
    }

    #endregion

    #region Получение ведущего документа

    /// <summary>
    /// Получить ведущий договор из фактов.
    /// </summary>
    /// <param name="facts">Список фактов.</param>
    /// <param name="businessUnit">Наша организация.</param>
    /// <param name="counterparty">Контрагент.</param>
    /// <returns>Структура, содержащая ведущий договорной документ, факт и признак доверия.</returns>
    [Public]
    public virtual IRecognizedContract GetLeadingContractualDocumentFuzzy(List<IArioFact> facts, IBusinessUnit businessUnit, ICounterparty counterparty)
    {
      var result = RecognizedContract.Create(Contracts.ContractualDocuments.Null, null, null);
      if (!facts.Any())
        return result;

      var fieldNames = new List<string>()
      {
        ArioGrammars.FinancialDocumentFact.DocumentBaseNameField,
        ArioGrammars.FinancialDocumentFact.DocumentBaseDateField
      };
      
      var baseDocumentFacts = facts
        .Where(f => f.Name == ArioGrammars.FinancialDocumentFact.Name &&
               f.Fields.Any(fl => fieldNames.Contains(fl.Name) && !string.IsNullOrEmpty(fl.Value)))
        .ToList();

      if (!baseDocumentFacts.Any())
        return result;

      // Получить список договоров для поиска. НОР обязательна, контрагент - нет.
      if (businessUnit == null)
        return result;

      var contractualDocuments = Contracts.ContractualDocuments.GetAll(d => Equals(d.BusinessUnit, businessUnit));
      if (counterparty != null)
        contractualDocuments = contractualDocuments.Where(d => Equals(d.Counterparty, counterparty));
      if (!contractualDocuments.Any())
        return result;
      
      // Если найдено несколько документов-оснований, отсортировать их по важности. Более приоритетные содержат слово "договор" в названии.
      if (baseDocumentFacts.Count > 1)
      {
        var contractualBaseNames = Resources.ContractualBaseDocumentNamesPriority.ToString().Split(';');
        
        var factScores = new Dictionary<IArioFact, double>();
        foreach (var fact in baseDocumentFacts)
        {
          var baseNumberDateFields = fact.Fields.Where(fl => fieldNames.Contains(fl.Name) && !string.IsNullOrEmpty(fl.Value)).ToList();
          var factProbability = baseNumberDateFields.Sum(fl => fl.Probability) / 2;
          var score = Math.Round(baseNumberDateFields.Count + (factProbability / 100), 4);
          var baseDocumentNameField = fact.Fields.FirstOrDefault(fl => fl.Name == ArioGrammars.FinancialDocumentFact.DocumentBaseNameField);
          if (baseDocumentNameField != null)
          {
            var baseDocumentNameFieldValue = baseDocumentNameField.Value.Trim().ToLower();
            for (var i = 0; i < contractualBaseNames.Length; i++)
            {
              if (baseDocumentNameFieldValue.Contains(contractualBaseNames[i]))
              {
                score += 10 * (contractualBaseNames.Length - i);
                if (baseDocumentNameFieldValue.StartsWith(contractualBaseNames[i]))
                  score += 100;
                break;
              }
            }
          }
          factScores.Add(fact, score);
        }
        baseDocumentFacts = factScores.OrderByDescending(x => x.Value).Select(x => x.Key).ToList();
      }

      // Найти документ по номеру и дате.
      var recognizedDocument = this.GetRecognizedDocumentFuzzy(baseDocumentFacts,
                                                               contractualDocuments,
                                                               ArioGrammars.FinancialDocumentFact.DocumentBaseNumberField,
                                                               ArioGrammars.FinancialDocumentFact.DocumentBaseDateField);
      
      if (recognizedDocument.Document != null)
      {
        // Создать копию факта для подсветки полей документа-основания.
        var fieldsForLink = recognizedDocument.Fact.Fields.Where(fl => fieldNames.Contains(fl.Name)).ToList();
        result.Fact = ArioFact.Create(recognizedDocument.Fact.Id, recognizedDocument.Fact.Name, fieldsForLink);
        result.Contract = Contracts.ContractualDocuments.As(recognizedDocument.Document);
        result.Probability = recognizedDocument.Probability;
      }
      else if (counterparty != null)
      {
        // Если договор по номеру и дате не нашли, но есть единственный действующий для указанного контрагента, то подставить его.
        contractualDocuments = contractualDocuments.Where(x => x.LifeCycleState.HasValue &&
                                                          x.LifeCycleState.Value == Contracts.ContractualDocument.LifeCycleState.Active);
        if (contractualDocuments.Count() == 1)
        {
          result.Contract = contractualDocuments.Single();
          result.Probability = Module.PropertyProbabilityLevels.Min;
        }
      }
      
      return result;
    }

    /// <summary>
    /// Получить документ из фактов по номеру и дате.
    /// </summary>
    /// <param name="orderedFacts">Список фактов, отсортированных для поиска документа.</param>
    /// <param name="filteredDocuments">Предварительно отфильтрованный запрос документов.</param>
    /// <param name="numberFieldName">Имя поля факта с номером документа.</param>
    /// <param name="dateFieldName">Имя поля факта с датой документа.</param>
    /// <returns>Распознанный документ.</returns>
    [Public]
    public virtual IRecognizedDocument GetRecognizedDocumentFuzzy(List<IArioFact> orderedFacts, IQueryable<IOfficialDocument> filteredDocuments,
                                                                  string numberFieldName, string dateFieldName)
    {
      var recognizedDocument = RecognizedDocument.Create();
      if (!orderedFacts.Any())
        return recognizedDocument;

      if (!filteredDocuments.Any())
        return recognizedDocument;

      foreach (var fact in orderedFacts)
      {
        var probability = 0d;
        var responseToDateField = fact.Fields
          .Where(fl => fl.Name == dateFieldName && !string.IsNullOrWhiteSpace(fl.Value))
          .OrderByDescending(fl => fl.Probability)
          .FirstOrDefault();
        if (responseToDateField != null)
        {
          DateTime responseToDate;
          if (Calendar.TryParseDate(responseToDateField.Value, out responseToDate))
          {
            filteredDocuments = filteredDocuments.Where(l => Equals(l.RegistrationDate, responseToDate));
            probability += responseToDateField.Probability;
          }
        }
        
        var responseToNumberField = fact.Fields
          .Where(fl => fl.Name == numberFieldName && !string.IsNullOrWhiteSpace(fl.Value))
          .OrderByDescending(fl => fl.Probability)
          .FirstOrDefault();
        if (responseToNumberField != null)
        {
          filteredDocuments = filteredDocuments.Where(l => string.Equals(l.RegistrationNumber, responseToNumberField.Value, StringComparison.InvariantCultureIgnoreCase));
          probability += responseToNumberField.Probability;
        }

        if (filteredDocuments.Count() == 1)
        {
          recognizedDocument.Document = filteredDocuments.First();
          recognizedDocument.Fact = fact;
          recognizedDocument.Probability = probability / 2;
          break;
        }
      }

      return recognizedDocument;
    }

    #endregion

    #region Получение контрагентов, контактов, подписантов, НОР
    /// <summary>
    /// Определить направление документа, НОР и КА у счет-фактуры с использованием нечеткого поиска.
    /// </summary>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <param name="responsible">Ответственный.</param>
    /// <returns>Результат подбора сторон сделки для документа.</returns>
    /// <remarks>Если НОР выступает продавцом, то счет-фактура - исходящая, иначе - входящая.</remarks>
    [Public]
    public virtual IRecognizedDocumentParties GetRecognizedTaxInvoicePartiesFuzzy(List<IArioFact> facts, IEmployee responsible)
    {
      // Определить является НОР продавцом (грузоотправителем) или покупателем (грузополучателем).
      var buyer = this.GetRecognizedBusinessUnitFuzzy(facts, ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer);
      if (buyer == null)
        buyer = this.GetRecognizedBusinessUnitFuzzy(facts, ArioGrammars.CounterpartyFact.CounterpartyTypes.Consignee);
      var buyerIsBusinessUnit = buyer != null && buyer.BusinessUnit != null;
      
      var seller = this.GetRecognizedBusinessUnitFuzzy(facts, ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller);
      if (seller == null)
        seller = this.GetRecognizedBusinessUnitFuzzy(facts, ArioGrammars.CounterpartyFact.CounterpartyTypes.Shipper);
      var sellerIsBusinessUnit = seller != null && seller.BusinessUnit != null;

      // Получить контргаента в зависимости от результатов поиска НОР.
      if (!buyerIsBusinessUnit)
      {
        buyer = this.GetRecognizedCounterpartyFuzzy(facts, ArioGrammars.CounterpartyFact.CounterpartyTypes.Buyer);
        if (buyer == null)
          buyer = this.GetRecognizedCounterpartyFuzzy(facts, ArioGrammars.CounterpartyFact.CounterpartyTypes.Consignee);
      }
      if (!sellerIsBusinessUnit)
      {
        seller = this.GetRecognizedCounterpartyFuzzy(facts, ArioGrammars.CounterpartyFact.CounterpartyTypes.Seller);
        if (seller == null)
          seller = this.GetRecognizedCounterpartyFuzzy(facts, ArioGrammars.CounterpartyFact.CounterpartyTypes.Shipper);
      }
      
      // Определить направление документа.
      var recognizedDocumentParties = RecognizedDocumentParties.Create();
      recognizedDocumentParties.ResponsibleEmployeeBusinessUnit = Docflow.PublicFunctions.Module.GetDefaultBusinessUnit(responsible);

      if (buyerIsBusinessUnit && sellerIsBusinessUnit)
      {
        // Мультинорность. Уточнить НОР по ответственному.
        if (Equals(seller.BusinessUnit, recognizedDocumentParties.ResponsibleEmployeeBusinessUnit))
        {
          // Исходящий документ.
          recognizedDocumentParties.IsDocumentOutgoing = true;
          recognizedDocumentParties.Counterparty = buyer;
          recognizedDocumentParties.BusinessUnit = seller;
        }
        else
        {
          // Входящий документ.
          recognizedDocumentParties.IsDocumentOutgoing = false;
          recognizedDocumentParties.Counterparty = seller;
          recognizedDocumentParties.BusinessUnit = buyer;
        }
      }
      else if (buyerIsBusinessUnit)
      {
        // Входящий документ.
        recognizedDocumentParties.IsDocumentOutgoing = false;
        recognizedDocumentParties.Counterparty = seller;
        recognizedDocumentParties.BusinessUnit = buyer;
      }
      else if (sellerIsBusinessUnit)
      {
        // Исходящий документ.
        recognizedDocumentParties.IsDocumentOutgoing = true;
        recognizedDocumentParties.Counterparty = buyer;
        recognizedDocumentParties.BusinessUnit = seller;
      }
      else
      {
        // НОР не найдена по фактам - НОР будет взята по ответственному.
        if (buyer != null && buyer.Counterparty != null && (seller == null || seller.Counterparty == null))
        {
          // Исходящий документ, потому что buyer - контрагент, а другой информации нет.
          recognizedDocumentParties.IsDocumentOutgoing = true;
          recognizedDocumentParties.Counterparty = buyer;
        }
        else
        {
          // Входящий документ.
          recognizedDocumentParties.IsDocumentOutgoing = false;
          recognizedDocumentParties.Counterparty = seller;
        }
      }

      return recognizedDocumentParties;
    }

    /// <summary>
    /// Поиск контрагента по извлеченным фактам с использованием нечеткого поиска.
    /// </summary>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <param name="counterpartyType">Тип контрагента.</param>
    /// <returns>Контрагент со связанным фактом.</returns>
    [Public]
    public virtual IRecognizedCounterparty GetRecognizedCounterpartyFuzzy(List<IArioFact> facts, string counterpartyType)
    {
      var recognizedCounterparty = RecognizedCounterparty.Create();

      // Задать приоритет полей для поиска. Вес определяет как учитывается вероятность каждого поля.
      var weightedFields = new Dictionary<string, double>()
      {
        { ArioGrammars.CounterpartyFact.TinField, 0.40 },
        { ArioGrammars.CounterpartyFact.TrrcField, 0.05 },
        { ArioGrammars.CounterpartyFact.PsrnField, 0.30 },
        { ArioGrammars.CounterpartyFact.NameField, 0.20 },
        { ArioGrammars.CounterpartyFact.LegalFormField, 0.05 }
      };
      
      // Отфильтровать факты по типу организации.
      var counterpartyFacts = facts
        .Where(f => f.Name == ArioGrammars.CounterpartyFact.Name &&
               f.Fields.Any(fl => fl.Name == ArioGrammars.CounterpartyFact.CounterpartyTypeField && fl.Value == counterpartyType) &&
               f.Fields.Any(fl => weightedFields.ContainsKey(fl.Name) && !string.IsNullOrEmpty(fl.Value)))
        .ToList();
      
      if (!counterpartyFacts.Any())
        return recognizedCounterparty;

      // Отсортировать факты по количеству и приоритету полей.
      var orderedCounterpartyFacts = Commons.PublicFunctions.Module.GetOrderedFactsByFieldPriorities(counterpartyFacts, ArioGrammars.CounterpartyFact.Name, weightedFields);
      
      // Получить контрагента по значениям полей факта с использованием Elasticsearch.
      foreach (var fact in orderedCounterpartyFacts)
      {
        var searchResult = this.SearchCounterpartyFuzzy(fact);

        if (searchResult.EntityIds.Count == 1)
        {
          var counterparty = Parties.Counterparties.GetAll(x => x.Id == searchResult.EntityIds.First()).FirstOrDefault();
          if (counterparty == null)
            continue;
          
          recognizedCounterparty.Fact = fact;
          recognizedCounterparty.Type = counterpartyType;
          recognizedCounterparty.Counterparty = counterparty;
          recognizedCounterparty.CounterpartyProbability = Commons.PublicFunctions.Module.GetAggregateFieldsProbability(fact, weightedFields);
          
          break;
        }
      }
      
      return recognizedCounterparty;
    }

    /// <summary>
    /// Поиск НОР по извлеченным фактам с использованием нечеткого поиска.
    /// </summary>
    /// <param name="facts">Извлеченные из документа факты.</param>
    /// <param name="counterpartyType">Тип факта с НОР.</param>
    /// <returns>НОР со связанным фактом.</returns>
    [Public]
    public virtual IRecognizedCounterparty GetRecognizedBusinessUnitFuzzy(List<IArioFact> facts, string counterpartyType)
    {
      var recognizedBusinessUnit = RecognizedCounterparty.Create();
      
      // Задать приоритет полей для поиска. Вес определяет как учитывается вероятность каждого поля.
      var weightedFields = new Dictionary<string, double>()
      {
        { ArioGrammars.CounterpartyFact.TinField, 0.40 },
        { ArioGrammars.CounterpartyFact.TrrcField, 0.05 },
        { ArioGrammars.CounterpartyFact.PsrnField, 0.30 },
        { ArioGrammars.CounterpartyFact.NameField, 0.20 },
        { ArioGrammars.CounterpartyFact.LegalFormField, 0.05 }
      };
      
      // Отфильтровать факты по типу организации.
      var counterpartyFacts = facts
        .Where(f => f.Name == ArioGrammars.CounterpartyFact.Name &&
               f.Fields.Any(fl => fl.Name == ArioGrammars.CounterpartyFact.CounterpartyTypeField && fl.Value == counterpartyType) &&
               f.Fields.Any(fl => weightedFields.ContainsKey(fl.Name) && !string.IsNullOrEmpty(fl.Value)))
        .ToList();
      
      if (!counterpartyFacts.Any())
        return recognizedBusinessUnit;

      // Отсортировать факты по количеству и приоритету полей.
      var orderedCounterpartyFacts = Commons.PublicFunctions.Module.GetOrderedFactsByFieldPriorities(counterpartyFacts, ArioGrammars.CounterpartyFact.Name, weightedFields);
      
      // Получить НОР по значениям полей факта с использованием Elasticsearch.
      foreach (var fact in orderedCounterpartyFacts)
      {
        var searchResult = this.SearchBusinessUnitFuzzy(fact);

        if (searchResult.EntityIds.Count == 1)
        {
          var businessUnit = Company.BusinessUnits.GetAll(x => x.Id == searchResult.EntityIds.First()).FirstOrDefault();
          if (businessUnit == null)
            continue;
          
          recognizedBusinessUnit.Fact = fact;
          recognizedBusinessUnit.Type = counterpartyType;
          recognizedBusinessUnit.BusinessUnit = businessUnit;
          recognizedBusinessUnit.BusinessUnitProbability = Commons.PublicFunctions.Module.GetAggregateFieldsProbability(fact, weightedFields);
          
          break;
        }
      }
      
      return recognizedBusinessUnit;
    }

    /// <summary>
    /// Найти корреспондента письма по значению полей факта с использованием нечеткого поиска.
    /// </summary>
    /// <param name="fact">Факт.</param>
    /// <returns>Результаты поиска корреспондента.</returns>
    [Public]
    public virtual IArioFactElasticsearchData SearchLetterCorrespondentFuzzy(IArioFact fact)
    {
      // Ограничить число записей, чтобы не выбирать из 10000 результатов, которые может вернуть Elasticsearch.
      const int LetterCorrespondentResultsLimit = 50;
      
      // Создать список запросов для поиска по полям входящего письма.
      var fieldQueries = new List<IArioFieldElasticsearchData>();

      // ИНН.
      var tin = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.LetterFact.TinField);
      if (tin != null)
      {
        // Проверить ИНН на валидность. По значению поля Ario, либо (если поле извлечено не было), с помощью функции справочника.
        var tinIsValid = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.LetterFact.TinIsValidField);
        if ((tinIsValid != null && tinIsValid.Value == "true") || (tinIsValid == null && string.IsNullOrEmpty(Parties.PublicFunctions.Counterparty.CheckTin(tin.Value, true))))
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(tin, "TIN", ElasticsearchTypes.Term));
      }

      // КПП. Только для уточнения организаций, найденных по ИНН.
      var trrc = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.LetterFact.TrrcField);
      if (tin != null && trrc != null)
        fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(trrc, "TRRC", ElasticsearchTypes.Term, true));
      
      // ОГРН.
      var psrn = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.LetterFact.PsrnField);
      if (psrn != null)
        fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(psrn, "PSRN", ElasticsearchTypes.Term));
      
      // Наименование.
      var names = Commons.PublicFunctions.Module.GetFields(fact, new List<string>() { ArioGrammars.LetterFact.CorrespondentNameField });
      if (names != null)
      {
        // Если есть несколько полей с наименованием, проверить их все, последовательно уточняя. Если не нашли по полному наименованию, искать по краткому.
        foreach (var name in names)
        {
          var normalizedValue = Sungero.Commons.PublicFunctions.Module.TrimSpecialSymbols(name.Value);
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "Name", ElasticsearchTypes.MatchAnd, normalizedValue));
          var fuzzyQuery = Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "Name", ElasticsearchTypes.FuzzyOr, normalizedValue);
          fuzzyQuery.ScoreMinLimit = Parties.PublicConstants.CompanyBase.ElasticsearchMinScore;
          fieldQueries.Add(fuzzyQuery);
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "ShortName", ElasticsearchTypes.MatchAnd, normalizedValue));
          fuzzyQuery = Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "ShortName", ElasticsearchTypes.FuzzyOr, normalizedValue);
          fuzzyQuery.ScoreMinLimit = Parties.PublicConstants.CompanyBase.ElasticsearchMinScore;
          fieldQueries.Add(fuzzyQuery);
        }
      }

      // Головная организация.
      var headCompany = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.LetterFact.HeadCompanyNameField);
      if (headCompany != null)
      {
        var normalizedValue = Sungero.Commons.PublicFunctions.Module.TrimSpecialSymbols(headCompany.Value);
        fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(headCompany, "HeadCompany", ElasticsearchTypes.FuzzyOr, normalizedValue));
      }
      
      // Адрес эл. почты.
      var emails = Commons.PublicFunctions.Module.GetFields(fact, new List<string>() { ArioGrammars.LetterFact.EmailField });
      if (emails != null)
      {
        foreach (var email in emails)
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(email, "Email", ElasticsearchTypes.MatchAnd));
      }
      
      // Сайт.
      var sites = Commons.PublicFunctions.Module.GetFields(fact, new List<string>() { ArioGrammars.LetterFact.WebsiteField });
      if (sites != null)
      {
        foreach (var site in sites)
        {
          var normalizedValue = Parties.PublicFunctions.Module.NormalizeSite(site.Value);
          if (!string.IsNullOrEmpty(normalizedValue))
            fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(site, "Homepage", ElasticsearchTypes.MatchAnd, normalizedValue));
        }
      }

      // Номер телефона.
      var phones = Commons.PublicFunctions.Module.GetFields(fact, new List<string>() { ArioGrammars.LetterFact.PhoneField });
      if (phones != null)
      {
        foreach (var phone in phones)
        {
          // По номеру телефона искать только для уточнения сущностей, найденных по другим полям.
          var normalizedValue = Parties.PublicFunctions.Module.NormalizePhone(phone.Value);
          if (!string.IsNullOrEmpty(normalizedValue))
            fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(phone, "Phones", ElasticsearchTypes.Wildcard, normalizedValue, true, false));
        }
      }

      // Выполнить запрос.
      var searchQuery = ArioFactElasticsearchData.Create();
      searchQuery.EntityName = CompanyBases.Info.Name;
      searchQuery.Fact = fact;
      searchQuery.Queries = fieldQueries;
      searchQuery.ScoredResultsLimit = LetterCorrespondentResultsLimit;
      return Commons.PublicFunctions.Module.ExecuteElasticsearchQuery(searchQuery);
    }

    /// <summary>
    /// Найти НОР письма по значению полей факта с использованием нечеткого поиска.
    /// </summary>
    /// <param name="fact">Факт.</param>
    /// <returns>Результаты поиска НОР.</returns>
    [Public]
    public virtual IArioFactElasticsearchData SearchLetterBusinessUnitFuzzy(IArioFact fact)
    {
      // Ограничить число записей, чтобы не выбирать из 10000 результатов, которые может вернуть Elasticsearch.
      const int LetterBusinessUnitResultsLimit = 50;
      
      // Создать список запросов для поиска по полям входящего письма.
      var fieldQueries = new List<IArioFieldElasticsearchData>();

      // ИНН.
      var tin = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.LetterFact.TinField);
      if (tin != null)
      {
        // Проверить ИНН на валидность. По значению поля Ario, либо (если поле извлечено не было), с помощью функции справочника.
        var tinIsValid = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.LetterFact.TinIsValidField);
        if ((tinIsValid != null && tinIsValid.Value == "true") || (tinIsValid == null && string.IsNullOrEmpty(Parties.PublicFunctions.Counterparty.CheckTin(tin.Value, true))))
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(tin, "TIN", ElasticsearchTypes.Term));
      }

      // КПП. Только для уточнения организаций, найденных по ИНН.
      var trrc = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.LetterFact.TrrcField);
      if (trrc != null)
        fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(trrc, "TRRC", ElasticsearchTypes.Term, true));

      // ОГРН.
      var psrn = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.LetterFact.PsrnField);
      if (psrn != null)
        fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(psrn, "PSRN", ElasticsearchTypes.Term));
      
      // Наименование.
      var names = Commons.PublicFunctions.Module.GetFields(fact, new List<string>() { ArioGrammars.LetterFact.CorrespondentNameField });
      if (names != null)
      {
        // Если есть несколько полей с наименованием, проверить их все, последовательно уточняя.
        foreach (var name in names)
        {
          var normalizedValue = Sungero.Commons.PublicFunctions.Module.TrimSpecialSymbols(name.Value);
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "Name", ElasticsearchTypes.MatchAnd, normalizedValue));
          var fuzzyQuery = Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "Name", ElasticsearchTypes.FuzzyOr, normalizedValue);
          fuzzyQuery.ScoreMinLimit = Company.PublicConstants.BusinessUnit.ElasticsearchMinScore;
          fieldQueries.Add(fuzzyQuery);
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "ShortName", ElasticsearchTypes.MatchAnd, normalizedValue));
          fuzzyQuery = Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "ShortName", ElasticsearchTypes.FuzzyOr, normalizedValue);
          fuzzyQuery.ScoreMinLimit = Company.PublicConstants.BusinessUnit.ElasticsearchMinScore;
          fieldQueries.Add(fuzzyQuery);
        }
      }
      
      // Выполнить запрос.
      var searchQuery = ArioFactElasticsearchData.Create();
      searchQuery.EntityName = Company.BusinessUnits.Info.Name;
      searchQuery.Fact = fact;
      searchQuery.Queries = fieldQueries;
      searchQuery.ScoredResultsLimit = LetterBusinessUnitResultsLimit;
      return Commons.PublicFunctions.Module.ExecuteElasticsearchQuery(searchQuery);
    }

    /// <summary>
    /// Найти контрагента по значению полей факта с использованием нечеткого поиска.
    /// </summary>
    /// <param name="fact">Факт.</param>
    /// <returns>Результаты поиска контрагента.</returns>
    [Public]
    public virtual IArioFactElasticsearchData SearchCounterpartyFuzzy(IArioFact fact)
    {
      // Подготовить данные для поиска полей.
      var fieldQueries = new List<IArioFieldElasticsearchData>();
      
      // ИНН.
      var tin = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.CounterpartyFact.TinField);
      if (tin != null)
      {
        // Проверить ИНН на валидность. По значению поля Ario, либо (если поле извлечено не было), с помощью функции справочника.
        var tinIsValid = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.CounterpartyFact.TinIsValidField);
        if ((tinIsValid != null && tinIsValid.Value == "true") || (tinIsValid == null && string.IsNullOrEmpty(Parties.PublicFunctions.Counterparty.CheckTin(tin.Value, true))))
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(tin, "TIN", ElasticsearchTypes.Term));
      }
      
      // КПП. Только для уточнения организаций, найденных по ИНН.
      var trrc = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.CounterpartyFact.TrrcField);
      if (tin != null && trrc != null)
        fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(trrc, "TRRC", ElasticsearchTypes.Term, true));

      // ОГРН.
      var psrn = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.CounterpartyFact.PsrnField);
      if (psrn != null)
        fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(psrn, "PSRN", ElasticsearchTypes.Term));
      
      // Наименование.
      var names = Commons.PublicFunctions.Module.GetFields(fact, new List<string>() { ArioGrammars.CounterpartyFact.NameField });
      if (names != null)
      {
        var legalForm = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.CounterpartyFact.LegalFormField);
        // Если есть несколько полей с наименованием, проверить их все, последовательно уточняя. Если не нашли по полному наименованию, искать по краткому.
        foreach (var name in names)
        {
          // Если есть поле с организационно-правовой формой, добавить значение ОПФ к наименованию.
          if (legalForm != null && !name.Value.Contains(legalForm.Value))
            name.Value = string.Format("{0} {1}", legalForm.Value, name.Value);
          var normalizedValue = Sungero.Commons.PublicFunctions.Module.TrimSpecialSymbols(name.Value);
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "Name", ElasticsearchTypes.MatchAnd, normalizedValue));
          var fuzzyQuery = Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "Name", ElasticsearchTypes.FuzzyOr, normalizedValue);
          fuzzyQuery.ScoreMinLimit = Parties.PublicConstants.CompanyBase.ElasticsearchMinScore;
          fieldQueries.Add(fuzzyQuery);
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "ShortName", ElasticsearchTypes.MatchAnd, normalizedValue));
          fuzzyQuery = Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "ShortName", ElasticsearchTypes.FuzzyOr, normalizedValue);
          fuzzyQuery.ScoreMinLimit = Parties.PublicConstants.CompanyBase.ElasticsearchMinScore;
          fieldQueries.Add(fuzzyQuery);
        }
      }

      // Выполнить запрос.
      var searchQuery = ArioFactElasticsearchData.Create();
      searchQuery.EntityName = Parties.CompanyBases.Info.Name;
      searchQuery.Fact = fact;
      searchQuery.Queries = fieldQueries;
      return Commons.PublicFunctions.Module.ExecuteElasticsearchQuery(searchQuery);
    }

    /// <summary>
    /// Найти НОР по значению полей факта с использованием нечеткого поиска.
    /// </summary>
    /// <param name="fact">Факт Ario.</param>
    /// <returns>Результаты поиска НОР.</returns>
    [Public]
    public virtual IArioFactElasticsearchData SearchBusinessUnitFuzzy(IArioFact fact)
    {
      // Подготовить данные для поиска полей.
      var fieldQueries = new List<IArioFieldElasticsearchData>();
      
      // ИНН.
      var tin = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.CounterpartyFact.TinField);
      if (tin != null)
      {
        // Проверить ИНН на валидность. По значению поля Ario, либо (если поле извлечено не было), с помощью функции справочника.
        var tinIsValid = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.CounterpartyFact.TinIsValidField);
        if ((tinIsValid != null && tinIsValid.Value == "true") || (tinIsValid == null && string.IsNullOrEmpty(Parties.PublicFunctions.Counterparty.CheckTin(tin.Value, true))))
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(tin, "TIN", ElasticsearchTypes.Term));
      }

      // КПП. Только для уточнения организаций, найденных по ИНН.
      var trrc = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.CounterpartyFact.TrrcField);
      if (tin != null && trrc != null)
        fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(trrc, "TRRC", ElasticsearchTypes.Term, true));

      // ОГРН.
      var psrn = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.CounterpartyFact.PsrnField);
      if (psrn != null)
        fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(psrn, "PSRN", ElasticsearchTypes.Term));
      
      // Наименование.
      var names = Commons.PublicFunctions.Module.GetFields(fact, new List<string>() { ArioGrammars.CounterpartyFact.NameField });
      if (names != null)
      {
        var legalForm = Commons.PublicFunctions.Module.GetField(fact, ArioGrammars.CounterpartyFact.LegalFormField);
        // Если есть несколько полей с наименованием, проверить их все, последовательно уточняя.
        foreach (var name in names)
        {
          // Если есть поле с организационно-правовой формой, добавить значение ОПФ к наименованию.
          if (legalForm != null && !name.Value.Contains(legalForm.Value))
            name.Value = string.Format("{0} {1}", legalForm.Value, name.Value);
          var normalizedValue = Sungero.Commons.PublicFunctions.Module.TrimSpecialSymbols(name.Value);
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "Name", ElasticsearchTypes.MatchAnd, normalizedValue));
          var fuzzyQuery = Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "Name", ElasticsearchTypes.FuzzyOr, normalizedValue);
          fuzzyQuery.ScoreMinLimit = Company.PublicConstants.BusinessUnit.ElasticsearchMinScore;
          fieldQueries.Add(fuzzyQuery);
          fieldQueries.Add(Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "ShortName", ElasticsearchTypes.MatchAnd, normalizedValue));
          fuzzyQuery = Commons.PublicFunctions.Module.CreateSearchFieldQuery(name, "ShortName", ElasticsearchTypes.FuzzyOr, normalizedValue);
          fuzzyQuery.ScoreMinLimit = Company.PublicConstants.BusinessUnit.ElasticsearchMinScore;
          fieldQueries.Add(fuzzyQuery);
        }
      }
      
      // Выполнить запрос.
      var searchQuery = ArioFactElasticsearchData.Create();
      searchQuery.EntityName = Company.BusinessUnits.Info.Name;
      searchQuery.Fact = fact;
      searchQuery.Queries = fieldQueries;
      return Commons.PublicFunctions.Module.ExecuteElasticsearchQuery(searchQuery);
    }

    /// <summary>
    /// Получить подписанта КА по факту Ario с учетом типа с использованием нечеткого поиска.
    /// </summary>
    /// <param name="facts">Список фактов Ario.</param>
    /// <param name="counterpartyType">Тип контрагента в Ario .</param>
    /// <param name="counterparty">Контрагент.</param>
    /// <returns>Найденный подписант.</returns>
    [Public]
    public virtual IRecognizedOfficial GetCounterpartySignatoryFuzzy(List<IArioFact> facts, string counterpartyType, ICounterparty counterparty)
    {
      if (counterparty != null)
      {
        // Получить все факты, содержащие поле с подписантом, с фильтром по типу организации.
        var signatoryFacts = Commons.PublicFunctions.Module.GetOrderedFactsByType(facts, ArioGrammars.CounterpartyFact.Name, counterpartyType,
                                                                                  ArioGrammars.CounterpartyFact.CounterpartyTypeField,
                                                                                  ArioGrammars.CounterpartyFact.SignatoryField);
        // Найти контакт по извлеченному ФИО.
        foreach (var fact in signatoryFacts)
        {
          var recognizedSignatory = this.GetRecognizedContactFuzzy(fact, ArioGrammars.CounterpartyFact.SignatoryField, counterparty.Id);
          if (recognizedSignatory.Contact != null)
            return recognizedSignatory;
        }
      }
      return RecognizedOfficial.Create();
    }

    /// <summary>
    /// Получить контакт входящего письма по фактам Ario с использованием нечеткого поиска.
    /// </summary>
    /// <param name="arioDocument">Документ Ario.</param>
    /// <param name="factType">Тип факта.</param>
    /// <param name="counterpartyId">ИД контрагента для поиска.</param>
    /// <returns>Найденный контакт.</returns>
    [Public, Obsolete("Используйте метод GetLetterContactFuzzy.")]
    public virtual IRecognizedOfficial GetRecognizedContactFuzzy(IArioDocument arioDocument, string factType, int counterpartyId)
    {
      var facts = Commons.PublicFunctions.Module.GetOrderedFactsByType(arioDocument.Facts, ArioGrammars.LetterPersonFact.Name, factType,
                                                                       ArioGrammars.LetterPersonFact.TypeField, ArioGrammars.LetterPersonFact.NameField);
      foreach (var fact in facts)
      {
        var recognizedContact = this.GetRecognizedContactFuzzy(fact, ArioGrammars.LetterPersonFact.NameField, counterpartyId);
        if (recognizedContact.Contact != null)
          return recognizedContact;
      }
      return RecognizedOfficial.Create();
    }

    /// <summary>
    /// Получить контакт по факту Ario с использованием нечеткого поиска.
    /// </summary>
    /// <param name="fact">Факт Ario с информацией о контактах.</param>
    /// <param name="fieldName">Имя поля, содержащего ФИО.</param>
    /// <param name="counterpartyId">ИД контрагента для поиска.</param>
    /// <returns>Найденный контакт.</returns>
    [Public]
    public virtual IRecognizedOfficial GetRecognizedContactFuzzy(IArioFact fact, string fieldName, int counterpartyId)
    {
      // В факте может быть несколько полей, содержащих ФИО. Искать до первого найденного контакта.
      var contactFields = fact.Fields
        .Where(fl => fl.Name == fieldName && !string.IsNullOrEmpty(fl.Value))
        .OrderByDescending(fl => fl.Probability);
      foreach (var field in contactFields)
      {
        var contact = Sungero.Parties.PublicFunctions.Contact.GetContactsByNameFuzzy(field.Value, counterpartyId);
        if (contact != null)
        {
          var recognizedContact = RecognizedOfficial.Create();
          recognizedContact.Contact = contact;
          recognizedContact.Fact = fact;
          recognizedContact.Probability = field.Probability;
          return recognizedContact;
        }
      }
      return RecognizedOfficial.Create();
    }

    /// <summary>
    /// Получить подписанта НОР по факту Ario с учетом типа с использованием нечеткого поиска.
    /// </summary>
    /// <param name="facts">Список фактов Ario.</param>
    /// <param name="counterpartyType">Тип контрагента.</param>
    /// <param name="document">Документ.</param>
    /// <returns>Найденный подписант.</returns>
    [Public]
    public virtual IRecognizedOfficial GetOurSignatoryFuzzy(List<IArioFact> facts, string counterpartyType, IOfficialDocument document)
    {
      if (document.BusinessUnit != null)
      {
        // Получить все факты, содержащие поле с подписантом, с фильтром по типу организации.
        var signatoryFacts = Commons.PublicFunctions.Module.GetOrderedFactsByType(facts, ArioGrammars.CounterpartyFact.Name, counterpartyType,
                                                                                  ArioGrammars.CounterpartyFact.CounterpartyTypeField,
                                                                                  ArioGrammars.CounterpartyFact.SignatoryField);
        // Найти сотрудника по извлеченному ФИО.
        foreach (var fact in signatoryFacts)
        {
          var recognizedEmployees = this.GetRecognizedEmployeesFuzzy(fact, ArioGrammars.CounterpartyFact.SignatoryField, document.BusinessUnit.Id);

          // Проверить у найденного сотрудника право подписи на документ.
          var recognizedSignatory = recognizedEmployees
            .FirstOrDefault(x => Docflow.PublicFunctions.OfficialDocument.Remote.CanSignByEmployee(document, x.Employee));
          
          if (recognizedSignatory != null)
            return recognizedSignatory;
        }
      }
      // Вернуть пустой список, если сотрудник по ФИО не найден или если найдено несколько однофамильцев.
      return RecognizedOfficial.Create();
    }

    /// <summary>
    /// Получить список сотрудников по факту Ario с использованием нечеткого поиска.
    /// </summary>
    /// <param name="fact">Факт Ario с информацией о сотрудниках.</param>
    /// <param name="fieldName">Имя поля факта, содержащего ФИО.</param>
    /// <param name="businessUnitid">ИД нашей организации для поиска.</param>
    /// <returns>Список найденных сотрудников.</returns>
    [Public]
    public virtual List<IRecognizedOfficial> GetRecognizedEmployeesFuzzy(IArioFact fact, string fieldName, int businessUnitid)
    {
      var recognizedEmployees = new List<IRecognizedOfficial>();
      
      // В факте может быть несколько полей, содержащих ФИО. По одному ФИО может быть найдено несколько сотрудников. Вернуть всех, исключив дубли.
      var employeeFields = fact.Fields
        .Where(fl => fl.Name == fieldName && !string.IsNullOrEmpty(fl.Value))
        .OrderByDescending(fl => fl.Probability);
      foreach (var field in employeeFields)
      {
        var employees = Sungero.Company.PublicFunctions.Employee.GetEmployeesByNameFuzzy(field.Value, businessUnitid);
        foreach (var employee in employees)
        {
          if (!recognizedEmployees.Any(x => Equals(x.Employee, employee)))
            recognizedEmployees.Add(RecognizedOfficial.Create(employee, null, fact, field.Probability));
        }
      }
      return recognizedEmployees;
    }

    #endregion

    #endregion

    #region Поиск по штрихкодам

    /// <summary>
    /// Получить документ по штрихкоду.
    /// </summary>
    /// <param name="documentInfo">Информация о документе.</param>
    /// <returns>Документ, если он найден в системе. Иначе - null.</returns>
    [Public]
    public virtual IOfficialDocument GetDocumentByBarcode(IDocumentInfo documentInfo)
    {
      var originalBlob = documentInfo.ArioDocument.OriginalBlob;
      var blobPackage = BlobPackages.GetAll(bp => bp.Blobs.Select(b => b.Blob).Contains(originalBlob)).FirstOrDefault();
      
      var arioDocument = documentInfo.ArioDocument;
      var document = Docflow.OfficialDocuments.Null;
      using (var body = new MemoryStream(arioDocument.BodyFromArio))
      {
        var docId = Functions.Module.SearchDocumentBarcodeIds(body).FirstOrDefault();
        // FOD на пустом List<int> вернет 0.
        if (docId != 0)
        {
          this.LogMessage(blobPackage, "Smart processing. GetDocumentByBarcode. Barcode contains document ID {0}.", docId);
          document = OfficialDocuments.GetAll().FirstOrDefault(x => x.Id == docId);
          // Если документ по штрихкоду нашелся в системе.
          if (document != null)
          {
            documentInfo.FoundByBarcode = true;
            this.LogMessage(blobPackage, "Smart processing. GetDocumentByBarcode. Found document with ID {0} by barcode. New version will be created.", docId);
          }
          else
            this.LogMessage(blobPackage, "Smart processing. GetDocumentByBarcode. Cannot find document with ID {0} by barcode. New document will be created.", docId);
        }
        else
          this.LogMessage("Smart processing. GetDocumentByBarcode. Barcode not found or it contains no document ID.", blobPackage);
      }
      
      return document;
    }

    /// <summary>
    /// Поиск ИД документа по штрихкодам.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>Список распознанных ИД документа.</returns>
    /// <remarks>
    /// Поиск штрихкодов осуществляется только на первой странице документа.
    /// Формат штрихкода: Code128.
    /// </remarks>
    [Public]
    public virtual List<long> SearchDocumentBarcodeIds(System.IO.Stream document)
    {
      var tenantId = Docflow.PublicFunctions.Module.Remote.GetCurrentTenantId();
      var formattedTenantId = Docflow.PublicFunctions.Module.FormatTenantIdForBarcode(tenantId).Trim();
      var documentBarcodeIds = Docflow.IsolatedFunctions.BarcodeParser.SearchDocumentBarcodeIds(document, formattedTenantId);
      
      Logger.DebugFormat("Smart processing. SearchDocumentBarcodeIds. Found {0} document Ids.", documentBarcodeIds.Count());
      return documentBarcodeIds;
    }

    #endregion

    #region Создание документа из тела письма

    /// <summary>
    /// Создание документа на основе тела письма.
    /// </summary>
    /// <param name="documentPackage">Пакет документов.</param>
    [Public]
    public virtual void CreateDocumentFromEmailBody(IDocumentPackage documentPackage)
    {
      var blobPackage = documentPackage.BlobPackage;
      
      // Для писем без тела не создавать простой документ.
      if (blobPackage.MailBodyBlob != null)
      {
        try
        {
          var emailBody = this.CreateSimpleDocumentFromEmailBody(blobPackage, documentPackage.Responsible);
          var documentInfo = new DocumentInfo();
          documentInfo.Document = emailBody;
          documentInfo.IsRecognized = false;
          documentInfo.IsEmailBody = true;
          documentPackage.DocumentInfos.Add(documentInfo);
        }
        catch (Exception ex)
        {
          this.LogError("Smart processing. CreateDocumentFromEmailBody. Error while creating document from email body.", ex, blobPackage);
          documentPackage.FailedCreateDocumentFromEmailBody = true;
        }
      }
    }

    /// <summary>
    /// Создать документ из тела эл. письма.
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    /// <param name="responsible">Сотрудник, ответственный за обработку документов.</param>
    /// <returns>ИД созданного документа.</returns>
    [Public, Remote]
    public virtual ISimpleDocument CreateSimpleDocumentFromEmailBody(IBlobPackage blobPackage,
                                                                     IEmployee responsible)
    {
      var emailBodyName = Resources.EmailBodyDocumentNameFormat(blobPackage.FromAddress).ToString();
      var document = SimpleDocuments.Create();

      this.FillSimpleDocumentProperties(document, null, responsible, emailBodyName);
      this.FillDeliveryMethod(document, blobPackage.SourceType);
      this.FillVerificationState(document);
      
      // Наименование и содержание.
      if (!string.IsNullOrWhiteSpace(blobPackage.Subject))
        emailBodyName = string.Format("{0} \"{1}\"", emailBodyName, blobPackage.Subject);
      
      if (document.DocumentKind != null && document.DocumentKind.GenerateDocumentName.Value)
      {
        // Автоформируемое имя.
        document.Subject = Docflow.PublicFunctions.OfficialDocument.AddClosingQuoteToSubject(emailBodyName, document);
      }
      else
      {
        // Не автоформируемое имя.
        document.Name = Docflow.PublicFunctions.OfficialDocument.AddClosingQuote(emailBodyName, document);
      }
      
      var mailBody = string.Empty;
      
      // Выключить error-логирование при доступе к зашифрованным бинарным данным.
      AccessRights.SuppressSecurityEvents(
        () =>
        {
          document.CreateVersion();
          var version = document.LastVersion;

          using (var reader = new StreamReader(blobPackage.MailBodyBlob.Body.Read(), System.Text.Encoding.UTF8))
            mailBody = reader.ReadToEnd();
          
          using (var body = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(mailBody)))
          {
            if (System.IO.Path.GetExtension(blobPackage.MailBodyBlob.FilePath).ToLower() == Module.HtmlExtension.WithDot)
            {
              using (var pdfDocumentStream = Sungero.Docflow.IsolatedFunctions.PdfConverter.GeneratePdf(body, Module.HtmlExtension.WithoutDot))
              {
                if (pdfDocumentStream != null)
                {
                  version.Body.Write(pdfDocumentStream);
                  version.AssociatedApplication = Content.AssociatedApplications.GetByExtension(Docflow.PublicConstants.OfficialDocument.PdfExtension);
                }
              }
            }
            
            // Если тело письма не удалось преобразовать в pdf или расширение не html, то в тело пишем исходный файл.
            if (version.Body.Size == 0)
            {
              version.Body.Write(body);
              version.AssociatedApplication = Docflow.PublicFunctions.Module.GetAssociatedApplicationByFileName(blobPackage.MailBodyBlob.FilePath);
            }
          }
        });
      
      document.Save();
      
      return document;
    }

    /// <summary>
    /// Удалить изображения из тела письма.
    /// </summary>
    /// <param name="mailBody">Содержимое тела письма.</param>
    /// <param name="filePath">Относительный путь до файла с телом письма.</param>
    /// <returns>Содержимое тела письма без ссылок на изображения.</returns>
    [Public, Obsolete("Теперь картинки корректно отображаются в теле письма, нет необходимости их удалять.")]
    public virtual string RemoveImagesFromEmailBody(string mailBody, string filePath)
    {
      // Нет смысла удалять изображения в файлах, расширение которых не html.
      if (Path.GetExtension(filePath).ToLower() == Module.HtmlExtension.WithDot)
      {
        try
        {
          mailBody = System.Text.RegularExpressions.Regex.Replace(mailBody, @"<img([^\>]*)>", string.Empty);

          // В некоторых случаях Aspose не может распознать файл как html, поэтому добавляем тег html, если его нет.
          if (!mailBody.Contains(Module.HtmlTags.MaskForSearch))
            mailBody = string.Concat(Module.HtmlTags.StartTag, mailBody, Module.HtmlTags.EndTag);
        }
        catch (Exception ex)
        {
          Logger.ErrorFormat("RemoveImagesFromEmailBody: Cannot remove images from email body.", ex);
        }
      }

      return mailBody;
    }

    #endregion

    #region Сборка и связывание пакета

    /// <summary>
    /// Упорядочить и связать документы в пакете.
    /// </summary>
    /// <param name="package">Пакет документов.</param>
    [Public]
    public virtual void OrderAndLinkDocumentPackage(IDocumentPackage package)
    {
      var packageId = package.BlobPackage.PackageId;
      this.LogMessage("Smart processing. OrderAndLinkDocumentPackage...", packageId);
      
      if (!package.DocumentInfos.Any())
        return;

      try
      {
        // Получить ведущий документ из распознанных документов комплекта. Если список пуст, то из нераспознанных.
        var leadingDocument = this.GetLeadingDocument(package);
        foreach (var documentInfo in package.DocumentInfos)
          documentInfo.IsLeadingDocument = Equals(documentInfo.Document, leadingDocument);
        
        this.LinkDocuments(package);
        
        // Для документов, нераспознанных Ario, заполнить имена:
        // со сканера - шаблонным названием,
        // с электронной почты - значением исходного вложения.
        // Для распознанных документов без автоимени заполнить имя:
        // если документ с почты - первоначальным именем файла.
        this.RenameDocuments(package);
      }
      catch (Exception ex)
      {
        this.LogError("Smart processing. OrderAndLinkDocumentPackage. Error while ordering, linking and renaming documents.", ex, packageId);
        package.FailedOrderAndLinkDocuments = true;
      }
      
      this.LogMessage("Smart processing. OrderAndLinkDocumentPackage.", packageId);
      
    }

    /// <summary>
    /// Определить ведущий документ распознанного комплекта.
    /// </summary>
    /// <param name="package">Комплект документов.</param>
    /// <returns>Ведущий документ.</returns>
    [Public]
    public virtual IOfficialDocument GetLeadingDocument(IDocumentPackage package)
    {
      if (package == null)
        throw new ApplicationException(string.Format("Parameter {0} is required.", nameof(package)));
      var blobPackage = package.BlobPackage;
      var packagePriority = new Dictionary<IDocumentInfo, int>();
      var leadingDocumentPriority = Functions.Module.GetPackageDocumentTypePriorities();
      this.LogMessage(blobPackage, "Smart processing. DocumentType priorities:");
      var priorityDescriptions = leadingDocumentPriority.Select(x => string.Format("-- Priority: {0}. Type: {1}.", x.Value, x.Key));
      foreach (var description in priorityDescriptions)
        this.LogMessage(blobPackage, description);
      this.LogMessage(blobPackage, "Smart processing. Package documents priorities:");
      int priority;
      foreach (var documentInfo in package.DocumentInfos)
      {
        var typeGuid = documentInfo.Document.GetType().GetFinalType();
        leadingDocumentPriority.TryGetValue(typeGuid, out priority);
        packagePriority.Add(documentInfo, priority);
        this.LogMessage(blobPackage,
                        "-- Priority: {0}. Type: {1}. Document: (ID={2}) {3}.",
                        priority,
                        typeGuid,
                        documentInfo.Document.Id,
                        documentInfo.Document.DisplayValue);
      }
      
      var leadingDocument = packagePriority
        .OrderByDescending(p => p.Value)
        .OrderByDescending(d => d.Key.IsRecognized)
        .FirstOrDefault().Key.Document;
      this.LogMessage(blobPackage,
                      "Smart processing. Package leading document: Document: (ID={0}) {1}.",
                      leadingDocument.Id,
                      leadingDocument.DisplayValue);
      return leadingDocument;
    }

    /// <summary>
    /// Связать документы комплекта.
    /// </summary>
    /// <param name="package">Распознанные документы комплекта.</param>
    /// <remarks>
    /// Для распознанных документов комплекта, если ведущий документ - простой, то тип связи - "Прочие". Иначе "Приложение".
    /// Для нераспознанных документов комплекта - тип связи "Прочие".
    /// </remarks>
    [Public]
    public virtual void LinkDocuments(IDocumentPackage package)
    {
      var leadingDocument = package.DocumentInfos.Where(i => i.IsLeadingDocument).Select(d => d.Document).FirstOrDefault();
      var leadingDocumentIsSimple = SimpleDocuments.Is(leadingDocument);
      
      var relation = leadingDocumentIsSimple
        ? Docflow.PublicConstants.Module.SimpleRelationName
        : Docflow.PublicConstants.Module.AddendumRelationName;
      
      // Связать приложения с ведущим документом.
      var addenda = package.DocumentInfos
        .Where(i => !i.IsLeadingDocument && i.ArioDocument != null && i.ArioDocument.IsProcessedByArio)
        .Select(d => d.Document);
      foreach (var addendum in addenda)
      {
        addendum.Relations.AddFrom(relation, leadingDocument);
        addendum.Save();
      }
      
      // Связать документы, которые не отправлялись в Ario, и тело письма с ведущим документом, тип связи - "Прочие".
      var notRecognizedDocuments = package.DocumentInfos
        .Where(i => !i.IsLeadingDocument && (i.ArioDocument == null || !i.ArioDocument.IsProcessedByArio))
        .Select(d => d.Document);
      foreach (var notRecognizedDocument in notRecognizedDocuments)
      {
        notRecognizedDocument.Relations.AddFrom(Docflow.PublicConstants.Module.SimpleRelationName, leadingDocument);
        notRecognizedDocument.Save();
      }
    }

    /// <summary>
    /// Переименовать документы в комплекте.
    /// </summary>
    /// <param name="package">Комплект документов.</param>
    /// <remarks>
    /// Для почты: всем документам без автоимени присвоить оригинальное имя файла,
    /// простому документу с автоименем - положить имя файла в содержание.
    /// Для папки: если неклассифицированных документов несколько и ведущий документ простой,
    /// то у ведущего будет номер 1, у остальных - следующие по порядку.
    /// </remarks>
    [Public]
    public virtual void RenameDocuments(IDocumentPackage package)
    {
      // Поступление документов с эл. почты.
      if (package.BlobPackage.SourceType == SmartProcessing.BlobPackage.SourceType.Mail)
      {
        // Переименовать простые документы. Не переименовывать, если документ найден по штрихкоду.
        var simpleDocumentInfos = package.DocumentInfos.Where(i => SimpleDocuments.Is(i.Document) &&
                                                              !i.IsEmailBody &&
                                                              !i.FoundByBarcode);
        foreach (var simpleDocumentInfo in simpleDocumentInfos)
        {
          var originalFileName = simpleDocumentInfo.ArioDocument.OriginalBlob.OriginalFileName;
          if (!string.IsNullOrWhiteSpace(originalFileName))
          {
            var document = simpleDocumentInfo.Document;
            if (document.DocumentKind.GenerateDocumentName == true)
              document.Subject = originalFileName;
            else
              document.Name = originalFileName;
            document.Save();
          }
        }
        
        // Переименовать распознанные документы. Не переименовывать, если документ найден по штрихкоду.
        var recognizedDocumentInfos = package.DocumentInfos.Where(i => !SimpleDocuments.Is(i.Document) &&
                                                                  !i.IsEmailBody &&
                                                                  !i.FoundByBarcode);
        foreach (var recognizedDocumentInfo in recognizedDocumentInfos)
        {
          var originalFileName = recognizedDocumentInfo.ArioDocument.OriginalBlob.OriginalFileName;
          if (!string.IsNullOrWhiteSpace(originalFileName))
          {
            var document = recognizedDocumentInfo.Document;
            if (document.DocumentKind.GenerateDocumentName != true)
              document.Name = originalFileName;
            document.Save();
          }
        }
        return;
      }
      
      // Поступление документов с папки.
      if (package.DocumentInfos.Select(d => SimpleDocuments.Is(d.Document)).Count() < 2)
        return;
      
      // Если ведущий документ SimpleDocument, то переименовываем его,
      // для того чтобы в имени содержался его порядковый номер.
      int simpleDocumentNumber = 1;
      var leadingDocument = package.DocumentInfos
        .Where(i => i.IsLeadingDocument && !i.FoundByBarcode)
        .Select(d => d.Document)
        .FirstOrDefault();
      var leadingDocumentIsSimple = SimpleDocuments.Is(leadingDocument);
      if (leadingDocumentIsSimple)
      {
        leadingDocument.Name = Resources.DocumentNameFormat(simpleDocumentNumber);
        leadingDocument.Save();
        simpleDocumentNumber++;
      }
      
      var addenda = package.DocumentInfos.Where(i => !i.IsLeadingDocument && !i.FoundByBarcode).Select(d => d.Document);
      foreach (var addendum in addenda)
      {
        if (SimpleDocuments.Is(addendum))
        {
          addendum.Name = leadingDocumentIsSimple
            ? Resources.DocumentNameFormat(simpleDocumentNumber)
            : Resources.AttachmentNameFormat(simpleDocumentNumber);
          addendum.Save();
          simpleDocumentNumber++;
        }
      }
    }

    #endregion

    #region Отправка в работу

    /// <summary>
    /// Отправить документы ответственному.
    /// </summary>
    /// <param name="documentPackage">Пакет документов в системе.</param>
    [Public]
    public virtual void SendToResponsible(IDocumentPackage documentPackage)
    {
      var packageId = documentPackage.BlobPackage.PackageId;
      this.LogMessage("Smart processing. SendToResponsible...", packageId);
      
      var responsible = documentPackage.Responsible;
      
      // Собрать пакет документов. Порядок важен, чтобы ведущий был первым.
      var leadingDocument = documentPackage.DocumentInfos.Where(i => i.IsLeadingDocument).Select(d => d.Document).FirstOrDefault();
      // Если при определении ведущего документа возникли ошибки, то берем первый попавшийся документ.
      if (leadingDocument == null)
        leadingDocument = documentPackage.DocumentInfos.Select(d => d.Document).FirstOrDefault();
      var package = documentPackage.DocumentInfos.OrderByDescending(d => d.IsLeadingDocument).Select(d => d.Document).ToList();
      if (!package.Any())
        return;
      
      // Тема.
      var task = VerificationTasks.Create();
      task.Subject = package.Count() > 1
        ? Resources.CheckPackageTaskNameFormat(leadingDocument)
        : Resources.CheckDocumentTaskNameFormat(leadingDocument);
      if (task.Subject.Length > task.Info.Properties.Subject.Length)
        task.Subject = task.Subject.Substring(0, task.Info.Properties.Subject.Length);
      
      // Записать наименование ведущего документа в свойство задачи для формирования темы задания.
      task.LeadingDocumentName = leadingDocument.ToString();
      
      // Вложить в задачу и выдать права на документы ответственному.
      foreach (var document in package)
      {
        try
        {
          document.AccessRights.Grant(responsible, DefaultAccessRightsTypes.FullAccess);
          document.AccessRights.Save();
        }
        catch (Exception e)
        {
          Logger.DebugFormat("Cannot grant rights to responsible: {0}", e.Message);
        }
        
        task.Attachments.Add(document);
      }
      
      // Добавить наблюдателями ответственных за документы, которые вернулись по ШК.
      var foundByBarcodeDocuments = documentPackage.DocumentInfos.Where(x => x.FoundByBarcode).Select(x => x.Document).ToList();
      var responsibleEmployees = this.GetDocumentsResponsibleEmployees(foundByBarcodeDocuments);
      responsibleEmployees = responsibleEmployees.Where(r => !Equals(r, responsible)).ToList();
      foreach (var responsibleEmployee in responsibleEmployees)
      {
        var observer = task.Observers.AddNew();
        observer.Observer = responsibleEmployee;
      }
      
      task.Assignee = responsible;
      task.ActiveText = this.GetVerificationTaskText(documentPackage);
      
      task.NeedsReview = false;
      task.Deadline = Calendar.Now.AddWorkingHours(task.Assignee, 4);
      task.Save();
      task.Start();
      
      Logger.Debug("Задача на верификацию отправлена в работу");
      
      // Старт фонового процесса для удаления блобов.
      Jobs.DeleteBlobPackages.Enqueue();
      
      this.LogMessage("Smart processing. SendToResponsible.", packageId);
    }

    /// <summary>
    /// Запустить асинхронные обработчики выдачи прав.
    /// </summary>
    /// <param name="documentPackage">Пакет документов в системе.</param>
    public virtual void EnqueueGrantAccessRightsJobs(IDocumentPackage documentPackage)
    {
      var packageId = documentPackage.BlobPackage.PackageId;
      this.LogMessage("Smart processing. EnqueueGrantAccessRightsJobs started.", packageId);
      var documents = documentPackage.DocumentInfos.Select(d => d.Document).ToList<IOfficialDocument>();
      if (!documents.Any())
        return;
      
      var enqueueGrantAccessRightsToProjectDocumentJob = false;
      foreach (var document in documents)
      {
        Docflow.PublicFunctions.Module.CreateGrantAccessRightsToDocumentAsyncHandler(document.Id, new List<int>(), true);
        this.LogMessage(string.Format("Smart processing. GrantAccessRightsToDocumentAsync started. Document ID = ({0})", document.Id), packageId);
        
        var documentParams = ((Sungero.Domain.Shared.IExtendedEntity)document).Params;
        if (documentParams.ContainsKey(Docflow.PublicConstants.OfficialDocument.GrantAccessRightsToProjectDocument))
        {
          enqueueGrantAccessRightsToProjectDocumentJob = true;
          documentParams.Remove(Docflow.PublicConstants.OfficialDocument.GrantAccessRightsToProjectDocument);
        }
      }
      
      if (enqueueGrantAccessRightsToProjectDocumentJob)
      {
        Sungero.Projects.Jobs.GrantAccessRightsToProjectDocuments.Enqueue();
        this.LogMessage("Smart processing. GrantAccessRightsToProjectDocuments job started.", packageId);
      }
      
      this.LogMessage("Smart processing. EnqueueGrantAccessRightsJobs completed.", packageId);
    }

    /// <summary>
    /// Получить список ответственных за документы.
    /// </summary>
    /// <param name="documents">Документы.</param>
    /// <returns>Список ответственных.</returns>
    /// <remarks>Ответственных искать только у документов, тип которых: договорной документ, акт, накладная, УПД.</remarks>
    [Public]
    public virtual List<IEmployee> GetDocumentsResponsibleEmployees(List<IOfficialDocument> documents)
    {
      var responsibleEmployees = new List<IEmployee>();
      var withResponsibleDocuments = documents.Where(d => Contracts.ContractualDocuments.Is(d) ||
                                                     FinancialArchive.ContractStatements.Is(d) ||
                                                     FinancialArchive.Waybills.Is(d) ||
                                                     FinancialArchive.UniversalTransferDocuments.Is(d));
      foreach (var document in withResponsibleDocuments)
      {
        var responsibleEmployee = Employees.Null;
        responsibleEmployee = Docflow.PublicFunctions.OfficialDocument.GetDocumentResponsibleEmployee(document);
        
        if (responsibleEmployee != Employees.Null && responsibleEmployee.IsSystem != true)
          responsibleEmployees.Add(responsibleEmployee);
      }
      
      return responsibleEmployees.Distinct().ToList();
    }

    /// <summary>
    /// Получить текст задачи на проверку документов.
    /// </summary>
    /// <param name="documentPackage">Пакет документов в системе.</param>
    /// <returns>Текст задачи на проверку документов.</returns>
    [Public]
    public virtual string GetVerificationTaskText(IDocumentPackage documentPackage)
    {
      var taskActiveText = new List<string>();
      
      // Добавить в текст задачи список документов, которые занесены по штрихкоду.
      taskActiveText.Add(this.GetFoundByBarcodeDocumentsTaskText(documentPackage));
      
      // Добавить в текст задачи список неклассифицированных документов.
      taskActiveText.Add(this.GetNotClassifiedDocumentsTaskText(documentPackage));
      
      // Добавить в текст задачи список документов, которые не удалось зарегистрировать.
      taskActiveText.Add(this.GetFailedRegistrationDocumentsTaskText(documentPackage));
      
      // Добавить в текст задачи список документов, которые были заблокированы при занесении новой версии.
      taskActiveText.Add(this.GetLockedDocumentsTaskText(documentPackage));
      
      // Добавить в текст задачи список ошибок при обработке пакета документов.
      taskActiveText.Add(this.GetDocumentPackageErrorsTaskText(documentPackage));
      
      taskActiveText = taskActiveText.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
      
      if (taskActiveText.Count > 0)
        taskActiveText.Add(Resources.ContactSysAdminIfNeeded);
      
      return string.Join(Environment.NewLine + Environment.NewLine, taskActiveText);
    }

    /// <summary>
    /// Получить блок текста задачи с ошибками обработки пакета документов.
    /// </summary>
    /// <param name="documentPackage">Пакет документов в системе.</param>
    /// <returns>Блок текста задачи с ошибками обработки пакета документов.</returns>
    public virtual string GetDocumentPackageErrorsTaskText(IDocumentPackage documentPackage)
    {
      var errorsTaskText = new List<string>();
      
      errorsTaskText.Add(Resources.DocumentPackageHasErrors);
      
      // Блок ошибок на уровне отдельных документов.
      foreach (var documentInfo in documentPackage.DocumentInfos)
      {
        if (documentInfo.FailedCreateDocument || documentInfo.FailedArioProcessDocument)
          errorsTaskText.Add(string.Format("    {0}: {1}{2}{3}",
                                           Hyperlinks.Get(documentInfo.Document),
                                           documentInfo.FailedCreateDocument ? Resources.FailedCreateDocumentTaskTextFormat(documentInfo.ArioDocument.RecognitionInfo.RecognizedClass) : string.Empty,
                                           documentInfo.FailedCreateDocument && documentInfo.FailedArioProcessDocument ? ", " : string.Empty,
                                           documentInfo.FailedArioProcessDocument ? Resources.FailedArioProcessDocumentTaskText : string.Empty));
      }
      
      // Блок ошибок на уровне пакета.
      if (documentPackage.FailedCreateDocumentFromEmailBody)
        errorsTaskText.Add(Environment.NewLine +
                           (documentPackage.FailedCreateDocumentFromEmailBody ? Resources.CreateDocumentFromEmailBodyErrorsTaskTextFormat(documentPackage.BlobPackage.FromAddress,
                                                                                                                                          documentPackage.BlobPackage.Subject) : string.Empty));
      if (documentPackage.FailedOrderAndLinkDocuments)
        errorsTaskText.Add(Environment.NewLine +
                           (documentPackage.FailedCreateDocumentFromEmailBody ? Resources.OrderAndLinkErrorsTaskText : string.Empty));
      
      if (errorsTaskText.Count > 1)
        return string.Join(Environment.NewLine, errorsTaskText);
      else
        return string.Empty;
    }

    /// <summary>
    /// Получить блок текста задачи со списком документов, которые не удалось классифицировать.
    /// </summary>
    /// <param name="documentPackage">Пакет документов в системе.</param>
    /// <returns>Блок текста задачи со списком гиперссылок на документы, которые не удалось классифицировать.</returns>
    [Public]
    public virtual string GetNotClassifiedDocumentsTaskText(IDocumentPackage documentPackage)
    {
      // Считать неклассифицированными все простые документы и те, чей класс по Ario не имеет соответствия в системе.
      // Не нужно считать неклассифицированными документами тело письма и документ, найденный по штрихкоду.
      // Также не считаются неклассифицированными документы, которые не удалось создать в системе.
      var notClassifiedDocuments = documentPackage.DocumentInfos
        .Where(i => !i.FailedCreateDocument && !i.FailedArioProcessDocument)
        .Where(i => !i.IsRecognized && !i.IsEmailBody && !i.FoundByBarcode || i.IsRecognized && SimpleDocuments.Is(i.Document))
        .Select(d => d.Document)
        .ToList();
      
      if (notClassifiedDocuments.Any())
      {
        var failedClassifyTaskText = notClassifiedDocuments.Count() == 1
          ? Resources.FailedClassifyDocumentTaskText
          : Resources.FailedClassifyDocumentsTaskText;
        
        return this.FormDocumentsTaskTextWithHyperlinks(failedClassifyTaskText, notClassifiedDocuments);
      }
      
      return string.Empty;
    }

    /// <summary>
    /// Получить блок текста задачи со списком документов, которые не удалось зарегистрировать.
    /// </summary>
    /// <param name="documentPackage">Пакет документов в системе.</param>
    /// <returns>Блок текста задачи со списком гиперссылок на документы, которые не удалось зарегистрировать.</returns>
    [Public]
    public virtual string GetFailedRegistrationDocumentsTaskText(IDocumentPackage documentPackage)
    {
      // Собрать документы, которые не удалось зарегистрировать.
      var failedRegistrationDocuments = documentPackage.DocumentInfos
        .Where(i => i.RegistrationFailed)
        .Select(d => d.Document)
        .ToList();
      
      if (failedRegistrationDocuments.Any())
      {
        failedRegistrationDocuments = failedRegistrationDocuments.OrderBy(x => x.DocumentKind.Name).ToList();
        var documentsText = failedRegistrationDocuments.Count() == 1 ? Resources.Document : Resources.Documents;
        var documentKinds = failedRegistrationDocuments.Select(x => string.Format("\"{0}\"", x.DocumentKind.Name)).Distinct();
        var documentKindsText = documentKinds.Count() == 1 ? Resources.Kind : Resources.Kinds;
        var documentKindsListText = string.Join(", ", documentKinds);
        
        var failedRegistrationDocumentsTaskText = string.Format(Resources.DocumentsWithRegistrationFailureTaskText,
                                                                documentsText, documentKindsText, documentKindsListText);
        
        return this.FormDocumentsTaskTextWithHyperlinks(failedRegistrationDocumentsTaskText, failedRegistrationDocuments);
      }
      
      return string.Empty;
    }

    /// <summary>
    /// Получить блок текста задачи со списком документов, которые занесены по штрихкоду.
    /// </summary>
    /// <param name="documentPackage">Пакет документов в системе.</param>
    /// <returns>Блок текста задачи со списком гиперссылок на документы, которые занесены по штрихкоду.</returns>
    [Public]
    public virtual string GetFoundByBarcodeDocumentsTaskText(IDocumentPackage documentPackage)
    {
      // Сформировать список документов, которые занесены по штрихкоду.
      var foundByBarcodeDocuments = documentPackage.DocumentInfos
        .Where(i => i.FoundByBarcode && !i.FailedCreateVersion)
        .Select(d => d.Document)
        .ToList();
      
      if (foundByBarcodeDocuments.Any())
      {
        var documentsFoundBarcodeTaskText = foundByBarcodeDocuments.Count() == 1
          ? Resources.DocumentFoundByBarcodeTaskText
          : Resources.DocumentsFoundByBarcodeTaskText;
        
        return this.FormDocumentsTaskTextWithHyperlinks(documentsFoundBarcodeTaskText, foundByBarcodeDocuments);
      }
      
      return string.Empty;
    }

    /// <summary>
    /// Получить блок текста задачи со списком документов, которые были заблокированы при занесении новой версии.
    /// </summary>
    /// <param name="documentPackage">Пакет документов в системе.</param>
    /// <returns>Блок текста задачи со списком гиперссылок на документы, которые были заблокированы при занесении новой версии.</returns>
    [Public]
    public virtual string GetLockedDocumentsTaskText(IDocumentPackage documentPackage)
    {
      // Сформировать список заблокированных документов.
      var lockedDocuments = documentPackage.DocumentInfos
        .Where(i => i.FailedCreateVersion)
        .Select(d => d.Document)
        .ToList();
      
      if (lockedDocuments.Any())
      {
        var failedCreateVersionTaskText = lockedDocuments.Count() == 1
          ? Resources.FailedCreateVersionTaskText
          : Resources.FailedCreateVersionsTaskText;

        var lockedDocumentsHyperlinksLabels = new List<string>();
        foreach (var lockedDocument in lockedDocuments)
        {
          var loginId = Locks.GetLockInfo(lockedDocument).LoginId;
          var employee = Employees.GetAll(x => x.Login.Id == loginId).FirstOrDefault();
          // Текстовка на случай, когда блокировка снята в момент создания задачи.
          var employeeLabel = Resources.DocumentWasLockedTaskText.ToString();
          if (employee != null)
            employeeLabel = string.Format(Resources.DocumentLockedByEmployeeTaskText,
                                          Hyperlinks.Get(employee));
          var documentHyperlink = Hyperlinks.Get(lockedDocument);
          lockedDocumentsHyperlinksLabels.Add(string.Format("{0} {1}", documentHyperlink, employeeLabel));
        }
        
        return this.FormDocumentsTaskTextWithHyperlinks(failedCreateVersionTaskText, lockedDocumentsHyperlinksLabels);
      }
      
      return string.Empty;
    }

    /// <summary>
    /// Сформировать блок текста задачи на верификацию с гиперссылками на документы комплекта.
    /// </summary>
    /// <param name="documentsSectionTitle">Заголовок для гиперссылок на документы комплекта.</param>
    /// <param name="documentsForHyperlinks">Документы, на которые в текст будут вставлены гиперссылки.</param>
    /// <returns>Текст с гиперссылками на документы комплекта.</returns>
    [Public]
    public virtual string FormDocumentsTaskTextWithHyperlinks(string documentsSectionTitle,
                                                              List<IOfficialDocument> documentsForHyperlinks)
    {
      // Собрать ссылки на документы.
      var hyperlinksLabels = documentsForHyperlinks.Select(x => Hyperlinks.Get(x)).ToList();
      return this.FormDocumentsTaskTextWithHyperlinks(documentsSectionTitle, hyperlinksLabels);
    }

    /// <summary>
    /// Сформировать блок текста задачи на верификацию с гиперссылками на документы комплекта.
    /// </summary>
    /// <param name="documentsSectionTitle">Заголовок для гиперссылок на документы комплекта.</param>
    /// <param name="hyperlinksLabels">Гиперссылки.</param>
    /// <returns>Текст с гиперссылками на документы комплекта.</returns>
    [Public]
    public virtual string FormDocumentsTaskTextWithHyperlinks(string documentsSectionTitle,
                                                              List<string> hyperlinksLabels)
    {
      // Между блоками отступ 1 строка, каждая гиперссылка с новой строки с отступом 4 пробела от начала.
      var documentHyperlinksLabel = string.Join(Environment.NewLine + "    ", hyperlinksLabels);
      var documentsTaskText = string.Format("{0}{1}    {2}", documentsSectionTitle, Environment.NewLine, documentHyperlinksLabel);
      
      return documentsTaskText;
    }

    #endregion

    #region Завершение процесса обработки

    /// <summary>
    /// Завершить процесс обработки.
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    [Public]
    public virtual void FinalizeProcessing(IBlobPackage blobPackage)
    {
      var packageId = blobPackage.PackageId;
      this.LogMessage("Smart processing. FinalizeProcessing...", packageId);
      
      blobPackage.ProcessState = SmartProcessing.BlobPackage.ProcessState.Processed;
      blobPackage.Save();
      
      this.LogMessage("Smart processing. FinalizeProcessing.", packageId);
    }

    #endregion
    
    #region Дообучение классификатора по типу документов
    
    /// <summary>
    /// Проверить, выполняется ли обучение классификатора.
    /// </summary>
    /// <param name="classifierId">ИД классификатора.</param>
    /// <returns>True - если обучение классификатора выполняется. Иначе - False.</returns>
    public virtual bool IsClassifierTrainingInProcess(int classifierId)
    {
      // Проверить статус в последней сессии обучения указанного классификатора.
      var completionStatuses = new List<Enumeration>()
      {
        Sungero.Commons.ClassifierTrainingSession.TrainingStatus.Preparation,
        Sungero.Commons.ClassifierTrainingSession.TrainingStatus.InProcess
      };
      var lastTrainingSession = ClassifierTrainingSessions
        .GetAll(x => x.ClassifierId == classifierId)
        .OrderByDescending(x => x.Id)
        .FirstOrDefault();
      return lastTrainingSession != null && completionStatuses.Contains(lastTrainingSession.TrainingStatus.Value);
    }
    
    /// <summary>
    /// Получить результаты распознавания для обучения классификатора.
    /// </summary>
    /// <returns>Список записей "Результат распознавания сущности".</returns>
    /// <remarks>Для обучения класса необходимо не менее 10 документов.</remarks>
    [Public]
    public virtual List<IEntityRecognitionInfo> GetRecognitionInfosForClassifierTraining()
    {
      Logger.Debug("ClassifierTraining. GetRecognitionInfosForClassifierTraining. Started to select documents for training.");
      
      var recognitionInfos = this.GetRecognitionInfos(null, Sungero.Commons.EntityRecognitionInfo.ClassifierTrainingStatus.Awaiting)
        .Where(x => x.VerifiedClass != null && x.VerifiedClass != string.Empty);
      
      Logger.DebugFormat("ClassifierTraining. GetRecognitionInfosForClassifierTraining. Total documents for training: {0}", recognitionInfos.Count());
      
      // Исключить зашифрованные документы.
      var encryptedDocuments = Sungero.Content.ElectronicDocuments
        .GetAll(x => x.IsEncrypted && recognitionInfos.Any(info => x.Id == info.EntityId))
        .Select(x => x.Id)
        .ToList();
      
      var excludedRecognitionInfos = recognitionInfos.Where(x => encryptedDocuments.Contains(x.EntityId.Value)).ToList();
      if (excludedRecognitionInfos.Any())
      {
        Logger.DebugFormat("ClassifierTraining. GetRecognitionInfosForClassifierTraining. Excluded encrypted documents: {0}", excludedRecognitionInfos.Count);
        foreach (var info in excludedRecognitionInfos)
          this.SetClassifierTrainingStatus(info, null, null);
      }
      
      // Получить результаты распознавания, сгруппированные по подтвержденным классам.
      var verifiedClasses = recognitionInfos
        .Where(x => !excludedRecognitionInfos.Contains(x))
        .GroupBy(x => x.VerifiedClass)
        .ToList();
      
      if (verifiedClasses.Any())
      {
        Logger.DebugFormat("ClassifierTraining. GetRecognitionInfosForClassifierTraining. Selected classes: {0}",
                           string.Join(", ", verifiedClasses.Select(x => string.Format("{0} ({1})", x.Key, x.Count()))));
      }
      
      // Исключить классы с недостаточным для обучения количеством документов.
      var excludedClasses = verifiedClasses
        .Where(x => x.Count() < Constants.Module.MinDocumentClassifierTrainingCount)
        .Select(x => x.Key)
        .ToList();
      
      if (excludedClasses.Any())
      {
        Logger.DebugFormat("ClassifierTraining. GetRecognitionInfosForClassifierTraining. Not enough documents for training classes: {0}",
                           string.Join(", ", excludedClasses));
      }
      
      Logger.DebugFormat("ClassifierTraining. GetRecognitionInfosForClassifierTraining. Finished to select documents for training");
      return verifiedClasses.Where(x => !excludedClasses.Contains(x.Key)).SelectMany(x => x).ToList();
    }

    /// <summary>
    /// Связать результаты распознавания с сессией обучения классификатора.
    /// </summary>
    /// <param name="trainingSession">Сессия обучения классификатора.</param>
    /// <param name="recognitionInfos">Список записей "Результат распознавания сущности".</param>
    [Public]
    public virtual void LinkRecognitionInfosWithTrainingSession(IClassifierTrainingSession trainingSession,
                                                                List<IEntityRecognitionInfo> recognitionInfos)
    {
      foreach (var info in recognitionInfos)
        this.SetClassifierTrainingStatus(info, trainingSession, Sungero.Commons.EntityRecognitionInfo.ClassifierTrainingStatus.Awaiting);
    }
    
    /// <summary>
    /// Создать Сессию обучения классификатора.
    /// </summary>
    /// <param name="classifierId">ИД классификатора.</param>
    /// <returns>Сессия обучения классификатора.</returns>
    [Public]
    public virtual IClassifierTrainingSession CreateClassifierTrainingSession(int classifierId)
    {
      // Получить информацию об опубликованной модели классификатора из Ario.
      var arioConnector = this.GetArioConnector();
      var classifierInfo = arioConnector.GetClassifier(classifierId);
      if (classifierInfo.PublishedModel == null)
      {
        Logger.Debug("ClassifierTraining. CreateClassifierTrainingSession. Published model not found for document type classifier.");
        throw AppliedCodeException.Create("Published classifier model not found");
      }
      
      var trainingSession = ClassifierTrainingSessions.Create();
      trainingSession.Name = string.Format(Resources.ClassifierTrainingSessionName, classifierId, Calendar.Now);
      trainingSession.ClassifierId = classifierId;
      trainingSession.TrainingStatus = Sungero.Commons.ClassifierTrainingSession.TrainingStatus.Preparation;
      trainingSession.OldModelId = classifierInfo.PublishedModel.Id;
      trainingSession.Save();

      return trainingSession;
    }
    
    /// <summary>
    /// Получить CSV-файл для обучения классификатора.
    /// </summary>
    /// <param name="trainingSession">Сессия обучения классификатора.</param>
    /// <returns>Бинарный файл для обучения классификатора.</returns>
    [Public]
    public virtual byte[] GetCsvClassifierTrainingSessionDataset(IClassifierTrainingSession trainingSession)
    {
      Logger.DebugFormat("ClassifierTraining. GetCsvClassifierTrainingSessionDataset. Start (sessionId={0}).", trainingSession.Id);
      
      // Собрать CSV из результатов распознавания.
      var recognitionInfos = this.GetRecognitionInfos(trainingSession, Sungero.Commons.EntityRecognitionInfo.ClassifierTrainingStatus.Awaiting).ToList();
      var classifierTrainingData = this.GetClassifierTrainingDataset(recognitionInfos);
      var formattedData = this.GetFormattedTrainingDataset(classifierTrainingData);
      var csvTrainingDataset = this.BuildTrainingDatasetCsv(formattedData);
      this.SetClassifierTrainingStatuses(formattedData, trainingSession);
      var trainingDatasetSize = csvTrainingDataset.Length / 1024;
      Logger.DebugFormat("ClassifierTraining. GetCsvClassifierTrainingSessionDataset. Finish. Csv size - {0} KB (sessionId={1}).", trainingDatasetSize, trainingSession.Id);
      return csvTrainingDataset;
    }
    
    /// <summary>
    /// Получить данные для обучения классификатора.
    /// </summary>
    /// <param name="recognitionInfos">Результаты распознавания.</param>
    /// <returns>Данные для обучения классификатора.</returns>
    [Public]
    public virtual List<Structures.Module.IClassifierTrainingData> GetClassifierTrainingDataset(List<Commons.IEntityRecognitionInfo> recognitionInfos)
    {
      var classifierTrainingDataset = new List<Sungero.SmartProcessing.Structures.Module.IClassifierTrainingData>();
      var serialNumber = 0;
      foreach (var recognitionInfo in recognitionInfos.OrderBy(x => x.VerifiedClass).ThenByDescending(x => x.Created).ThenByDescending(x => x.Id))
      {
        if (Locks.GetLockInfo(recognitionInfo).IsLocked)
          continue;
        
        var documentId = recognitionInfo.EntityId.Value;
        var documentText = this.GetTextForTraining(documentId);
        
        var trainingData = ClassifierTrainingData.Create();
        trainingData.SerialNumber = serialNumber;
        trainingData.VerifiedClass = recognitionInfo.VerifiedClass;
        trainingData.Text = documentText;
        trainingData.RecognitionInfo = recognitionInfo;
        classifierTrainingDataset.Add(trainingData);
        serialNumber++;
      }
      
      return classifierTrainingDataset;
    }
    
    /// <summary>
    /// Сформировать CSV-файл для обучения.
    /// </summary>
    /// <param name="classifierTrainingDataset">Данные для обучения классификатора.</param>
    /// <returns>CSV-файл для обучения.</returns>
    [Public]
    public virtual byte[] BuildTrainingDatasetCsv(List<Structures.Module.IClassifierTrainingData> classifierTrainingDataset)
    {
      const string DatasetHeader = "Id,Category,Text";
      
      // Сформировать набор данных для обучения в разрезе классов и документов.
      var datasetInSession = classifierTrainingDataset.Where(x => x.IncludedInSession).OrderBy(x => x.SerialNumber);
      // Если все документы, отобранные для обучения, были исключены из обучающей выборки
      // (нет текстового слоя, недостаточное количество документов в классе), то вернуть пустую строку,
      // чтобы завершить фоновый процесс и корректно заполнить статус обучения в записях справочников.
      var result = new byte[0];
      if (datasetInSession.Any())
      {
        var trainingDataset = new System.Text.StringBuilder();
        trainingDataset.AppendLine(DatasetHeader);
        foreach (var trainingData in datasetInSession)
        {
          trainingDataset.AppendLine(trainingData.Text);
        }
        result = System.Text.Encoding.UTF8.GetBytes(trainingDataset.ToString());
      }
      return result;
    }
    
    /// <summary>
    /// Привести текст документов к требуемому Ario виду для обучения.
    /// </summary>
    /// <param name="classifierTrainingData">Данные для обучения классификатора.</param>
    /// <returns>Отформатированные данные для обучения классификатора.</returns>
    [Public]
    public virtual List<Structures.Module.IClassifierTrainingData> GetFormattedTrainingDataset(List<Structures.Module.IClassifierTrainingData> classifierTrainingData)
    {
      /* Для обучения классификатора необходимо отправить в сервисы Ario файл в формате CSV.
       * Заголовок файла - это строка с названиями полей: ИД документа, имя класса, текст для обучения.
       * Названия полей указываются в конфигурационном файле сервиса классификации текстов Ario.
       * По умолчанию - Id,Category,Text.
       * Данные каждого обучающего документа должны содержаться в отдельной строке. Разделитель полей внутри строки - запятая.
       * Если в имени класса содержится запятая, то имя класса требуется заключить в обратные кавычки.
       * Текст всегда долюен быть заключен в обратные кавычки. Внутри текста таких кавычек быть не должно.
       * Кодировка CSV-файла - UTF-8.
       * Пример CSV:
       * Id,Category,Text
       * 1,Акт выполненных работ,`adipiscing urna. molestie `
       * 2,Входящее письмо,`vestibulum orci nec vestibulum, ligula orci et mauris lobortis, et Aliquam`
       * 3,Входящее письмо,`orci elementum non finibus dolor. Cras`
       * 4,`Входящее, исходящее письмо`,`accumsan dolor scelerisque non et`
       */
      
      // Сформировать набор данных для обучения в разрезе классов и документов. Сортировать классы по возрастанию числа документов.
      var dataset = new StringBuilder();
      var verifiedClasses = classifierTrainingData.GroupBy(x => x.VerifiedClass).OrderBy(g => g.Count());
      var resultDataset = new List<Structures.Module.IClassifierTrainingData>();
      foreach (var classGroup in verifiedClasses)
      {
        var datasetSize = Encoding.Default.GetByteCount(dataset.ToString());
        var classDataset = this.GetTrainingDatasetForClassConsiderSizeLimit(classGroup.Select(x => x).ToList(), datasetSize);
        var datasetInSession = classDataset.Where(x => x.IncludedInSession);
        foreach (var trainingData in datasetInSession)
        {
          dataset.AppendLine(trainingData.Text);
        }
        resultDataset.AddRange(classDataset);
      }
      return resultDataset;
    }
    
    /// <summary>
    /// Привести текст документов к требуемому Ario виду для обучения для класса.
    /// </summary>
    /// <param name="classifierTrainingDataset">Данные для обучения классификатора.</param>
    /// <param name="datasetSize">Текущий размер CSV-файла.</param>
    /// <returns>Отформатированные данные для обучения классификатора.</returns>
    [Public]
    public virtual List<IClassifierTrainingData> GetTrainingDatasetForClassConsiderSizeLimit(List<IClassifierTrainingData> classifierTrainingDataset,
                                                                                             int datasetSize)
    {
      var sizeLimit = this.GetCsvTrainingDatasetLimit() * 1024 * 1024;
      var dataset = new StringBuilder();
      foreach (var trainingData in classifierTrainingDataset.OrderBy(x => x.SerialNumber))
      {
        // Исключить, если нет текста в документе.
        if (string.IsNullOrEmpty(trainingData.Text))
        {
          trainingData.IncludedInSession = false;
          continue;
        }
        
        // Сформировать строку по формату CSV для Ario.
        var documentText = this.GetFormattedTextForTrainingDataset(trainingData.SerialNumber,
                                                                   trainingData.VerifiedClass,
                                                                   trainingData.Text);
        
        // Проверить формируемый набор данных на максимальный размер.
        var classSize = Encoding.Default.GetByteCount(dataset.ToString());
        var documentsSize = Encoding.Default.GetByteCount(documentText);
        var size = datasetSize + classSize + documentsSize;
        if (size > sizeLimit)
        {
          trainingData.IncludedInSession = false;
          continue;
        }
        
        dataset.AppendLine(documentText);
        trainingData.IncludedInSession = true;
        trainingData.Text = documentText;
      }
      
      // Исключить классы с недостаточным для обучения количеством документов.
      var documentsForTraining = classifierTrainingDataset.Where(x => x.IncludedInSession);
      if (documentsForTraining.Count() < Constants.Module.MinDocumentClassifierTrainingCount)
      {
        Logger.DebugFormat("GetTrainingDatasetForClassConsiderSizeLimit: not enough documents for training class {0}. CSV file size limit exceeded.",
                           documentsForTraining.FirstOrDefault()?.VerifiedClass);
        foreach (var classifierTrainingData in documentsForTraining)
          classifierTrainingData.IncludedInSession = false;
      }
      return classifierTrainingDataset;
    }
    
    /// <summary>
    /// Сформировать строку по формату CSV-файла для Ario.
    /// </summary>
    /// <param name="number">Номер.</param>
    /// <param name="className">Наименование класса.</param>
    /// <param name="text">Текст документа.</param>
    /// <returns>Cтрока по формату CSV-файла для Ario.</returns>
    [Public]
    public virtual string GetFormattedTextForTrainingDataset(int number, string className, string text)
    {
      const char CommaEscapeSymbol = '`';
      // Экранировать запятую в имени класса при ее наличии.
      if (className.Contains(","))
        className = string.Format("{1}{0}{1}", className, CommaEscapeSymbol);

      return string.Format("{0},{1},{3}{2}{3}", number, className, text, CommaEscapeSymbol);
    }
    
    /// <summary>
    /// Получить лимит для CSV-файла в МБ.
    /// </summary>
    /// <returns>Лимит для CSV-файла в МБ.</returns>
    [Public]
    public virtual int GetCsvTrainingDatasetLimit()
    {
      var limit = (int)Docflow.PublicFunctions.Module.Remote.GetDocflowParamsNumbericValue(PublicConstants.Module.CsvTrainingDatasetLimitKey);
      if (limit == 0)
        limit = PublicConstants.Module.CsvTrainingDatasetLimit;
      
      return limit;
    }
    
    /// <summary>
    /// Обновить статусы обучения в результатах распознавания.
    /// </summary>
    /// <param name="classifierTrainingData">Данные для обучения классификатора.</param>
    /// <param name="trainingSession">Сессия обучения классификатора.</param>
    [Public]
    public virtual void SetClassifierTrainingStatuses(List<Sungero.SmartProcessing.Structures.Module.IClassifierTrainingData> classifierTrainingData,
                                                      IClassifierTrainingSession trainingSession)
    {
      foreach (var trainingData in classifierTrainingData)
      {
        var trainingStatus = Sungero.Commons.EntityRecognitionInfo.ClassifierTrainingStatus.InProcess;
        if (string.IsNullOrEmpty(trainingData.Text))
        {
          trainingStatus = Sungero.Commons.EntityRecognitionInfo.ClassifierTrainingStatus.Error;
        }
        if (!string.IsNullOrEmpty(trainingData.Text) && !trainingData.IncludedInSession)
        {
          trainingStatus = Sungero.Commons.EntityRecognitionInfo.ClassifierTrainingStatus.Awaiting;
          trainingSession = ClassifierTrainingSessions.Null;
        }
        this.SetClassifierTrainingStatus(trainingData.RecognitionInfo, trainingSession, trainingStatus);
      }
    }

    /// <summary>
    /// Получить текст версии документа.
    /// </summary>
    /// <param name="documentId">ИД документа.</param>
    /// <returns>Текст версии. Либо пустая строка, если текст извлечь не удалось.</returns>
    [Public]
    public virtual string GetTextForTraining(int documentId)
    {
      var result = string.Empty;
      var document = Sungero.Content.ElectronicDocuments.GetAll(x => x.Id == documentId).FirstOrDefault();
      if (document == null)
      {
        Logger.ErrorFormat("GetTextForTraining. Document (Id {0}) not found.", documentId);
        return string.Empty;
      }

      if (!document.HasVersions)
      {
        Logger.ErrorFormat("GetTextForTraining. Document (Id {0}) has no versions.", documentId);
        return string.Empty;
      }
      
      // Выключить error-логирование при чтении документов со строгим доступом.
      AccessRights.SuppressSecurityEvents(
        () =>
        {
          // Для дообучения важна не последняя, а исходная версия документа.
          var version = document.Versions.First();
          var versionBody = version.PublicBody != null && version.PublicBody.Size > 0 ? version.PublicBody : version.Body;

          var textExtractionResult = IsolatedFunctions.PdfTextExtractor.GetTextFromMetadata(versionBody.Read());

          if (!string.IsNullOrEmpty(textExtractionResult.ErrorMessage))
            Logger.DebugFormat("GetTextForTraining. Error while extracting text from document (Id {0}). {1}", documentId, textExtractionResult.ErrorMessage);
          else
            // Исключить обратную кавычку и управляющие символы из текста.
            result = Regex.Replace(textExtractionResult.Text, @"[\p{C}`]", string.Empty);

          if (string.IsNullOrEmpty(result))
            Logger.DebugFormat("GetTextForTraining. Document (Id {0}) has no text.", documentId);
        });
      
      return result;
    }
    
    /// <summary>
    /// Начать асинхронное обучение классификатора в Ario.
    /// </summary>
    /// <param name="trainingSession">Сессия обучения классификатора.</param>
    /// <param name="trainingDataset">Данные для обучения, бинарный файл в формате csv.</param>
    public virtual void TrainClassifierAsync(IClassifierTrainingSession trainingSession, byte[] trainingDataset)
    {
      Sungero.ArioExtensions.Models.ProcessTask arioTaskInfo = null;
      Logger.DebugFormat("ClassifierTraining. TrainClassifierAsync: start training classifier in Ario (sessionId={0}).", trainingSession.Id);
      try
      {
        var arioConnector = this.GetArioConnector();
        arioTaskInfo = arioConnector.TrainClassifier(trainingSession.ClassifierId.Value, trainingSession.OldModelId.Value, trainingDataset);
        if (arioTaskInfo.State == Constants.Module.ProcessingTaskStates.ErrorOccurred ||
            arioTaskInfo.State == Constants.Module.ProcessingTaskStates.Terminated)
        {
          Logger.ErrorFormat("ClassifierTraining. TrainClassifierAsync: Ario service error (sessionId={0}).", trainingSession.Id);
          this.ResetTrainingSessionStatus(trainingSession);
          return;
        }
        
        var arioTaskId = arioTaskInfo.Id;
        trainingSession.ArioTaskId = arioTaskId;
        trainingSession.TrainingStatus = Sungero.Commons.ClassifierTrainingSession.TrainingStatus.InProcess;
        trainingSession.Save();
        Logger.DebugFormat("ClassifierTraining. TrainClassifierAsync: csv file sent to Ario (taskId={0}, sessionId={1}).", arioTaskId, trainingSession.Id);
        
        // Запустить обработчик для отслеживания статуса выполнения задачи Ario и завершения обучения.
        var asyncHandler = AsyncHandlers.TrainClassifier.Create();
        asyncHandler.ClassifierTrainingSessionId = trainingSession.Id;
        asyncHandler.ExecuteAsync();
      }
      catch (Exception ex)
      {
        Logger.ErrorFormat("ClassifierTraining. TrainClassifierAsync: error training classifier in Ario (taskId={0}, sessionId={1}).", ex, arioTaskInfo?.Id, trainingSession.Id);
        this.ResetTrainingSessionStatus(trainingSession);
      }
    }
    
    /// <summary>
    /// Сбросить статус сессии обучения на "Возникла ошибка".
    /// </summary>
    /// <param name="trainingSession">Сессия обучения.</param>
    [Public]
    public virtual void ResetTrainingSessionStatus(IClassifierTrainingSession trainingSession)
    {
      trainingSession.TrainingStatus = Sungero.Commons.ClassifierTrainingSession.TrainingStatus.Error;
      trainingSession.Save();
      this.SetClassifierTrainingStatus(trainingSession, Commons.EntityRecognitionInfo.ClassifierTrainingStatus.Awaiting);
    }
    
    /// <summary>
    /// Заполнить статус обучения в результате распознавания.
    /// </summary>
    /// <param name="recognitionInfo">Результат распознавания сущности.</param>
    /// <param name="trainingSession">Сессия обучения классификатора.</param>
    /// <param name="trainingStatus">Статус обучения.</param>
    [Public]
    public virtual void SetClassifierTrainingStatus(IEntityRecognitionInfo recognitionInfo,
                                                    IClassifierTrainingSession trainingSession,
                                                    Enumeration? trainingStatus)
    {
      if (!Locks.GetLockInfo(recognitionInfo).IsLocked)
      {
        recognitionInfo.ClassifierTrainingSession = trainingSession;
        recognitionInfo.ClassifierTrainingStatus = trainingStatus;
        recognitionInfo.Save();
      }
      else
      {
        Logger.DebugFormat("ClassifierTraining. SetClassifierTrainingStatus. Failed to change training status. Recognition Info (Id={0}) is locked.",
                           recognitionInfo.Id);
      }
    }
    
    /// <summary>
    /// Получить информацию о задаче на обучение классификатора.
    /// </summary>
    /// <param name="arioTaskId">ИД задачи в Ario.</param>
    /// <returns>Информация о задаче в Ario.</returns>
    public virtual Sungero.ArioExtensions.Models.TrainTaskInfo GetTrainingTaskInfo(int arioTaskId)
    {
      var arioConnector = this.GetArioConnector();
      return arioConnector.GetTrainTaskInfo(arioTaskId.ToString());
    }
    
    /// <summary>
    /// Получить F1 - меру.
    /// </summary>
    /// <param name="classifierId">ИД классификатора.</param>
    /// <param name="modelId">ИД модели.</param>
    /// <returns>F1 - мера.</returns>
    [Public]
    public virtual double GetFMeasureFromModel(int classifierId, int modelId)
    {
      var arioConnector = this.GetArioConnector();
      var classifierInfo = arioConnector.GetClassifier(classifierId);
      // Получить F1-меру.
      var fisherMeasure = classifierInfo.ClassifierModels.Where(x => x.Id == modelId).FirstOrDefault().Metrics.F1Measure;
      return fisherMeasure;
    }
    
    /// <summary>
    /// Проверить, что f1-мера достаточной величины для публикации модели.
    /// </summary>
    /// <param name="fisherMeasure">F1-мера после обучения.</param>
    /// <returns>True - модель может быть опубликована (f1-мера >= порога), false - не может.</returns>
    [Public]
    public virtual bool IsFMeasureEnoughToPublish(double fisherMeasure)
    {
      var lowerFMeasureLimit = Docflow.PublicFunctions.Module.Remote.GetDocflowParamsNumbericValue(SmartProcessing.PublicConstants.Module.LowerFMeasureLimitParamName);
      return fisherMeasure >= lowerFMeasureLimit;
    }
    
    /// <summary>
    /// Завершить дообучение классификатора.
    /// </summary>
    /// <param name="classifierTrainingSession">Сессия дообучения.</param>
    /// <param name="arioTask">Задача Арио.</param>
    public virtual void FinalizeClassifierTraining(Sungero.Commons.IClassifierTrainingSession classifierTrainingSession,
                                                   Sungero.ArioExtensions.Models.TrainTask arioTask)
    {
      var classifierId = arioTask.Classifier.Id;
      var modelId = arioTask.ClassifierModel.Id;
      
      // Получить F1-меру и время дообучения.
      var fisherMeasure = SmartProcessing.Functions.Module.GetFMeasureFromModel(classifierId, modelId);
      var trainingTime = arioTask.Finished.Value - arioTask.Started;
      Logger.DebugFormat("ClassifierTraining. TrainClassifier: training successfully completed. F-measure - {0}, training time - {1} (sessionId={2}).",
                         fisherMeasure,
                         trainingTime.ToString(@"hh\:mm\:ss"),
                         classifierTrainingSession.Id);
      
      var isFMeasureEnoughToPublish = SmartProcessing.Functions.Module.IsFMeasureEnoughToPublish(fisherMeasure);
      
      // Опубликовать модель.
      var isAutoPublish = arioTask.Classifier.AutoPublish.Value;
      this.PublishModel(classifierTrainingSession, isAutoPublish, isFMeasureEnoughToPublish, classifierId, modelId);
      
      // Проставить статус сессии и результатов распознавания.
      this.SetTrainingStatus(classifierTrainingSession, fisherMeasure, isFMeasureEnoughToPublish);
    }
    
    /// <summary>
    /// Опубликовать модель.
    /// </summary>
    /// <param name="classifierTrainingSession">Сессия дообучения.</param>
    /// <param name="isAutoPublish">Модель автопубликуемая.</param>
    /// <param name="isFMeasureEnoughToPublish">F1-мера превышает предел.</param>
    /// <param name="classifierId">ИД классификатора.</param>
    /// <param name="modelId">ИД модели.</param>
    public virtual void PublishModel(Sungero.Commons.IClassifierTrainingSession classifierTrainingSession,
                                     bool isAutoPublish,
                                     bool isFMeasureEnoughToPublish,
                                     int classifierId,
                                     int modelId)
    {
      Logger.DebugFormat("ClassifierTraining. TrainClassifier: start publishing model, isAutoPublish = {0} & isFMeasureEnoughToPublish = {1} (sessionId={2}).",
                         isAutoPublish, isFMeasureEnoughToPublish, classifierTrainingSession.Id);
      
      if (!isAutoPublish)
      {
        if (isFMeasureEnoughToPublish)
        {
          // Обучение успешно. Опубликовать новую модель.
          var newModelId = modelId;
          classifierTrainingSession.NewModelId = newModelId;
          classifierTrainingSession.Save();
          this.PublishClassifierModel(classifierId, newModelId);
          Logger.DebugFormat("ClassifierTraining. TrainClassifier: model publishing completed (modelId = {0}, sessionId={1}).", newModelId, classifierTrainingSession.Id);
        }
        else
        {
          // Обучение не успешно. Ничего не делать.
          Logger.DebugFormat("ClassifierTraining. TrainClassifier: f-measure is too low, model publishing canceled (sessionId={0}).", classifierTrainingSession.Id);
        }
      }
      else
      {
        // Обучение не успешно. Восстановить старую модель.
        if (!isFMeasureEnoughToPublish)
        {
          var oldModelId = classifierTrainingSession.OldModelId.Value;
          this.PublishClassifierModel(classifierId, oldModelId);
          Logger.DebugFormat("ClassifierTraining. TrainClassifier: f-measure is too low, old model publishing completed (modelId = {0}, sessionId={1}).", oldModelId, classifierTrainingSession.Id);
        }
      }
    }
    
    /// <summary>
    /// Опубликовать модель.
    /// </summary>
    /// <param name="classifierId">ИД классификатора.</param>
    /// <param name="modelId">ИД модели.</param>
    [Public]
    public virtual void PublishClassifierModel(int classifierId, int modelId)
    {
      this.GetArioConnector().PublishClassifierModel(classifierId, modelId.ToString());
    }
    
    /// <summary>
    /// Заполнить статус дообучения.
    /// </summary>
    /// <param name="classifierTrainingSession">Сессия дообучения.</param>
    /// <param name="fisherMeasure">F1-мера.</param>
    /// <param name="isFMeasureEnoughToPublish">F1-мера превышает предел.</param>
    public virtual void SetTrainingStatus(Sungero.Commons.IClassifierTrainingSession classifierTrainingSession, double fisherMeasure, bool isFMeasureEnoughToPublish)
    {
      var sessionTrainingStatus = new Sungero.Core.Enumeration();
      var classifierTrainingStatus = new Sungero.Core.Enumeration();
      if (isFMeasureEnoughToPublish)
      {
        sessionTrainingStatus = Sungero.Commons.ClassifierTrainingSession.TrainingStatus.Completed;
        classifierTrainingStatus = Sungero.Commons.EntityRecognitionInfo.ClassifierTrainingStatus.Completed;
        
        Logger.DebugFormat("ClassifierTraining. TrainClassifier: finish async handler, training completed (sessionId={0}).", classifierTrainingSession.Id);
      }
      else
      {
        sessionTrainingStatus = Sungero.Commons.ClassifierTrainingSession.TrainingStatus.Rollback;
        classifierTrainingStatus = Sungero.Commons.EntityRecognitionInfo.ClassifierTrainingStatus.Awaiting;
        
        Logger.DebugFormat("ClassifierTraining. TrainClassifier: finish async handler, training rollback (sessionId={0}).", classifierTrainingSession.Id);
      }
      
      this.SetClassifierTrainingSessionStatus(fisherMeasure, classifierTrainingSession, sessionTrainingStatus);
      this.SetClassifierTrainingStatus(classifierTrainingSession, classifierTrainingStatus);
    }
    
    /// <summary>
    /// Заполнить статус обучения в сессии обучения классификатора.
    /// </summary>
    /// <param name="fisherMeasure">F1-мера.</param>
    /// <param name="classifierTrainingSession">Сессия обучения.</param>
    /// <param name="trainingSessionStatus">Статус сессии обучения.</param>
    [Public]
    public virtual void SetClassifierTrainingSessionStatus(double? fisherMeasure,
                                                           Sungero.Commons.IClassifierTrainingSession classifierTrainingSession,
                                                           Enumeration trainingSessionStatus)
    {
      if (fisherMeasure != null)
        classifierTrainingSession.FMeasure = fisherMeasure.ToString();
      
      classifierTrainingSession.TrainingStatus = trainingSessionStatus;
      classifierTrainingSession.Save();
    }
    
    /// <summary>
    /// Заполнить статус обучения в результатах распознавания.
    /// </summary>
    /// <param name="trainingSession">Сессия обучения классификатора.</param>
    /// <param name="trainingStatus">Статус обучения.</param>
    [Public]
    public virtual void SetClassifierTrainingStatus(IClassifierTrainingSession trainingSession,
                                                    Enumeration trainingStatus)
    {
      Logger.DebugFormat("ClassifierTraining. SetClassifierTrainingStatus. Setting status \"{0}\" (sessionId={1})",
                         trainingStatus.ToString(),
                         trainingSession.Id);
      var inProcessStatus = Sungero.Commons.EntityRecognitionInfo.ClassifierTrainingStatus.InProcess;
      var recognitionInfos = this.GetRecognitionInfos(trainingSession, inProcessStatus).ToList();
      
      if (trainingStatus == Sungero.Commons.EntityRecognitionInfo.ClassifierTrainingStatus.Awaiting)
        trainingSession = ClassifierTrainingSessions.Null;
      
      foreach (var info in recognitionInfos)
      {
        // Для заблокированных результатов распознавания статус проставляется асинхронно.
        if (!Locks.GetLockInfo(info).IsLocked)
        {
          info.ClassifierTrainingStatus = trainingStatus;
          info.Save();
        }
        else
        {
          var asyncHandler = AsyncHandlers.SetClassifierTrainingStatus.Create();
          asyncHandler.RecognitionInfoId = info.Id;
          asyncHandler.TrainingStatus = trainingStatus.ToString();
          asyncHandler.ExecuteAsync();
        }
      }
    }
    
    /// <summary>
    /// Получить результаты распознавания по сессии обучения.
    /// </summary>
    /// <param name="trainingSession">Сессия обучения классификатора.</param>
    /// <param name="classifierTrainingStatus">Статус обучения.</param>
    /// <returns>Результаты распознавания.</returns>
    [Public]
    public virtual IQueryable<Commons.IEntityRecognitionInfo> GetRecognitionInfos(IClassifierTrainingSession trainingSession, Enumeration classifierTrainingStatus)
    {
      var recognitionInfos = EntityRecognitionInfos.GetAll(x => x.ClassifierTrainingStatus == classifierTrainingStatus);
      if (trainingSession != null)
        recognitionInfos = recognitionInfos.Where(x => Equals(x.ClassifierTrainingSession, trainingSession));
      return recognitionInfos;
    }
    
    #endregion

    #region Фиксация верифицированного класса
    
    /// <summary>
    /// Получить класс Ario по типу сущности.
    /// </summary>
    /// <param name="entity">Сущность.</param>
    /// <returns>Класс Ario.</returns>
    [Public]
    public virtual string GetArioClassByEntityType(IEntity entity)
    {
      // Получить модуль и функцию обработки по типу сущности.
      var mapping = this.GetEntityTypeAndProcessingFunctionMapping(entity);
      var entityTypeGuid = entity.GetEntityMetadata().GetOriginal().NameGuid.ToString();
      var moduleAndFunctionNames = mapping
        .Where(x => string.Equals(x.Key, entityTypeGuid, StringComparison.InvariantCultureIgnoreCase))
        .Select(x => x.Value);
      if (moduleAndFunctionNames.Count() != 1)
        return string.Empty;
      
      // Получить класс Ario по модулю и функции обработки.
      var moduleAndFunctionName = moduleAndFunctionNames.FirstOrDefault();
      var smartProcessingSettings = Docflow.PublicFunctions.SmartProcessingSetting.GetSettings();
      var classNames = smartProcessingSettings.ProcessingRules
        .Where(x => string.Equals(x.ModuleName, moduleAndFunctionName[0], StringComparison.InvariantCultureIgnoreCase) &&
               string.Equals(x.FunctionName, moduleAndFunctionName[1], StringComparison.InvariantCultureIgnoreCase))
        .Select(x => x.ClassName);
      
      if (classNames.Count() != 1)
        return string.Empty;
      
      return classNames.FirstOrDefault();
    }
    
    /// <summary>
    /// Получить словарь соответствий типа сущности паре модуль и имя функции обработки.
    /// </summary>
    /// <param name="entity">Сущность.</param>
    /// <returns>Словарь соответствий.</returns>
    [Public]
    public virtual System.Collections.Generic.IDictionary<string, string[]> GetEntityTypeAndProcessingFunctionMapping(IEntity entity)
    {
      const string ModuleName = "Sungero.SmartProcessing";
      
      bool? isAdjustment = false;
      if (Docflow.AccountingDocumentBases.Is(entity))
        isAdjustment = Docflow.AccountingDocumentBases.As(entity).IsAdjustment;
      
      var taxInvoiceFunctionName = isAdjustment != true
        ? ProcessingFunctionName.CreateTaxInvoice
        : ProcessingFunctionName.CreateTaxInvoiceCorrection;
      
      var universalTransferDocumentFunctionName = isAdjustment != true
        ? ProcessingFunctionName.CreateUniversalTransferDocument
        : ProcessingFunctionName.CreateUniversalTransferCorrectionDocument;
      
      var mapping = new Dictionary<string, string[]>();
      mapping.Add(RecordManagement.PublicConstants.Module.IncomingLetterGuid,
                  new[] { ModuleName, ProcessingFunctionName.CreateIncomingLetter });
      
      mapping.Add(Docflow.PublicConstants.AccountingDocumentBase.ContractStatementInvoiceGuid,
                  new[] { ModuleName, ProcessingFunctionName.CreateContractStatement });
      
      mapping.Add(Docflow.PublicConstants.AccountingDocumentBase.WaybillInvoiceGuid,
                  new[] { ModuleName, ProcessingFunctionName.CreateWaybill });
      
      mapping.Add(Docflow.PublicConstants.AccountingDocumentBase.IncomingTaxInvoiceGuid,
                  new[] { ModuleName, taxInvoiceFunctionName });
      
      mapping.Add(Docflow.PublicConstants.AccountingDocumentBase.OutcomingTaxInvoiceGuid,
                  new[] { ModuleName, taxInvoiceFunctionName });
      
      mapping.Add(Docflow.PublicConstants.AccountingDocumentBase.UniversalTransferDocumentGuid,
                  new[] { ModuleName, universalTransferDocumentFunctionName });
      
      mapping.Add(Docflow.PublicConstants.AccountingDocumentBase.IncomingInvoiceGuid,
                  new[] { ModuleName, ProcessingFunctionName.CreateIncomingInvoice });
      
      mapping.Add(Contracts.PublicConstants.Module.ContractGuid,
                  new[] { ModuleName, ProcessingFunctionName.CreateContract });
      
      mapping.Add(Contracts.PublicConstants.Module.SupAgreementGuid,
                  new[] { ModuleName, ProcessingFunctionName.CreateSupAgreement });
      
      return mapping;
    }
    
    #endregion
    
    #region Тесты

    /// <summary>
    /// Обработать пакет бинарных образов документов (для теста).
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    [Public]
    public virtual void ProcessCapturedPackageTest(IBlobPackage blobPackage)
    {
      var arioPackage = this.UnpackArioPackage(blobPackage);
      
      var documentPackage = this.BuildDocumentPackageTest(blobPackage, arioPackage);
      
      this.OrderAndLinkDocumentPackage(documentPackage);
      
      this.SendToResponsible(documentPackage);
      
      // Вызываем асинхронную выдачу прав, так как убрали ее при сохранении.
      this.EnqueueGrantAccessRightsJobs(documentPackage);
      
      this.FinalizeProcessing(blobPackage);
    }

    /// <summary>
    /// Сформировать пакет документов (для теста).
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    /// <param name="arioPackage">Пакет результатов обработки документов в Ario.</param>
    /// <returns>Пакет созданных документов.</returns>
    public virtual IDocumentPackage BuildDocumentPackageTest(IBlobPackage blobPackage, IArioPackage arioPackage)
    {
      var documentPackage = this.PrepareDocumentPackageTest(blobPackage, arioPackage);
      
      documentPackage.Responsible = this.GetResponsible(blobPackage);

      foreach (var documentInfo in documentPackage.DocumentInfos)
      {
        var document = this.CreateDocument(documentInfo, documentPackage);

        this.CreateVersion(document, documentInfo.ArioDocument);

        this.FillDeliveryMethod(document, blobPackage.SourceType);

        this.FillVerificationState(document);

        this.SaveDocument(document, documentInfo);
      }

      this.CreateDocumentFromEmailBody(documentPackage);

      return documentPackage;
    }

    /// <summary>
    /// Создать незаполненный пакет документов (для теста).
    /// </summary>
    /// <param name="blobPackage">Пакет бинарных образов документов.</param>
    /// <param name="arioPackage">Пакет результатов обработки документов в Ario.</param>
    /// <returns>Заготовка пакета документов.</returns>
    [Public]
    public virtual IDocumentPackage PrepareDocumentPackageTest(IBlobPackage blobPackage, IArioPackage arioPackage)
    {
      var documentPackage = Structures.Module.DocumentPackage.Create();
      
      var documentInfos = new List<IDocumentInfo>();
      foreach (var arioDocument in arioPackage.Documents)
      {
        var filePath = arioDocument.OriginalBlob.FilePath;
        arioDocument.BodyFromArio = File.ReadAllBytes(filePath);

        var documentInfo = new DocumentInfo();
        documentInfo.ArioDocument = arioDocument;
        documentInfo.IsRecognized = arioDocument.IsRecognized;
        documentInfo.IsFuzzySearchEnabled = this.IsFuzzySearchEnabled();
        documentInfos.Add(documentInfo);
      }

      documentPackage.DocumentInfos = documentInfos;
      documentPackage.BlobPackage = blobPackage;

      return documentPackage;
    }

    /// <summary>
    /// Запустить фоновый процесс, удаляющий пакеты бинарных образов документов, которые отправлены на верификацию.
    /// </summary>
    [Public, Remote]
    public static void RequeueDeleteBlobPackagesJob()
    {
      Jobs.DeleteBlobPackages.Enqueue();
    }

    #endregion

    #region Логирование

    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="package">Пакет бинарных образов документов.</param>
    /// <param name="format">Формат сообщения.</param>
    /// <param name="args">Аргументы.</param>
    public void LogMessage(IBlobPackage package, string format, params object[] args)
    {
      this.LogMessage(string.Format(format, args),
                      package != null ? package.PackageId : string.Empty);
    }

    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="package">Пакет бинарных образов документов.</param>
    public void LogMessage(string message, IBlobPackage package)
    {
      this.LogMessage(message, package != null ? package.PackageId : string.Empty);
    }

    /// <summary>
    /// Записать сообщение в лог.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="packageId">ИД пакета бинарных образов документов.</param>
    public void LogMessage(string message, string packageId)
    {
      var logMessageFormat = "{0} (pkg={1})";
      Logger.DebugFormat(logMessageFormat, message, packageId);
    }

    /// <summary>
    /// Записать ошибку в лог.
    /// </summary>
    /// <param name="message">Текст ошибки.</param>
    /// <param name="exception">Исключение.</param>
    /// <param name="package">Пакет бинарных образов документов.</param>
    public void LogError(string message, System.Exception exception, IBlobPackage package)
    {
      this.LogError(message,
                    exception,
                    package != null ? package.PackageId : string.Empty);
    }

    /// <summary>
    /// Записать ошибку в лог.
    /// </summary>
    /// <param name="message">Текст ошибки.</param>
    /// <param name="exception">Исключение.</param>
    /// <param name="packageId">ИД пакета бинарных образов документов.</param>
    public void LogError(string message, System.Exception exception, string packageId)
    {
      var logMessage = string.Format("{0} (pkg={1})", message, packageId);
      Logger.Error(logMessage, exception);
    }

    #endregion

    #region Перекомплектование
    
    /// <summary>
    /// Создать сессию перекомплектования.
    /// </summary>
    /// <param name="assignmentId">Ид задания, из которого вызвано перекомплектование.</param>
    /// <param name="repackingDocuments">Документы для перекомплектования.</param>
    /// <returns>Сессия перекомплектования.</returns>
    [Remote]
    public virtual IRepackingSession CreateRepackingSession(int assignmentId, List<Structures.RepackingSession.RepackingDocument> repackingDocuments)
    {
      var newSession = RepackingSessions.Create();
      newSession.SessionId = Guid.NewGuid().ToString();
      newSession.AssignmentId = assignmentId;
      
      foreach (var repackingDocument in repackingDocuments)
      {
        var originalDocument = newSession.OriginalDocuments.AddNew();
        originalDocument.DocumentId = repackingDocument.Document.Id;
        originalDocument.VersionNumber = repackingDocument.Version.Number;
        originalDocument.DocumentName = repackingDocument.Document.Name;
      }
      
      try
      {
        newSession.Save();
        return newSession;
      }
      catch (Exception ex)
      {
        Logger.ErrorFormat("Repacking. CreateRepackingSession. Cannot save RepackingSession {0}", ex, ex.Message);
      }
      return null;
    }

    /// <summary>
    /// Сформировать строку с информацией о документах и типах для перекомплектования.
    /// </summary>
    /// <param name="sessionId">Ид сессии.</param>
    /// <returns>Строка в формате JSON с описание документов и типов.</returns>
    [Public(WebApiRequestType = RequestType.Get)]
    public virtual string GetSessionDocumentsAndTypes(string sessionId)
    {
      var response = string.Empty;
      
      var documentsTypesForRepacking = this.GetTypesForRepacking();
      var defaultType = documentsTypesForRepacking.FirstOrDefault().Id;
      var session = RepackingSessions.GetAll(l => l.SessionId == sessionId).FirstOrDefault();
      if (session != null)
      {
        if (session.ResultsApplied.HasValue && session.ResultsApplied.Value)
          return Constants.Module.RepackingClosedSessionResponse;
        
        if (session.Status == SmartProcessing.RepackingSession.Status.Closed && session.CloseDate.HasValue)
        {
          var closeTimeout = (int)Docflow.PublicFunctions.Module.Remote.GetDocflowParamsNumbericValue(Constants.Module.RepackingTabCloseTimeoutNameParamName);
          
          // Если вызов функции произошел больше чем через заданный период, значит, окно перекомплектования для указанной сессии было закрыто.
          if (Calendar.Now > session.CloseDate.Value.AddSeconds(closeTimeout != 0 ? closeTimeout : Constants.Module.RepackingTabCloseTimeout))
            return Constants.Module.RepackingClosedSessionResponse;
          else
          {
            session.CloseDate = null;
            session.Status = SmartProcessing.RepackingSession.Status.Active;
            var documentsAndVersions = Functions.RepackingSession.GetDocumentsWithVersions(session);
            if (!Functions.Module.TryLockRepackingSessionDocuments(documentsAndVersions))
              return Constants.Module.RepackingFailRelockResponse;
            Functions.RepackingSession.SaveSession(session);
          }
        }
        
        var repackingDocs = new List<Structures.Module.IRepackingDocumentDTO>();
        foreach (var document in session.OriginalDocuments)
        {
          var repackingDoc = Structures.Module.RepackingDocumentDTO.Create();
          repackingDoc.DocumentId = document.DocumentId.Value;
          repackingDoc.VersionId = document.VersionNumber.Value;
          repackingDoc.Name = document.DocumentName;
          repackingDoc.Type = defaultType;
          repackingDocs.Add(repackingDoc);
        }
        response = IsolatedFunctions.Repacking.BuildDocumentAndTypesResponseContent(repackingDocs, documentsTypesForRepacking);
      }
      return response;
    }
    
    /// <summary>
    /// Получить типы документов доступные для перекомплектования текущим пользователем.
    /// </summary>
    /// <returns>Список доступных типов документов.</returns>
    public virtual List<Structures.Module.IRepackingDocumentType> GetTypesForRepacking()
    {
      var documentTypes = Functions.Module.GetPackageDocumentTypePriorities();
      documentTypes.Add(typeof(Docflow.IAddendum).GetFinalType(), -1);
      
      var filtratedDocumentTypes = documentTypes.Where(x => (new Sungero.Domain.Security.EntityTypeAccessRights(x.Key)).CanCreate());

      // Получить список guid'ов типов документов из guid'ов интерфейсов.
      return filtratedDocumentTypes
        .OrderBy(x => x.Value)
        .Select(l => l.Key.GetEntityMetadata())
        .Select(x => Structures.Module.RepackingDocumentType.Create(x.NameGuid.ToString(), x.GetDisplayName().ToString()))
        .ToList();
    }

    /// <summary>
    /// Добавить исходные тела документов в сборщик Pdf документов.
    /// </summary>
    /// <param name="builderGuid">Guid сборщика Pdf документов.</param>
    /// <param name="repackingDocuments">Список из документов и их версий для загрузки в сборщик.</param>
    public virtual void AppendPdfBuilderSourceDocuments(Guid builderGuid,
                                                        List<Structures.RepackingSession.RepackingDocument> repackingDocuments)
    {
      foreach (var repackingDocument in repackingDocuments)
      {
        if (repackingDocument.Version == null)
          continue;
        var body = repackingDocument.Version.PublicBody.Size != 0 ? repackingDocument.Version.PublicBody : repackingDocument.Version.Body;
        Sungero.SmartProcessing.IsolatedFunctions.Repacking.AddSourceDocument(builderGuid, repackingDocument.Document.Id, body.Read());
      }
    }

    /// <summary>
    /// Изменить документы по результатам перекомплектования.
    /// </summary>
    /// <param name="sessionId">ИД сессии.</param>
    /// <param name="deletedDocuments">Список ИД удаленных документов.</param>
    /// <param name="newDocuments">Список новых документов.</param>
    /// <param name="changedDocuments">Список измененных документов.</param>
    /// <returns>Строка со списком ошибок.</returns>
    [Public(WebApiRequestType = RequestType.Post)]
    public virtual string ApplyRepackingResults(string sessionId,
                                                List<int> deletedDocuments,
                                                List<Structures.Module.INewDocument> newDocuments,
                                                List<Structures.Module.IChangedDocument> changedDocuments)
    {
      var session = RepackingSessions.GetAll(l => l.SessionId == sessionId).FirstOrDefault();
      if (session.ResultsApplied == true)
        return string.Empty;
      
      var assignment = VerificationAssignments.GetAll(t => t.Id == session.AssignmentId.Value).FirstOrDefault();
      var task = VerificationTasks.As(assignment.Task);
      if (session.Status == SmartProcessing.RepackingSession.Status.Closed ||
          !task.AccessRights.CanUpdate() && (newDocuments.Any() || deletedDocuments.Any()))
        return Sungero.SmartProcessing.Resources.RepackingSaveResult;
      
      var documentsAndVersions = Functions.RepackingSession.GetDocumentsWithVersions(session);
      if (newDocuments.Any() || changedDocuments.Any())
      {
        var builderGuid = Sungero.SmartProcessing.IsolatedFunctions.Repacking.CreateNewPdfBuilder();
        var documentPages = newDocuments.SelectMany(l => l.Pages).ToList();
        documentPages.AddRange(changedDocuments.SelectMany(l => l.Pages));

        var documentsIdsForLoad = documentPages
          .Select(x => x.DocumentId)
          .Distinct()
          .ToList();
        var documentsAndVersionsForLoad = documentsAndVersions.Where(l => documentsIdsForLoad.Contains(l.Document.Id)).ToList();
        
        this.AppendPdfBuilderSourceDocuments(builderGuid, documentsAndVersionsForLoad);
        
        if (newDocuments.Any())
          Functions.RepackingSession.CreateNewDocumentsAfterRepacking(session, newDocuments, builderGuid, task, deletedDocuments);

        if (changedDocuments.Any())
          Functions.RepackingSession.ChangeDocumentsAfterRepacking(session, changedDocuments, builderGuid);
        
        Sungero.SmartProcessing.IsolatedFunctions.Repacking.DeletePdfBuilder(builderGuid);
        Docflow.PublicFunctions.Module.PrepareAllAttachmentsPreviews(task);
        Functions.VerificationTask.PrepareAllAttachmentsRepackingPreviews(task);
      }

      if (deletedDocuments.Any())
        Functions.RepackingSession.DeleteDocumentsAfterRepacking(session, deletedDocuments);
      
      if (newDocuments.Any())
        Functions.RepackingSession.RenameDocumentsAfterRepacking(session, task);
      
      if (newDocuments.Any() || changedDocuments.Any() || deletedDocuments.Any())
        Functions.VerificationTask.AddRelationsToLeadingDocument(task, deletedDocuments);

      session.ResultsApplied = true;
      Functions.RepackingSession.SaveSession(session);
      
      this.FinalizeRepackingSession(session);
      
      var result = session.Errors.Any() ? Sungero.SmartProcessing.Resources.RepackingSaveResult : string.Empty;
      return result;
    }
    
    /// <summary>
    /// Установить дату закрытия сессии и снять блокировки с документов.
    /// </summary>
    /// <param name="sessionId">Ид сессии.</param>
    /// <returns>True - если дата успешно установлена.</returns>
    [Public(WebApiRequestType = RequestType.Get)]
    public virtual bool FinalizeRepackingSession(string sessionId)
    {
      var session = RepackingSessions.GetAll(l => l.SessionId == sessionId).FirstOrDefault();
      return this.FinalizeRepackingSession(session);
    }
    
    /// <summary>
    /// Установить дату закрытия сессии и снять блокировки с документов.
    /// </summary>
    /// <param name="session">Сессия.</param>
    /// <returns>True - если дата успешно установлена.</returns>
    public virtual bool FinalizeRepackingSession(IRepackingSession session)
    {
      if (session.Status != SmartProcessing.RepackingSession.Status.Closed)
      {
        session.CloseDate = Calendar.Now;
        session.Status = SmartProcessing.RepackingSession.Status.Closed;
        Functions.RepackingSession.SaveSession(session);
        var documentsAndVersions = Functions.RepackingSession.GetDocumentsWithVersions(session);
        Functions.Module.UnlockDocumentsWithVersions(documentsAndVersions);
      }
      return true;
    }
    
    /// <summary>
    /// Переименовать созданные в перекомплектовании простые документы.
    /// </summary>
    /// <param name="documentsIds">Список ИД документов.</param>
    /// <param name="documentName">Имя документа.</param>
    /// <param name="lastNumber">Номер документа.</param>
    public virtual void RenameSimpleDocuments(List<int?> documentsIds, string documentName, int lastNumber)
    {
      var documents = OfficialDocuments.GetAll(x => documentsIds.Contains(x.Id)).ToList();
      foreach (var document in documents)
      {
        if (document.DocumentKind.DocumentType.DocumentTypeGuid == typeof(Docflow.ISimpleDocument).GetFinalType().GetTypeGuid().ToString() &&
            (!document.DocumentKind.GenerateDocumentName.Value) && (document.Name == document.DocumentKind.ShortName))
        {
          lastNumber++;
          document.Name = string.Format(SmartProcessing.Resources.SimpleAttachmentName, documentName, lastNumber);
          document.Save();
        }
      }
    }
    
    /// <summary>
    /// Попытаться выполнить все операции по удалению документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>True - если все операции по удалению документа прошли успешно, иначе - False.</returns>
    /// <remarks>Документ не будет удален из базы, так как это очень тяжелая операция для СУБД.
    /// У документа будут:
    /// - очищен статус верификации;
    /// - установлен статус "Устаревший";
    /// - удалены версии;
    /// - удалены связи;
    /// - сменен тип на "Простой документ".</remarks>
    [Remote]
    public virtual bool TryMakeDocumentDeleted(IOfficialDocument document)
    {
      
      // Вычисление возможности смены типа должно происходить до начала всех действий с удаляемым документом,
      // так как при вычислении проверяется статус верификации документа и для корректной работы проверки он должен быть равен "В процессе",
      // а ниже в коде он очищается, поэтому проверка должна быть в этом месте.
      var canConvertDocument = Functions.Module.CanConvertDocument(document);
      
      Functions.Module.ClearVerificationState(document);
      document.LifeCycleState = Docflow.OfficialDocument.LifeCycleState.Obsolete;
      Functions.Module.DeleteDocumentVersions(document);
      Functions.Module.ClearDocumentRelations(document);
      document.Save();
      
      if (canConvertDocument)
      {
        Functions.Module.ConvertDocumentToSimpleObsolete(document);
        return true;
      }
      
      return false;
    }
    
    /// <summary>
    /// Определить новый ведущий документ комплекта исходя из типов документов.
    /// </summary>
    /// <param name="newDocuments">Список документов, которые будут созданы.</param>
    /// <param name="documents">Список документов, которые уже есть во вложениях.</param>
    /// <returns>Главный документ комплекта.</returns>
    /// <remarks>Если главный документ комплекта будет среди вновь создаваемых, то функция вернет null.
    /// В структуре такого нового документа будет установлен признак IsLeading = true.</remarks>
    public virtual IOfficialDocument GetNewLeadingDocumentByType(List<Structures.Module.INewDocument> newDocuments,
                                                                 List<IOfficialDocument> documents)
    {
      var documentGuidPriority = Functions.Module.GetPackageDocumentTypePriorities();
      var allDocumentsGuids = documents.Select(d => d.GetType().GetFinalType()).ToList();
      allDocumentsGuids.AddRange(newDocuments.Select(d => Sungero.Domain.Shared.TypeExtension.GetTypeByGuid(Guid.Parse(d.TypeId))));
      var leadingDocumentGuid = documentGuidPriority.Where(g => allDocumentsGuids.Contains(g.Key)).OrderByDescending(g => g.Value).Select(g => g.Key).FirstOrDefault();
      
      var leadingDocument = documents.Where(d => d.GetType().GetFinalType() == leadingDocumentGuid).FirstOrDefault();
      if (leadingDocument != null)
        return leadingDocument;

      // Если ведущий документ комплекта среди новых документов, пометить его ведущим и учесть при создании.
      var newLeadDocument = newDocuments.Where(d => Sungero.Domain.Shared.TypeExtension.GetTypeByGuid(Guid.Parse(d.TypeId)) == leadingDocumentGuid).FirstOrDefault();
      if (newLeadDocument != null)
        newLeadDocument.IsLeading = true;
      
      return null;
    }
    
    #endregion
  }
}
