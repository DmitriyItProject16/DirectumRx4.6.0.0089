using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleBase;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration
{
  partial class ProcessingRuleBaseClientHandlers
  {

    public virtual void DeadlineInHoursValueInput(Sungero.Presentation.IntegerValueInputEventArgs e)
    {
      if (e.NewValue.HasValue && e.NewValue <= 0)
        e.AddError(ProcessingRuleBases.Resources.IncorrectHourDeadline);
    }

    public virtual void DeadlineInDaysValueInput(Sungero.Presentation.IntegerValueInputEventArgs e)
    {
      if (e.NewValue.HasValue && e.NewValue < 0)
        e.AddError(ProcessingRuleBases.Resources.IncorrectDayDeadline);
    }

    public override void Refresh(Sungero.Presentation.FormRefreshEventArgs e)
    {
      Functions.ProcessingRuleBase.SetPropertiesVisibility(_obj);
      Functions.ProcessingRuleBase.SetRequiredProperties(_obj);
      Functions.ProcessingRuleBase.SetPropertiesAvailability(_obj);
    }
  }

}