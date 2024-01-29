using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow.FormalizedPowerOfAttorney;

namespace Sungero.Docflow
{
  partial class FormalizedPowerOfAttorneySharedHandlers
  {

    public virtual void FormatVersionChanged(Sungero.Domain.Shared.EnumerationPropertyChangedEventArgs e)
    {
      // Для триггера рефреша при изменении значения.
    }
    
    public virtual void FtsListStateChanged(Sungero.Domain.Shared.EnumerationPropertyChangedEventArgs e)
    {
      if (e.NewValue != FtsListState.Rejected && !string.IsNullOrWhiteSpace(_obj.FtsRejectReason))
        _obj.FtsRejectReason = string.Empty;
    }

    public virtual void UnifiedRegistrationNumberChanged(Sungero.Domain.Shared.StringPropertyChangedEventArgs e)
    {
      this.FillName();
    }

  }
}