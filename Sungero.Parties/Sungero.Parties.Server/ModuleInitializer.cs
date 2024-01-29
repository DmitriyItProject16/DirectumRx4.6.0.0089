using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Domain.Initialization;
using IdentityDocKindPropsPattern = Sungero.Parties.Constants.Module.IdentityDocumentKindPropsPattern;

namespace Sungero.Parties.Server
{
  public partial class ModuleInitializer
  {
    public override void Initializing(Sungero.Domain.ModuleInitializingEventArgs e)
    {
      CreateDefaultDueDiligenceWebsites();
      CreateDistributionListCounterparty();
      UpdateBanksFromCBR();
      CreateCounterpartyIndices();
      CreateIdentityDocumentKinds();
    }
    
    /// <summary>
    /// Создать предопределенные сайты проверки контрагента.
    /// </summary>
    public static void CreateDefaultDueDiligenceWebsites()
    {
      InitializationLogger.Debug("Init: Create Default Due Diligence Websites");
      
      CreateDueDiligenceWebsite(Parties.Constants.DueDiligenceWebsite.Initialize.OwnerOnlineWebsite,
                                Parties.DueDiligenceWebsites.Resources.OwnerOnlineWebsiteName,
                                Constants.DueDiligenceWebsite.Websites.OwnerOnline.HomePage,
                                Constants.DueDiligenceWebsite.Websites.OwnerOnline.DueDiligencePage, false,
                                Parties.DueDiligenceWebsites.Resources.OwnerOnlineNote,
                                Constants.DueDiligenceWebsite.Websites.OwnerOnline.DueDiligencePage);
      CreateDueDiligenceWebsite(Parties.Constants.DueDiligenceWebsite.Initialize.ForFairBusinessWebsite,
                                Parties.DueDiligenceWebsites.Resources.ForFairBusinessWebsiteName,
                                Constants.DueDiligenceWebsite.Websites.ForFairBusiness.HomePage,
                                Constants.DueDiligenceWebsite.Websites.ForFairBusiness.DueDiligencePage, true,
                                Parties.DueDiligenceWebsites.Resources.ForFairBusinessNote,
                                Constants.DueDiligenceWebsite.Websites.ForFairBusiness.DueDiligencePageSE);
      CreateDueDiligenceWebsite(Parties.Constants.DueDiligenceWebsite.Initialize.HonestBusinessWebsite,
                                Parties.DueDiligenceWebsites.Resources.HonestBusinessWebsiteName,
                                Constants.DueDiligenceWebsite.Websites.HonestBusiness.HomePage,
                                Constants.DueDiligenceWebsite.Websites.HonestBusiness.DueDiligencePage, false,
                                Parties.DueDiligenceWebsites.Resources.HonestBusinessNote);
      CreateDueDiligenceWebsite(Parties.Constants.DueDiligenceWebsite.Initialize.KonturFocusWebsite,
                                Parties.DueDiligenceWebsites.Resources.KonturFocusWebsiteName,
                                Constants.DueDiligenceWebsite.Websites.KonturFocus.HomePage,
                                Constants.DueDiligenceWebsite.Websites.KonturFocus.DueDiligencePage, false,
                                Parties.DueDiligenceWebsites.Resources.KonturFocusNote);
      CreateDueDiligenceWebsite(Parties.Constants.DueDiligenceWebsite.Initialize.SbisWebsite,
                                Parties.DueDiligenceWebsites.Resources.SbisWebsiteName,
                                Constants.DueDiligenceWebsite.Websites.Sbis.HomePage,
                                Constants.DueDiligenceWebsite.Websites.Sbis.DueDiligencePage, false,
                                Parties.DueDiligenceWebsites.Resources.SbisNote);
    }
    
    /// <summary>
    /// Создать системного контрагента для рассылки нескольким адресатам.
    /// </summary>
    public static void CreateDistributionListCounterparty()
    {
      var needLink = false;
      var guid = Parties.Constants.Counterparty.DistributionListCounterpartyGuid;
      var name = Parties.Resources.DistributionListCounterpartyName;
      var company = Parties.PublicFunctions.Counterparty.Remote.GetDistributionListCounterparty();
      if (company == null)
      {
        company = Companies.Create();
        needLink = true;
      }
      
      company.Name = name;
      company.State.IsEnabled = false;
      company.Save();
      
      if (needLink)
        Docflow.PublicFunctions.Module.CreateExternalLink(company, guid);
    }
    
