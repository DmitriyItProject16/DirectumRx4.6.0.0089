using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using DirRX.DCTSIntegration.ProcessingRuleBase;
using Sungero.Company;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Domain.Shared;

namespace DirRX.DCTSIntegration.Server
{
  partial class ProcessingRuleBaseFunctions
  {
    /// <summary>
    /// Обрабатывает новый документ - заполняет свойства объекта, создает связанные сущности.
    /// </summary>
    /// <param name="entity">Созданный документ.</param>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Словарь с параметрами захвата.</param>
    /// <param name="isCaptureByMail">Признак захвата через почту.</param>
    protected void ProcessNewEntity(IEntity entity, Dictionary<string, string> files, Dictionary<string, string> pars, bool isCaptureByMail)
    {
      this.FillProperties(entity, files, pars);
      if (isCaptureByMail)
        this.ProcessAttachments(entity, files, pars);
      entity.Save();
    }
    
    /// <summary>
    /// Производит полный процесс обработки сущности.
    /// </summary>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Xml-данные DCS с дополнительной информацией.</param>
    /// <param name="isCaptureByMail">Признак захвата через почту.</param>
    /// <param name="existEntity">Существующая сущность с которым ассоциировано данное правило (новая сущность не создается).</param>
    protected virtual void FullProcessEntity(Dictionary<string, string> files, Dictionary<string, string> pars, bool isCaptureByMail, IEntity existEntity)
    {
      var entity = this.CreateEntity(files, pars);
      this.ProcessNewEntity(entity, files, pars, isCaptureByMail);
      Functions.ProcessingRuleBase.GrantAccessRights(_obj, entity);
      Functions.ProcessingRuleBase.SendTasks(_obj, entity);
    }
    
    /// <summary>
    /// Заполняет свойства объекта.
    /// </summary>
    /// <param name="entity">Созданный объект.</param>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Словарь с параметрами захвата. Ключ - имя параметра, значение - значение параметра.</param>
    protected virtual void FillProperties(IEntity entity, Dictionary<string, string> files, Dictionary<string, string> pars)
    {

    }
    
    /// <summary>
    /// Обрабатывает вложения из письма, захваченного DCTS.
    /// </summary>
    /// <param name="entity">Созданный объект.</param>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Словарь с параметрами захвата. Ключ - имя параметра, значение - значение параметра.</param>
    protected virtual void ProcessAttachments(IEntity entity, Dictionary<string, string> files, Dictionary<string, string> pars)
    {

    }
    
    /// <summary>
    /// Создает объект.
    /// </summary>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Словарь с параметрами захвата. Ключ - имя параметра, значение - значение параметра.</param>
    /// <returns>Объект.</returns>
    protected virtual IEntity CreateEntity(Dictionary<string, string> files, Dictionary<string, string> pars)
    {
      return null;
    }
    
    /// <summary>
    /// Заполнение шаблона.
    /// </summary>
    /// <param name="pattern">Шаблон.</param>
    /// <param name="entity">Сущность для которой применяется шаблон.</param>
    /// <returns>Результат применения шаблона.</returns>
    protected virtual string FillPattern(string pattern, IEntity entity)
    {
      var result = pattern;

      if (string.IsNullOrWhiteSpace(result))
        return null;
      
      result = result.Replace(Constants.Module.DatePattern, Calendar.Now.ToString("dd.MM.yyyy"));
      result = result.Replace(Constants.Module.DateTimePattern, Calendar.Now.ToString("dd.MM.yyyy HH:mm"));
      
      return result;
    }
    
    /// <summary>
    /// Признак того, что правило соответствует параметрам отбора.
    /// </summary>
    /// <param name="entity">Сущность существующая в БД с которой ассоциировать правило.</param>
    /// <param name="line">Линия.</param>
    /// <param name="captureServices">Модуль ввода.</param>
    /// <returns>Признак того, что правило соответствует параметрам отбора. true - соотвествует, иначе  - false.</returns>
    public virtual bool IsMatchesParams(IEntity entity, string line, Enumeration captureServices)
    {
      return entity == null && _obj.Line.ToLower() == line.ToLower() && _obj.CaptureService == captureServices;
    }
    
