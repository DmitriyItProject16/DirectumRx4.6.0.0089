using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using Sungero.Content;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow;
using Sungero.RecordManagement;

namespace DirRX.DCTSIntegration.Server
{
  public class ModuleFunctions
  {
    /// <summary>
    /// Получает контрагента по его email.
    /// </summary>
    /// <param name="email">Почта.</param>
    /// <returns>Контрагент.</returns>
    [Remote]
    public static Sungero.Parties.ICounterparty GetCounterpartyByEmail(string email)
    {
      return Sungero.Parties.Counterparties.GetAll().FirstOrDefault(c => c.Email == email);
    }
    
    /// <summary>
    /// Возвращает документ по указанному идентификатору.
    /// </summary>
    /// <param name="id">Идентификатор документа.</param>
    /// <returns>Документ.</returns>
    [Remote]
    public virtual IOfficialDocument LocateDocumentById(int id)
    {
      return OfficialDocuments.GetAll().Where(d => d.Id == id).FirstOrDefault();
    }
    
    /// <summary>
    /// Получает XDocument из xml файла.
    /// </summary>
    /// <param name="path">Путь к файлу.</param>
    /// <returns>XElement документ.</returns>
    private static XDocument GetXDocumentFromFile(string path)
    {
      if (!File.Exists(path)) throw new FileNotFoundException(string.Format(DCTSIntegration.Resources.FileNotFoundTemplate, path));
      return XDocument.Load(path);
    }
    
    /// <summary>
    /// Получает XDocument из xml строки.
    /// </summary>
    /// <param name="path">xml строка.</param>
    /// <returns>XElement документ.</returns>
    private static XDocument GetXDocumentFromString(string xmlString)
    {
      return XDocument.Parse(xmlString);
    }
    
    /// <summary>
    /// Получает параметры DCTSKit. Ключ: наименование параметра, значение: значение параметра.
    /// </summary>
    /// <returns>Словарь параметров.</returns>
    private static Dictionary<string, string> GetCustomConfigSettings()
    {
      var res = new Dictionary<string, string>();
      var configPath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, DCTSIntegration.Resources.CustomConfigSettings);
      if (File.Exists(configPath))
      {
        var document = GetXDocumentFromFile(configPath);
        foreach (var el in document.Element("settings").Elements())
        {
          res.Add(el.Attribute("name").Value, el.Attribute("value").Value);
        }
      }
      return res;
    }
    
    /// <summary>
    /// Получает сервер распознавания из конфигурационных файлов.
    /// </summary>
    /// <returns>Сервер распознавания.</returns>
    private static string GetServerBarcodeRecognition()
    {
      var customConfigSettings = GetCustomConfigSettings();
      return customConfigSettings.ContainsKey(DCTSIntegration.Resources.ServerBarcodeRecognition) ? customConfigSettings[DCTSIntegration.Resources.ServerBarcodeRecognition] : DCTSIntegration.Resources.DefaultServerBarcode;
    }
    
    [Public(WebApiRequestType = RequestType.Post)]
    public static void ProcessingDCTSKit(string lineSender, string instanceInfos, string deviceInfo, string inputFiles, List<string> filesBase64, List<string> filesName)
    {
      Functions.Module.InternalProcessingDCTS(lineSender, instanceInfos, deviceInfo, inputFiles, filesBase64, filesName);
    }
    
