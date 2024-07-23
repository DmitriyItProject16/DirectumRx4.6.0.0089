using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleContract;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration.Shared
{
  partial class ProcessingRuleContractFunctions
  {
    
    /// <summary>
    /// Установить видимость свойств.
    /// </summary>
    public override void SetPropertiesVisibility()
    {
      base.SetPropertiesVisibility();
      
      // Определить является ли модуль ввода электронной почтой.
      var isMail = _obj.CaptureService.HasValue && _obj.CaptureService.Value == DCTSIntegration.ProcessingRuleBase.CaptureService.Mail;
      _obj.State.Properties.FillFromSubject.IsVisible = isMail;
      _obj.State.Properties.FillFromFileName.IsVisible = !isMail;
      _obj.State.Properties.IsAutoCalcCounterparty.IsVisible = isMail;
    }
    
    /// <summary>
    /// Установить доступность свойств в зависимости от модуля ввода.
    /// </summary>
    public override void SetPropertiesAvailability()
    {
      base.SetPropertiesAvailability();
      
      // Определить выбрано ли автоматическое заполнение содержания.
      var isFillSubject = (_obj.FillFromSubject.HasValue && _obj.FillFromSubject.Value) || (_obj.FillFromFileName.HasValue && _obj.FillFromFileName.Value);
      _obj.State.Properties.SubjectPattern.IsEnabled = !isFillSubject;
    }

    /// <summary>
    /// Установить обязательность категории в зависимости от вида документа.
    /// </summary>
    public override void SetRequiredProperties()
    {
      base.SetRequiredProperties();
      
      // Свойство категория обязательна, если у вида документа есть категории.
      var hasAvailableCategories = Sungero.Docflow.DocumentGroupBases.GetAllCached(g => g.Status == Sungero.CoreEntities.DatabookEntry.Status.Active &&
                                                                                g.DocumentKinds.Any(d => Equals(d.DocumentKind, _obj.DocumentKind))).Any();
      _obj.State.Properties.Category.IsRequired = _obj.DocumentKind != null && hasAvailableCategories;
      _obj.State.Properties.Performers.IsRequired = _obj.State.Properties.Performers.IsRequired && !(_obj.IsSendResponsibleEmployee.HasValue && _obj.IsSendResponsibleEmployee.Value);
    }
  }
}