    /// <summary>
    /// Обновить банки.
    /// </summary>
    public static void UpdateBanksFromCBR()
    {
      if (Commons.PublicFunctions.Module.IsServerCultureRussian())
      {
        InitializationLogger.DebugFormat("Init: Update banks from CBR.");
        Docflow.PublicFunctions.Module.ExecuteSQLCommand(Queries.Module.PrepareBanksUpdate);
        Docflow.PublicFunctions.Module.ExecuteSQLCommand(Queries.Module.UpdateBanksFromCBR);
        var count = int.Parse(Docflow.PublicFunctions.Module.ExecuteScalarSQLCommand(Queries.Module.GetNewCountBanks).ToString());
        if (count > 0)
        {
          // BUG 75302, Zamerov: генерируем ID для новых банков кодом, иначе создание контрагентов через Create будет ставить дедлок.
          var tableName = Banks.Info.DBTableName;
          var ids = Domain.IdentifierGenerator.GenerateIdentifiers(tableName, count).ToList();
          using (var command = SQL.GetCurrentConnection().CreateCommand())
          {
            command.CommandText = Queries.Module.CreateBanksFromCBR;
            Docflow.PublicFunctions.Module.AddIntegerParameterToCommand(command, "@newId", ids.First());
            command.ExecuteNonQuery();
          }
        }
        Docflow.PublicFunctions.Module.ExecuteSQLCommand(Queries.Module.CleanTempTablesAfterUpdateBanks);
      }
    }
    
    /// <summary>
    /// Создать сайт проверки контрагента.
    /// </summary>
    /// <param name="entityId">GUID сайта.</param>
    /// <param name="name">Имя сайта.</param>
    /// <param name="homeUrl">Домашняя страница сайта.</param>
    /// <param name="url">Шаблон адреса сайта.</param>
    /// <param name="isDefault">Признак, что сайт используется при открытии сайта из карточки КА.</param>
    /// <param name="note">Примечание.</param>
    /// <param name="selfEmployedUrl">Шаблон адреса сайта (ИП) (по умолчанию - null).</param>
    public static void CreateDueDiligenceWebsite(Guid entityId, string name, string homeUrl, string url, bool isDefault, string note, string selfEmployedUrl = null)
    {
      var externalLink = Docflow.PublicFunctions.Module.GetExternalLink(DueDiligenceWebsite.ClassTypeGuid, entityId);
      var dueDiligenceWebsite = DueDiligenceWebsites.Null;
      if (externalLink != null)
      {
        InitializationLogger.DebugFormat("Init: Refresh Due Diligence Website {0}", name);
        dueDiligenceWebsite = DueDiligenceWebsites.Get(externalLink.EntityId.Value);
      }
      else
      {
        InitializationLogger.DebugFormat("Init: Create Due Diligence Website {0}", name);
        dueDiligenceWebsite = DueDiligenceWebsites.Create();
        dueDiligenceWebsite.IsDefault = isDefault;
      }
      
      dueDiligenceWebsite.IsSystem = true;
      dueDiligenceWebsite.Name = name;
      dueDiligenceWebsite.HomeUrl = homeUrl;
      dueDiligenceWebsite.Url = url;
      if (selfEmployedUrl != null)
        dueDiligenceWebsite.UrlForSelfEmployed = selfEmployedUrl;
      dueDiligenceWebsite.Note = note;
      
      dueDiligenceWebsite.Save();
      
      if (externalLink == null)
        Docflow.PublicFunctions.Module.CreateExternalLink(dueDiligenceWebsite, entityId);
    }
    
    public static void CreateCounterpartyIndices()
    {
      Sungero.Docflow.PublicFunctions.Module.ExecuteSQLCommand(Queries.Module.SungeroCounterpartyIndicesNameQuery);
      
      var tableName = Sungero.Parties.Constants.Module.SugeroCounterpartyTableName;
      var indexName = "idx_Counterparty_Discriminator_Status";
      var indexQuery = string.Format(Queries.Module.SungeroCounterpartyIndexQuery, tableName, indexName);
      Sungero.Docflow.PublicFunctions.Module.CreateIndexOnTable(tableName, indexName, indexQuery);
    }
    
