using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow.FormalizedPowerOfAttorney;
using Sungero.Parties;

namespace Sungero.Docflow.Shared
{
  partial class FormalizedPowerOfAttorneyFunctions
  {
    /// <summary>
    /// Изменить отображение панели регистрации.
    /// </summary>
    /// <param name="needShow">Признак отображения.</param>
    /// <param name="repeatRegister">Признак повторной регистрации\изменения реквизитов.</param>
    public override void ChangeRegistrationPaneVisibility(bool needShow, bool repeatRegister)
    {
      base.ChangeRegistrationPaneVisibility(needShow, repeatRegister);
      
      var properties = _obj.State.Properties;
      
      properties.UnifiedRegistrationNumber.IsVisible = needShow;
      properties.FtsListState.IsVisible = needShow;
    }
    
    /// <summary>
    /// Установить состояние жизненного цикла эл. доверенности в Действующее.
    /// </summary>
    [Public, Obsolete("Используйте метод SetLifeCycleActiveAfterImport")]
    public virtual void SetActiveLifeCycleState()
    {
      var issuedToDefined =
        _obj.AgentType == AgentType.Employee && _obj.IssuedTo != null ||
        _obj.AgentType == AgentType.LegalEntity && _obj.IssuedToParty != null && _obj.Representative != null ||
        _obj.AgentType == AgentType.Entrepreneur && _obj.IssuedToParty != null && _obj.Representative != null ||
        _obj.AgentType == AgentType.Person && _obj.IssuedToParty != null;
      
      if (_obj.LifeCycleState == Docflow.OfficialDocument.LifeCycleState.Draft &&
          issuedToDefined && _obj.BusinessUnit != null && _obj.Department != null &&
          _obj.ValidFrom != null && _obj.ValidTill != null && _obj.HasVersions)
      {
        _obj.LifeCycleState = Docflow.OfficialDocument.LifeCycleState.Active;
        _obj.FtsListState = Docflow.FormalizedPowerOfAttorney.FtsListState.Registered;
      }
    }
    
    /// <summary>
    /// Установить состояние ЖЦ эл. доверенности - Действующий, в реестре ФНС - Зарегистрирован.
    /// </summary>
    [Public, Obsolete("Используйте метод SetLifeCycleActiveAfterImport")]
    public virtual void SetLifeCycleAndFtsListStates()
    {
      var issuedToDefined =
        _obj.AgentType == AgentType.Employee && _obj.IssuedTo != null ||
        _obj.AgentType == AgentType.LegalEntity && _obj.IssuedToParty != null && _obj.Representative != null ||
        _obj.AgentType == AgentType.Entrepreneur && _obj.IssuedToParty != null && _obj.Representative != null ||
        _obj.AgentType == AgentType.Person && _obj.IssuedToParty != null;
      
      if (_obj.LifeCycleState == Docflow.OfficialDocument.LifeCycleState.Draft &&
          issuedToDefined && _obj.BusinessUnit != null && _obj.Department != null &&
          _obj.ValidFrom != null && _obj.ValidTill != null && !string.IsNullOrWhiteSpace(_obj.Powers) &&
          _obj.OurSignatory != null && _obj.OurSigningReason != null && _obj.HasVersions)
      {
        this.SetLifeCycleAndFtsListStates(LifeCycleState.Active, FtsListState.Registered);
      }
    }
    
    /// <summary>
    /// Установить состояние ЖЦ импортируемой эл. доверенности - Действующий.
    /// </summary>
    /// <remarks>Для установки активного состояния эл. доверенность должна соответствовать определенным критериям.</remarks>
    [Public]
    public virtual void SetLifeCycleActiveAfterImport()
    {
      var issuedToDefined =
        _obj.AgentType == AgentType.Employee && _obj.IssuedTo != null ||
        _obj.AgentType == AgentType.LegalEntity && _obj.IssuedToParty != null && _obj.Representative != null ||
        _obj.AgentType == AgentType.Entrepreneur && _obj.IssuedToParty != null && _obj.Representative != null ||
        _obj.AgentType == AgentType.Person && _obj.IssuedToParty != null;
      
      if (_obj.LifeCycleState == Docflow.OfficialDocument.LifeCycleState.Draft &&
          issuedToDefined && _obj.BusinessUnit != null && _obj.Department != null &&
          _obj.ValidFrom != null && _obj.ValidTill != null && !string.IsNullOrWhiteSpace(_obj.Powers) &&
          _obj.OurSignatory != null && _obj.OurSigningReason != null && _obj.HasVersions)
      {
        this.SetLifeCycleAndFtsListStates(LifeCycleState.Active, _obj.FtsListState);
      }
    }
    
