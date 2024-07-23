using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using DirRX.DCTSIntegration.ProcessingRuleDocBase;
using Sungero.Content;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow;
using Sungero.Domain.Shared;

namespace DirRX.DCTSIntegration.Server
{
  partial class ProcessingRuleDocBaseFunctions
  {
    /// <summary>
    /// Создает простой документ.
    /// </summary>
    /// <returns>Простой документ.</returns>
    [Remote]
    public static ISimpleDocument CreateSimpleDocument()
    {
      return SimpleDocuments.Create();
    }
    
    /// <summary>
    /// Возвращает список доступных видов документов для типа документа.
    /// </summary>
    /// <param name="documentType">Тип документа.</param>
    /// <returns>Доступные виды.</returns>
    [Remote]
    public static IQueryable<Sungero.Docflow.IDocumentKind> GetAvailableDocumentKinds(Sungero.Docflow.IDocumentType documentType)
    {
      return Sungero.Docflow.DocumentKinds.GetAll(d => d.Status == Sungero.CoreEntities.DatabookEntry.Status.Active && Equals(d.DocumentType, documentType));
    }
    
    /// <summary>
    /// Возвращает тип документа.
    /// </summary>
    /// <returns>Тип документа.</returns>
    public virtual Sungero.Docflow.IDocumentType GetDocumentType()
    {
      return null;
    }
    
    /// <summary>
    /// Отправляет задачу по правилу согласования.
    /// </summary>
    /// <param name="attachment">Вложение.</param>
    protected virtual void SendApprovalTask(IEntity attachment)
    {
      // Если установлен регламент, то создать согласование по регламенту.
      if (_obj.ApprovalRule != null)
      {
        var task = Sungero.Docflow.ApprovalTasks.Create();
        task.ApprovalRule = _obj.ApprovalRule;
        task.DocumentGroup.All.Add(attachment);
        task.Start();
      }
    }
    
    /// <summary>
    /// Отправляет простую задачу с созданным документом и связанными с ним.
    /// </summary>
    /// <param name="attachment">Созданный документ.</param>
    public virtual void SendSimpleTaskWithExtraData(IEntity attachment)
    {
      IOfficialDocument doc = OfficialDocuments.As(attachment);
      
      if (doc == null)
        throw new ArgumentException(string.Format(ProcessingRuleDocBases.Resources.IncorrectTypeOfCreatedEntity, _obj.Name), "entity");
      
      var attachments = new List<IEntity>() { doc };
      foreach (var relDoc in doc.Relations.GetRelated(ProcessingRuleDocBases.Resources.SimpleRelationName))
      {
        attachments.Add(relDoc);
      }
      
      this.SendSimpleTask(attachments, null);
    }
    
    /// <summary>
    /// Отправляет задачи с вложениями в соответствии с настроенными параметрами отправки.
    /// </summary>
    /// <param name="attachment">Вложение.</param>
    public override void SendTasks(IEntity attachment)
    {
      // В потомках для изменения состава исполнителей и вложений нужно перекрывать метод SendSimpleTaskWithExtraData.
      this.SendSimpleTaskWithExtraData(attachment);
      this.SendApprovalTask(attachment);
    }
    
    /// <summary>
    /// Создает официальный документ.
    /// </summary>
    /// <returns>Официальный документ.</returns>
    [Remote]
    public virtual IOfficialDocument CreateDocument()
    {
      throw new NotImplementedException();
    }

    /// <summary>
    /// Заполняет свойства создаваемого документа.
    /// </summary>
    /// <param name="entity">Созданный документ.</param>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Словарь с параметрами захвата. Ключ - имя параметра, значение - значение параметра.</param>
    protected override void FillProperties(IEntity entity, Dictionary<string, string> files, Dictionary<string, string> pars)
    {
      IOfficialDocument doc = OfficialDocuments.As(entity);
      if (doc == null)
        throw new ArgumentException(string.Format(ProcessingRuleDocBases.Resources.IncorrectTypeOfCreatedEntity, _obj.Name), "entity");
      using (var stream = new System.IO.MemoryStream())
      {
        var file = Convert.FromBase64String(files.First().Key.Split(';')[0]);
        stream.Write(file, 0, file.Length);
        doc.CreateVersionFrom(stream, Path.GetExtension(files.First().Value));
      }
      doc.Versions.Last().Note = this.GetVersionNote();
      doc.DocumentKind = _obj.DocumentKind;
      this.FillEntityName(doc, files, pars);
    }
    
