using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow.FormalizedPowerOfAttorney;
using HistoryOperation = Sungero.Docflow.Structures.OfficialDocument.HistoryOperation;

namespace Sungero.Docflow
{
  partial class FormalizedPowerOfAttorneyFilteringServerHandler<T>
  {
    
  }

  partial class FormalizedPowerOfAttorneyServerHandlers
  {

    public override void BeforeSave(Sungero.Domain.BeforeSaveEventArgs e)
    {
      base.BeforeSave(e);
      
      if (e.Params.Contains(Constants.FormalizedPowerOfAttorney.FPoAWasJustImportedParamName))
      {
        var duplicatesError = Functions.FormalizedPowerOfAttorney.GetDuplicatesErrorText(_obj);
        if (!string.IsNullOrEmpty(duplicatesError))
          e.AddError(duplicatesError, _obj.Info.Actions.ShowDuplicates);
        Functions.FormalizedPowerOfAttorney.SetLifeCycleActiveAfterImport(_obj);
      }
    }

    public override void Created(Sungero.Domain.CreatedEventArgs e)
    {
      base.Created(e);
      _obj.LifeCycleState = FormalizedPowerOfAttorney.LifeCycleState.Draft;
      
      if (!_obj.State.IsCopied)
        _obj.FormatVersion = FormatVersion.Version003;
    }
  }

  partial class FormalizedPowerOfAttorneyCreatingFromServerHandler
  {
    
    public override void CreatingFrom(Sungero.Domain.CreatingFromEventArgs e)
    {
      base.CreatingFrom(e);
      e.Without(_info.Properties.UnifiedRegistrationNumber);
      e.Without(_info.Properties.RegisteredSignatureId);
      e.Without(_info.Properties.Index);
      e.Without(_info.Properties.ValidFrom);
      e.Map(_info.Properties.LifeCycleState, Sungero.Docflow.FormalizedPowerOfAttorney.LifeCycleState.Draft);
      e.Without(_info.Properties.FtsListState);
      e.Without(_info.Properties.FtsRejectReason);
      e.Without(_info.Properties.Versions);
      e.Without(_info.Properties.HasVersions);
      e.Without(_info.Properties.Note);
    }
  }

}