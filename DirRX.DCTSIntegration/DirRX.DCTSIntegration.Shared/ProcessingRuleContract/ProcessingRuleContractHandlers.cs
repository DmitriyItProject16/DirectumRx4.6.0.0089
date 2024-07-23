using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleContract;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration
{
  partial class ProcessingRuleContractSharedHandlers
  {

    public virtual void FillFromFileNameChanged(Sungero.Domain.Shared.BooleanPropertyChangedEventArgs e)
    {
      Functions.ProcessingRuleContract.SetPropertiesAvailability(_obj);
      if (e.NewValue.HasValue && e.NewValue.Value)
        _obj.SubjectPattern = string.Empty;
    }

    public virtual void FillFromSubjectChanged(Sungero.Domain.Shared.BooleanPropertyChangedEventArgs e)
    {
      Functions.ProcessingRuleContract.SetPropertiesAvailability(_obj);
      if (e.NewValue.HasValue && e.NewValue.Value)
        _obj.SubjectPattern = string.Empty;
    }

    public virtual void IsSendResponsibleEmployeeChanged(Sungero.Domain.Shared.BooleanPropertyChangedEventArgs e)
    {
      Functions.ProcessingRuleContract.SetRequiredProperties(_obj);
    }

    public override void DocumentKindChanged(DirRX.DCTSIntegration.Shared.ProcessingRuleDocBaseDocumentKindChangedEventArgs e)
    {
      if (e.NewValue == e.OldValue)
        return;
      
      base.DocumentKindChanged(e);
      
      Functions.ProcessingRuleContract.SetRequiredProperties(_obj);
      _obj.Category = null;
      
    }

    public override void CaptureServiceChanged(Sungero.Domain.Shared.EnumerationPropertyChangedEventArgs e)
    {
      base.CaptureServiceChanged(e);

      Functions.ProcessingRuleContract.SetPropertiesVisibility(_obj);
      // Очистить значения признаков при выборе модуля ввода не электронная почта.
      if (!e.NewValue.HasValue || e.NewValue.Value != DCTSIntegration.ProcessingRuleBase.CaptureService.Mail)
      {
        _obj.FillFromSubject = false;
        _obj.IsAutoCalcCounterparty = false;
      }
      if (!e.NewValue.HasValue || e.NewValue.Value == DCTSIntegration.ProcessingRuleBase.CaptureService.Mail)
        _obj.FillFromFileName = false;
    }

  }
}