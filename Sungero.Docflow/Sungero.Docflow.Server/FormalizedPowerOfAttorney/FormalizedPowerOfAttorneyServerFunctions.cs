using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow.FormalizedPowerOfAttorney;
using Sungero.FormalizeDocumentsParser.PowerOfAttorney;
using Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model;
using Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAEnums;
using Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV2;
using Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV3;
using Sungero.Parties;
using PoAServiceErrors = Sungero.PowerOfAttorneyCore.PublicConstants.Module.PowerOfAttorneyServiceErrors;
using XmlElementNames = Sungero.Docflow.Constants.FormalizedPowerOfAttorney.XmlElementNames;
using XmlFPoAInfoAttributeNames = Sungero.Docflow.Constants.FormalizedPowerOfAttorney.XmlFPoAInfoAttributeNames;
using XmlIssuedToAttributeNames = Sungero.Docflow.Constants.FormalizedPowerOfAttorney.XmlIssuedToAttributeNames;

namespace Sungero.Docflow.Server
{
  partial class FormalizedPowerOfAttorneyFunctions
  {
    
    #region Заявление на отзыв МЧД

    /// <summary>
    /// Получить заявление на отзыв эл. доверенности без учета прав доступа.
    /// </summary>
    /// <returns>Заявление на отзыв эл. доверенности.</returns>
    [Public, Remote]
    public virtual Sungero.Docflow.IPowerOfAttorneyRevocation GetRevocation()
    {
      IPowerOfAttorneyRevocation revocation = null;
      AccessRights.AllowRead(() =>
                             {
                               revocation = PowerOfAttorneyRevocations.GetAll().FirstOrDefault(r => Equals(r.FormalizedPowerOfAttorney, _obj));
                             });
      return revocation;
    }
    
    /// <summary>
    /// Создать заявление на отзыв эл. доверенности.
    /// </summary>
    /// <param name="reason">Причина отзыва доверенности.</param>
    /// <returns>Заявление на отзыв эл. доверенности.</returns>
    [Public, Remote]
    public virtual Sungero.Docflow.IPowerOfAttorneyRevocation CreateRevocation(string reason)
    {
      var revocation = PowerOfAttorneyRevocations.Create();
      Functions.PowerOfAttorneyRevocation.FillRevocationProperties(revocation, _obj, reason);

      if (!Functions.PowerOfAttorneyRevocation.GenerateRevocationBody(revocation))
        return null;
      
      revocation.Relations.Add(Docflow.PublicConstants.Module.SimpleRelationName, _obj);
      revocation.Save();
      Functions.PowerOfAttorneyRevocation.GenerateRevocationPdf(revocation);
      return revocation;
    }
    
    #endregion
    
    #region Генерация МЧД
    
    /// <summary>
    /// Сформировать тело эл. доверенности.
    /// </summary>
    /// <returns>True - если генерация завершилась успешно.</returns>
    [Public, Remote]
    public virtual bool GenerateFormalizedPowerOfAttorneyBody()
    {
      var unifiedRegistrationNumber = Guid.NewGuid();
      var xml = this.CreateFormalizedPowerOfAttorneyXml(unifiedRegistrationNumber);
      var isValidXml = this.ValidateGeneratedFormalizedPowerOfAttorneyXml(xml);
      
      if (isValidXml)
      {
        _obj.UnifiedRegistrationNumber = unifiedRegistrationNumber.ToString();
        this.AddNewVersionIfLastVersionApproved();
        
        // Сохраняем сущность, чтобы избежать формирования некорректной версии в случае, если сущность создана копированием (Bug 283975).
        if (_obj.State.IsCopied)
          _obj.Save();
        
        Functions.OfficialDocument.WriteBytesToDocumentLastVersionBody(_obj, xml, Constants.FormalizedPowerOfAttorney.XmlExtension);
        _obj.LifeCycleState = Docflow.FormalizedPowerOfAttorney.LifeCycleState.Draft;
        _obj.FtsListState = null;
        _obj.FtsRejectReason = string.Empty;
        _obj.Save();
      }
      else
      {
        Logger.DebugFormat("Generate formalized power of attorney body validation error. Document id: {0}", _obj.Id);
        return false;
      }
      
      return true;
    }
    
    /// <summary>
    /// Создать тело эл. доверенности.
    /// </summary>
    /// <param name="unifiedRegistrationNumber">Единый регистрационный номер доверенности.</param>
    /// <returns>Тело эл. доверенности.</returns>
    public virtual Docflow.Structures.Module.IByteArray CreateFormalizedPowerOfAttorneyXml(Guid unifiedRegistrationNumber)
    {
      if (_obj.FormatVersion == FormatVersion.Version002)
        return this.CreateFormalizedPowerOfAttorneyXmlV2(unifiedRegistrationNumber);
      
      if (_obj.FormatVersion == FormatVersion.Version003)
        return this.CreateFormalizedPowerOfAttorneyXmlV3(unifiedRegistrationNumber);
      
      Logger.Debug("Unsupported power of attorney format version.");
      return null;
    }
    
    /// <summary>
    /// Получить значение атрибута "ИдФайл".
    /// </summary>
    /// <param name="unifiedRegistrationNumber">Единый регистрационный номер доверенности.</param>
    /// <returns>Значение атрибута "ИдФайл".</returns>
    public virtual string GetFileIdAttribute(Guid unifiedRegistrationNumber)
    {
      return string.Format("{0}_{1}", Calendar.UserNow.ToString("yyyyMMdd"), unifiedRegistrationNumber);
    }
    
    private CitizenshipFlag GetCitizenshipFlag(Sungero.Commons.ICountry country)
    {
      if (country == null)
        return CitizenshipFlag.None;
      
      if (country.Code == Constants.FormalizedPowerOfAttorney.RussianFederationCountryCode)
        return CitizenshipFlag.Russia;

      return CitizenshipFlag.Other;
    }

    /// <summary>
    /// Проверить сформированную xml доверенности.
    /// </summary>
    /// <param name="xml">Тело эл. доверенности.</param>
    /// <returns>True - если проверка xml прошла успешно.</returns>
    [Public]
    public virtual bool ValidateGeneratedFormalizedPowerOfAttorneyXml(Docflow.Structures.Module.IByteArray xml)
    {
      if (xml == null || xml.Bytes == null || xml.Bytes.Length == 0)
        return false;
      
      var validationResult = FormalizeDocumentsParser.Extension.ValidatePowerOfAttorneyXml(xml.Bytes);
      var isValidXml = !validationResult.Any();
      if (!isValidXml)
      {
        Logger.WithProperty("details", string.Join(Environment.NewLine, validationResult))
          .Error("ValidateGeneratedFormalizedPowerOfAttorneyXml. Validation error. Document id: {id}", _obj.Id);
      }
      
      return isValidXml;
    }
    
    /// <summary>
    /// Создать новую версию, если последняя утверждена.
    /// </summary>
    [Public]
    public virtual void AddNewVersionIfLastVersionApproved()
    {
      if (!_obj.HasVersions)
        return;
      
      if (_obj.LastVersionApproved == true)
        _obj.Versions.AddNew();
    }
    
    #endregion
    
    #region Импорт МЧД из xml
    
    /// <summary>
    /// Загрузить тело эл. доверенности из XML и импортировать внешнюю подпись.
    /// </summary>
    /// <param name="xml">Структура с XML.</param>
    /// <param name="signature">Структура с подписью.</param>
    [Remote, Public]
    public virtual void ImportFormalizedPowerOfAttorneyFromXmlAndSign(Docflow.Structures.Module.IByteArray xml,
                                                                      Docflow.Structures.Module.IByteArray signature)
    {
      Functions.FormalizedPowerOfAttorney.SetJustImportedParam(_obj);
      
      this.ValidateFormalizedPowerOfAttorneyXml(xml);
      
      signature = this.ConvertSignatureFromBase64(signature);
      this.VerifyExternalSignature(xml, signature);
      
      this.FillFormalizedPowerOfAttorney(xml);
      this.VerifyDocumentUniqueness();
      
      Functions.OfficialDocument.WriteBytesToDocumentLastVersionBody(_obj, xml, Constants.FormalizedPowerOfAttorney.XmlExtension);
      
      // Сохранение необходимо для импорта подписи.
      _obj.Save();
      
      // Сохранение записи об импорте xml-файла в историю.
      var importFromXmlOperationText = Constants.FormalizedPowerOfAttorney.Operation.ImportFromXml;
      var importFromXmlComment = Sungero.Docflow.FormalizedPowerOfAttorneys.Resources.ImportFromXmlHistoryComment;
      _obj.History.Write(new Enumeration(importFromXmlOperationText), null, importFromXmlComment, _obj.LastVersion.Number);
      
      this.ImportSignature(xml, signature);
      this.CheckSignature();
    }
    
    /// <summary>
    /// Проверка уникальности эл. доверенности по рег.номеру.
    /// </summary>
    /// <remarks>Если эл. доверенности с таким же рег.номером существуют, то генерируется ошибка.</remarks>
    public virtual void VerifyDocumentUniqueness()
    {
      var duplicates = this.GetFormalizedPowerOfAttorneyDuplicates();
      if (duplicates.Any())
        throw new AppliedCodeException(FormalizedPowerOfAttorneys.Resources.DuplicatesDetected);
    }
    
    /// <summary>
    /// Декодировать подпись из base64.
    /// </summary>
    /// <param name="signature">Подпись.</param>
    /// <returns>Декодированная подпись.</returns>
    [Public]
    public virtual Docflow.Structures.Module.IByteArray ConvertSignatureFromBase64(Docflow.Structures.Module.IByteArray signature)
    {
      var signatureInfo = ExternalSignatures.GetSignatureInfo(signature.Bytes);
      // Если подпись передали в закодированном виде, попытаться раскодировать.
      if (signatureInfo.SignatureFormat == SignatureFormat.Hash)
      {
        try
        {
          var byteString = System.Text.Encoding.UTF8.GetString(signature.Bytes);
          var signatureBytes = Convert.FromBase64String(byteString);
          signature = Docflow.Structures.Module.ByteArray.Create(signatureBytes);
        }
        catch
        {
          Logger.Error("Import formalized power of attorney. Failed to import signature: cannot decode given signature.");
          throw AppliedCodeException.Create(FormalizedPowerOfAttorneys.Resources.SignatureImportFailed);
        }
      }
      
      return signature;
    }
    
    /// <summary>
    /// Проверить подпись на достоверность.
    /// </summary>
    /// <param name="xml">Подписанные данные.</param>
    /// <param name="signature">Подпись.</param>
    [Public]
    public virtual void VerifyExternalSignature(Docflow.Structures.Module.IByteArray xml, Docflow.Structures.Module.IByteArray signature)
    {
      using (var xmlStream = new System.IO.MemoryStream(xml.Bytes))
      {
        var signatureInfo = ExternalSignatures.Verify(signature.Bytes, xmlStream);
        if (signatureInfo.Errors.Any())
        {
          Logger.ErrorFormat("Import formalized power of attorney. Failed to import signature: {0}", string.Join("\n", signatureInfo.Errors.Select(x => x.Message)));
          throw AppliedCodeException.Create(FormalizedPowerOfAttorneys.Resources.SignatureImportFailed);
        }
      }
    }
    
    /// <summary>
    /// Заполнить свойства эл. доверенности.
    /// </summary>
    /// <param name="xml">Тело эл. доверенности.</param>
    [Public]
    public virtual void FillFormalizedPowerOfAttorney(Docflow.Structures.Module.IByteArray xml)
    {
      var version = Sungero.FormalizeDocumentsParser.Extension.GetPoAVersion(xml.Bytes);
      
      if (version == PoAVersion.V002)
      {
        this.FillFPoAV2(xml);
        return;
      }
      
      if (version == PoAVersion.V003)
      {
        this.FillFPoAV3(xml);
        return;
      }
      
      this.FillFPoADefault(xml);
    }
    
    /// <summary>
    /// Заполнить поля доверенности из десериализованного объекта.
    /// </summary>
    /// <param name="xml">Тело доверенности.</param>
    public virtual void FillFPoADefault(Docflow.Structures.Module.IByteArray xml)
    {
      System.Xml.Linq.XDocument xdoc;
      using (var memoryStream = new System.IO.MemoryStream(xml.Bytes))
        xdoc = System.Xml.Linq.XDocument.Load(memoryStream);
      
      var poaInfo = this.TryGetPoAInfoElement(xdoc);
      if (poaInfo == null)
      {
        Logger.Error("Import formalized power of attorney. Failed to parse given XML as formalized power of attorney.");
        throw AppliedCodeException.Create(FormalizedPowerOfAttorneys.Resources.XmlLoadFailed);
      }
      
      this.FillFormatVersionFromXml(xdoc);
      
      this.FillUnifiedRegistrationNumberFromXml(xdoc,
                                                poaInfo,
                                                XmlFPoAInfoAttributeNames.UnifiedRegistrationNumber);
      
      this.FillValidDatesFromXml(xdoc,
                                 poaInfo,
                                 XmlFPoAInfoAttributeNames.ValidFrom,
                                 XmlFPoAInfoAttributeNames.ValidTill);
      
      // Получить регистрационные данные из xml и попытаться пронумеровать документ.
      // Если в xml нет даты регистрации, но есть номер, взять текущую дату в качестве даты регистрации.
      string number = this.GetAttributeValueByName(poaInfo, XmlFPoAInfoAttributeNames.RegistrationNumber);
      DateTime? date = this.GetDateFromXml(poaInfo, XmlFPoAInfoAttributeNames.RegistrationDate) ?? Calendar.Today;
      this.FillRegistrationData(number, date);
      this.FillIssuedToFromXml(xdoc);
      this.FillDocumentName(xdoc);
    }
    
