using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleDocBase;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration
{
  partial class ProcessingRuleDocBaseSharedHandlers
  {

    public virtual void FillNameFromFileNameChanged(Sungero.Domain.Shared.BooleanPropertyChangedEventArgs e)
    {
      Functions.ProcessingRuleDocBase.SetPropertiesAvailability(_obj);
    }

    public virtual void FillNameFromSubjectChanged(Sungero.Domain.Shared.BooleanPropertyChangedEventArgs e)
    {
      Functions.ProcessingRuleDocBase.SetPropertiesAvailability(_obj);
    }

    public override void CaptureServiceChanged(Sungero.Domain.Shared.EnumerationPropertyChangedEventArgs e)
    {
      base.CaptureServiceChanged(e);
      
      Functions.ProcessingRuleDocBase.SetPropertiesVisibility(_obj);
      
      // Очистить значения признаков при выборе модуля ввода не электронная почта.
      if (!e.NewValue.HasValue || e.NewValue.Value != DCTSIntegration.ProcessingRuleBase.CaptureService.Mail)
        _obj.FillNameFromSubject = false;
      if (!e.NewValue.HasValue || e.NewValue.Value == DCTSIntegration.ProcessingRuleBase.CaptureService.Mail)
        _obj.FillNameFromFileName = false;
    }

    public virtual void DocumentKindChanged(DirRX.DCTSIntegration.Shared.ProcessingRuleDocBaseDocumentKindChangedEventArgs e)
    {
      Functions.ProcessingRuleDocBase.SetPropertiesAvailability(_obj);
      
      var isGenerateName = e.NewValue != null && e.NewValue.GenerateDocumentName.HasValue && e.NewValue.GenerateDocumentName.Value;
      if (isGenerateName)
      {
        _obj.NamePattern = null;
        _obj.FillNameFromSubject = false;
        _obj.FillNameFromFileName = false;
      }
    }
  }

}