    public static void CreateIdentityDocumentKinds()
    {
      if (Commons.PublicFunctions.Module.IsServerCultureRussian())
      {
        // 03 – Свидетельство о рождении.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.BirthCertificateKindName, Sungero.Parties.Resources.BirthCertificateKindShortName,
                                   Sungero.Parties.Resources.BirthCertificateKindCode, Constants.Module.IdentityDocumentKindsGuid.BirthCertificate,
                                   true, false, true, true,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
        // 07 – Военный билет.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.MilitaryIDKindName, Sungero.Parties.Resources.MilitaryIDKindShortName,
                                   Sungero.Parties.Resources.MilitaryIDKindCode, Constants.Module.IdentityDocumentKindsGuid.MilitaryID,
                                   true, false, true, true,
                                   IdentityDocKindPropsPattern.SixToSevenDigits,
                                   IdentityDocKindPropsPattern.TwoAlphaChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
        // 09 - Дипломатический паспорт.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.DiplomaticPassportKindName, Sungero.Parties.Resources.DiplomaticPassportKindShortName,
                                   Sungero.Parties.Resources.DiplomaticPassportKindCode, Constants.Module.IdentityDocumentKindsGuid.DiplomaticPassport,
                                   true, true, true, true,
                                   IdentityDocKindPropsPattern.SevenDigits,
                                   IdentityDocKindPropsPattern.TwoDigits,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
        // 10 – Паспорт иностранного гражданина.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.ForeignCitizenPassportKindName, Sungero.Parties.Resources.ForeignCitizenPassportKindShortName,
                                   Sungero.Parties.Resources.ForeignCitizenPassportKindCode, Constants.Module.IdentityDocumentKindsGuid.ForeignCitizenPassport,
                                   true, false, false, false,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   null);
        // 11 – Свидетельство о рассмотрении ходатайства о признании лица беженцем на территории Российской Федерации по существу.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.CertificateOfConsiderationAsRefugeeKindName, Sungero.Parties.Resources.CertificateOfConsiderationAsRefugeeKindShortName,
                                   Sungero.Parties.Resources.CertificateOfConsiderationAsRefugeeKindCode, Constants.Module.IdentityDocumentKindsGuid.CertificateOfConsiderationAsRefugee,
                                   true, true, true, false,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
        // 12 - Вид на жительство в Российской Федерации.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.ResidentCardKindName, Sungero.Parties.Resources.ResidentCardKindShortName,
                                   Sungero.Parties.Resources.ResidentCardKindCode, Constants.Module.IdentityDocumentKindsGuid.ResidentCard,
                                   true, true, true, true,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
        // 13 – Удостоверение беженца.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.RefugeeCertificateKindName, Sungero.Parties.Resources.RefugeeCertificateKindShortName,
                                   Sungero.Parties.Resources.RefugeeCertificateKindCode, Constants.Module.IdentityDocumentKindsGuid.RefugeeCertificate,
                                   true, true, true, true,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
        // 14 - Временное удостоверение личности гражданина Российской Федерации.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.TemporaryIDKindName, Sungero.Parties.Resources.TemporaryIDKindShortName,
                                   Sungero.Parties.Resources.TemporaryIDKindCode, Constants.Module.IdentityDocumentKindsGuid.TemporaryID,
                                   false, true, true, true,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   null,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
        // 15 – Разрешение на временное проживание в Российской Федерации.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.TemporaryResidencePermitKindName, Sungero.Parties.Resources.TemporaryResidencePermitKindShortName,
                                   Sungero.Parties.Resources.TemporaryResidencePermitKindCode, Constants.Module.IdentityDocumentKindsGuid.TemporaryResidencePermit,
                                   true, true, true, true,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
        // 19 – Свидетельство о предоставлении временного убежища на территории Российской Федерации.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.CertificateOfProvisionTemporaryAsylumKindName, Sungero.Parties.Resources.CertificateOfProvisionTemporaryAsylumKindShortName,
                                   Sungero.Parties.Resources.CertificateOfProvisionTemporaryAsylumKindCode, Constants.Module.IdentityDocumentKindsGuid.CertificateOfProvisionTemporaryAsylum,
                                   true, true, true, true,
                                   IdentityDocKindPropsPattern.SevenDigits,
                                   IdentityDocKindPropsPattern.TwoAlphaChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
        // 21 – Паспорт гражданина Российской Федерации.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.CitizenPassportKindName, Sungero.Parties.Resources.CitizenPassportKindShortName,
                                   Sungero.Parties.Resources.CitizenPassportKindShortCode, Constants.Module.IdentityDocumentKindsGuid.CitizenPassport,
                                   true, false, true, true,
                                   IdentityDocKindPropsPattern.SixDigits,
                                   IdentityDocKindPropsPattern.TwoDigitsOptionalSpaceTwoDigits,
                                   IdentityDocKindPropsPattern.ThreeDigitsDashThreeDigits);
        // 22 - Загранпаспорт гражданина Российской Федерации.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.InternationalPassportKindName, Sungero.Parties.Resources.InternationalPassportKindShortName,
                                   Sungero.Parties.Resources.InternationalPassportKindCode, Constants.Module.IdentityDocumentKindsGuid.InternationalPassport,
                                   true, true, true, false,
                                   IdentityDocKindPropsPattern.SevenDigits,
                                   IdentityDocKindPropsPattern.TwoDigits,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
        // 24 – Удостоверение личности военнослужащего Российской Федерации.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.IdentityCardOfMilitaryPersonnelKindName, Sungero.Parties.Resources.IdentityCardOfMilitaryPersonnelKindShortName,
                                   Sungero.Parties.Resources.IdentityCardOfMilitaryPersonnelKindCode, Constants.Module.IdentityDocumentKindsGuid.IdentityCardOfMilitaryPersonnel,
                                   true, false, true, false,
                                   IdentityDocKindPropsPattern.SevenDigits,
                                   IdentityDocKindPropsPattern.TwoAlphaChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
        // 26 - Паспорт моряка.
        CreateIdentityDocumentKind(Sungero.Parties.Resources.SailorPassportKindName, Sungero.Parties.Resources.SailorPassportKindShortName,
                                   Sungero.Parties.Resources.SailorPassportKindCode, Constants.Module.IdentityDocumentKindsGuid.SailorPassport,
                                   true, true, true, true,
                                   IdentityDocKindPropsPattern.SixToSevenDigits,
                                   IdentityDocKindPropsPattern.TwoAlphaChars,
                                   IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
      }
      
