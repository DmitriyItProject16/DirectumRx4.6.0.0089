using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleIncomingLetter;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration
{
  partial class ProcessingRuleIncomingLetterSharedHandlers
  {

    public virtual void FillFromFileNameChanged(Sungero.Domain.Shared.BooleanPropertyChangedEventArgs e)
    {
      Functions.ProcessingRuleIncomingLetter.SetPropertiesAvailability(_obj);
      if (e.NewValue.HasValue && e.NewValue.Value)
        _obj.SubjectPattern = string.Empty;
    }

    public virtual void FillFromSubjectChanged(Sungero.Domain.Shared.BooleanPropertyChangedEventArgs e)
    {
      Functions.ProcessingRuleIncomingLetter.SetPropertiesAvailability(_obj);
      if (e.NewValue.HasValue && e.NewValue.Value)
        _obj.SubjectPattern = string.Empty;
    }

    public override void CaptureServiceChanged(Sungero.Domain.Shared.EnumerationPropertyChangedEventArgs e)
    {
      base.CaptureServiceChanged(e);
      
      Functions.ProcessingRuleIncomingLetter.SetPropertiesVisibility(_obj);
      // Очистить значения признаков при выборе модуля ввода не электронная почта.
      if (!e.NewValue.HasValue || e.NewValue.Value != DCTSIntegration.ProcessingRuleBase.CaptureService.Mail)
      {
        _obj.FillFromSubject = false;
        _obj.IsAutoCalcCorrespondent = false;
      }
      if (!e.NewValue.HasValue || e.NewValue.Value == DCTSIntegration.ProcessingRuleBase.CaptureService.Mail)
        _obj.FillFromFileName = false;
    }

  }
}