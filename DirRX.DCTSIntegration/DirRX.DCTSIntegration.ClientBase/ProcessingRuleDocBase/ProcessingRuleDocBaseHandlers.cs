using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleDocBase;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration
{
  partial class ProcessingRuleDocBaseClientHandlers
  {
    public override void Refresh(Sungero.Presentation.FormRefreshEventArgs e)
    {
      base.Refresh(e);      
      Functions.ProcessingRuleDocBase.SetPropertiesAvailability(_obj);
      Functions.ProcessingRuleDocBase.SetPropertiesVisibility(_obj);
    }
  }

}