    /// <summary>
    /// Установить состояние эл. доверенности.
    /// </summary>
    /// <param name="lifeCycleState">Состояние жизненного цикла.</param>
    /// <param name="ftsListState">Состояние в реестре ФНС.</param>
    [Public]
    public virtual void SetLifeCycleAndFtsListStates(Enumeration? lifeCycleState, Enumeration? ftsListState)
    {
      if (_obj.LifeCycleState != Docflow.FormalizedPowerOfAttorney.LifeCycleState.Obsolete &&
          _obj.LifeCycleState != lifeCycleState)
      {
        _obj.LifeCycleState = lifeCycleState;
      }
      
      if (_obj.FtsListState != ftsListState)
        _obj.FtsListState = ftsListState;
    }
    
    /// <summary>
    /// Проверять рег. номер на уникальность.
    /// </summary>
    /// <returns>True - проверять, False - не проверять.</returns>
    public override bool CheckRegistrationNumberUnique()
    {
      return false;
    }
    
    /// <summary>
    /// Обновить жизненный цикл документа.
    /// </summary>
    /// <param name="registrationState">Статус регистрации.</param>
    /// <param name="approvalState">Статус согласования.</param>
    /// <param name="counterpartyApprovalState">Статус согласования с контрагентом.</param>
    public override void UpdateLifeCycle(Enumeration? registrationState,
                                         Enumeration? approvalState,
                                         Enumeration? counterpartyApprovalState)
    {
      // Не обновлять жизненный цикл в зависимости от других статусов.
    }
    
    public override void FillName()
    {
      var documentKind = _obj.DocumentKind;
      
      if (documentKind != null && !documentKind.GenerateDocumentName.Value && _obj.Name == Docflow.Resources.DocumentNameAutotext)
        _obj.Name = string.Empty;
      
      if (documentKind == null || !documentKind.GenerateDocumentName.Value)
        return;
      
      var name = string.Empty;
      
      /* Имя в формате:
        <Вид документа> для <Кому выдана> №<Единый рег. номер> (рег. №<номер>) от <дата> "<содержание>".
       */
      using (TenantInfo.Culture.SwitchTo())
      {
        if (_obj.IssuedTo != null && _obj.AgentType == Docflow.PowerOfAttorneyBase.AgentType.Employee)
          name += PowerOfAttorneyBases.Resources.DocumentnameFor + _obj.IssuedTo.Name;
        else if (_obj.IssuedToParty != null)
          name += PowerOfAttorneyBases.Resources.DocumentnameFor + _obj.IssuedToParty.Name;
        
        if (!string.IsNullOrWhiteSpace(_obj.UnifiedRegistrationNumber))
          name += FormalizedPowerOfAttorneys.Resources.UnifiedRegistrationNumberFormat(_obj.UnifiedRegistrationNumber);
        
        if (!string.IsNullOrWhiteSpace(_obj.RegistrationNumber))
          name += FormalizedPowerOfAttorneys.Resources.RegistrationNumberInBracketsFormat(_obj.RegistrationNumber);
        
        if (_obj.RegistrationDate != null)
          name += OfficialDocuments.Resources.DateFrom + _obj.RegistrationDate.Value.ToString("d");
        
        if (!string.IsNullOrWhiteSpace(_obj.Subject))
          name += " \"" + _obj.Subject + "\"";
      }
      
      if (string.IsNullOrWhiteSpace(name))
        name = Docflow.Resources.DocumentNameAutotext;
      else if (documentKind != null)
        name = documentKind.ShortName + name;
      
      name = Functions.Module.TrimSpecialSymbols(name);
      
      _obj.Name = Functions.OfficialDocument.AddClosingQuote(name, _obj);
      
    }
    
