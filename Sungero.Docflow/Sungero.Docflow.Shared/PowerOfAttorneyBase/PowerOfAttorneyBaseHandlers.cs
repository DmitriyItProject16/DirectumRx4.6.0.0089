using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow.PowerOfAttorneyBase;

namespace Sungero.Docflow
{
  partial class PowerOfAttorneyBaseSharedHandlers
  {

    public virtual void IssuedToPartyChanged(Sungero.Docflow.Shared.PowerOfAttorneyBaseIssuedToPartyChangedEventArgs e)
    {
      this.FillName();
    }

    public virtual void AgentTypeChanged(Sungero.Domain.Shared.EnumerationPropertyChangedEventArgs e)
    {
      Functions.PowerOfAttorneyBase.SetAgentFieldsVisibleAndRequiredFlags(_obj);
      Functions.PowerOfAttorneyBase.ResetAgentFields(_obj);
      Functions.PowerOfAttorneyBase.FillPrincipalFields(_obj, _obj.PreparedBy, e.NewValue);
    }

    public override void PreparedByChanged(Sungero.Docflow.Shared.OfficialDocumentPreparedByChangedEventArgs e)
    {
      base.PreparedByChanged(e);
      
      Functions.PowerOfAttorneyBase.FillPrincipalFields(_obj, e.NewValue, _obj.AgentType);
    }

    public virtual void IssuedToChanged(Sungero.Docflow.Shared.PowerOfAttorneyBaseIssuedToChangedEventArgs e)
    {
      this.FillName();
      
      if (e.NewValue != null && _obj.Department == null)
        _obj.Department = e.NewValue.Department;
      if (e.NewValue != null && _obj.BusinessUnit == null)
        _obj.BusinessUnit = e.NewValue.Department.BusinessUnit;
    }

  }
}