    /// <summary>
    /// Возвращает список правил обработки, подходящих по соответствующим параметрам.
    /// </summary>
    /// <param name="entity">Сущность существующая в БД с которой ассоциировать правила.</param>
    /// <param name="line">Линия.</param>
    /// <param name="captureServices">Модуль ввода.</param>
    /// <returns>Список правил обработки.</returns>
    public static IQueryable<IProcessingRuleBase> GetRuleByParams(IEntity entity, string line, Enumeration captureServices)
    {
      // return Functions.ProcessingRuleBase.Remote.GetProcessingRuleBases().Where(r => r.Line.ToLower() == line.ToLower() && r.CaptureService == captureServices);
      var rules = new List<IProcessingRuleBase>();
      foreach (var rule in Functions.ProcessingRuleBase.GetProcessingRuleBases())
      {
        if (Functions.ProcessingRuleBase.IsMatchesParams(rule, entity, line, captureServices))
          rules.Add(rule);
      }
      return rules.AsQueryable();
    }
    
    /// <summary>
    /// Обрабатывает захваченный DCS документ.
    /// </summary>
    /// <param name="xmlFiles">Строка с xml-данными DCS по захваченным файлам.</param>
    /// <param name="xmlInfo">Строка с xml-данными DCS с дополнительной информацией.</param>
    /// <param name="isCaptureByMail">Признак захвата через почту.</param>
    /// <param name="files">Захваченные DCS файлы.</param>
    /// <param name="existEntity">Сущность системы.</param>
    public virtual void ProcessEntity(string xmlFiles, string xmlInfo, bool isCaptureByMail, List<Structures.Module.IDocumentPackage> files, IEntity existEntity)
    {
      XElement filesElement = XElement.Parse(xmlFiles);
      XElement infoElement = XElement.Parse(xmlInfo);
      List<string> fileName = new List<string>();
      if (isCaptureByMail)
      {
        foreach (var el in infoElement.Element("MailCaptureInstanceInfo").Element("Files").Elements())
        {
          fileName.Add(el.Element("FileDescription").Value);
        }
      }
      else
      {
        foreach (var el in infoElement.Element("FileSystemCaptureInstanceInfo").Element("Files").Elements())
        {
          fileName.Add(el.Element("FileDescription").Value);
        }
      }
      // Сформировать словарь с файлами. Ключ - файл base64, значение - имя.
      var fileDict = new Dictionary<string, string>();
      int i = 0;
      foreach (var el in filesElement.Element("Files").Elements())
      {
        var fileBase64 = files.Where(f => f.FileName.Contains(el.Element("FileName").Value)).FirstOrDefault();
        if (fileBase64 != null)
          fileDict.Add(string.Join(";", fileBase64.FileBase64, i), string.Format("{0}{1}", Path.GetFileNameWithoutExtension(fileName.ElementAt(i)), Path.GetExtension(fileBase64.FileName)));
        i++;
      }
      
      // Сформировать словарь с дополнительными параметрами. Ключ - имя параметра, значение - описание.
      var paramDict = new Dictionary<string, string>();
      paramDict.Add(ProcessingRuleBases.Resources.ParamNameCaptureSerivce, isCaptureByMail ? ProcessingRuleBases.Resources.ParamValueCaptureServiceMail : ProcessingRuleBases.Resources.ParamValueCaptureServiceFileSystem);
      if (isCaptureByMail)
      {
        var mailElem = infoElement.Elements().First();
        paramDict.Add(ProcessingRuleBases.Resources.ParamNameSender, mailElem.Element("From").Attribute("Address").Value);
        paramDict.Add(ProcessingRuleBases.Resources.ParamNameSubject, mailElem.Attribute("Subject") != null ? mailElem.Attribute("Subject").Value : ProcessingRuleBases.Resources.WithoutSubject);
        paramDict.Add(ProcessingRuleBases.Resources.ParamNamePriority, mailElem.Attribute("Priority").Value);
        paramDict.Add(ProcessingRuleBases.Resources.ParamNameAddressee, mailElem.Element("To").Element("Recipient").Attribute("Address").Value);
      }
      this.FullProcessEntity(fileDict, paramDict, isCaptureByMail, existEntity);
    }
    
