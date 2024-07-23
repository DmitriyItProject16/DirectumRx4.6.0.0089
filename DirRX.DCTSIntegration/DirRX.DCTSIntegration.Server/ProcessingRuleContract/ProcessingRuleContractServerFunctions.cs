using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using DirRX.DCTSIntegration.ProcessingRuleContract;
using Sungero.Company;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow;
using Sungero.Domain.Shared;

namespace DirRX.DCTSIntegration.Server
{
  partial class ProcessingRuleContractFunctions
  {
    
    /// <summary>
    /// Возвращает тип документа.
    /// </summary>
    /// <returns>Тип документа.</returns>
    public override Sungero.Docflow.IDocumentType GetDocumentType()
    {
      return Sungero.Docflow.DocumentTypes.GetAll().First(_ => _.DocumentTypeGuid == "f37c7e63-b134-4446-9b5b-f8811f6c9666");
    }
    
    /// <summary>
    /// Создает официальный документ.
    /// </summary>
    /// <returns>Официальный документ.</returns>
    [Remote]
    public override IOfficialDocument CreateDocument()
    {
      return Sungero.Contracts.Contracts.Create();
    }
    
    /// <summary>
    /// Отправляет простую задачу с созданным документом и связанными с ним.
    /// </summary>
    /// <param name="attachment">Созданный документ.</param>
    public override void SendSimpleTaskWithExtraData(IEntity attachment)
    {
      Sungero.Contracts.IContract doc = Sungero.Contracts.Contracts.As(attachment);
      
      if (doc == null)
        throw new ArgumentException(string.Format(ProcessingRuleDocBases.Resources.IncorrectTypeOfCreatedEntity, _obj.Name), "entity");
      
      var attachments = new List<IEntity>() { doc };
      foreach (var relDoc in doc.Relations.GetRelated(ProcessingRuleDocBases.Resources.SimpleRelationName))
      {
        attachments.Add(relDoc);
      }
      
      var additionalPerformers = new List<IUser>();
      if (_obj.IsSendResponsibleEmployee.HasValue && _obj.IsSendResponsibleEmployee.Value && doc.ResponsibleEmployee != null)
        additionalPerformers.Add(doc.ResponsibleEmployee);
      
      SendSimpleTask(attachments, additionalPerformers);
    }
    
    public override void GrantAccessRights(IEntity attachment)
    {
      base.GrantAccessRights(attachment);
      
      var doc = Sungero.Contracts.ContractualDocuments.As(attachment);
      if (doc == null)
        throw new ArgumentException(string.Format(ProcessingRuleContracts.Resources.CreatedEntityNotContractualDoc, _obj.Name), "entity");
      
      if (_obj.IsSendResponsibleEmployee.HasValue && _obj.IsSendResponsibleEmployee.Value && doc.ResponsibleEmployee != null)
      {
        if (!doc.AccessRights.IsGrantedDirectly(DefaultAccessRightsTypes.FullAccess, doc.ResponsibleEmployee))
          doc.AccessRights.Grant(doc.ResponsibleEmployee, DefaultAccessRightsTypes.FullAccess);
        
        doc.AccessRights.Save();
        
        if (doc.HasRelations)
        {
          var relations = doc.Relations.GetRelated();
          if (relations.Any())
          {
            foreach (var relation in relations)
            {
              if (!relation.AccessRights.IsGrantedDirectly(DefaultAccessRightsTypes.FullAccess, doc.ResponsibleEmployee))
                relation.AccessRights.Grant(doc.ResponsibleEmployee, DefaultAccessRightsTypes.FullAccess);
              
              relation.AccessRights.Save();
            }
          }
        }
      }
    }
    
    /// <summary>
    /// Заполняет свойства создаваемого документа.
    /// </summary>
    /// <param name="entity">Созданный документ.</param>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Словарь с параметрами захвата. Ключ - имя параметра, значение - значение параметра.</param>
    protected override void FillProperties(IEntity entity, Dictionary<string, string> files, Dictionary<string, string> pars)
    {
      Sungero.Contracts.IContract doc = Sungero.Contracts.Contracts.As(entity);
      if (doc == null)
        throw new ArgumentException(string.Format(ProcessingRuleDocBases.Resources.IncorrectTypeOfCreatedEntity, _obj.Name), "entity");
      
      // Заполнение контрагента.
      if (_obj.IsAutoCalcCounterparty.HasValue && _obj.IsAutoCalcCounterparty.Value)
      {
        var sender = pars[ProcessingRuleDocBases.Resources.ParamNameSender];
        doc.Counterparty = Functions.Module.GetCounterpartyByEmail(sender);
      }
      doc.Counterparty = doc.Counterparty ?? _obj.Counterparty;
      if (doc.Counterparty == null) throw new InvalidOperationException(ProcessingRuleContracts.Resources.IncorrectDefaultCounterparty);
      
      // Заполнение подразделения.
      doc.Department = _obj.Department;
      if (doc.Department == null) throw new InvalidOperationException(ProcessingRuleContracts.Resources.IncorrectDefaultDepartment);
      
      // Заполнение категории.
      if (_obj.Category != null)
        doc.DocumentGroup = _obj.Category;
      
      base.FillProperties(doc, files, pars);
      
      // Заполнение содержания.
      this.FillEntitySubject(doc, files, pars);
    }
    
    /// <summary>
    /// Заполняет наименование документа.
    /// </summary>
    /// <param name="doc">Созданный документ.</param>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Словарь с параметрами захвата. Ключ - имя параметра, значение - значение параметра.</param>
    protected virtual void FillEntitySubject(Sungero.Contracts.IContract doc, Dictionary<string, string> files, Dictionary<string, string> pars)
    {
      // Если в правиле установлен флажок "Автозаполнение" для шаблона содержания, то в качестве содержания документа, поступившего из почты, задать тему письма, из файловой системы - имя файла.
      var isMail = _obj.CaptureService.HasValue && _obj.CaptureService.Value == DCTSIntegration.ProcessingRuleBase.CaptureService.Mail;
      
      if (isMail && _obj.FillFromSubject.HasValue && _obj.FillFromSubject.Value)
        doc.Subject = pars[ProcessingRuleDocBases.Resources.ParamNameSubject];
      
      if (!isMail && _obj.FillFromFileName.HasValue && _obj.FillFromFileName.Value)
        doc.Subject = Path.GetFileNameWithoutExtension(files.First().Value);
      
      // Заполнить по шаблону.
      if (!string.IsNullOrWhiteSpace(_obj.SubjectPattern) && _obj.FillFromFileName.HasValue && !_obj.FillFromFileName.Value && _obj.FillFromSubject.HasValue && !_obj.FillFromSubject.Value)
        doc.Subject = this.FillPattern(_obj.SubjectPattern, doc);
    }
    
    /// <summary>
    /// Заполнение шаблона.
    /// </summary>
    /// <param name="pattern">Шаблон.</param>
    /// <param name="entity">Сущность для которой применяется шаблон.</param>
    /// <returns>Результат применения шаблона.</returns>
    protected override string FillPattern(string pattern, IEntity entity)
    {
      var result = base.FillPattern(pattern, entity);
      if (string.IsNullOrWhiteSpace(result))
        return null;
      
      Sungero.Contracts.IContract contract = Sungero.Contracts.Contracts.As(entity);
      if (contract == null)
        throw new ArgumentException(string.Format(ProcessingRuleDocBases.Resources.IncorrectTypeOfCreatedEntity, _obj.Name), "entity");
      
      return result.Replace(Constants.Module.CounterpartyPattern, contract.Counterparty != null ? contract.Counterparty.Name : string.Empty);
    }
  }
}