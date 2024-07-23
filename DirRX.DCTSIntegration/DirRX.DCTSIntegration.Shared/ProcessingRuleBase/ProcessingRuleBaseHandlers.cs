using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleBase;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration
{
  partial class ProcessingRuleBaseSharedHandlers
  {

    public virtual void TaskSubjectChanged(Sungero.Domain.Shared.StringPropertyChangedEventArgs e)
    {
      Functions.ProcessingRuleBase.SetPropertiesVisibility(_obj);
      Functions.ProcessingRuleBase.SetRequiredProperties(_obj);
    }

    public virtual void PerformersChanged(Sungero.Domain.Shared.CollectionPropertyChangedEventArgs e)
    {
      Functions.ProcessingRuleBase.SetPropertiesVisibility(_obj);
      Functions.ProcessingRuleBase.SetRequiredProperties(_obj);
    }

    public virtual void TaskTypeChanged(Sungero.Domain.Shared.EnumerationPropertyChangedEventArgs e)
    {
      if (!e.NewValue.Equals(e.OldValue))
      {
        _obj.TaskType = e.NewValue;
        Functions.ProcessingRuleBase.SetPropertiesVisibility(_obj);
        Functions.ProcessingRuleBase.SetRequiredProperties(_obj);
        
        if (e.NewValue.HasValue && e.NewValue.Value == DCTSIntegration.ProcessingRuleBase.TaskType.Notice)
        {
          _obj.DeadlineInDays = null;
          _obj.DeadlineInHours = null;
        }
      }
    }
  }

}