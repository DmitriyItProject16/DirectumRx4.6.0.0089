using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow.FormalizedPowerOfAttorney;

namespace Sungero.Docflow
{
  partial class FormalizedPowerOfAttorneyClientHandlers
  {

    public override void Closing(Sungero.Presentation.FormClosingEventArgs e)
    {
      base.Closing(e);
      
      e.Params.Remove(Constants.FormalizedPowerOfAttorney.IsLastVersionApprovedParamName);
    }

    public override void Refresh(Sungero.Presentation.FormRefreshEventArgs e)
    {
      // Занесение доверенности не завершено. Не дизейблить обязательные поля, чтобы можно было завершить занесение.
      if (_obj.RegistrationState == Docflow.OfficialDocument.RegistrationState.Registered &&
          _obj.LifeCycleState == Docflow.OfficialDocument.LifeCycleState.Draft &&
          (_obj.AgentType == null || _obj.BusinessUnit == null || _obj.Department == null))
      {
        e.Params.AddOrUpdate(Sungero.Docflow.Constants.OfficialDocument.RepeatRegister, true);
        e.Params.AddOrUpdate(Sungero.Docflow.Constants.OfficialDocument.NeedValidateRegisterFormat, true);
      }
      
      if (!e.Params.Contains(Constants.FormalizedPowerOfAttorney.IsLastVersionApprovedParamName))
      {
        var signatures = Signatures.Get(_obj.LastVersion);
        // Запретить повторный импорт и генерацию XML, если уже занесена подписанная версия.
        if (_obj.HasVersions && (_obj.LastVersionApproved == true || signatures.Any(x => x.SignatureType == SignatureType.Approval)))
          e.Params.AddOrUpdate(Constants.FormalizedPowerOfAttorney.IsLastVersionApprovedParamName, signatures.Any(x => x.IsExternal == true));
      }
      
      var registrationErrorIsVisible = _obj.FtsListState == Docflow.FormalizedPowerOfAttorney.FtsListState.Rejected;
      _obj.State.Properties.FtsRejectReason.IsVisible = registrationErrorIsVisible;
      _obj.State.Properties.FormatVersion.IsEnabled = !_obj.HasVersions;
      
      base.Refresh(e);
      if (_obj.AgentType == AgentType.LegalEntity && _obj.FormatVersion == FormatVersion.Version003)
      {
        _obj.State.Properties.Representative.IsEnabled = false;
        _obj.State.Properties.Representative.IsRequired = false;
      }
    }

    public virtual void UnifiedRegistrationNumberValueInput(Sungero.Presentation.StringValueInputEventArgs e)
    {
      if (!string.IsNullOrEmpty(e.NewValue))
        e.NewValue = e.NewValue.Trim();
      
      Guid guid;
      if (!Guid.TryParse(e.NewValue, out guid))
        e.AddError(Sungero.Docflow.FormalizedPowerOfAttorneys.Resources.ErrorValidateUnifiedRegistrationNumber);
    }

  }
}