    /// <summary>
    /// Получить XML-элемент с информацией об эл. доверенности.
    /// </summary>
    /// <param name="xdoc">XML-документ.</param>
    /// <returns>XML-элемент с информацией о доверенности.</returns>
    [Public]
    public virtual System.Xml.Linq.XElement TryGetPoAInfoElement(System.Xml.Linq.XDocument xdoc)
    {
      var poaFormat = this.GetPoAFormatVersionFromXml(xdoc);
      switch (poaFormat)
      {
        case "001":
          return xdoc.Element(XmlElementNames.PowerOfAttorney)
            ?.Element(XmlElementNames.Document)
            ?.Element(XmlElementNames.PowerOfAttorneyInfo);
        case "002":
        default:
          return xdoc.Element(XmlElementNames.PowerOfAttorney)
            ?.Element(XmlElementNames.Document)
            ?.Element(XmlElementNames.PowerOfAttorneyVersion2)
            ?.Element(XmlElementNames.PowerOfAttorneyInfo);
      }
    }
    
    /// <summary>
    /// Получить XML-элемент с информацией об эл. доверенности.
    /// </summary>
    /// <param name="xdoc">XML-документ.</param>
    /// <param name="poaElementName">Имя элемента, содержащего доверенность.</param>
    /// <param name="documentElementName">Имя элемента, содержащего документ.</param>
    /// <param name="poaInfoElementName">Имя элемента, содержащего информацию о доверенности.</param>
    /// <returns>XML-элемент с информацией о доверенности.</returns>
    [Public, Obsolete("Используйте метод TryGetPoAInfoElement(XDocument)")]
    public virtual System.Xml.Linq.XElement TryGetPoAInfoElement(System.Xml.Linq.XDocument xdoc,
                                                                 string poaElementName,
                                                                 string documentElementName,
                                                                 string poaInfoElementName)
    {
      try
      {
        return xdoc.Element(poaElementName).Element(documentElementName).Element(poaInfoElementName);
      }
      catch (Exception ex)
      {
        Logger.ErrorFormat("Import formalized power of attorney. Failed to parse given XML as formalized power of attorney: {0}",
                           ex.Message);
        throw AppliedCodeException.Create(FormalizedPowerOfAttorneys.Resources.XmlLoadFailed);
      }
    }
    
    /// <summary>
    /// Заполнить версию формата эл. доверенности.
    /// </summary>
    /// <param name="xdoc">XML-документ.</param>
    public virtual void FillFormatVersionFromXml(System.Xml.Linq.XDocument xdoc)
    {
      var fpoaFormat = this.GetPoAFormatVersionFromXml(xdoc);
      
      switch (fpoaFormat)
      {
        case "001":
          {
            _obj.FormatVersion = null;
            break;
          }
        case "002":
          {
            _obj.FormatVersion = FormatVersion.Version002;
            break;
          }
        case "EMCHD_1":
          {
            _obj.FormatVersion = FormatVersion.Version003;
            break;
          }
        default:
          {
            Logger.Error("Import formalized power of attorney. Unsupported formalized power of attorney format version.");
            throw AppliedCodeException.Create(FormalizedPowerOfAttorneys.Resources.XmlLoadFailed);
          }
      }
    }
    
    /// <summary>
    /// Заполнить единый рег. номер эл. доверенности из xml-файла.
    /// </summary>
    /// <param name="xdoc">Тело доверенности в xml-формате.</param>
    /// <param name="powerOfAttorneyInfo">Xml-элемент с информацией об эл. доверенности.</param>
    /// <param name="poaUnifiedRegNumberAttributeName">Имя атрибута, содержащего единый рег.номер доверенности.</param>
    [Public]
    public virtual void FillUnifiedRegistrationNumberFromXml(System.Xml.Linq.XDocument xdoc,
                                                             System.Xml.Linq.XElement powerOfAttorneyInfo,
                                                             string poaUnifiedRegNumberAttributeName)
    {
      var unifiedRegNumber = this.GetAttributeValueByName(powerOfAttorneyInfo, poaUnifiedRegNumberAttributeName);
      _obj.UnifiedRegistrationNumber = GetUniformGuid(unifiedRegNumber);
    }
    
    private static string GetUniformGuid(string guidStr)
    {
      Guid guid;
      if (!Guid.TryParse(guidStr, out guid))
      {
        throw AppliedCodeException.Create(FormalizedPowerOfAttorneys.Resources.XmlLoadFailed,
                                          FormalizedPowerOfAttorneys.Resources.ErrorValidateUnifiedRegistrationNumber);
      }
      
      return guid.ToString();
    }
    
    /// <summary>
    /// Заполнить дату начала и окончания действия эл. доверенности из xml-файла.
    /// </summary>
    /// <param name="xdoc">Тело доверенности в xml-формате.</param>
    /// <param name="powerOfAttorneyInfo">Xml-элемент с информацией об эл. доверенности.</param>
    /// <param name="poaValidFromAttributeName">Имя атрибута, содержащего дату начала действия доверенности.</param>
    /// <param name="poaValidTillAttributeName">Имя атрибута, содержащего дату окончания действия доверенности.</param>
    [Public]
    public virtual void FillValidDatesFromXml(System.Xml.Linq.XDocument xdoc,
                                              System.Xml.Linq.XElement powerOfAttorneyInfo,
                                              string poaValidFromAttributeName,
                                              string poaValidTillAttributeName)
    {
      DateTime? validFrom;
      DateTime? validTill;
      try
      {
        validFrom = this.GetDateFromXml(powerOfAttorneyInfo, poaValidFromAttributeName);
        validTill = this.GetDateFromXml(powerOfAttorneyInfo, poaValidTillAttributeName);
      }
      catch (Exception ex)
      {
        Logger.Error("Import formalized power of attorney. Failed to parse validity dates from xml.", ex);
        throw AppliedCodeException.Create(FormalizedPowerOfAttorneys.Resources.XmlLoadFailed);
      }
      if (validFrom == null || validTill == null)
      {
        Logger.Error("Import formalized power of attorney. Failed to parse validity dates from xml.");
        throw AppliedCodeException.Create(FormalizedPowerOfAttorneys.Resources.XmlLoadFailed);
      }
      _obj.ValidFrom = validFrom;
      _obj.ValidTill = validTill;
    }
    
    /// <summary>
    /// Заполнить рег. данные эл. доверенности в зависимости от настроек вида документа.
    /// </summary>
    /// <param name="number">Регистрационный номер.</param>
    /// <param name="date">Дата регистрации.</param>
    /// <remarks>Если вид документа ненумеруемый, данные не будут заполнены.</remarks>
    [Public]
    public virtual void FillRegistrationData(string number, DateTime? date)
    {
      if (string.IsNullOrEmpty(number) || !date.HasValue)
        return;
      
      // Проверить настройки RX на возможность нумерации документа.
      if (_obj.DocumentKind == null || _obj.DocumentKind.NumberingType == Docflow.DocumentKind.NumberingType.NotNumerable)
        return;
      
      if (_obj.DocumentKind.NumberingType == Docflow.DocumentKind.NumberingType.Numerable)
      {
        var matchingRegistersIds = Functions.OfficialDocument.GetDocumentRegistersIdsByDocument(_obj, Docflow.RegistrationSetting.SettingType.Numeration);
        if (matchingRegistersIds.Count == 1)
        {
          var register = DocumentRegisters.Get(matchingRegistersIds.First());
          Functions.OfficialDocument.RegisterDocument(_obj, register, date, number, false, false);
          return;
        }
      }
      if (_obj.AccessRights.CanRegister())
      {
        _obj.RegistrationDate = date;
        _obj.RegistrationNumber = number;
      }
      else
      {
        var registrationDataString = FormalizedPowerOfAttorneys.Resources.FormalizedPowerOfAttorneyFormat(_obj.DocumentKind.ShortName,
                                                                                                          number,
                                                                                                          date.Value.Date.ToString("d"));
        _obj.Note = registrationDataString + Environment.NewLine + _obj.Note;
      }
      return;
    }
    
    /// <summary>
    /// Заполнить поле Кому эл. доверенности из xml-файла.
    /// </summary>
    /// <param name="xdoc">Тело доверенности в xml-формате.</param>
    [Public]
    public virtual void FillIssuedToFromXml(System.Xml.Linq.XDocument xdoc)
    {
      // Не перезаполнять Кому.
      if (_obj.IssuedTo != null)
        return;
      
      // Не заполнять Кому, если тип представителя не Сотрудник.
      if (_obj.AgentType != Docflow.FormalizedPowerOfAttorney.AgentType.Employee)
        return;
      
      // Получить ИНН, СНИЛС и ФИО из xml.
      var issuedToInfoFromXml = this.GetIssuedToInfoFromXml(xdoc);
      this.FillIssuedTo(issuedToInfoFromXml);
    }
    
    /// <summary>
    /// Заполнить поле Кому эл. доверенности.
    /// </summary>
    /// <param name="info">Структура с информацией о представителе.</param>
    public virtual void FillIssuedTo(Structures.FormalizedPowerOfAttorney.IIssuedToInfo info)
    {
      // Попытаться заполнить Кому по ИНН.
      if (!string.IsNullOrWhiteSpace(info.TIN))
      {
        var employees = Company.PublicFunctions.Employee.Remote.GetEmployeesByTIN(info.TIN);
        if (employees.Count() == 1)
        {
          _obj.IssuedTo = employees.FirstOrDefault();
          return;
        }
      }
      
      // Попытаться заполнить Кому по СНИЛС.
      if (!string.IsNullOrWhiteSpace(info.INILA))
      {
        var employees = Company.PublicFunctions.Employee.Remote.GetEmployeesByINILA(info.INILA);
        if (employees.Count() == 1)
        {
          _obj.IssuedTo = employees.FirstOrDefault();
          return;
        }
      }
      
      // Попытаться заполнить Кому по ФИО.
      if (!string.IsNullOrWhiteSpace(info.FullName))
      {
        if (_obj.IssuedTo == null)
          _obj.IssuedTo = Company.PublicFunctions.Employee.Remote.GetEmployeeByName(info.FullName);
      }
    }
    
    /// <summary>
    /// Заполнить имя эл. доверенности.
    /// </summary>
    /// <param name="xdoc">Тело доверенности в xml-формате.</param>
    [Public]
    public virtual void FillDocumentName(System.Xml.Linq.XDocument xdoc)
    {
      this.SetDefaultDocumentName();
    }
    
    /// <summary>
    /// Заполнить имя эл. доверенности значением по умолчанию.
    /// </summary>
    public virtual void SetDefaultDocumentName()
    {
      // Заполнить пустое имя документа из сокращенного имени вида документа.
      if (string.IsNullOrWhiteSpace(_obj.Name) && _obj.DocumentKind != null && _obj.DocumentKind.GenerateDocumentName != true)
        _obj.Name = _obj.DocumentKind.ShortName;
    }
    
    /// <summary>
    /// Импортировать подпись.
    /// </summary>
    /// <param name="xml">Структура с подписанными данными.</param>
    /// <param name="signature">Структура с подписью.</param>
    /// <remarks>В случае если подпись без даты, которая в Sungero обязательна, будет выполнена попытка проставить подпись
    /// хоть как-нибудь. Подпись после этого будет отображаться как невалидная, но она хотя бы будет.
    /// Валидная подпись останется только в сервисе.</remarks>
    [Public]
    public virtual void ImportSignature(Docflow.Structures.Module.IByteArray xml, Docflow.Structures.Module.IByteArray signature)
    {
      var signatureBytes = signature.Bytes;

      // Получить подписавшего из сертификата.
      var certificateInfo = Docflow.PublicFunctions.Module.GetSignatureCertificateInfo(signatureBytes);
      var signatoryName = Docflow.PublicFunctions.Module.GetCertificateSignatoryName(certificateInfo.SubjectInfo);
      
      // Импортировать подпись.
      Signatures.Import(_obj, SignatureType.Approval, signatoryName, signatureBytes, _obj.LastVersion);
    }
    