    #region Доступность свойств
    
    public override void ChangeDocumentPropertiesAccess(bool isEnabled, bool isRepeatRegister)
    {
      base.ChangeDocumentPropertiesAccess(isEnabled, isRepeatRegister);
      
      if (_obj.State.IsInserted)
        return;
      
      if (!_obj.HasVersions)
      {
        _obj.State.Properties.AgentType.IsEnabled = true;
        _obj.State.Properties.IssuedTo.IsEnabled = true;
        _obj.State.Properties.IssuedToParty.IsEnabled = true;
        _obj.State.Properties.Representative.IsEnabled = true;
        _obj.State.Properties.Powers.IsEnabled = true;
        _obj.State.Properties.BusinessUnit.IsEnabled = true;
        _obj.State.Properties.Department.IsEnabled = true;
        _obj.State.Properties.OurSignatory.IsEnabled = true;
        _obj.State.Properties.PreparedBy.IsEnabled = true;
        _obj.State.Properties.OurSigningReason.IsEnabled = _obj.OurSignatory != null;
        return;
      }
      
      // Наличие параметра IsLastVersionApprovedParamName говорит о том, что подпись была импортирована.
      var fpoaParams = ((Domain.Shared.IExtendedEntity)_obj).Params;
      var isSignatureImported = fpoaParams.ContainsKey(Constants.FormalizedPowerOfAttorney.IsLastVersionApprovedParamName);
      object isExternalSignature;
      fpoaParams.TryGetValue(Constants.FormalizedPowerOfAttorney.IsLastVersionApprovedParamName, out isExternalSignature);
      
      var needEnableProperties = true;
      var isRegistered = _obj.DocumentRegister != null;
      var isProcessedInFts = _obj.FtsListState == Docflow.FormalizedPowerOfAttorney.FtsListState.OnRegistration ||
        _obj.FtsListState == Docflow.FormalizedPowerOfAttorney.FtsListState.Registered ||
        _obj.FtsListState == Docflow.FormalizedPowerOfAttorney.FtsListState.OnRevoke ||
        _obj.FtsListState == Docflow.FormalizedPowerOfAttorney.FtsListState.Revoked;
      var isRegistrationError = _obj.FtsListState == Docflow.FormalizedPowerOfAttorney.FtsListState.Rejected;
      
      if (isExternalSignature != null)
        bool.TryParse(isExternalSignature.ToString(), out isSignatureImported);
      
      if (isSignatureImported)
        needEnableProperties = (isRegistrationError || _obj.FtsListState == null) && (isRepeatRegister || !isRegistered);
      else
        needEnableProperties = isEnabled && (isRegistrationError || _obj.LastVersionApproved == false && _obj.FtsListState == null) && (isRepeatRegister || !isRegistered);
      
      _obj.State.Properties.AgentType.IsEnabled = needEnableProperties;
      _obj.State.Properties.IssuedTo.IsEnabled = needEnableProperties;
      _obj.State.Properties.IssuedToParty.IsEnabled = needEnableProperties;
      _obj.State.Properties.Representative.IsEnabled = needEnableProperties;
      _obj.State.Properties.Powers.IsEnabled = needEnableProperties;
      
      _obj.State.Properties.BusinessUnit.IsEnabled = needEnableProperties;
      _obj.State.Properties.Department.IsEnabled = needEnableProperties;
      _obj.State.Properties.OurSignatory.IsEnabled = needEnableProperties;
      _obj.State.Properties.PreparedBy.IsEnabled = needEnableProperties;
      _obj.State.Properties.OurSigningReason.IsEnabled = _obj.OurSignatory != null && needEnableProperties;
      
      _obj.State.Properties.ValidFrom.IsEnabled = needEnableProperties;
      _obj.State.Properties.ValidTill.IsEnabled = needEnableProperties;
      
      _obj.State.Properties.RegistrationNumber.IsEnabled = !isProcessedInFts && isRepeatRegister;
      _obj.State.Properties.RegistrationDate.IsEnabled = !isProcessedInFts && isRepeatRegister;
    }
    
