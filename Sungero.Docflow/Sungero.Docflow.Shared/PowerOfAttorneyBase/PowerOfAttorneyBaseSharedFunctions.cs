using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow.PowerOfAttorneyBase;

namespace Sungero.Docflow.Shared
{
  partial class PowerOfAttorneyBaseFunctions
  {
    /// <summary>
    /// Сбросить поля агента.
    /// </summary>
    public virtual void ResetAgentFields()
    {
      _obj.IssuedToParty = null;
      _obj.Representative = null;
      _obj.IssuedTo = null;
    }
    
    /// <summary>
    /// Настроить у полей агента признаки видимости и обязательности.
    /// </summary>
    public virtual void SetAgentFieldsVisibleAndRequiredFlags()
    {
      var properties = _obj.State.Properties;
      
      if (_obj.AgentType == AgentType.LegalEntity || _obj.AgentType == AgentType.Entrepreneur)
      {
        properties.IssuedToParty.IsVisible = true;
        properties.Representative.IsVisible = true;
        properties.IssuedTo.IsVisible = false;
        
        properties.IssuedToParty.IsRequired = true;
        properties.Representative.IsRequired = true;
        properties.IssuedTo.IsRequired = false;
      }
      else if (_obj.AgentType == AgentType.Person)
      {
        properties.IssuedToParty.IsVisible = true;
        properties.Representative.IsVisible = false;
        properties.IssuedTo.IsVisible = false;
        
        properties.IssuedToParty.IsRequired = true;
        properties.Representative.IsRequired = false;
        properties.IssuedTo.IsRequired = false;
      }
      else if (_obj.AgentType == AgentType.Employee)
      {
        properties.IssuedToParty.IsVisible = false;
        properties.Representative.IsVisible = false;
        properties.IssuedTo.IsVisible = true;
        
        properties.IssuedToParty.IsRequired = false;
        properties.Representative.IsRequired = false;
        properties.IssuedTo.IsRequired = true;
      }
      else
      {
        properties.IssuedToParty.IsVisible = false;
        properties.Representative.IsVisible = false;
        properties.IssuedTo.IsVisible = false;
      }
    }
    
    /// <summary>
    /// Заполнить поля доверителя, если поле "Подготовил" было заполнено.
    /// </summary>
    /// <param name="preparedBy">Подготовил.</param>
    /// <param name="agentType">Тип представителя.</param>
    public virtual void FillPrincipalFields(Company.IEmployee preparedBy, Sungero.Core.Enumeration? agentType)
    {
      if (preparedBy != null && agentType != Docflow.PowerOfAttorneyBase.AgentType.Employee && _obj.BusinessUnit == null && _obj.Department == null)
      {
        _obj.BusinessUnit = preparedBy?.Department?.BusinessUnit;
        _obj.Department = preparedBy?.Department;
      }
    }
    
    public override void ChangeDocumentPropertiesAccess(bool isEnabled, bool isRepeatRegister)
    {
      base.ChangeDocumentPropertiesAccess(isEnabled, isRepeatRegister);
      
      // Поле "Действует по" доступно для редактирования при изменении реквизитов и для доверенностей в разработке.
      var isDraft = _obj.LifeCycleState == Docflow.PowerOfAttorney.LifeCycleState.Draft;
      _obj.State.Properties.ValidTill.IsEnabled = isDraft || isEnabled;
      _obj.State.Properties.ValidFrom.IsEnabled = isDraft || isEnabled;

      // При перерегистрации "Кому выдана" недоступно, если в формате номера журнала есть код подразделения.
      var documentRegister = _obj.DocumentRegister;
      var departmentCodeIncludedInNumber = isRepeatRegister && documentRegister != null &&
        documentRegister.NumberFormatItems.Any(n => n.Element == DocumentRegisterNumberFormatItems.Element.DepartmentCode);
      _obj.State.Properties.IssuedTo.IsEnabled = isEnabled && !departmentCodeIncludedInNumber;
      
      _obj.State.Properties.AgentType.IsEnabled = isEnabled;
      _obj.State.Properties.IssuedToParty.IsEnabled = isEnabled;
      _obj.State.Properties.Representative.IsEnabled = isEnabled;
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
        <Вид документа> для <Кому выдана> №<номер> от <дата> "<содержание>".
       */
      using (TenantInfo.Culture.SwitchTo())
      {
        if (_obj.IssuedTo != null && _obj.AgentType == Docflow.PowerOfAttorneyBase.AgentType.Employee)
          name += PowerOfAttorneyBases.Resources.DocumentnameFor + _obj.IssuedTo.Name;
        else if (_obj.IssuedToParty != null)
          name += PowerOfAttorneyBases.Resources.DocumentnameFor + _obj.IssuedToParty.Name;
        
        if (!string.IsNullOrWhiteSpace(_obj.RegistrationNumber))
          name += OfficialDocuments.Resources.Number + _obj.RegistrationNumber;
        
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
    
    public override void ChangeRegistrationPaneVisibility(bool needShow, bool repeatRegister)
    {
      base.ChangeRegistrationPaneVisibility(needShow, repeatRegister);
      
      _obj.State.Properties.ExecutionState.IsVisible = false;
      _obj.State.Properties.ControlExecutionState.IsVisible = false;
      
    }
    
    /// <summary>
    /// Очистка скрытых полей.
    /// </summary>
    public virtual void CleanupAgentFields()
    {
      if (_obj.AgentType == AgentType.Employee)
      {
        _obj.IssuedToParty = _obj.IssuedTo?.Person;
        _obj.Representative = null;
      }
      else if (_obj.AgentType == AgentType.Person)
      {
        _obj.IssuedTo = null;
        _obj.Representative = null;
      }
      else if (_obj.AgentType == AgentType.LegalEntity || _obj.AgentType == AgentType.Entrepreneur)
      {
        _obj.IssuedTo = null;
      }
    }
  }
}