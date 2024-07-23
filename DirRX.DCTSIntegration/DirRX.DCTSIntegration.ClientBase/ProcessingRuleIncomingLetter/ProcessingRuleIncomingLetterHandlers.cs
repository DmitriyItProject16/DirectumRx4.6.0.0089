using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleIncomingLetter;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration
{
  partial class ProcessingRuleIncomingLetterClientHandlers
  {

    public override void Showing(Sungero.Presentation.FormShowingEventArgs e)
    {
      base.Showing(e);
      
      if (_obj.Department != null && _obj.Department.BusinessUnit == null)
        e.AddWarning(DirRX.DCTSIntegration.ProcessingRuleIncomingLetters.Resources.BusinessUnitUnfilled);
    }

    public virtual void DepartmentValueInput(DirRX.DCTSIntegration.Client.ProcessingRuleIncomingLetterDepartmentValueInputEventArgs e)
    {
      if (e.NewValue != null && e.NewValue.BusinessUnit == null)
        e.AddWarning(DirRX.DCTSIntegration.ProcessingRuleIncomingLetters.Resources.BusinessUnitUnfilled);
    }

    public override void Refresh(Sungero.Presentation.FormRefreshEventArgs e)
    {
      base.Refresh(e);
      
      Functions.ProcessingRuleIncomingLetter.SetPropertiesVisibility(_obj);
      Functions.ProcessingRuleIncomingLetter.SetPropertiesAvailability(_obj);
    }

  }
}