    /// <summary>
    /// Установить обязательность свойств в зависимости от заполненных данных.
    /// </summary>
    public override void SetRequiredProperties()
    {
      base.SetRequiredProperties();
      
      _obj.State.Properties.Subject.IsRequired = _obj.Info.Properties.Subject.IsRequired;
      
      // Изменить обязательность полей в зависимости от того, программная или визуальная работа.
      var fpoaParams = ((Domain.Shared.IExtendedEntity)_obj).Params;
      var isVisualMode = fpoaParams.ContainsKey(Docflow.PublicConstants.OfficialDocument.IsVisualModeParamName);
      
      // Изменить обязательность полей в зависимости от внешней подписи.
      object hasExternalSignature;
      fpoaParams.TryGetValue(Docflow.PublicConstants.FormalizedPowerOfAttorney.IsLastVersionApprovedParamName, out hasExternalSignature);
      var needSetRequired = this.NeedSetRequiredProperties(hasExternalSignature != null ? (bool)hasExternalSignature : false);
      
      _obj.State.Properties.BusinessUnit.IsRequired = isVisualMode;
      _obj.State.Properties.Department.IsRequired = isVisualMode;
      _obj.State.Properties.ValidFrom.IsRequired = isVisualMode;
      _obj.State.Properties.Powers.IsRequired = isVisualMode && needSetRequired;
      _obj.State.Properties.OurSignatory.IsRequired = isVisualMode && needSetRequired;
      _obj.State.Properties.OurSigningReason.IsRequired = isVisualMode && needSetRequired;
      
      if (!isVisualMode)
      {
        _obj.State.Properties.IssuedToParty.IsRequired = false;
        _obj.State.Properties.Representative.IsRequired = false;
        _obj.State.Properties.IssuedTo.IsRequired = false;
        _obj.State.Properties.Powers.IsRequired = false;
        _obj.State.Properties.OurSignatory.IsRequired = false;
        _obj.State.Properties.OurSigningReason.IsRequired = false;
      }
    }
    
    #endregion
    
    #region Доступность действий
    
    /// <summary>
    /// Проверить возможность импорта доверенности.
    /// </summary>
    /// <returns>True - да, иначе - false.</returns>
    [Public]
    public virtual bool CanImportVersionWithSignature()
    {
      if (_obj.FtsListState != null)
        return false;
      
      if (_obj.HasVersions || _obj.LastVersionApproved == true)
        return false;
      
      return FormalizedPowerOfAttorneys.AccessRights.CanCreate() &&
        FormalizedPowerOfAttorneys.AccessRights.CanApprove() &&
        (Functions.Module.IsLockedByMe(_obj) || _obj.State.IsInserted);
    }
    
    /// <summary>
    /// Проверить возможность сформировать доверенность.
    /// </summary>
    /// <returns>True - да, иначе - false.</returns>
    [Public]
    public virtual bool CanGenerateBodyWithPdf()
    {
      if (_obj.State.IsInserted && _obj.FormatVersion != FormatVersion.Version002)
        return false; // для 003 должен присвоиться внутренний номер, прежде чем можно генерировать XML
      
      if (_obj.FtsListState != null &&
          _obj.FtsListState != Docflow.FormalizedPowerOfAttorney.FtsListState.Rejected)
        return false;
      
      var documentParams = ((Sungero.Domain.Shared.IExtendedEntity)_obj).Params;
      var hasExternalSignature = documentParams.ContainsKey(Constants.FormalizedPowerOfAttorney.IsLastVersionApprovedParamName) &&
        (bool)documentParams[Constants.FormalizedPowerOfAttorney.IsLastVersionApprovedParamName];
      
      if (hasExternalSignature)
        return false;
      
      if (_obj.LastVersionApproved == true &&
          _obj.FtsListState != null &&
          _obj.FtsListState != Docflow.FormalizedPowerOfAttorney.FtsListState.Rejected)
        return false;
      
      return _obj.AccessRights.CanUpdate() &&
        (Functions.Module.IsLockedByMe(_obj) || _obj.State.IsInserted);
    }
    