    /// <summary>
    /// Заполняет наименование документа.
    /// </summary>
    /// <param name="entity">Созданный документ.</param>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Словарь с параметрами захвата. Ключ - имя параметра, значение - значение параметра.</param>
    protected virtual void FillEntityName(IEntity entity, Dictionary<string, string> files, Dictionary<string, string> pars)
    {
      IOfficialDocument doc = OfficialDocuments.As(entity);
      
      if (doc == null)
        throw new ArgumentException(string.Format(ProcessingRuleDocBases.Resources.IncorrectTypeOfCreatedEntity, _obj.Name), "entity");

      // Если для вида документа не установлен флажок "Формировать имя документа автоматически", то сформировать имя документа.
      if (_obj.DocumentKind.GenerateDocumentName.HasValue && !_obj.DocumentKind.GenerateDocumentName.Value)
      {
        // Если в правиле установлен флажок "Автозаполнение" для шаблона наименования, то в качестве имени документа, поступившего из почты, задать тему письма, из файловой системы - имя файла.
        var isMail = _obj.CaptureService.HasValue && _obj.CaptureService.Value == DCTSIntegration.ProcessingRuleBase.CaptureService.Mail;
        
        if (isMail && _obj.FillNameFromSubject.HasValue && _obj.FillNameFromSubject.Value)
          doc.Name = pars[ProcessingRuleDocBases.Resources.ParamNameSubject];
        
        if (!isMail && _obj.FillNameFromFileName.HasValue && _obj.FillNameFromFileName.Value)
          doc.Name = Path.GetFileNameWithoutExtension(files.First().Value);
        // Заполнить по шаблону.
        files.Remove(files.First().Key);
        if (!string.IsNullOrWhiteSpace(_obj.NamePattern) && _obj.FillNameFromFileName.HasValue && !_obj.FillNameFromFileName.Value && _obj.FillNameFromSubject.HasValue && !_obj.FillNameFromSubject.Value)
          doc.Name = this.FillPattern(_obj.NamePattern, doc);
      }
    }
    
    /// <summary>
    /// Обрабатывает переданные вложения.
    /// </summary>
    /// <param name="entity">Созданный документ.</param>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Словарь с параметрами захвата. Ключ - имя параметра, значение - значение параметра.</param>
    protected override void ProcessAttachments(IEntity entity, Dictionary<string, string> files, Dictionary<string, string> pars)
    {
      IOfficialDocument doc = OfficialDocuments.As(entity);

      if (doc == null)
        throw new ArgumentException(string.Format(ProcessingRuleDocBases.Resources.IncorrectTypeOfCreatedEntity, _obj.Name), "entity");
      
      // Базовый метод не вызывается, так как он пустой.
      // Пропускаем два элемента (BODY.TXT и BODY.HTML), так как они отвечают за тело письма.
      foreach (var key in files.Where(f => f.Value.ToUpper() != ProcessingRuleDocBases.Resources.NameTXTFileBodyMail &&
                                      f.Value.ToUpper() != ProcessingRuleDocBases.Resources.NameHTMLFileBodyMail))
      {
        // Создать простой документ с типом связи "Прочие".
        var simpleDoc = Functions.ProcessingRuleDocBase.CreateSimpleDocument();
        using (var stream = new System.IO.MemoryStream())
        {
          var file = Convert.FromBase64String(key.Key.Split(';')[0]);
          stream.Write(file, 0, file.Length);
          simpleDoc.CreateVersionFrom(stream, Path.GetExtension(key.Value));
        }
        simpleDoc.Versions.Last().Note = this.GetVersionNote();
        simpleDoc.Name = Path.GetFileNameWithoutExtension(key.Value);
        simpleDoc.Save();
        
        doc.Relations.Add(ProcessingRuleDocBases.Resources.SimpleRelationName, simpleDoc);
      }
    }
    
    /// <summary>
    /// Возвращает примечание для версии документа.
    /// </summary>
    /// <returns>Примечание для версии документа.</returns>
    protected virtual string GetVersionNote()
    {
      return string.Format(DCTSIntegration.ProcessingRuleDocBases.Resources.NewVersionNoteTemplate, _obj.Name);
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
      
      return result.Replace(Constants.Module.DocKindPattern, _obj.DocumentKind != null ? _obj.DocumentKind.Name : string.Empty);
    }
    
    /// <summary>
    /// Создает новый документ или возвращает существующий если из захваченного файла удалось получить штрихкод.
    /// </summary>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Словарь с параметрами захвата. Ключ - имя параметра, значение - значение параметра.</param>
    /// <returns>Документ.</returns>
    protected override IEntity CreateEntity(Dictionary<string, string> files, Dictionary<string, string> pars)
    {
      return Functions.ProcessingRuleDocBase.CreateDocument(_obj);
    }
  }
}