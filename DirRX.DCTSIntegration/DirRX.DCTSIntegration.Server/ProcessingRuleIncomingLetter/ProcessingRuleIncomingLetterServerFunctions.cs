using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleIncomingLetter;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Domain.Shared;

namespace DirRX.DCTSIntegration.Server
{
  partial class ProcessingRuleIncomingLetterFunctions
  {
    /// <summary>
    /// Возвращает тип документа.
    /// </summary>
    /// <returns>Тип документа.</returns>
    public override Sungero.Docflow.IDocumentType GetDocumentType()
    {
      return Sungero.Docflow.DocumentTypes.GetAll().First(_ => _.DocumentTypeGuid == "8dd00491-8fd0-4a7a-9cf3-8b6dc2e6455d");
    }
    
    /// <summary>
    /// Возвращает правило обработки входящего письма.
    /// </summary>
    /// <param name="id">Id.</param>
    /// <returns>Правило обработки входящего письма.</returns>
    [Remote]
    public static DirRX.DCTSIntegration.IProcessingRuleIncomingLetter GetProcessingRule(int id)
    {
      return DirRX.DCTSIntegration.ProcessingRuleIncomingLetters.GetAll(rule => Equals(rule.Id, id)).FirstOrDefault();
    }
    
    /// <summary>
    /// Создает официальный документ.
    /// </summary>
    /// <returns>Официальный документ.</returns>
    [Remote]
    public override Sungero.Docflow.IOfficialDocument CreateDocument()
    {
      return Sungero.RecordManagement.IncomingLetters.Create();
    }

    /// <summary>
    /// Заполняет свойства создаваемого документа.
    /// </summary>
    /// <param name="entity">Созданный документ.</param>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла./param>
    /// <param name="pars">Словарь с параметрами захвата. Ключ - имя параметра, значение - значение параметра.</param>
    protected override void FillProperties(IEntity entity, Dictionary<string, string> files, Dictionary<string, string> pars)
    {
      // HACK: На клиентской строне свойства правила обработки не актуальны. Получить правило обработки для назначения для установки свойств входящего письма.
      var processingRuleItem = Functions.ProcessingRuleIncomingLetter.GetProcessingRule(_obj.Id);
      
      if (processingRuleItem == null) throw new InvalidOperationException(ProcessingRuleIncomingLetters.Resources.IncorrectProcessingRule);
      
      Sungero.RecordManagement.IIncomingLetter doc = Sungero.RecordManagement.IncomingLetters.As(entity);
      
      // Заполнение корреспондента.
      if (_obj.IsAutoCalcCorrespondent.HasValue && _obj.IsAutoCalcCorrespondent.Value)
      {
        var sender = pars[ProcessingRuleDocBases.Resources.ParamNameSender];
        doc.Correspondent = Functions.Module.GetCounterpartyByEmail(sender);
      }
      if (doc.Correspondent == null) doc.Correspondent = processingRuleItem.Correspondent;
      if (doc.Correspondent == null) throw new InvalidOperationException(ProcessingRuleIncomingLetters.Resources.IncorrectDefaultCorrespondent);
      
      // Заполнение подразделения.
      doc.Department = processingRuleItem.Department;
      if (doc.Department == null) throw new InvalidOperationException(ProcessingRuleIncomingLetters.Resources.IncorrectDefaultDepartment);
      
      // Заполнение НОР.
      doc.BusinessUnit = processingRuleItem.Department.BusinessUnit;
      if (doc.BusinessUnit == null) throw new InvalidOperationException(ProcessingRuleIncomingLetters.Resources.IncorrectDefaultBussinesUnit);
      
      base.FillProperties(entity, files, pars);
      
      this.FillEntitySubject(doc, files, pars);
    }

    /// <summary>
    /// Заполняет содержание документа.
    /// </summary>
    /// <param name="doc">Созданный документ.</param>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Словарь с параметрами захвата. Ключ - имя параметра, значение - значение параметра.</param>
    protected virtual void FillEntitySubject(Sungero.RecordManagement.IIncomingLetter doc, Dictionary<string, string> files, Dictionary<string, string> pars)
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
      
      Sungero.RecordManagement.IIncomingLetter doc = Sungero.RecordManagement.IncomingLetters.As(entity);
      if (doc == null)
        throw new ArgumentException(string.Format(ProcessingRuleDocBases.Resources.IncorrectTypeOfCreatedEntity, _obj.Name), "entity");
      
      return result.Replace(Constants.Module.CounterpartyPattern, doc.Correspondent != null ? doc.Correspondent.Name : string.Empty);
    }
  }
}