    /// <summary>
    /// Проверить возможность зарегистрировать доверенность.
    /// </summary>
    /// <returns>True - да, иначе - false.</returns>
    [Public]
    public virtual bool CanRegisterWithService()
    {
      if (_obj.FtsListState != null &&
          _obj.FtsListState != Docflow.FormalizedPowerOfAttorney.FtsListState.Rejected)
        return false;
      
      return _obj.HasVersions &&
        _obj.AccessRights.CanUpdate() &&
        Functions.Module.IsLockedByMe(_obj);
    }
    
    /// <summary>
    /// Проверить возможность актуализировать состояние доверенности.
    /// </summary>
    /// <returns>True - да, иначе - false.</returns>
    [Public]
    public virtual bool CanCheckStateWithService()
    {
      return _obj.AccessRights.CanUpdate() && _obj.HasVersions &&
        Functions.Module.IsLockedByMe(_obj);
    }
    
    /// <summary>
    /// Проверить возможность отозвать доверенность.
    /// </summary>
    /// <returns>True - да, иначе - false.</returns>
    [Public]
    public virtual bool CanCreateRevocation()
    {
      if (_obj.FtsListState != Docflow.FormalizedPowerOfAttorney.FtsListState.Registered &&
          _obj.FtsListState != Docflow.FormalizedPowerOfAttorney.FtsListState.OnRevoke)
        return false;
      
      if (_obj.FtsListState == Docflow.FormalizedPowerOfAttorney.FtsListState.Registered &&
          _obj.ValidTill.HasValue && _obj.ValidTill < Calendar.UserToday)
        return false;
      
      var documentParams = ((Sungero.Domain.Shared.IExtendedEntity)_obj).Params;
      var hasExternalSignature = documentParams.ContainsKey(Constants.FormalizedPowerOfAttorney.IsLastVersionApprovedParamName) &&
        (bool)documentParams[Constants.FormalizedPowerOfAttorney.IsLastVersionApprovedParamName];
      return _obj.AccessRights.CanRead() &&
        (_obj.LastVersionApproved == true || hasExternalSignature);
    }
    
    #endregion
    
    #region Поиск дублей
    
    /// <summary>
    /// Получить текст ошибки о наличии дублей.
    /// </summary>
    /// <returns>Текст ошибки или пустая строка, если ошибок нет.</returns>
    public virtual string GetDuplicatesErrorText()
    {
      var duplicates = this.GetDuplicates();
      
      if (!duplicates.Any())
        return string.Empty;
      
      // Сформировать текст ошибки.
      return FormalizedPowerOfAttorneys.Resources.DuplicatesDetected;
    }
    
    /// <summary>
    /// Получить дубли эл. доверенности.
    /// </summary>
    /// <returns>Дубли эл. доверенности.</returns>
    public virtual List<IFormalizedPowerOfAttorney> GetDuplicates()
    {
      return Functions.FormalizedPowerOfAttorney.Remote.GetFormalizedPowerOfAttorneyDuplicates(_obj);
    }
    
    #endregion
    
    public override void SetLifeCycleState()
    {
      // Эл. доверенность становится действующей только если она зарегистрирована в ФНС,
      // независимо от того, является ли она автонумеруемой.
      return;
    }
    
