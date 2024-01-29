using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow.PowerOfAttorneyBase;

namespace Sungero.Docflow
{

  partial class PowerOfAttorneyBaseIssuedToPartyPropertyFilteringServerHandler<T>
  {

    public virtual IQueryable<T> IssuedToPartyFiltering(IQueryable<T> query, Sungero.Domain.PropertyFilteringEventArgs e)
    {
      if (_obj.AgentType == AgentType.Person)
        return query.Where(cp => !Sungero.Parties.CompanyBases.Is(cp));
      
      if (_obj.AgentType == AgentType.LegalEntity || _obj.AgentType == AgentType.Entrepreneur)
        return query.Where(cp => Sungero.Parties.CompanyBases.Is(cp));
      
      return query;
    }
  }

  partial class PowerOfAttorneyBaseFilteringServerHandler<T>
  {

    public override IQueryable<T> Filtering(IQueryable<T> query, Sungero.Domain.FilteringEventArgs e)
    {
      if (_filter == null)
        return query;
      
      query = this.FilterByStatus(query);
      query = this.FilterByProperties(query);
      return query;
    }
    
    protected IQueryable<T> FilterByStatus(IQueryable<T> query)
    {
      if (_filter.DraftState && _filter.ActiveState && _filter.ObsoleteState)
        return query;
      
      if (!(_filter.DraftState || _filter.ActiveState || _filter.ObsoleteState))
        return query;

      query = query.Where(poa => _filter.DraftState && poa.LifeCycleState == PowerOfAttorneyBase.LifeCycleState.Draft ||
                          _filter.ActiveState && poa.LifeCycleState == PowerOfAttorneyBase.LifeCycleState.Active ||
                          _filter.ObsoleteState && poa.LifeCycleState == PowerOfAttorneyBase.LifeCycleState.Obsolete);
      return query;
    }
    
    /// <summary>
    /// Отфильтровать по свойствам, кроме статуса.
    /// </summary>
    /// <param name="query">Фильтруемый запрос.</param>
    /// <returns>Запрос после фильтрации.</returns>
    /// <remarks>Необходимо для переопределения в потомках.</remarks>
    protected IQueryable<T> FilterByProperties(IQueryable<T> query)
    {
      // Фильтр "Наша организация".
      if (_filter.BusinessUnit != null)
        query = query.Where(poa => Equals(poa.BusinessUnit, _filter.BusinessUnit));
      
      // Фильтр "Подразделение".
      if (_filter.Department != null)
        query = query.Where(poa => Equals(poa.Department, _filter.Department));
      
      // Фильтр "Кому выдана".
      if (_filter.Performer != null)
        query = query.Where(poa => Equals(poa.IssuedToParty, _filter.Performer));
      
      // Период.
      if (_filter.Today)
        query = query.Where(poa => (!poa.RegistrationDate.HasValue || poa.RegistrationDate <= Calendar.UserToday) &&
                            (!poa.ValidTill.HasValue || poa.ValidTill >= Calendar.UserToday));
      
      if (_filter.ManualPeriodPoA)
      {
        if (_filter.DateRangeFrom.HasValue)
          query = query.Where(poa => !poa.ValidTill.HasValue || poa.ValidTill >= _filter.DateRangeFrom);
        if (_filter.DateRangeTo.HasValue)
          query = query.Where(poa => !poa.RegistrationDate.HasValue || poa.RegistrationDate <= _filter.DateRangeTo);
      }
      
      return query;
    }
  }

  partial class PowerOfAttorneyBaseCreatingFromServerHandler
  {

    public override void CreatingFrom(Sungero.Domain.CreatingFromEventArgs e)
    {
      base.CreatingFrom(e);
      e.Without(_info.Properties.ValidTill);
    }
  }

  partial class PowerOfAttorneyBaseServerHandlers
  {

    public override void Created(Sungero.Domain.CreatedEventArgs e)
    {
      base.Created(e);
      
      // Очистить поля Подразделение и НОР, заполненные в предке.
      if (!_obj.State.IsCopied)
      {
        _obj.Department = null;
        _obj.BusinessUnit = null;
        _obj.AgentType = Docflow.PowerOfAttorneyBase.AgentType.Employee;
      }
    }

    public override void BeforeSave(Sungero.Domain.BeforeSaveEventArgs e)
    {
      base.BeforeSave(e);
      
      if (_obj.ValidFrom > _obj.ValidTill)
      {
        e.AddError(_obj.Info.Properties.ValidFrom, PowerOfAttorneyBases.Resources.IncorrectValidDates, _obj.Info.Properties.ValidTill);
        e.AddError(_obj.Info.Properties.ValidTill, PowerOfAttorneyBases.Resources.IncorrectValidDates, _obj.Info.Properties.ValidFrom);
      }
      
      // При изменении сотрудника в "Кому выдана" проверить наличие действующих прав подписи.
      if (!Equals(_obj.IssuedTo, _obj.State.Properties.IssuedTo.OriginalValue))
      {
        var signSettings = Functions.PowerOfAttorneyBase.GetActiveSignatureSettingsByPoA(_obj);
        if (signSettings.Any())
          e.AddError(PowerOfAttorneyBases.Resources.AlreadyExistSignatureSetting, _obj.Info.Actions.FindActiveSignatureSetting);
      }
      
      if (!e.IsValid)
      {
        return;
      }
      
      if (_obj.AccessRights.CanUpdate())
        Functions.PowerOfAttorneyBase.CleanupAgentFields(_obj);
      
      // Выдать права на документ сотруднику, указанному в поле "Кому выдана".
      if (_obj.IssuedTo != null && _obj.AccessRights.StrictMode != AccessRightsStrictMode.Enhanced)
        _obj.AccessRights.Grant(_obj.IssuedTo, DefaultAccessRightsTypes.Read);
    }
    
  }
}