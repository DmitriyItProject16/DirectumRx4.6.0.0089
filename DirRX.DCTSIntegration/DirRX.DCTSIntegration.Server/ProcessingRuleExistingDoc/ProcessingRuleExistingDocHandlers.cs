using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleExistingDoc;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration
{
  partial class ProcessingRuleExistingDocServerHandlers
  {

    public override void Created(Sungero.Domain.CreatedEventArgs e)
    {
      base.Created(e);
      
      if (!_obj.State.IsCopied)
      {
        _obj.SendNotice = true;
      }
    }

    public override void BeforeSave(Sungero.Domain.BeforeSaveEventArgs e)
    {
      // Проверка на уникальность линии.
      var countDuplicationExistingDoc = ProcessingRuleExistingDocs.GetAll().Count(x => x.Line == _obj.Line && x.Status == Sungero.CoreEntities.DatabookEntry.Status.Active);
      if (countDuplicationExistingDoc > 1 || (_obj.State.IsInserted && countDuplicationExistingDoc > 0))
        e.AddError(string.Format(DCTSIntegration.ProcessingRuleBases.Resources.DuplicationLineName, _obj.Line));
    }
  }

}