    /// <summary>
    /// Проверить корректность значений для свойств, влияющих на формирование xml доверенности.
    /// </summary>
    /// <returns>True, если свойства заполнены корректно, иначе - false.</returns>
    public virtual bool CheckRequiredPropertiesValues()
    {
      if (_obj.OurSignatory == null || _obj.OurSignatory?.Person?.DateOfBirth == null)
      {
        Logger.ErrorFormat("Execute CheckRequiredPropertiesValues: formalized power of attorney id {0}, date of birth of our signatory not specified.", _obj.Id);
        return false;
      }
      if (_obj.Representative != null && !_obj.Representative.DateOfBirth.HasValue)
      {
        Logger.ErrorFormat("Execute CheckRequiredPropertiesValues: formalized power of attorney id {0}, date of birth of representative not specified.", _obj.Id);
        return false;
      }
      if (_obj.IssuedTo != null && !_obj.IssuedTo.Person.DateOfBirth.HasValue)
      {
        Logger.ErrorFormat("Execute CheckRequiredPropertiesValues: formalized power of attorney id {0}, date of birth of issued to not specified.", _obj.Id);
        return false;
      }
      if (_obj.IssuedTo != null && _obj.IssuedTo.Person.Citizenship == null)
      {
        Logger.ErrorFormat("Execute CheckRequiredPropertiesValues: formalized power of attorney id {0}, citizenship of issued to not specified.", _obj.Id);
        return false;
      }
      if (_obj.FormatVersion == FormatVersion.Version003 &&
          (_obj.AgentType == AgentType.Employee && _obj.IssuedTo.Person?.IdentityKind == null ||
           _obj.AgentType == AgentType.Person && People.Is(_obj.IssuedToParty) && People.As(_obj.IssuedToParty).IdentityKind == null))
      {
        Logger.ErrorFormat("Execute CheckRequiredPropertiesValues: formalized power of attorney id {0}, identity document of issued to not specified.", _obj.Id);
        return false;
      }
      
      var allowedIdentityKinds = Functions.Module.Remote.GetDocflowParamsStringValue(Constants.Module.FPoAIdentityDocumentCodesParamName)?.Split(',') ??
        Constants.Module.FPoAIdentityDocumentCodes.Split(',');
      if (_obj.FormatVersion == FormatVersion.Version003 &&
          (_obj.AgentType == AgentType.Employee && !allowedIdentityKinds.Contains(_obj.IssuedTo.Person?.IdentityKind?.Code) ||
           _obj.AgentType == AgentType.Person && People.Is(_obj.IssuedToParty) && !allowedIdentityKinds.Contains(People.As(_obj.IssuedToParty).IdentityKind?.Code) ||
           _obj.AgentType == AgentType.Entrepreneur && !allowedIdentityKinds.Contains(_obj.Representative.IdentityKind?.Code)))
      {
        Logger.ErrorFormat("Execute CheckRequiredPropertiesValues: formalized power of attorney id {0}, identity document of issued to or representative is not supported.", _obj.Id);
        return false;
      }
      
      if (_obj.FormatVersion == FormatVersion.Version003 &&
          _obj.AgentType == AgentType.Entrepreneur && _obj.Representative.IdentityKind == null)
      {
        Logger.ErrorFormat("Execute CheckRequiredPropertiesValues: formalized power of attorney id {0}, identity document of representative not specified.", _obj.Id);
        return false;
      }
      
      if (string.IsNullOrWhiteSpace(_obj.BusinessUnit.PSRN))
      {
        Logger.ErrorFormat("Execute CheckRequiredPropertiesValues: formalized power of attorney id {0}, business unit PSRN is not specified.", _obj.Id);
        return false;
      }
      
      return true;
    }
    
    /// <summary>
    /// Определить необходимость установить обязательные поля.
    /// </summary>
    /// <param name="hasExternalSignature">Есть ли внешняя подпись.</param>
    /// <returns>Нужно ли устанавливать обязательные поля.</returns>
    public virtual bool NeedSetRequiredProperties(bool hasExternalSignature)
    {
      return hasExternalSignature && (_obj.LifeCycleState == LifeCycleState.Draft || _obj.FtsListState == FtsListState.Rejected) ||
        !hasExternalSignature && (_obj.FtsListState == null || _obj.FtsListState == FtsListState.OnRegistration || _obj.FtsListState == FtsListState.Rejected);
    }
    
    /// <summary>
    /// Установить признак "Эл. доверенность только что была импортирована".
    /// </summary>
    [Public]
    public virtual void SetJustImportedParam()
    {
      var fpoaParams = ((Sungero.Domain.Shared.IExtendedEntity)_obj).Params;
      if (fpoaParams.ContainsKey(Constants.FormalizedPowerOfAttorney.FPoAWasJustImportedParamName))
        fpoaParams[Constants.FormalizedPowerOfAttorney.FPoAWasJustImportedParamName] = true;
      else
        fpoaParams.Add(Constants.FormalizedPowerOfAttorney.FPoAWasJustImportedParamName, true);
    }
  }
}