    /// <summary>
    /// Проверить, что документ подписан. Если нет, сгенерировать исключение.
    /// </summary>
    [Public]
    public virtual void CheckSignature()
    {
      Sungero.Domain.Shared.ISignature importedSignature;
      importedSignature = Signatures.Get(_obj.LastVersion)
        .Where(s => s.IsExternal == true && s.SignCertificate != null)
        .OrderByDescending(x => x.Id)
        .FirstOrDefault();
      
      if (importedSignature == null)
      {
        Logger.DebugFormat("Can't find signature on document with version id: '{0}'", _obj.LastVersion.Id);
        throw AppliedCodeException.Create(FormalizedPowerOfAttorneys.Resources.SignatureImportFailed);
      }
    }
    
    /// <summary>
    /// Получить дату из информации об эл. доверенности из xml-файла.
    /// </summary>
    /// <param name="element">Элемент с датой.</param>
    /// <param name="attributeName">Наименование атрибута для даты.</param>
    /// <returns>Дата.</returns>
    [Public]
    public virtual DateTime? GetDateFromXml(System.Xml.Linq.XElement element, string attributeName)
    {
      var dateValue = this.GetAttributeValueByName(element, attributeName);
      if (string.IsNullOrEmpty(dateValue))
        return null;
      
      DateTime date;
      if (Calendar.TryParseDate(dateValue, out date))
        return date;
      
      return Convert.ToDateTime(dateValue);
    }
    
    /// <summary>
    /// Получить из xml информацию об уполномоченном представителе.
    /// </summary>
    /// <param name="xdoc">Тело доверенности в xml-формате.</param>
    /// <returns>Структура с информацией.</returns>
    [Public]
    public virtual Structures.FormalizedPowerOfAttorney.IIssuedToInfo GetIssuedToInfoFromXml(System.Xml.Linq.XDocument xdoc)
    {
      var result = Structures.FormalizedPowerOfAttorney.IssuedToInfo.Create();
      
      // Получить элементы, связанные с уполномоченным представителем.
      var representativeElements = this.GetRepresentativeElements(xdoc);
      
      // Не искать по сотрудникам, если в xml нет узлов или больше одного узла с уполномоченным представителем, который является физ. лицом.
      if (representativeElements == null || !representativeElements.Any() || representativeElements.Count() > 1)
        return result;
      
      var representativeElement = representativeElements.FirstOrDefault();
      if (representativeElement == null)
        return result;
      
      // Получить ИНН, СНИЛС и ФИО уполномоченного представителя.
      var individualElement = representativeElement.Element(XmlElementNames.Individual);
      if (individualElement == null)
        return result;
      
      var tin = this.GetAttributeValueByName(individualElement, XmlIssuedToAttributeNames.TIN);
      var inila = this.GetAttributeValueByName(individualElement, XmlIssuedToAttributeNames.INILA);
      var fullName = this.GetIssuedToFullNameFromXml(individualElement);
      
      return Structures.FormalizedPowerOfAttorney.IssuedToInfo.Create(fullName, tin, inila);
    }
    
    /// <summary>
    /// Получить XML-элемент c информацией об уполномоченном представителе.
    /// </summary>
    /// <param name="xdoc">Тело доверенности в xml-формате.</param>
    /// <returns>XML-элемент с информацией об уполномоченном представителе.</returns>
    [Public]
    public virtual List<System.Xml.Linq.XElement> GetRepresentativeElements(System.Xml.Linq.XDocument xdoc)
    {
      var poaFormat = this.GetPoAFormatVersionFromXml(xdoc);
      // Получить элементы, связанные с уполномоченным представителем.
      switch (poaFormat)
      {
        case "001":
          return xdoc?.Element(XmlElementNames.PowerOfAttorney)
            ?.Element(XmlElementNames.Document)
            ?.Element(XmlElementNames.AuthorizedRepresentative)
            ?.Elements(XmlElementNames.Representative)
            ?.ToList();
        case "002":
        default:
          return xdoc?.Element(XmlElementNames.PowerOfAttorney)
            ?.Element(XmlElementNames.Document)
            ?.Element(XmlElementNames.PowerOfAttorneyVersion2)
            ?.Elements(XmlElementNames.AuthorizedRepresentative)
            ?.ToList();
      }
    }
    
    /// <summary>
    /// Получить версию формата эл. доверенности из xml-файла.
    /// </summary>
    /// <param name="xdoc">Тело доверенности в xml-формате.</param>
    /// <returns>Версия формата эл. доверенности.</returns>
    [Public]
    public virtual string GetPoAFormatVersionFromXml(System.Xml.Linq.XDocument xdoc)
    {
      var versionFormatElement = xdoc?.Element(XmlElementNames.PowerOfAttorney);
      
      try
      {
        return this.GetAttributeValueByName(versionFormatElement, Constants.FormalizedPowerOfAttorney.XmlFPoAFormatVersionAttributeName);
      }
      catch (Exception ex)
      {
        Logger.ErrorFormat("Import formalized power of attorney. Failed to parse given XML as formalized power of attorney: {0}",
                           ex.Message);
        throw AppliedCodeException.Create(FormalizedPowerOfAttorneys.Resources.XmlLoadFailed);
      }
    }
    
    /// <summary>
    /// Получить имя того, кому выдана эл. доверенность из xml-файла.
    /// </summary>
    /// <param name="individualElement">Элемент xml с информацией о полномочном представителе.</param>
    /// <returns>ФИО.</returns>
    [Public]
    public virtual string GetIssuedToFullNameFromXml(System.Xml.Linq.XElement individualElement)
    {
      var individualNameElement = individualElement.Element(XmlIssuedToAttributeNames.IndividualName);
      if (individualNameElement == null)
        return string.Empty;
      
      // Собрать полные ФИО из фамилии, имени и отчества.
      var parts = new List<string>();
      var surname = this.GetAttributeValueByName(individualNameElement, XmlIssuedToAttributeNames.LastName);
      if (!string.IsNullOrWhiteSpace(surname))
        parts.Add(surname);
      var name = this.GetAttributeValueByName(individualNameElement, XmlIssuedToAttributeNames.FirstName);
      if (!string.IsNullOrWhiteSpace(name))
        parts.Add(name);
      var patronymic = this.GetAttributeValueByName(individualNameElement, XmlIssuedToAttributeNames.MiddleName);
      if (!string.IsNullOrWhiteSpace(patronymic))
        parts.Add(patronymic);
      
      var fullName = string.Join(" ", parts);
      return fullName;
    }
    
    /// <summary>
    /// Получить значение атрибута по имени.
    /// </summary>
    /// <param name="element">Элемент, которому принадлежит атрибут.</param>
    /// <param name="attributeName">Имя атрибута.</param>
    /// <returns>Значение или пустая строка, если атрибут не найден.</returns>
    [Public]
    public virtual string GetAttributeValueByName(System.Xml.Linq.XElement element, string attributeName)
    {
      var attribute = element?.Attribute(attributeName);
      return attribute == null ? string.Empty : attribute.Value;
    }
    
