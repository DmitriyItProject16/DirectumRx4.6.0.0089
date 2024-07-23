using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleIncomingLetter;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration.Shared
{
  partial class ProcessingRuleIncomingLetterFunctions
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
      _obj.State.Properties.IsAutoCalcCorrespondent.IsVisible = isMail;
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

  }
}