    /// <summary>
    /// Обрабатывает пакеты службы ввода DCS.
    /// </summary>
    /// <param name="lineSender">Наименование линии.</param>
    /// <param name="instanceInfos">Xml файл DCS в формате строки c информацией об экземплярах захвата и о захваченных файлах.</param>
    /// <param name="deviceInfo">Xml файл DCS в формате строки c информацией об устройствах ввода.</param>
    /// <param name="inputFiles">Xml файл DCS в формате строки c информацией об отправляемых в конечную систему файлах.</param>
    /// <param name="filesBase64">Список файлов в формате Base64 переданных DCS</param>
    /// <param name="filesName">Список наименований файлов в формате Base64 переданных DCS</param>
    public virtual void InternalProcessingDCTS(string lineSender, string instanceInfos, string deviceInfo, string inputFiles, List<string> filesBase64, List<string> filesName)
    {
      var files = new List<Structures.Module.IDocumentPackage>();
      var numberFile = 0;
      foreach (var file in filesName)
      {
        var document = Structures.Module.DocumentPackage.Create();
        document.FileName = file;
        document.FileBase64 = filesBase64.ElementAt(numberFile);
        files.Add(document);
        numberFile++;
      }
      var instanceInfosXDoc = GetXDocumentFromString(instanceInfos);
      var inputFilesXDoc = GetXDocumentFromString(inputFiles);
      var deviceInfoXDoc = GetXDocumentFromString(deviceInfo);
      
      // Модуль ввода, с которого пришел пакет.
      var captureServiceXElement = deviceInfoXDoc.FirstNode as XElement;
      var captureService = captureServiceXElement.Name.LocalName == "MailSourceInfo" ? DCTSIntegration.ProcessingRuleBase.CaptureService.Mail : DCTSIntegration.ProcessingRuleBase.CaptureService.FileSystem;
      
      var barcode = GetBarcodeFromXml(instanceInfosXDoc, captureService);
      var id = this.GetIdDocumentByBarcode(barcode);
      
      // Если найдено правило существующего документа, то поиск штрихкода в документе.
      var doc = Functions.ProcessingRuleExistingDoc.GetProcessingRuleExistingDocByLineSender(lineSender) != null ? Functions.Module.LocateDocumentById(id) : null;
      var rules = Functions.ProcessingRuleBase.GetRuleByParams(doc, lineSender, captureService);
      rules = rules.Any(c => ProcessingRuleExistingDocs.Is(c)) ? rules.Where(c => ProcessingRuleExistingDocs.Is(c)) : rules;
      if (!rules.Any())
        throw new InvalidOperationException(DCTSIntegration.Resources.ProcessingRuleNotFound);
      if (rules.Count() > 1)
        throw new InvalidOperationException(DCTSIntegration.Resources.MultilpleProcessingRuleFound);
      Functions.ProcessingRuleBase.ProcessEntity(rules.First(), inputFilesXDoc.ToString(), instanceInfosXDoc.ToString(), captureService == DCTSIntegration.ProcessingRuleBase.CaptureService.Mail, files, doc);
    }
    
    /// <summary>
    /// Открывает документ по его ИД.
    /// </summary>
    /// <param name="id">Ид.</param>
    public static void OpenDocumentById(string id)
    {
      var hyperLink = Hyperlinks.Get(Sungero.RecordManagement.IncomingLetters.Info, int.Parse(id));
      System.Diagnostics.Process.Start(hyperLink);
    }
    
    /// <summary>
    /// Получить значение из xml-файла по имени элемента.
    /// </summary>
    /// <param name="file">Xml-файл.</param>
    /// <returns>Значение элемента.</returns>
    public static string GetBarcodeFromXml(System.Xml.Linq.XDocument file, Sungero.Core.Enumeration deviceType)
    {
      var nameNode = deviceType == DCTSIntegration.ProcessingRuleBase.CaptureService.Mail ? "MailCaptureInstanceInfo" : "FileSystemCaptureInstanceInfo";
      try
      {
        var barcode = file.Element("CaptureInstanceInfoList").Element(nameNode).Element("Files").Element("FileInfo").Element("Barcodes")
          .Value.Split(new string[] { "-" }, StringSplitOptions.RemoveEmptyEntries);
        return barcode[barcode.Length - 1].Trim();
      }
      catch (Exception ex)
      {
        Logger.DebugFormat("Отсутствует штрихкод документа в InstanceInfos.xml", ex);
      }
      return string.Empty;

    }
    
    /// <summary>
    /// Получить ИД документа по штрихкоду.
    /// </summary>
    /// <param name="barcode">Штрихкод.</param>
    /// <returns>ИД документа.</returns>
    /// <remarks>По умолчанию, в штрихкоде содержится ИД документа.</remarks>
    public virtual int GetIdDocumentByBarcode(string barcode)
    {
      return barcode != string.Empty ? Convert.ToInt32(barcode) : 0;
    }
  }
}