      // 91 - Иные документы.
      CreateIdentityDocumentKind(Sungero.Parties.Resources.OtherDocumentsKindName, Sungero.Parties.Resources.OtherDocumentsKindShortName,
                                 Sungero.Parties.Resources.OtherDocumentsKindCode, Constants.Module.IdentityDocumentKindsGuid.OtherDocuments,
                                 false, false, true, false,
                                 IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars,
                                 null,
                                 IdentityDocKindPropsPattern.OneToTwentyFiveAlphanumericChars);
    }
    
    /// <summary>
    /// Создать вид документа, удостоверяющего личность.
    /// </summary>
    /// <param name="name">Имя.</param>
    /// <param name="shortName">Сокращенное имя.</param>
    /// <param name="code">Код вида документа.</param>
    /// <param name="sid">SID.</param>
    /// <param name="specifySeries">Указывать серию документа.</param>
    /// <param name="specifyExpDate">Указывать срок действия.</param>
    /// <param name="specifyAuthCode">Указывать код подразделения.</param>
    /// <param name="specifyBirthPlace">Указывать место рождения.</param>
    /// <param name="numberPattern">Формат номера документа.</param>
    /// <param name="seriesPattern">Формат серии документа.</param>
    /// <param name="authorityCodePattern">Формат кода подразделения.</param>
    [Public]
    public static void CreateIdentityDocumentKind(string name, string shortName, string code, string sid,
                                                  bool specifySeries, bool specifyExpDate, bool specifyAuthCode, bool specifyBirthPlace,
                                                  string numberPattern, string seriesPattern, string authorityCodePattern)
    {
      InitializationLogger.DebugFormat("Init: Create identity document kind {0}", name);
      
      var documentKind = IdentityDocumentKinds.GetAll(x => Equals(x.SID, sid)).FirstOrDefault();
      if (documentKind == null)
        documentKind = IdentityDocumentKinds.Create();
      
      documentKind.Name = name;
      documentKind.ShortName = shortName;
      documentKind.Code = code;
      documentKind.SID = sid;
      documentKind.SpecifyIdentitySeries = specifySeries;
      documentKind.SpecifyIdentityExpirationDate = specifyExpDate;
      documentKind.SpecifyIdentityAuthorityCode = specifyAuthCode;
      documentKind.SpecifyBirthPlace = specifyBirthPlace;
      documentKind.IdentityNumberPattern = numberPattern;
      documentKind.IdentitySeriesPattern = seriesPattern;
      documentKind.IdentityAuthorityCodePattern = authorityCodePattern;
      
      documentKind.Save();
    }
  }
}
