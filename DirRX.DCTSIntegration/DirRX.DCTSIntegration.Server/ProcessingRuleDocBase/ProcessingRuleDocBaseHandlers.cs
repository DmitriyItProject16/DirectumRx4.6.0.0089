using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleDocBase;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration
{
  partial class ProcessingRuleDocBaseApprovalRulePropertyFilteringServerHandler<T>
  {

    public virtual IQueryable<T> ApprovalRuleFiltering(IQueryable<T> query, Sungero.Domain.PropertyFilteringEventArgs e)
    {
      return query.Where(d => d.DocumentFlow == _obj.DocumentKind.DocumentFlow)
        .Where(d => !d.DocumentKinds.Any() || d.DocumentKinds.Any(o => Equals(o.DocumentKind, _obj.DocumentKind)));
    }
  }

  partial class ProcessingRuleDocBaseDocumentKindPropertyFilteringServerHandler<T>
  {

    public virtual IQueryable<T> DocumentKindFiltering(IQueryable<T> query, Sungero.Domain.PropertyFilteringEventArgs e)
    {
      return query.Where(d => Equals(d.DocumentType, Functions.ProcessingRuleDocBase.GetDocumentType(_obj)));
    }
  }

  partial class ProcessingRuleDocBaseServerHandlers
  {

    public override void BeforeSave(Sungero.Domain.BeforeSaveEventArgs e)
    {
      base.BeforeSave(e);
      
      // Проверить что выбрано хотябы одно из свойств по заполнению имени.
      var isGenerateName = _obj.DocumentKind != null && _obj.DocumentKind.GenerateDocumentName.HasValue && _obj.DocumentKind.GenerateDocumentName.Value;
      if (!isGenerateName && string.IsNullOrWhiteSpace(_obj.NamePattern) && 
          ((_obj.FillNameFromSubject.HasValue && !_obj.FillNameFromSubject.Value) && (_obj.FillNameFromFileName.HasValue && !_obj.FillNameFromFileName.Value)))
        e.AddError(ProcessingRuleDocBases.Resources.RequiredNamePattern);
      // Проверка на уникальность линии.
      var countDuplicationExistingDoc = ProcessingRuleExistingDocs.GetAll().Count(x => x.Line == _obj.Line && x.Status == Sungero.CoreEntities.DatabookEntry.Status.Active);
      var countDuplicationNotExistingDoc = ProcessingRuleDocBases.GetAll().Count(x => x.Line == _obj.Line && x.Status == Sungero.CoreEntities.DatabookEntry.Status.Active) - countDuplicationExistingDoc;      
      if (countDuplicationNotExistingDoc > 1 || (_obj.State.IsInserted && countDuplicationNotExistingDoc > 0))
        e.AddError(string.Format(DCTSIntegration.ProcessingRuleBases.Resources.DuplicationLineName, _obj.Line));     
    }

    public override void Created(Sungero.Domain.CreatedEventArgs e)
    {
      base.Created(e);
      
      if (!_obj.State.IsCopied)
      {
        _obj.FillNameFromSubject = false;
        _obj.FillNameFromFileName = false;
        // Установить вид документа, если он один для данного типа.
        var docType = Functions.ProcessingRuleDocBase.GetDocumentType(_obj);
        var docKinds = Functions.ProcessingRuleDocBase.GetAvailableDocumentKinds(docType);
        if (docKinds.Count() == 1)
          _obj.DocumentKind = docKinds.First();
      }
    }
  }

}