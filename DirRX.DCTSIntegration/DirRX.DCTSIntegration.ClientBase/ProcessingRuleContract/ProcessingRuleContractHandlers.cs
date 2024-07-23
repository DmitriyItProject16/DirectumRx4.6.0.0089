using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleContract;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration
{
  partial class ProcessingRuleContractClientHandlers
  {

    public override void Refresh(Sungero.Presentation.FormRefreshEventArgs e)
    {
      base.Refresh(e);
      Functions.ProcessingRuleContract.SetRequiredProperties(_obj);
      Functions.ProcessingRuleContract.SetPropertiesAvailability(_obj);
      Functions.ProcessingRuleContract.SetPropertiesVisibility(_obj);
    }

  }
}