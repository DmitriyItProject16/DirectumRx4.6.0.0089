using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleContract;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration
{
  partial class ProcessingRuleContractCategoryPropertyFilteringServerHandler<T>
  {

    public virtual IQueryable<T> CategoryFiltering(IQueryable<T> query, Sungero.Domain.PropertyFilteringEventArgs e)
    {
      // Ограничить список категорий по виду документа и состоянию.
      return query.Where(c => c.Status == Sungero.CoreEntities.DatabookEntry.Status.Active &&
                         c.DocumentKinds.Any(d => Equals(d.DocumentKind, _obj.DocumentKind)));
    }
  }

  partial class ProcessingRuleContractServerHandlers
  {

    public override void BeforeSave(Sungero.Domain.BeforeSaveEventArgs e)
    {
      base.BeforeSave(e);
      
      // Проверить что выбрано хотябы одно из свойств по заполнению содержанию.
      if (string.IsNullOrWhiteSpace(_obj.SubjectPattern) &&
          (_obj.FillFromSubject.HasValue && !_obj.FillFromSubject.Value) && (_obj.FillFromFileName.HasValue && !_obj.FillFromFileName.Value))
        e.AddError(ProcessingRuleContracts.Resources.RequiredSubjectPattern);
    }

    public override void Created(Sungero.Domain.CreatedEventArgs e)
    {
      base.Created(e);
      
      if (!_obj.State.IsCopied)
      {
        _obj.FillFromSubject = false;
        _obj.FillFromFileName = false;
        _obj.IsAutoCalcCounterparty = false;
        _obj.IsSendResponsibleEmployee = false;
      }
    }
  }

}