    /// <summary>
    /// Возвращает полный список правил обработки.
    /// </summary>
    /// <returns>Полный список правил обработки.</returns>
    [Remote]
    public static IQueryable<IProcessingRuleBase> GetProcessingRuleBases()
    {
      return ProcessingRuleBases.GetAll().Where(c => c.Status == Sungero.CoreEntities.DatabookEntry.Status.Active);
    }
    
    /// <summary>
    /// Отправляет простую задачу в соответствии с настроенными параметрами отправки.
    /// </summary>
    /// <param name="attachments">Вложения.</param>
    /// <param name="additionalPerformers">Дополнительные согласующие.</param>
    protected virtual void SendSimpleTask(IList<IEntity> attachments, IList<IUser> additionalPerformers)
    {
      List<IUser> performers = _obj.Performers.Select(p => Users.As(p.Performer)).ToList();
      if (additionalPerformers != null)
        performers.AddRange(additionalPerformers);
      // Исключить повторяющихся исполнителей.
      performers = performers.Distinct().ToList();
      if (performers.Any())
      {
        // Создание простой задачи.
        DateTime? deadline = null;
        if (_obj.DeadlineInDays.HasValue)
          deadline = Calendar.AddWorkingDays(Calendar.Now, _obj.DeadlineInDays.Value);
        if (deadline.HasValue && _obj.DeadlineInHours.HasValue)
          deadline = Calendar.AddWorkingHours(deadline.Value, _obj.DeadlineInHours.Value);
        
        var task = Sungero.Workflow.SimpleTasks.Create(_obj.TaskSubject ?? string.Empty, performers.ToArray());
        
        task.Deadline = deadline;
        task.AssignmentType = _obj.TaskType == DCTSIntegration.ProcessingRuleBase.TaskType.Assignment ? Sungero.Workflow.SimpleTaskRouteSteps.AssignmentType.Assignment : Sungero.Workflow.SimpleTaskRouteSteps.AssignmentType.Notice;
        
        // Добавить вложения.
        if (attachments != null)
          foreach (var ent in attachments)
            task.Attachments.Add(ent);
        
        task.Start();
      }
    }
    
    /// <summary>
    /// Отправляет задачи в соответствии с настроенными параметрами отправки.
    /// </summary>
    /// <param name="attachment">Вложение.</param>
    [Remote]
    public virtual void SendTasks(Sungero.Domain.Shared.IEntity attachment)
    {
      this.SendSimpleTask(null, null);
    }
    
    /// <summary>
    /// Выдать Исполнителям права на вложение и его связи.
    /// </summary>
    /// <param name="attachment">Вложение.</param>
    protected virtual void GrantAccessRightsForAttachmentPerformers(Sungero.Domain.Shared.IEntity attachment)
    {
      var doc = Sungero.Docflow.OfficialDocuments.As(attachment);
      if (doc == null)
        throw new ArgumentException(string.Format(ProcessingRuleDocBases.Resources.IncorrectTypeOfCreatedEntity, _obj.Name), "entity");
      
      List<IUser> performers = _obj.Performers.Select(p => Users.As(p.Performer)).ToList();
      if (performers.Any())
      {
        foreach (var performer in performers)
        {
          if (!doc.AccessRights.IsGrantedDirectly(DefaultAccessRightsTypes.FullAccess, performer))
            doc.AccessRights.Grant(performer, DefaultAccessRightsTypes.FullAccess);
          
          if (doc.HasRelations)
          {
            var relations = doc.Relations.GetRelated();
            if (relations.Any())
            {
              foreach (var relation in relations)
              {
                if (!relation.AccessRights.IsGrantedDirectly(DefaultAccessRightsTypes.FullAccess, performer))
                  relation.AccessRights.Grant(performer, DefaultAccessRightsTypes.FullAccess);
                
                relation.AccessRights.Save();
              }
            }
          }
        }
        doc.AccessRights.Save();
      }
    }
    
    /// <summary>
    /// Выдать права на вложение.
    /// </summary>
    /// <param name="attachment">Вложение.</param>
    [Remote]
    public virtual void GrantAccessRights(Sungero.Domain.Shared.IEntity attachment)
    {
      this.GrantAccessRightsForAttachmentPerformers(attachment);
    }
    
  }
}