    private static string GetFullName(string lastName, string firstName, string middleName)
    {
      var parts = new string[] { lastName, firstName, middleName };
      return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
    
    #endregion
    
    #region Регистрация МЧД
    
    /// <summary>
    /// Зарегистрировать эл. доверенность в ФНС.
    /// </summary>
    /// <returns>Результат отправки: ИД операции регистрации в сервисе доверенностей или ошибка.</returns>
    [Public, Remote]
    public virtual PowerOfAttorneyCore.Structures.Module.IResponseResult RegisterFormalizedPowerOfAttorneyWithService()
    {
      return this.RegisterFormalizedPowerOfAttorneyWithService(null);
    }
    
    /// <summary>
    /// Зарегистрировать эл. доверенность в ФНС.
    /// </summary>
    /// <param name="taskId">Ид задачи, если регистрация происходит в контексте задачи.</param>
    /// <returns>Результат отправки: ИД операции регистрации в сервисе доверенностей или ошибка.</returns>
    [Public, Remote]
    public virtual PowerOfAttorneyCore.Structures.Module.IResponseResult RegisterFormalizedPowerOfAttorneyWithService(int? taskId)
    {
      // Отправка запроса на регистрацию.
      var powerOfAttorneyBytes = Functions.Module.GetBinaryData(_obj.LastVersion.Body);
      var signature = Functions.OfficialDocument.GetSignatureFromOurSignatory(_obj, _obj.LastVersion.Id);
      var signatureBytes = signature?.GetDataSignature();
      var sendingResult = PowerOfAttorneyCore.PublicFunctions.Module.SendPowerOfAttorneyForRegistration(_obj.BusinessUnit,
                                                                                                        powerOfAttorneyBytes,
                                                                                                        signatureBytes);
      if (!string.IsNullOrWhiteSpace(sendingResult.ErrorCode) || !string.IsNullOrWhiteSpace(sendingResult.ErrorType))
      {
        this.HandleRegistrationError(sendingResult.ErrorCode);
        return sendingResult;
      }

      // Успешная отправка на регистрацию.
      _obj.LifeCycleState = Docflow.FormalizedPowerOfAttorney.LifeCycleState.Draft;
      _obj.FtsListState = Docflow.FormalizedPowerOfAttorney.FtsListState.OnRegistration;
      _obj.RegisteredSignatureId = signature.Id;
      _obj.Save();
      
      var queueItem = PowerOfAttorneyQueueItems.Create();
      queueItem.OperationType = Docflow.PowerOfAttorneyQueueItem.OperationType.Registration;
      queueItem.DocumentId = _obj.Id;
      queueItem.OperationId = sendingResult.OperationId;
      queueItem.TaskId = taskId;
      queueItem.Save();
      
      sendingResult.QueueItem = queueItem;

      // Запуск мониторинга регистрации доверенности в сервисе доверенностей.
      var getFPoAStateHandler = AsyncHandlers.SetFPoARegistrationState.Create();
      getFPoAStateHandler.QueueItemId = queueItem.Id;
      getFPoAStateHandler.ExecuteAsync(FormalizedPowerOfAttorneys.Resources.FormalizedPowerOfAttorneySentForRegistrationSuccessfully,
                                       FormalizedPowerOfAttorneys.Resources.FormalizedPowerOfAttorneyRegistrationCompleted);
      return sendingResult;
    }
    
    /// <summary>
    /// Заполнить Состояние, статус В реестре ФНС и сообщение об ошибке по коду ошибки.
    /// </summary>
    /// <param name="errorCode">Код ошибки.</param>
    [Public]
    public virtual void HandleRegistrationError(string errorCode)
    {
      _obj.LifeCycleState = Sungero.Docflow.FormalizedPowerOfAttorney.LifeCycleState.Draft;
      _obj.FtsListState = Sungero.Docflow.FormalizedPowerOfAttorney.FtsListState.Rejected;
      _obj.RegisteredSignatureId = null;
      
      var reasonsDictionary = this.GetErrorCodeAndReasonMapping();
      _obj.FtsRejectReason = reasonsDictionary
        .Where(x => string.Equals(x.Key, errorCode, StringComparison.InvariantCultureIgnoreCase))
        .Select(x => x.Value)
        .FirstOrDefault();
      if (string.IsNullOrEmpty(_obj.FtsRejectReason))
        _obj.FtsRejectReason = Sungero.Docflow.FormalizedPowerOfAttorneys.Resources.DefaultErrorMessage;
      
      _obj.Save();
      
      Logger.Error($"HandleRegistrationError. Power of attorney registration error = {errorCode} (PoA id = {_obj.Id}).");
    }
    
    public virtual System.Collections.Generic.Dictionary<string, string> GetErrorCodeAndReasonMapping()
    {
      var result = new Dictionary<string, string>();
      
      result.Add(Constants.FormalizedPowerOfAttorney.FPoARegistrationErrors.ExternalSystemIsUnavailableError,
                 FormalizedPowerOfAttorneys.Resources.ExternalSystemIsUnavailableErrorMessage);
      result.Add(Constants.FormalizedPowerOfAttorney.FPoARegistrationErrors.InvalidCertificateError,
                 FormalizedPowerOfAttorneys.Resources.DifferentSignatureErrorMessage);
      result.Add(Constants.FormalizedPowerOfAttorney.FPoARegistrationErrors.RepeatedRegistrationError,
                 FormalizedPowerOfAttorneys.Resources.ConflictDifferentSignatureErrorMessage);
      result.Add(Constants.FormalizedPowerOfAttorney.FPoARegistrationErrors.DifferentSignatureError,
                 FormalizedPowerOfAttorneys.Resources.DifferentSignatureErrorMessage);
      result.Add(Constants.FormalizedPowerOfAttorney.FPoARegistrationErrors.UnsupportedPoA,
                 FormalizedPowerOfAttorneys.Resources.UnsupportedPoAErrorMessage);
      
      return result;
    }

    #endregion

    #region Поиск дублей

    /// <summary>
    /// Получить дубли эл. доверенности.
    /// </summary>
    /// <returns>Дубли эл. доверенности.</returns>
    [Public, Remote(IsPure = true)]
    public virtual List<IFormalizedPowerOfAttorney> GetFormalizedPowerOfAttorneyDuplicates()
    {
      var duplicates = new List<IFormalizedPowerOfAttorney>();
      if (string.IsNullOrEmpty(_obj.UnifiedRegistrationNumber))
      {
        return duplicates;
      }

      AccessRights.AllowRead(
        () =>
        {
          duplicates = FormalizedPowerOfAttorneys
            .GetAll()
            .Where(f => !Equals(f, _obj) &&
                   f.UnifiedRegistrationNumber == _obj.UnifiedRegistrationNumber)
            .ToList();
        });
      return duplicates;
    }
    
    #endregion
    
    #region Печатная форма
    
    /// <summary>
    /// Сгенерировать PDF из тела доверенности.
    /// </summary>
    [Public, Remote(IsPure = true)]
    public virtual void GenerateFormalizedPowerOfAttorneyPdf()
    {
      this.ConvertToPdfAndAddSignatureMark(_obj.LastVersion.Id);
      
      PublicFunctions.Module.LogPdfConverting("Signature mark. Added interactively", _obj, _obj.LastVersion);
    }
    
    /// <summary>
    /// Преобразовать документ в PDF с наложением отметки об ЭП.
    /// </summary>
    /// <returns>Результат преобразования.</returns>
    /// <remarks>Перед преобразованием валидируются документ и подпись на версии.</remarks>
    [Remote]
    public override Structures.OfficialDocument.СonversionToPdfResult ConvertToPdfWithSignatureMark()
    {
      var versionId = _obj.LastVersion.Id;
      var info = this.ValidateDocumentBeforeConvertion(versionId);
      if (info.HasErrors)
        return info;
      
      info = this.ConvertToPdfAndAddSignatureMark(versionId);
      info.ErrorTitle = OfficialDocuments.Resources.ConvertionErrorTitleBase;
      
      return info;
    }
    
    /// <summary>
    /// Преобразовать документ в PDF и поставить отметку об ЭП, если есть утверждающая подпись.
    /// </summary>
    /// <param name="versionId">ИД версии документа.</param>
    /// <returns>Результат преобразования в PDF.</returns>
    [Remote]
    public override Structures.OfficialDocument.СonversionToPdfResult ConvertToPdfAndAddSignatureMark(int versionId)
    {
      var signatureMark = string.Empty;
      
      var signature = Functions.OfficialDocument.GetSignatureFromOurSignatory(_obj, versionId);
      if (signature != null)
        signatureMark = Functions.Module.GetSignatureMarkAsHtml(_obj, signature);
      
      return this.GeneratePublicBodyWithSignatureMark(versionId, signatureMark);
    }
    
    /// <summary>
    /// Получить электронную подпись для простановки отметки.
    /// </summary>
    /// <param name="versionId">Номер версии.</param>
    /// <returns>Электронная подпись.</returns>
    [Public]
    public override Sungero.Domain.Shared.ISignature GetSignatureForMark(int versionId)
    {
      return this.GetSignatureFromOurSignatory(versionId);
    }
    
    /// <summary>
    /// Получить тело и расширение версии для преобразования в PDF с отметкой об ЭП.
    /// </summary>
    /// <param name="version">Версия для генерации.</param>
    /// <param name="isSignatureMark">Признак отметки об ЭП. True - отметка об ЭП, False - отметка о поступлении.</param>
    /// <returns>Тело версии документа и расширение.</returns>
    /// <remarks>Для преобразования в PDF эл. доверенности необходимо сначала получить ее в виде html.</remarks>
    [Public]
    public override Structures.OfficialDocument.IVersionBody GetBodyToConvertToPdf(Sungero.Content.IElectronicDocumentVersions version, bool isSignatureMark)
    {
      var result = Structures.OfficialDocument.VersionBody.Create();
      if (version == null)
        return result;
      
      var html = this.GetFormalizedPowerOfAttorneyAsHtml(version);
      if (string.IsNullOrWhiteSpace(html))
        return result;

      result.Body = System.Text.Encoding.UTF8.GetBytes(html);
      result.Extension = Constants.FormalizedPowerOfAttorney.HtmlExtension;
      return result;
    }
    
    /// <summary>
    /// Получить эл. доверенность в виде html.
    /// </summary>
    /// <param name="version">Версия, на основании которой формируется html.</param>
    /// <returns>Эл. доверенность в виде html.</returns>
    [Public]
    public virtual string GetFormalizedPowerOfAttorneyAsHtml(Sungero.Content.IElectronicDocumentVersions version)
    {
      if (version == null)
        return string.Empty;
      
      // Получить модель эл. доверенности из xml.
      using (var body = new System.IO.MemoryStream())
      {
        // Выключить error-логирование при доступе к зашифрованным бинарным данным.
        AccessRights.SuppressSecurityEvents(() => version.Body.Read().CopyTo(body));
        return FormalizeDocumentsParser.Extension.ProducePoAHtml(body.ToArray(), this.GetNameMapping());
      }
    }
    
    /// <summary>
    /// Получить класс с заполненным словарем кодов документов и его сокращенного названия.
    /// </summary>
    /// <returns>Класс с заполненным словарем: Key - код документа, Value - сокращенное имя документа.</returns>
    private Sungero.FormalizeDocumentsParser.PowerOfAttorney.NameMapping GetNameMapping()
    {
      var nameMapping = new Sungero.FormalizeDocumentsParser.PowerOfAttorney.NameMapping();
      nameMapping.IdentityDocuments = new Dictionary<string, string>();
      var kinds = IdentityDocumentKinds.GetAll(idk => idk.Status == CoreEntities.DatabookEntry.Status.Active);
      
      foreach (var kind in kinds)
      {
        if (!nameMapping.IdentityDocuments.ContainsKey(kind.Code))
          nameMapping.IdentityDocuments.Add(kind.Code, kind.ShortName.ToLower());
      }
      
      return nameMapping;
    }
    
    /// <summary>
    /// Определить, поддерживается ли преобразование в PDF для переданного расширения.
    /// </summary>
    /// <param name="extension">Расширение.</param>
    /// <returns>True, если поддерживается, иначе False.</returns>
    /// <remarks>МЧД имеют расширение XML, которое всегда поддерживается.</remarks>
    [Public]
    public override bool CheckPdfConvertibilityByExtension(string extension)
    {
      return true;
    }

    #endregion
    
    #region Проверка состояния в сервисе
    
    /// <summary>
    /// Синхронизировать статус эл. доверенности в реестре ФНС.
    /// </summary>
    [Public, Remote]
    public virtual void SyncFormalizedPowerOfAttorneyFtsListState()
    {
      var batchGuid = Guid.NewGuid().ToString();
      Functions.Module.CreateFormalizedPoAQueueItemBatch(new List<int>() { _obj.Id }, batchGuid);
      
      var documentHyperlink = Hyperlinks.Get(_obj);
      var completeMessage = FormalizedPowerOfAttorneys.Resources.SyncFtsListStateSuccessNotificationFormat(documentHyperlink, Environment.NewLine);
      
      var asyncHandler = Docflow.AsyncHandlers.SyncFormalizedPoAWithService.Create();
      asyncHandler.BatchGuid = batchGuid;
      asyncHandler.BusinessUnitId = _obj.BusinessUnit.Id;
      asyncHandler.ExecuteAsync(completeMessage);
      Logger.DebugFormat("ExecuteSyncFormalizedPoAWithServiceAsyncHandler. FormalizedPoABatchGuid: '{0}'.", batchGuid);
    }
    
    /// <summary>
    /// Проверить состояние эл. доверенности в ФНС.
    /// </summary>
    /// <returns>Результат проверки.</returns>
    [Public, Remote]
    public virtual string CheckFormalizedPowerOfAttorneyState()
    {
      // Если доверенность в процессе регистрации или отзыва - не выполнять запрос в сервис.
      var registrationInProcess = this.RegistrationInProcess();
      if (registrationInProcess)
        return FormalizedPowerOfAttorneys.Resources.FPoARegistrationInProcess;
      
      var revocationInProcess = this.RevocationInProcess();
      if (revocationInProcess)
        return FormalizedPowerOfAttorneys.Resources.FPoARevocationInProcess;
      
      // Отправка запроса на валидацию.
      var agent = this.CreateAgent();
      var powerOfAttorneyXml = Docflow.PublicFunctions.Module.GetBinaryData(_obj.LastVersion.Body);
      var signature = Docflow.PublicFunctions.FormalizedPowerOfAttorney.GetRegisteredSignature(_obj);
      var signatureBytes = signature?.GetDataSignature();
      var validationState = PowerOfAttorneyCore.PublicFunctions.Module.CheckPowerOfAttorneyState(_obj.BusinessUnit, _obj.UnifiedRegistrationNumber,
                                                                                                 agent, powerOfAttorneyXml, signatureBytes);
      
      if (validationState.Errors.Any(x => string.Equals(x.Type, PoAServiceErrors.ConnectionError, StringComparison.InvariantCultureIgnoreCase)))
        return PowerOfAttorneyCore.Resources.PowerOfAttorneyNoConnection;
      
      if (string.IsNullOrEmpty(validationState.Result))
        return Sungero.Docflow.FormalizedPowerOfAttorneys.Resources.FPoAStateCheckFailed;
      
      if (validationState.Result == Constants.FormalizedPowerOfAttorney.FPoAState.Valid)
      {
        // Ответ не получен за таймаут или сразу вернулся ответ, что данные не актуальны.
        // При переповторе данные могут успеть актуализироваться.
        if (validationState.Errors.Any(x => string.Equals(x.Code, PoAServiceErrors.StateIsOutdated, StringComparison.InvariantCultureIgnoreCase)))
          return Sungero.Docflow.FormalizedPowerOfAttorneys.Resources.FPoAStateCheckFailed;
        
        if (validationState.Errors.Any(x => string.Equals(x.Code, PoAServiceErrors.PoANotFound, StringComparison.InvariantCultureIgnoreCase)))
          return Sungero.Docflow.FormalizedPowerOfAttorneys.Resources.FPoANotFound;
        
        Functions.FormalizedPowerOfAttorney.SetLifeCycleAndFtsListStates(_obj, LifeCycleState.Active, FtsListState.Registered);
        _obj.Save();
        return FormalizedPowerOfAttorneys.Resources.FPoAStateHasBeenUpdated;
      }
      
      if (validationState.Result == Constants.FormalizedPowerOfAttorney.FPoAState.Invalid)
      {
        // Для отозванных дополнительно запросить причину и дату подписания отзыва.
        if (validationState.Errors.Any(x => string.Equals(x.Code, Constants.FormalizedPowerOfAttorney.FPoAState.Revoked, StringComparison.InvariantCultureIgnoreCase)))
        {
          if (!PublicFunctions.FormalizedPowerOfAttorney.SetRevokedState(_obj))
            return Sungero.Docflow.FormalizedPowerOfAttorneys.Resources.FPoASetRevokedStateFailed;
          
          PublicFunctions.FormalizedPowerOfAttorney.Remote.CreateSetSignatureSettingsValidTillAsyncHandler(_obj, _obj.ValidTill.Value);
          return FormalizedPowerOfAttorneys.Resources.FPoAStateHasBeenUpdated;
        }
        
        if (validationState.Errors.Any(x => string.Equals(x.Code, Constants.FormalizedPowerOfAttorney.FPoAState.Expired, StringComparison.InvariantCultureIgnoreCase)))
        {
          Functions.FormalizedPowerOfAttorney.SetLifeCycleAndFtsListStates(_obj, LifeCycleState.Obsolete, FtsListState.Registered);
          _obj.Save();
          return FormalizedPowerOfAttorneys.Resources.FPoAStateHasBeenUpdated;
        }
        
        if (validationState.Errors.Any(x => string.Equals(x.Code, PoAServiceErrors.PoaIsNotValidYet, StringComparison.InvariantCultureIgnoreCase)))
          return Sungero.Docflow.FormalizedPowerOfAttorneys.Resources.FPoANotFound;
        
        return Sungero.Docflow.FormalizedPowerOfAttorneys.Resources.FPoAAttributesNotMatchXml;
      }
      
      return Sungero.Docflow.FormalizedPowerOfAttorneys.Resources.FPoAStateCheckFailed;
    }
    
    /// <summary>
    /// Проверить, находится ли эл. доверенность в процессе регистрации.
    /// </summary>
    /// <returns>True - если в процессе регистрации, иначе - false.</returns>
    public virtual bool RegistrationInProcess()
    {
      if (_obj.FtsListState == Docflow.FormalizedPowerOfAttorney.FtsListState.OnRegistration)
      {
        var registrationQueueItem = PowerOfAttorneyQueueItems.GetAll()
          .Where(q => q.DocumentId == _obj.Id && q.OperationType == Docflow.PowerOfAttorneyQueueItem.OperationType.Registration)
          .FirstOrDefault();
        
        return registrationQueueItem != null;
      }
      
      return false;
    }
    
    /// <summary>
    /// Проверить, находится ли эл. доверенность в процессе отзыва.
    /// </summary>
    /// <returns>True - если в процессе отзыва, иначе - false.</returns>
    public virtual bool RevocationInProcess()
    {
      var revocation = PowerOfAttorneyRevocations.GetAll().Where(r => Equals(r.FormalizedPowerOfAttorney, _obj)).FirstOrDefault();
      if (revocation != null)
      {
        var revocationQueueItem = PowerOfAttorneyQueueItems.GetAll()
          .Where(q => q.DocumentId == revocation.Id && q.OperationType == Docflow.PowerOfAttorneyQueueItem.OperationType.Revocation)
          .FirstOrDefault();
        
        return revocationQueueItem != null;
      }
      
      return false;
    }
    
    /// <summary>
    /// Создать асинхронное событие установки "Действует по" во всех правах подписи,
    /// где в качестве документа-основания указана эл. доверенность.
    /// </summary>
    /// <param name="validTill">Дата, по которую действует эл. доверенность.</param>
    /// <remarks>Выполняется асинхронно.</remarks>
    [Public, Remote]
    public virtual void CreateSetSignatureSettingsValidTillAsyncHandler(DateTime validTill)
    {
      var signatureSettingIds = Docflow.SignatureSettings.GetAll().Where(x => Equals(x.Document, _obj)).Select(x => x.Id);
      foreach (var settingId in signatureSettingIds)
      {
        var asyncSetSignatureSettingValidTillHandler = Docflow.AsyncHandlers.SetSignatureSettingValidTill.Create();
        asyncSetSignatureSettingValidTillHandler.SignatureSettingId = settingId;
        asyncSetSignatureSettingValidTillHandler.ValidTill = validTill;
        asyncSetSignatureSettingValidTillHandler.ExecuteAsync();
      }
    }
    
    /// <summary>
    /// Зарегистрировать операцию валидации доверенности на сервисе.
    /// </summary>
    /// <param name="serviceConnection">Подключение к сервису доверенностей.</param>
    /// <param name="queueItem">Элемент очереди синхронизации эл. доверенностей.</param>
    /// <returns>True - если нужно продолжить дальнейшую обработку элемента очереди.</returns>
    [Public]
    public virtual bool EnqueueValidation(Sungero.PowerOfAttorneyCore.IPowerOfAttorneyServiceConnection serviceConnection, IPowerOfAttorneyQueueItem queueItem)
    {
      queueItem = PowerOfAttorneyQueueItems.GetAll().FirstOrDefault(x => Equals(x, queueItem));
      if (queueItem == null)
      {
        Logger.DebugFormat("EnqueueValidation: Queue item is null. Formalized power of attorney id {0}.", _obj.Id);
        return false;
      }
      
      if (!_obj.HasVersions)
      {
        Logger.DebugFormat("EnqueueValidation: Formalized power of attorney with id {0} has no version.", _obj.Id);
        return false;
      }
      
      var contentBytes = Docflow.PublicFunctions.Module.GetBinaryData(_obj.LastVersion.Body);
      
      if (contentBytes == null || contentBytes.Length == 0)
      {
        Logger.DebugFormat("EnqueueValidation: Document body is empty. Formalized power of attorney id {0}.", _obj.Id);
        return false;
      }

      var signature = Docflow.PublicFunctions.FormalizedPowerOfAttorney.GetRegisteredSignature(_obj);
      var signatureBytes = signature?.GetDataSignature();
      
      if (signatureBytes == null)
      {
        Logger.DebugFormat("EnqueueValidation: Signature is empty. Formalized power of attorney id {0}.", _obj.Id);
        return false;
      }
      
      var agent = this.CreateAgent();
      var sendingResult = PowerOfAttorneyCore.PublicFunctions.Module.EnqueuePoAValidation(serviceConnection, _obj.BusinessUnit, _obj.UnifiedRegistrationNumber, agent, contentBytes, signatureBytes);
      
      // Проверить, что queueItem ещё существует.
      queueItem = PowerOfAttorneyQueueItems.GetAll().FirstOrDefault(x => Equals(x, queueItem));
      if (queueItem == null)
      {
        Logger.DebugFormat("EnqueueValidation: Queue item is null. Formalized power of attorney id {0}.", _obj.Id);
        return false;
      }
      
      if (!string.IsNullOrEmpty(sendingResult.OperationId))
      {
        Logger.DebugFormat("EnqueueValidation: Operation id {0} successfully received. Formalized power of attorney id {1}.", sendingResult.OperationId, _obj.Id);
        queueItem.OperationId = sendingResult.OperationId;
        queueItem.Save();
      }
      
      return true;
    }
    
    /// <summary>
    /// Получить подпись, с которой была зарегистрирована эл. доверенность в реестре ФНС.
    /// </summary>
    /// <returns>Подпись, с которой была зарегистрирована эл. доверенность в реестре ФНС.</returns>
    /// <remarks>Если не заполнено свойство RegisteredSignatureId, то возвращается последняя подпись.</remarks>
    [Public]
    public virtual Sungero.Domain.Shared.ISignature GetRegisteredSignature()
    {
      if (_obj.RegisteredSignatureId.HasValue)
        return Signatures.Get(_obj).FirstOrDefault(x => x.Id == _obj.RegisteredSignatureId.Value);
      return Docflow.PublicFunctions.OfficialDocument.GetSignatureFromOurSignatory(_obj, _obj.LastVersion.Id);
    }
    
    /// <summary>
    /// Сформировать представителя в зависимости от типа.
    /// </summary>
    /// <returns>Представитель.</returns>
    public virtual PowerOfAttorneyCore.Structures.Module.IAgent CreateAgent()
    {
      var agent = PowerOfAttorneyCore.Structures.Module.Agent.Create();
      var representative = People.Null;
      
      if ((_obj.AgentType == Sungero.Docflow.FormalizedPowerOfAttorney.AgentType.Person ||
           _obj.AgentType == Sungero.Docflow.FormalizedPowerOfAttorney.AgentType.Employee) &&
          _obj.IssuedToParty != null)
      {
        representative = People.As(_obj.IssuedToParty);
        agent.Name = representative?.FirstName;
        agent.Middlename = representative?.MiddleName;
        agent.Surname = representative?.LastName;
        agent.TIN = representative?.TIN;
        agent.INILA = representative?.INILA;
      }
      else if (_obj.AgentType == Sungero.Docflow.FormalizedPowerOfAttorney.AgentType.Entrepreneur &&
               _obj.Representative != null && _obj.IssuedToParty != null)
      {
        representative = People.As(_obj.Representative);
        agent.Name = representative?.FirstName;
        agent.Middlename = representative?.MiddleName;
        agent.Surname = representative?.LastName;
        agent.TIN = representative?.TIN;
      }
      else if (_obj.AgentType == Sungero.Docflow.FormalizedPowerOfAttorney.AgentType.LegalEntity &&
               _obj.IssuedToParty != null)
      {
        if (_obj.Representative != null && _obj.FormatVersion == FormatVersion.Version002)
        {
          representative = People.As(_obj.Representative);
          agent.Name = representative?.FirstName;
          agent.Middlename = representative?.MiddleName;
          agent.Surname = representative?.LastName;
          agent.INILA = representative?.INILA;
        }
        var legalEntity = CompanyBases.As(_obj.IssuedToParty);
        agent.TINUl = legalEntity?.TIN;
        agent.TRRC = legalEntity?.TRRC;
      }
      else
      {
        Logger.ErrorFormat("CreateAgent. Power of attorney validation error: AgentType is incorrect.");
        agent = null;
      }
      
      return agent;
    }

    /// <summary>
    /// Обновить статус валидации доверенности на сервисе.
    /// </summary>
    /// <param name="serviceConnection">Подключение к сервису доверенностей.</param>
    /// <param name="queueItem">Элемент очереди синхронизации эл. доверенностей.</param>
    /// <returns>True - если нужно продолжить дальнейшую обработку элемента очереди.</returns>
    [Public]
    public virtual bool UpdateValidationServiceStatus(Sungero.PowerOfAttorneyCore.IPowerOfAttorneyServiceConnection serviceConnection, IPowerOfAttorneyQueueItem queueItem)
    {
      if (queueItem == null)
      {
        Logger.DebugFormat("UpdateValidationServiceStatus: Queue item is null (PoA id = {0}).", _obj.Id);
        return false;
      }
      
      var validationState = PowerOfAttorneyCore.PublicFunctions.Module.GetPoAValidationState(serviceConnection, queueItem.OperationId);
      
      if (validationState.OperationStatus == Constants.FormalizedPowerOfAttorney.FPoARegistrationStatus.Error)
      {
        var notFoundError = validationState.Errors.FirstOrDefault(x => string.Equals(x.Code,
                                                                                     Constants.FormalizedPowerOfAttorney.FPoAState.PoANotFoundError,
                                                                                     StringComparison.InvariantCultureIgnoreCase));
        if (notFoundError?.Code != null)
        {
          queueItem.RejectCode = notFoundError.Code;
          queueItem.Save();
          Logger.DebugFormat("UpdateValidationServiceStatus: Formalized power of attorney with id {0} not found in FTS register.", _obj.Id);
          return true;
        }
        
        Logger.DebugFormat("UpdateValidationServiceStatus: Formalized power of attorney with id {0} validation error.", _obj.Id);
        return false;
      }
      
      if (validationState.OperationStatus == Constants.FormalizedPowerOfAttorney.FPoARegistrationStatus.Done)
      {
        if (validationState.Result == Constants.FormalizedPowerOfAttorney.FPoAState.Valid)
        {
          Logger.DebugFormat("UpdateValidationServiceStatus: Formalized power of attorney with id {0} is valid, no processing required.", _obj.Id);
          queueItem.FormalizedPoAServiceStatus = Sungero.Docflow.PowerOfAttorneyQueueItem.FormalizedPoAServiceStatus.Valid;
          queueItem.Save();
          return true;
        }
        else if (validationState.Result == Constants.FormalizedPowerOfAttorney.FPoAState.Invalid)
        {
          var revokedError = validationState.Errors.FirstOrDefault(x => string.Equals(x.Code,
                                                                                      Constants.FormalizedPowerOfAttorney.FPoAState.Revoked,
                                                                                      StringComparison.InvariantCultureIgnoreCase));
          if (revokedError?.Code != null)
          {
            queueItem.RejectCode = revokedError.Code;
            queueItem.FormalizedPoAServiceStatus = Sungero.Docflow.PowerOfAttorneyQueueItem.FormalizedPoAServiceStatus.Invalid;
            queueItem.Save();
            Logger.DebugFormat("UpdateValidationServiceStatus: Formalized power of attorney with id {0} was revoked in FTS register, trying to update validation service status.", _obj.Id);
            return true;
          }
          
          Logger.DebugFormat("UpdateValidationServiceStatus: Formalized power of attorney with id {0} is invalid.", _obj.Id);
          queueItem.FormalizedPoAServiceStatus = Sungero.Docflow.PowerOfAttorneyQueueItem.FormalizedPoAServiceStatus.Invalid;
          queueItem.Save();
          return false;
        }
      }
      
      return true;
    }
    
    /// <summary>
    /// Установить эл. доверенность в отозванное состояние.
    /// </summary>
    /// <returns>True - если доверенность успешно перешла в отозванное состояние.</returns>
    [Public]
    public virtual bool SetRevokedState()
    {
      var serviceConnection = Sungero.PowerOfAttorneyCore.PublicFunctions.Module.GetPowerOfAttorneyServiceConnection(_obj.BusinessUnit);
      return this.SetRevokedState(serviceConnection);
    }
    
    /// <summary>
    /// Установить эл. доверенность в отозванное состояние.
    /// </summary>
    /// <param name="serviceConnection">Подключение к сервису доверенностей.</param>
    /// <returns>True - если доверенность успешно перешла в отозванное состояние.</returns>
    [Public]
    public virtual bool SetRevokedState(Sungero.PowerOfAttorneyCore.IPowerOfAttorneyServiceConnection serviceConnection)
    {
      var revocationInfo = PowerOfAttorneyCore.PublicFunctions.Module.Remote.GetPowerOfAttorneyRevocationInfo(serviceConnection, _obj.UnifiedRegistrationNumber);
      
      if (revocationInfo == null)
      {
        Logger.DebugFormat("SetRevokedState. Cannot obtain revocation data from service, trying to set power of attorney with id {0} to revoked state.", _obj.Id);
        return this.SetRevokedState(string.Empty, Calendar.UserToday);
      }
      
      Logger.DebugFormat("SetRevokedState. Revocation data for power of attorney with id {0}: reason {1}, date {2}.", _obj.Id, revocationInfo.Reason, revocationInfo.Date);
      return this.SetRevokedState(revocationInfo.Reason, revocationInfo.Date);
    }
    
    /// <summary>
    /// Установить эл. доверенность в отозванное состояние.
    /// </summary>
    /// <param name="reason">Причина отзыва.</param>
    /// <param name="revocationDate">Дата отзыва.</param>
    /// <returns>True - если доверенность успешно перешла в отозванное состояние.</returns>
    [Public]
    public virtual bool SetRevokedState(string reason, DateTime revocationDate)
    {
      Logger.DebugFormat("SetRevokedState. Business unit id = {0}. Unified registration number = {1}.", _obj.BusinessUnit?.Id,  _obj.UnifiedRegistrationNumber);
      
      try
      {
        if (_obj.LifeCycleState != Sungero.Docflow.FormalizedPowerOfAttorney.LifeCycleState.Obsolete)
          _obj.LifeCycleState = Sungero.Docflow.FormalizedPowerOfAttorney.LifeCycleState.Obsolete;
        if (_obj.FtsListState != Sungero.Docflow.FormalizedPowerOfAttorney.FtsListState.Revoked)
          _obj.FtsListState = Sungero.Docflow.FormalizedPowerOfAttorney.FtsListState.Revoked;
        // Нельзя установить дату меньше, чем "Действует с".
        if (_obj.ValidTill != revocationDate && _obj.ValidTill > revocationDate)
          _obj.ValidTill = _obj.ValidFrom > revocationDate ? _obj.ValidFrom : revocationDate;
        
        if (!string.IsNullOrEmpty(reason) && (string.IsNullOrEmpty(_obj.Note) || !_obj.Note.Contains(reason)))
        {
          var reasonPrefix = string.IsNullOrWhiteSpace(_obj.Note) ? string.Empty : Environment.NewLine;
          _obj.Note += Sungero.Docflow.FormalizedPowerOfAttorneys.Resources.FormalizedPowerOfAttorneyRevocationReasonFormat(reasonPrefix, reason);
        }
        if (_obj.State.IsChanged)
          _obj.Save();
      }
      catch (Exception ex)
      {
        Logger.ErrorFormat("SetRevokedState. Failed to set revoked state (PoA id = {0}).", ex, _obj.Id);
        return false;
      }
      
      return true;
    }
    
    /// <summary>
    /// Установить состояние эл. доверенности.
    /// </summary>
    /// <param name="lifeCycleState">Состояние жизненного цикла.</param>
    /// <param name="ftsListState">Состояние в реестре ФНС.</param>
    /// <returns>True - если доверенность успешно перешла в состояние.</returns>
    [Public]
    public virtual bool TrySetLifeCycleAndFtsListStates(Enumeration? lifeCycleState, Enumeration? ftsListState)
    {
      try
      {
        Functions.FormalizedPowerOfAttorney.SetLifeCycleAndFtsListStates(_obj, lifeCycleState, ftsListState);
        _obj.Save();
      }
      catch (Exception ex)
      {
        Logger.ErrorFormat("TrySetLifeCycleStateAndFtsListState. Failed to set LifeCycleState and FtsListState (PoA id = {0}).", ex, _obj.Id);
        return false;
      }
      
      return true;
    }
    
    #endregion

    #region Валидации доверенности
    
    /// <summary>
    /// Проверить эл. доверенность перед отправкой запроса к сервису доверенностей.
    /// </summary>
    /// <returns>Сообщение об ошибке или пустая строка, если ошибок нет.</returns>
    [Public, Remote]
    public virtual string ValidateFormalizedPoABeforeSending()
    {
      var validationError = this.ValidateBodyAndSignature();

      if (!string.IsNullOrEmpty(validationError))
        return validationError;

      if (!Sungero.PowerOfAttorneyCore.PublicFunctions.Module.HasPowerOfAttorneyServiceConnection(_obj.BusinessUnit))
        return Sungero.PowerOfAttorneyCore.Resources.ServiceConnectionNotConfigured;
      
      // Валидация xml по схеме.
      try
      {
        var body = Docflow.PublicFunctions.Module.GetBinaryData(_obj.LastVersion.Body);
        var xml = Docflow.Structures.Module.ByteArray.Create(body);
        this.ValidateFormalizedPowerOfAttorneyXml(xml);
      }
      catch
      {
        return FormalizedPowerOfAttorneys.Resources.XmlLoadFailed;
      }
      
      return string.Empty;
    }
    
    /// <summary>
    /// Проверить валидность xml-файла эл. доверенности.
    /// </summary>
    /// <param name="xml">Тело эл. доверенности.</param>
    [Public]
    public virtual void ValidateFormalizedPowerOfAttorneyXml(Docflow.Structures.Module.IByteArray xml)
    {
      var version = Sungero.FormalizeDocumentsParser.Extension.GetPoAVersion(xml.Bytes);
      if (version == PoAVersion.V001)
        return;
      if (!this.ValidateGeneratedFormalizedPowerOfAttorneyXml(xml))
      {
        Logger.Error("Import formalized power of attorney. Failed to load XML");
        throw AppliedCodeException.Create(FormalizedPowerOfAttorneys.Resources.XmlLoadFailed);
      }
    }
    
    /// <summary>
    /// Проверить, отключена ли валидация рег.номера.
    /// </summary>
    /// <returns>Для МЧД всегда отключена.</returns>
    public override bool IsNumberValidationDisabled()
    {
      return true;
    }
    
    #endregion
    
    #region История смены состояний
    
    public override System.Collections.Generic.IEnumerable<Sungero.Docflow.Structures.OfficialDocument.HistoryOperation> StatusChangeHistoryOperations(Sungero.Content.DocumentHistoryEventArgs e)
    {
      foreach (var operation in base.StatusChangeHistoryOperations(e))
      {
        yield return operation;
      }
      
      if (_obj.FtsListState != _obj.State.Properties.FtsListState.OriginalValue)
      {
        if (_obj.FtsListState != null)
          yield return Sungero.Docflow.Structures.OfficialDocument.HistoryOperation.Create(
            Constants.FormalizedPowerOfAttorney.Operation.FtsStateChange,
            Sungero.Docflow.FormalizedPowerOfAttorneys.Info.Properties.FtsListState.GetLocalizedValue(_obj.FtsListState));
        else
          yield return Sungero.Docflow.Structures.OfficialDocument.HistoryOperation.Create(
            Constants.FormalizedPowerOfAttorney.Operation.FtsStateClear, null);
      }
    }
    
    #endregion
    
    /// <summary>
    /// Создать простую задачу с уведомлением по отзыву электронной доверенности.
    /// </summary>
    [Public]
    public virtual void SendNoticeForRevokedFormalizedPoA()
    {
      // Получение параметров для задачи с уведомлением.
      var subject = FormalizedPowerOfAttorneys.Resources.TitleForNoticeFormat(_obj.Name);
      var performers = this.GetRevokedPoANotificationReceivers();
      
      // Проверка на корректность параметров.
      if (performers.Count == 0)
      {
        Logger.DebugFormat("SendNoticeForRevokedFormalizedPoA. No users to receive notification (PoA id = {0}).", _obj.Id);
        return;
      }
      
      try
      {
        var task = Workflow.SimpleTasks.CreateWithNotices(subject, performers, new[] { _obj });
        task.ActiveText = _obj.Note;
        task.Start();
        Logger.DebugFormat("SendNoticeForRevokedFormalizedPoA. Notice of revocation was sent successfully (task id = {0}, recipient id = {1}).", task.Id, string.Join<int>(", ", performers.Select(u => u.Id)));
      }
      catch (Exception ex)
      {
        Logger.ErrorFormat("SendNoticeForRevokedFormalizedPoA. Error sending notice of revocation: {0}.", ex);
      }
    }
    
    /// <summary>
    /// Получить список адресатов уведомления об отзыве  электронной доверенности.
    /// </summary>
    /// <returns> Список адресатов.</returns>
    public virtual List<IUser> GetRevokedPoANotificationReceivers()
    {
      var issuedTo = _obj.IssuedTo;
      var preparedBy = _obj.PreparedBy;
      var issuedToManager = Company.Employees.Null;
      if (issuedTo != null)
        issuedToManager = PublicFunctions.Module.Remote.GetManager(issuedTo);
      
      var performers = new List<IUser>();
      
      if (issuedTo != null && issuedTo.Status == Sungero.Company.Employee.Status.Active)
      {
        var needNotice = Docflow.PublicFunctions.PersonalSetting.GetPersonalSettings(issuedTo).MyRevokedFormalizedPoANotification;
        if (needNotice == true)
          performers.Add(issuedTo);
      }
      
      if (preparedBy != null && preparedBy.Status == Sungero.Company.Employee.Status.Active)
      {
        var needNotice = Docflow.PublicFunctions.PersonalSetting.GetPersonalSettings(preparedBy).MyRevokedFormalizedPoANotification;
        if (needNotice == true)
          performers.Add(preparedBy);
      }
      
      if (issuedToManager != null && issuedToManager.Status == Sungero.Company.Employee.Status.Active)
      {
        var needNotice = Docflow.PublicFunctions.PersonalSetting.GetPersonalSettings(issuedToManager).MySubordinatesRevokedFormalizedPoANotification;
        if (needNotice == true)
          performers.Add(issuedToManager);
      }

      return performers.Distinct().ToList();
    }
    
    /// <summary>
    /// Заполнить подписывающего в карточке документа.
    /// </summary>
    /// <param name="employee">Сотрудник.</param>
    [Remote]
    public override void SetDocumentSignatory(Company.IEmployee employee)
    {
      if (_obj.OurSignatory != null)
        return;
      base.SetDocumentSignatory(employee);
    }
    
    /// <summary>
    /// Заполнить основание в карточке документа.
    /// </summary>
    /// <param name="employee">Сотрудник.</param>
    /// <param name="e">Аргументы события подписания.</param>
    /// <param name="changedSignatory">Признак смены подписывающего.</param>
    public override void SetOurSigningReason(Company.IEmployee employee, Sungero.Domain.BeforeSigningEventArgs e, bool changedSignatory)
    {
      if (!Equals(_obj.OurSignatory, employee))
        return;
      base.SetOurSigningReason(employee, e, changedSignatory);
    }
    
    /// <summary>
    /// Заполнить Единый рег. № из эл. доверенности в подпись.
    /// </summary>
    /// <param name="employee">Сотрудник.</param>
    /// <param name="signature">Подпись.</param>
    /// <param name="certificate">Сертификат для подписания.</param>
    public override void SetUnifiedRegistrationNumber(Company.IEmployee employee, Sungero.Domain.Shared.ISignature signature, ICertificate certificate)
    {
      if (signature.SignCertificate == null)
        return;

      var changedSignatory = !Equals(_obj.OurSignatory, employee);
      var signingReason = this.GetSuitableOurSigningReason(employee, certificate, changedSignatory);
      this.SetUnifiedRegistrationNumber(signingReason, signature, certificate);
    }
    
    /// <summary>
    /// Проверить блокировку электронной доверенности.
    /// </summary>
    /// <returns>True - заблокирована, иначе - false.</returns>
    [Public]
    public virtual bool FormalizedPowerOfAttorneyIsLocked()
    {
      if (Locks.GetLockInfo(_obj).IsLocked)
      {
        Logger.DebugFormat("ProcessPowerOfAttorneyQueueItem: Formalized power of attorney with id {0} is locked.", _obj.Id);
        return true;
      }
      return false;
    }
    
    #region Генерация МЧД V2
    
    /// <summary>
    /// Создать тело эл. доверенности по формату 002.
    /// </summary>
    /// <param name="unifiedRegistrationNumber">Уникальный рег. номер доверенности.</param>
    /// <returns>Тело эл. доверенности.</returns>
    public virtual Docflow.Structures.Module.IByteArray CreateFormalizedPowerOfAttorneyXmlV2(Guid unifiedRegistrationNumber)
    {
      var fpoa = PoAV2Builder.CreateEmptyPoA();
      fpoa.ИдФайл = "ON_DOVBB_" + this.GetFileIdAttribute(unifiedRegistrationNumber);
      var dover = PoAV2Builder.CreatePoAElement();
      fpoa.Документ.Item = dover;
      
      this.FillFPoAInfoV2(PoAV2Builder.GetPoAInfo(fpoa), unifiedRegistrationNumber);
      
      this.AddLegalEntityPrincipalV2(fpoa);
      
      if (_obj.AgentType == Docflow.PowerOfAttorneyBase.AgentType.Employee)
      {
        this.AddIndividualAgentV2(fpoa, _obj.IssuedTo.Person);
      }
      if (_obj.AgentType == Docflow.PowerOfAttorneyBase.AgentType.Person)
      {
        this.AddIndividualAgentV2(fpoa, Sungero.Parties.People.As(_obj.IssuedToParty));
      }
      if (_obj.AgentType == Docflow.PowerOfAttorneyBase.AgentType.LegalEntity)
      {
        this.AddLegalEntityAgentV2(fpoa);
      }
      if (_obj.AgentType == Docflow.PowerOfAttorneyBase.AgentType.Entrepreneur)
      {
        this.AddEntrepreneurAgentV2(fpoa);
      }
      
      this.FillPowersV2(fpoa);
      
      var xml = Sungero.FormalizeDocumentsParser.Extension.GetPowerOfAttorneyXmlV2(fpoa);
      return Docflow.Structures.Module.ByteArray.Create(xml);
    }
    
    /// <summary>
    /// Заполнить основные сведения доверенности.
    /// </summary>
    /// <param name="info">Элемент со сведениями доверенности.</param>
    /// <param name="unifiedRegistrationNumber">Единый регистрационный номер доверенности.</param>
    public virtual void FillFPoAInfoV2(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV2.СвДовТип info, Guid unifiedRegistrationNumber)
    {
      info.НомДовер = unifiedRegistrationNumber.ToString();
      
      if (_obj.ValidFrom.HasValue)
        info.ДатаВыдДовер = _obj.ValidFrom.Value;
      
      if (_obj.ValidTill.HasValue)
        info.ДатаКонДовер = _obj.ValidTill.Value;
      
      info.ПрПередов = PoAV2Enums.RetrustToNative(Retrust.NoRetrust);
      info.ВнНомДовер = _obj.RegistrationNumber;
      
      if (_obj.RegistrationDate.HasValue)
      {
        info.ДатаВнРегДовер = _obj.RegistrationDate.Value;
        info.ДатаВнРегДоверSpecified = true;
      }
      
      info.Безотзыв.ПрБезотзыв = PoAV2Enums.RevocableToNative(true);
      info.СведСистОтм = $"{Constants.FormalizedPowerOfAttorney.XmlGeneralInfoAttributeValues.SourceSystemInfo}{unifiedRegistrationNumber}";
    }
    
    /// <summary>
    /// Добавить доверителя - юридическое лицо.
    /// </summary>
    /// <param name="fpoa">Эл. доверенность 002 формата.</param>
    public virtual void AddLegalEntityPrincipalV2(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV2.Доверенность fpoa)
    {
      var legalEntityPrincipal = PoAV2Builder.CreateLegalEntityPrincipal(fpoa);
      var principal = PoAV2Builder.GetLegalEntityPrincipal(legalEntityPrincipal).СвРосОрг;
      var head = PoAV2Builder.GetLegalEntityPrincipal(legalEntityPrincipal).ЛицоБезДов.СвФЛ;
      var signatory = legalEntityPrincipal.Подписант;
      
      // Заполнение элемента "Сведения о российском юридическом лице (СвРосОрг)".
      if (_obj.BusinessUnit != null)
      {
        principal.НаимОрг = _obj.BusinessUnit.LegalName;
        principal.ИННЮЛ = _obj.BusinessUnit.TIN;
        principal.ОГРН = _obj.BusinessUnit.PSRN;
        principal.КПП = _obj.BusinessUnit.TRRC;
        principal.АдрРФ = _obj.BusinessUnit.LegalAddress;
      }
      
      // Заполнение элемента "Сведения о лице, действующем от имени юридического лица без доверенности (ЛицоБезДов)".
      if (_obj.BusinessUnit.CEO != null && _obj.BusinessUnit.CEO.Person != null)
      {
        head.ИННФЛ = _obj.BusinessUnit.CEO.Person.TIN;
        head.СНИЛС = Parties.PublicFunctions.Person.GetFormattedInila(_obj.BusinessUnit.CEO.Person);
        
        if (_obj.BusinessUnit.CEO.Person.DateOfBirth.HasValue)
          head.СведФЛ.ДатаРожд = _obj.BusinessUnit.CEO.Person.DateOfBirth.Value;
        
        if (_obj.BusinessUnit.CEO.JobTitle != null)
          head.Должность = _obj.BusinessUnit.CEO.JobTitle.Name;
      }
      
      if (_obj.OurSigningReason != null)
        head.НаимДокПолн = _obj.OurSigningReason.Name;

      // TODO Сделать признак наличия гражданства для иностранцев.
      head.СведФЛ.ПрГражд = PoAV2Enums.CitizenshipFlagToNative(CitizenshipFlag.Russia);
      
      // Заполнение элемента "Сведения о физическом лице, подписывающем доверенность от имени доверителя (Подписант)".
      if (_obj.OurSignatory != null)
      {
        signatory.Имя = _obj.OurSignatory.Person.FirstName;
        signatory.Фамилия = _obj.OurSignatory.Person.LastName;
        signatory.Отчество = _obj.OurSignatory.Person.MiddleName;
      }
    }
    
    /// <summary>
    /// Добавить представителя - физ. лицо.
    /// </summary>
    /// <param name="fpoa">Эл. доверенность 002 формата.</param>
    /// <param name="person">Кому выдана.</param>
    public virtual void AddIndividualAgentV2(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV2.Доверенность fpoa,
                                             Sungero.Parties.IPerson person)
    {
      var representative = PoAV2Builder.CreateIndividualAgent(fpoa);
      var agent = PoAV2Builder.GetIndividualAgent(representative);
      agent.ИННФЛ = person.TIN;
      agent.СНИЛС = Parties.PublicFunctions.Person.GetFormattedInila(person);
      agent.ФИО.Имя = person.FirstName;
      agent.ФИО.Фамилия = person.LastName;
      agent.ФИО.Отчество = person.MiddleName;
      
      agent.СведФЛ.ДатаРожд = person.DateOfBirth.Value;
      
      var citizenshipFlag = this.GetCitizenshipFlag(person.Citizenship);
      agent.СведФЛ.ПрГражд = PoAV2Enums.CitizenshipFlagToNative(citizenshipFlag);
      agent.СведФЛ.Гражданство = citizenshipFlag == CitizenshipFlag.Other ? person.Citizenship.Code : null;
    }
    
    /// <summary>
    /// Добавить представителя - юр. лицо.
    /// </summary>
    /// <param name="fpoa">Эл. доверенность 002 формата.</param>
    public virtual void AddLegalEntityAgentV2(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV2.Доверенность fpoa)
    {
      var representative = PoAV2Builder.CreateLegalEntityAgent(fpoa);
      var legalEntity = PoAV2Builder.GetLegalEntityAgent(representative);
      var legalEntityAgent = legalEntity.СвОрг;

      if (_obj.IssuedToParty != null)
      {
        legalEntityAgent.НаимОрг = _obj.IssuedToParty.Name;
        legalEntityAgent.ОГРН = _obj.IssuedToParty.PSRN;
        legalEntityAgent.КПП = CompanyBases.As(_obj.IssuedToParty).TRRC;
        legalEntityAgent.ИННЮЛ = _obj.IssuedToParty.TIN;
        
        if (_obj.IssuedToParty.LegalAddress != null && _obj.IssuedToParty.Region?.Code != null)
        {
          var address = PoAV2Builder.GetLegalEntityAddress(legalEntityAgent);
          address.АдрРФ = _obj.IssuedToParty.LegalAddress;
          address.Регион = _obj.IssuedToParty.Region?.Code;
        }
      }
      
      if (_obj.Representative != null)
      {
        var agentRepresentative = PoAV2Builder.GetLegalEntityAgentRepresentative(legalEntity);
        agentRepresentative.ФИО.Имя = _obj.Representative.FirstName;
        agentRepresentative.ФИО.Отчество = _obj.Representative.MiddleName;
        agentRepresentative.ФИО.Фамилия = _obj.Representative.LastName;
        agentRepresentative.ИННФЛ = _obj.Representative.TIN;
        agentRepresentative.СНИЛС = Parties.PublicFunctions.Person.GetFormattedInila(_obj.Representative);
        
        agentRepresentative.СведФЛ.ДатаРожд = _obj.Representative.DateOfBirth.Value;
        agentRepresentative.СведФЛ.КонтактТлф = _obj.Representative.Phones;
        
        var citizenshipFlag = this.GetCitizenshipFlag(_obj.Representative.Citizenship);
        agentRepresentative.СведФЛ.ПрГражд = PoAV2Enums.CitizenshipFlagToNative(citizenshipFlag);
        agentRepresentative.СведФЛ.Гражданство = citizenshipFlag == CitizenshipFlag.Other ? _obj.Representative.Citizenship.Code : null;
      }
    }
    
    /// <summary>
    /// Добавить представителя - ИП.
    /// </summary>
    /// <param name="fpoa">Эл. доверенность 002 формата.</param>
    public virtual void AddEntrepreneurAgentV2(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV2.Доверенность fpoa)
    {
      var representative = PoAV2Builder.CreateEntrepreneurAgent(fpoa);
      var agent = PoAV2Builder.GetEntrepreneurAgent(representative);
      
      if (_obj.IssuedToParty != null)
      {
        agent.НаимИП = _obj.IssuedToParty.Name;
        agent.ОГРНИП = _obj.IssuedToParty.PSRN;
      }
      
      if (_obj.Representative != null)
      {
        agent.ФИО.Имя = _obj.Representative.FirstName;
        agent.ФИО.Отчество = _obj.Representative.MiddleName;
        agent.ФИО.Фамилия = _obj.Representative.LastName;
        agent.СНИЛС = Parties.PublicFunctions.Person.GetFormattedInila(_obj.Representative);
        agent.ИННФЛ = _obj.Representative.TIN;
        
        agent.СведФЛ.ДатаРожд = _obj.Representative.DateOfBirth.Value;
        agent.СведФЛ.КонтактТлф = _obj.Representative.Phones;
        
        var citizenshipFlag = this.GetCitizenshipFlag(_obj.Representative.Citizenship);
        agent.СведФЛ.ПрГражд = PoAV2Enums.CitizenshipFlagToNative(citizenshipFlag);
        agent.СведФЛ.Гражданство = citizenshipFlag == CitizenshipFlag.Other ? _obj.Representative.Citizenship.Code : null;
      }
    }
    
    /// <summary>
    /// Заполнить полномочия доверенности.
    /// </summary>
    /// <param name="fpoa">Эл. доверенность 002 формата.</param>
    public virtual void FillPowersV2(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV2.Доверенность fpoa)
    {
      PoAV2Builder.CreatePowers(fpoa, _obj.Powers != null ? new string[] { _obj.Powers } : new string[0]);
    }
    
    #endregion
    
    #region Заполнение карточки МЧД V2
    
    /// <summary>
    /// Заполнить поля доверенности из десериализованного объекта.
    /// </summary>
    /// <param name="xml">Тело доверенности.</param>
    [Public]
    public virtual void FillFPoAV2(Docflow.Structures.Module.IByteArray xml)
    {
      var fpoa = Sungero.FormalizeDocumentsParser.Extension.DeserializePoAV2(xml.Bytes);
      var dover = PoAV2Builder.GetDover(fpoa);
      
      this.FillDocumentNameV2(fpoa);
      
      _obj.FormatVersion = FormatVersion.Version002;
      _obj.UnifiedRegistrationNumber = GetUniformGuid(dover.СвДов.НомДовер);
      var internalRegistrationDate = dover.СвДов.ДатаВнРегДоверSpecified ?
        dover.СвДов.ДатаВнРегДовер :
        Calendar.Today;
      this.FillRegistrationData(dover.СвДов.ВнНомДовер, internalRegistrationDate);
      
      // Представитель.
      this.FillRepresentativeV2(fpoa);
      
      // TODO: Доверитель
      
      // Срок действия.
      _obj.ValidFrom = dover.СвДов.ДатаВыдДовер;
      _obj.ValidTill = dover.СвДов.ДатаКонДовер;
    }
    
    /// <summary>
    /// Заполнить поле Кому для сотрудника или физ. лица.
    /// </summary>
    /// <param name="fpoa">Десериализованный объект доверенности.</param>
    public virtual void FillRepresentativeV2(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV2.Доверенность fpoa)
    {
      var dover = PoAV2Builder.GetDover(fpoa);
      
      if (_obj.IssuedTo == null)
      {
        if (_obj.AgentType == Docflow.FormalizedPowerOfAttorney.AgentType.Employee &&
            dover.СвУпПред[0].ТипПред == PoAV2Enums.AgentTypeToNative(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAEnums.AgentType.Individual))
        {
          var individual = PoAV2Builder.GetIndividualAgent(dover.СвУпПред[0]);
          var fullName = GetFullName(individual.ФИО.Фамилия, individual.ФИО.Имя, individual.ФИО.Отчество);
          var info = Structures.FormalizedPowerOfAttorney.IssuedToInfo.Create(fullName, individual.ИННФЛ, individual.СНИЛС);
          this.FillIssuedTo(info);
        }
        // TODO: добавить методы заполнения для остальных типов доверенных лиц
      }
    }
    
    /// <summary>
    /// Заполнить имя эл. доверенности.
    /// </summary>
    /// <param name="fpoa">Десериализованный объект доверенности.</param>
    public virtual void FillDocumentNameV2(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV2.Доверенность fpoa)
    {
      this.SetDefaultDocumentName();
    }
    
    #endregion
    
    #region Генерация МЧД V3
    
    /// <summary>
    /// Создать тело эл. доверенности, формат 003.
    /// </summary>
    /// <param name="unifiedRegistrationNumber">Единый регистрационный номер доверенности.</param>
    /// <returns>Тело эл. доверенности.</returns>
    public virtual Docflow.Structures.Module.IByteArray CreateFormalizedPowerOfAttorneyXmlV3(Guid unifiedRegistrationNumber)
    {
      var poa = PoAV3Builder.CreateEmptyPoA();
      poa.ИдФайл = "ON_EMCHD_" + this.GetFileIdAttribute(unifiedRegistrationNumber);
      
      this.FillPowersV3(poa);
      this.FillPoaInfoV3(poa, unifiedRegistrationNumber);
      
      this.AddLegalEntityPrincipalV3(poa);
      
      if (_obj.AgentType == Docflow.PowerOfAttorneyBase.AgentType.Employee)
      {
        this.AddIndividualAgentV3(poa, _obj.IssuedTo.Person);
      }
      if (_obj.AgentType == Docflow.PowerOfAttorneyBase.AgentType.Person)
      {
        this.AddIndividualAgentV3(poa, Sungero.Parties.People.As(_obj.IssuedToParty));
      }
      if (_obj.AgentType == Docflow.PowerOfAttorneyBase.AgentType.LegalEntity)
      {
        this.AddLegalEntityAgentV3(poa);
      }
      if (_obj.AgentType == Docflow.PowerOfAttorneyBase.AgentType.Entrepreneur)
      {
        this.AddEntrepreneurAgentV3(poa);
      }
      
      var xml = Sungero.FormalizeDocumentsParser.Extension.GetPowerOfAttorneyXmlV3(poa);
      return Docflow.Structures.Module.ByteArray.Create(xml);
    }
    
    /// <summary>
    /// Заполнить полномочия доверенности.
    /// </summary>
    /// <param name="poa">Эл. доверенность 003 формата.</param>
    public virtual void FillPowersV3(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV3.Доверенность poa)
    {
      var dover = PoAV3Builder.Довер(poa);
      dover.СвПолн.ПрСовмПолн = PoAV3Enums.JointPowersToNative(JointPowers.Individual);
      dover.СвПолн.ТипПолн = PoAV3Enums.PowersTypeToNative(PowersType.HumanReadable);
      dover.СвПолн.ТекстПолн = _obj.Powers;
    }
    
    /// <summary>
    /// Заполнить основные сведения доверенности.
    /// </summary>
    /// <param name="poa">Эл. доверенность 003 формата.</param>
    /// <param name="unifiedRegistrationNumber">Единый регистрационный номер доверенности.</param>
    public virtual void FillPoaInfoV3(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV3.Доверенность poa, Guid unifiedRegistrationNumber)
    {
      var info = PoAV3Builder.Довер(poa).СвДов;
      info.НомДовер = unifiedRegistrationNumber.ToString();
      
      if (_obj.ValidFrom.HasValue)
        info.ДатаВыдДовер = _obj.ValidFrom.Value;
      
      if (_obj.ValidTill.HasValue)
        info.СрокДейст = _obj.ValidTill.Value;
      
      info.ПрПередов = PoAV3Enums.RetrustToNative(Retrust.NoRetrust);
      info.ВнНомДовер = _obj.RegistrationNumber;
      
      info.ВидДовер = PoAV3Enums.RevocableToNative(true);
      info.СведСист = $"{Constants.FormalizedPowerOfAttorney.XmlGeneralInfoAttributeValues.SourceSystemInfo}{unifiedRegistrationNumber}";
      
    }
    
    /// <summary>
    /// Добавить доверителя - юридическое лицо.
    /// </summary>
    /// <param name="poa">Эл. доверенность 003 формата.</param>
    public virtual void AddLegalEntityPrincipalV3(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV3.Доверенность poa)
    {
      var lep = PoAV3Builder.CreateLegalEntityPrincipal(poa);
      
      // Заполнение элемента "Сведения о российском юридическом лице (СвРосОрг)".
      if (_obj.BusinessUnit != null)
      {
        var org = lep.СвРосОрг;
        org.НаимОрг = _obj.BusinessUnit.LegalName;
        org.ИННЮЛ = _obj.BusinessUnit.TIN;
        org.ОГРН = _obj.BusinessUnit.PSRN;
        org.КПП = _obj.BusinessUnit.TRRC;
        org.АдрРег.Item = _obj.BusinessUnit.LegalAddress;
        org.АдрРег.Регион = _obj.BusinessUnit.Region?.Code;
      }
      
      var head = lep.ЛицоБезДов[0].СвФЛ;
      // Заполнение элемента "Сведения о лице, действующем от имени юридического лица без доверенности (ЛицоБезДов)".
      if (_obj.BusinessUnit.CEO != null && _obj.BusinessUnit.CEO.Person != null)
      {
        
        head.ИННФЛ = _obj.BusinessUnit.CEO.Person.TIN;
        head.СНИЛС = Parties.PublicFunctions.Person.GetFormattedInila(_obj.BusinessUnit.CEO.Person);
        
        if (_obj.BusinessUnit.CEO.Person.DateOfBirth.HasValue)
        {
          head.СведФЛ.ДатаРожд = _obj.BusinessUnit.CEO.Person.DateOfBirth.Value;
          head.СведФЛ.ДатаРождSpecified = true;
        }
        
        if (_obj.BusinessUnit.CEO.JobTitle != null)
          head.Должность = _obj.BusinessUnit.CEO.JobTitle.Name;
      }

      // TODO Сделать признак наличия гражданства для иностранцев.
      head.СведФЛ.ПрГражд = PoAV3Enums.CitizenshipFlagToNative(CitizenshipFlag.Russia);
      
      // Заполнение элемента "Сведения о физическом лице, подписывающем доверенность от имени доверителя (Подписант)".
      if (_obj.OurSignatory != null)
      {
        head.СведФЛ.ФИО.Имя = _obj.OurSignatory.Person.FirstName;
        head.СведФЛ.ФИО.Фамилия = _obj.OurSignatory.Person.LastName;
        head.СведФЛ.ФИО.Отчество = _obj.OurSignatory.Person.MiddleName;
      }
    }
    
    /// <summary>
    /// Добавить представителя - физ. лицо.
    /// </summary>
    /// <param name="poa">Эл. доверенность 003 формата.</param>
    /// <param name="person">Кому выдана.</param>
    public virtual void AddIndividualAgentV3(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV3.Доверенность poa, Sungero.Parties.IPerson person)
    {
      var agent = PoAV3Builder.CreateIndividualAgent(poa);
      agent.ИННФЛ = person.TIN;
      agent.СНИЛС = Parties.PublicFunctions.Person.GetFormattedInila(person);
      agent.СведФЛ.ФИО.Имя = person.FirstName;
      agent.СведФЛ.ФИО.Фамилия = person.LastName;
      agent.СведФЛ.ФИО.Отчество = person.MiddleName;
      
      agent.СведФЛ.ДатаРожд = person.DateOfBirth.Value;
      agent.СведФЛ.ДатаРождSpecified = true;
      var flag = this.GetCitizenshipFlag(person.Citizenship);
      agent.СведФЛ.ПрГражд = PoAV3Enums.CitizenshipFlagToNative(flag);
      agent.СведФЛ.Гражданство = flag == CitizenshipFlag.Other ? person.Citizenship.Code : null;
      
      this.AddIdentificationV3(agent.СведФЛ.УдЛичнФЛ, person);
    }
    
    /// <summary>
    /// Добавить представителя - ИП.
    /// </summary>
    /// <param name="poa">Эл. доверенность 003 формата.</param>
    public virtual void AddEntrepreneurAgentV3(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV3.Доверенность poa)
    {
      var agent = PoAV3Builder.CreateEntrepreneurAgent(poa);
      agent.ОГРНИП = _obj.IssuedToParty?.PSRN;
      
      if (_obj.Representative != null)
      {
        agent.СведФЛ.ФИО.Имя = _obj.Representative.FirstName;
        agent.СведФЛ.ФИО.Отчество = _obj.Representative.MiddleName;
        agent.СведФЛ.ФИО.Фамилия = _obj.Representative.LastName;
        agent.СНИЛС = Parties.PublicFunctions.Person.GetFormattedInila(_obj.Representative);
        agent.ИННФЛ = _obj.Representative.TIN;
        
        agent.СведФЛ.ДатаРожд = _obj.Representative.DateOfBirth.Value;
        agent.СведФЛ.ДатаРождSpecified = true;
        agent.СведФЛ.КонтактТлф = _obj.Representative.Phones;
        var flag = this.GetCitizenshipFlag(_obj.Representative.Citizenship);
        agent.СведФЛ.ПрГражд = PoAV3Enums.CitizenshipFlagToNative(flag);
        agent.СведФЛ.Гражданство = flag == CitizenshipFlag.Other ? _obj.Representative.Citizenship.Code : null;
      }
      
      this.AddIdentificationV3(agent.СведФЛ.УдЛичнФЛ, _obj.Representative);
    }
    
    /// <summary>
    /// Добавить документ, удостоверяющий личность, для физ. лица.
    /// </summary>
    /// <param name="identityDocument">Сведения о документе, удостоверяющем личность.</param>
    /// <param name="person">Физ. лицо.</param>
    public virtual void AddIdentificationV3(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV3.СведФЛТипУдЛичнФЛ identityDocument, Sungero.Parties.IPerson person)
    {
      identityDocument.КодВидДок = person.IdentityKind.Code;
      identityDocument.ДатаДок = person.IdentityDateOfIssue.Value;
      identityDocument.ВыдДок =
        person.IdentityAuthority.Length <= PublicConstants.FormalizedPowerOfAttorney.IdentityAuthorityMaxLength ?
        person.IdentityAuthority :
        person.IdentityAuthority.Substring(0, PublicConstants.FormalizedPowerOfAttorney.IdentityAuthorityMaxLength);
      identityDocument.КодВыдДок = person.IdentityAuthorityCode;
      identityDocument.СерНомДок = string.Join("-", new[] { person.IdentitySeries, person.IdentityNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
    
    /// <summary>
    /// Добавить представителя - юр. лицо.
    /// </summary>
    /// <param name="poa">Эл. доверенность 003 формата.</param>
    public virtual void AddLegalEntityAgentV3(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV3.Доверенность poa)
    {
      var agent = PoAV3Builder.CreateLegalEntityAgent(poa);
      
      if (_obj.IssuedToParty != null)
      {
        agent.НаимОрг = _obj.IssuedToParty.Name;
        agent.ОГРН = _obj.IssuedToParty.PSRN;
        agent.КПП = CompanyBases.As(_obj.IssuedToParty).TRRC;
        agent.ИННЮЛ = _obj.IssuedToParty.TIN;
        
        if (_obj.IssuedToParty.LegalAddress != null && _obj.IssuedToParty.Region?.Code != null)
        {
          agent.АдрРег.Item = _obj.IssuedToParty.LegalAddress;
          agent.АдрРег.Регион = _obj.IssuedToParty.Region?.Code;
        }
      }
    }
    #endregion
    
    #region Заполнение карточки МЧД V3
    
    /// <summary>
    /// Заполнить поля доверенности версии 003 из десериализованного объекта.
    /// </summary>
    /// <param name="xml">Тело доверенности.</param>
    [Public]
    public virtual void FillFPoAV3(Docflow.Structures.Module.IByteArray xml)
    {
      var poa = Sungero.FormalizeDocumentsParser.Extension.DeserializePoAV3(xml.Bytes);
      var dover = PoAV3Builder.Довер(poa);
      
      _obj.UnifiedRegistrationNumber = GetUniformGuid(dover.СвДов.НомДовер);
      _obj.ValidFrom = dover.СвДов.ДатаВыдДовер;
      _obj.ValidTill = dover.СвДов.СрокДейст;
      _obj.FormatVersion = FormatVersion.Version003;
      
      var registrationDate = dover.СвДов.ДатаВнРегДоверSpecified ? dover.СвДов.ДатаВнРегДовер : Calendar.Today;
      this.FillRegistrationData(dover.СвДов.ВнНомДовер, registrationDate);
      if (_obj.IssuedTo == null)
      {
        if (_obj.AgentType == Docflow.FormalizedPowerOfAttorney.AgentType.Employee)
          this.FillRepresentativeV3(poa);
        // TODO добавить методы заполнения для остальных типов доверенных лиц
      }
      this.FillDocumentNameV3(poa);
    }
    
    /// <summary>
    /// Заполнить поле Кому для сотрудника или физ. лица.
    /// </summary>
    /// <param name="poa">Десериализованный объект доверенности.</param>
    public virtual void FillRepresentativeV3(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV3.Доверенность poa)
    {
      var individual = PoAV3Builder.GetIndividualAgent(poa);
      if (individual == null)
        return;
      
      var fio = individual.СведФЛ.ФИО;
      var fullName = GetFullName(fio.Фамилия, fio.Имя, fio.Отчество);
      var info = Structures.FormalizedPowerOfAttorney.IssuedToInfo.Create(fullName, individual.ИННФЛ, individual.СНИЛС);
      this.FillIssuedTo(info);
    }
    
    /// <summary>
    /// Заполнить имя эл. доверенности.
    /// </summary>
    /// <param name="poa">Десериализованный объект доверенности.</param>
    public virtual void FillDocumentNameV3(Sungero.FormalizeDocumentsParser.PowerOfAttorney.Model.PoAV3.Доверенность poa)
    {
      this.SetDefaultDocumentName();
    }
    
    #endregion
    
  }
}
