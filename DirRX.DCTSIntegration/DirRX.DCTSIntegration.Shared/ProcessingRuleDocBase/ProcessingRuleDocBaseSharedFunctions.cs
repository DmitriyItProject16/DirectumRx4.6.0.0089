using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleDocBase;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration.Shared
{
  partial class ProcessingRuleDocBaseFunctions
  {

    /// <summary>
    /// Установить видимость свойств.
    /// </summary>
    public override void SetPropertiesVisibility()
    {
      base.SetPropertiesVisibility();
      
      // Определить является ли модуль ввода электронной почтой.
      var isMail = _obj.CaptureService.HasValue && _obj.CaptureService.Value == DCTSIntegration.ProcessingRuleBase.CaptureService.Mail;
      _obj.State.Properties.FillNameFromSubject.IsVisible = isMail;
      _obj.State.Properties.FillNameFromFileName.IsVisible = !isMail;
    }
    
    /// <summary>
    /// Установить доступность свойств.
    /// </summary>
    public override void SetPropertiesAvailability()
    {
      base.SetPropertiesAvailability();
      
      var isGenerateName = _obj.DocumentKind != null && _obj.DocumentKind.GenerateDocumentName.HasValue && _obj.DocumentKind.GenerateDocumentName.Value;
      _obj.State.Properties.NamePattern.IsEnabled = !isGenerateName;
      _obj.State.Properties.FillNameFromSubject.IsEnabled = !isGenerateName;
      _obj.State.Properties.FillNameFromFileName.IsEnabled = !isGenerateName;
      
      // Определить выбрано ли автоматическое заполнение наименования.
      var isFillName = (_obj.FillNameFromSubject.HasValue && _obj.FillNameFromSubject.Value) || (_obj.FillNameFromFileName.HasValue && _obj.FillNameFromFileName.Value);
      _obj.State.Properties.NamePattern.IsEnabled = !isFillName && !isGenerateName;
    }
  }
}