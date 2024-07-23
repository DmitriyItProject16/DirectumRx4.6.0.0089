using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using DirRX.DCTSIntegration.ProcessingRuleExistingDoc;
using Sungero.Company;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow;
using Sungero.Domain.Shared;
using Sungero.Workflow;

namespace DirRX.DCTSIntegration.Server
{
  partial class ProcessingRuleExistingDocFunctions
  {
    /// <summary>
    /// Возвращает правило обработки существующего документа.
    /// </summary>
    /// <param name="lineSender">Наименование линии.</param>
    /// <returns>Правило обработки существующего документа.</returns>
    [Remote(IsPure = true)]
    public static IProcessingRuleExistingDoc GetProcessingRuleExistingDocByLineSender(string lineSender)
    {
      return ProcessingRuleExistingDocs.GetAll().FirstOrDefault(c => c.Status == Sungero.CoreEntities.DatabookEntry.Status.Active && c.Line == lineSender);
    }
    
    /// <summary>
    /// Возвращает тип документа.
    /// </summary>
    /// <returns>Тип документа.</returns>
    [Remote]
    public override Sungero.Docflow.IDocumentType GetDocumentType()
    {
      // Правило обработки существующего документа будет вызываться для документов разных типов, поэтому метод не возвращает конкретный тип.
      return null;
    }
    
    /// <summary>
    /// Отправляет задачи с вложениями в соответствии с настроенными параметрами отправки.
    /// </summary>
    /// <param name="attachment">Вложение.</param>
    [Remote]
    public override void SendTasks(IEntity attachment)
    {
      if (_obj.SendNotice.HasValue && !_obj.SendNotice.Value)
        return;
      
      var doc = OfficialDocuments.As(attachment);
      if (doc == null)
        throw new ArgumentException(string.Format(ProcessingRuleDocBases.Resources.IncorrectTypeOfCreatedEntity, _obj.Name), "attachment");
      
      var additionalPerformers = new List<Sungero.CoreEntities.IUser>();
      // Получение списка задач отправленных на согласование по регламенту во вложении которого есть данный документ. Key - задача, Value - списко исполнителей связанных заданий по этапам.
      var tasks = new Dictionary<Sungero.Workflow.ITask, List<Sungero.CoreEntities.IUser>>(this.GetApprovalTasks(doc));
      
      // Если документ является договором, то отправить уведомление ответственному и установить состояние "Подписан" для свойства "Согл. с контрагентом".
      var contract = Sungero.Contracts.ContractualDocuments.As(doc);
      if (contract != null)
      {
        if (contract.ResponsibleEmployee != null)
          additionalPerformers.Add(contract.ResponsibleEmployee);
        
        // Если есть задачи на согласование договора, то установить состояние "Подписан" для свойства "Согл. с контрагентом".
        if (tasks.Any())
        {
          // Если существует строка с действием Согласование с контрагентом и незаполненной датой возврата, то заполнить поля с датой возврата и результатом.
          var track = doc.Tracking.LastOrDefault(c => c.Action == Sungero.Docflow.OfficialDocumentTracking.Action.Endorsement && !c.ReturnDate.HasValue);
          if (track != null)
          {
            track.ReturnDate = Calendar.Now;
            track.ReturnResult = Sungero.Docflow.OfficialDocumentTracking.ReturnResult.Signed;
          }
          contract.ExternalApprovalState = Sungero.Docflow.OfficialDocument.ExternalApprovalState.Signed;
          contract.Save();
        }
      }
      
      if (tasks.Any())
      {
        // Если задачи по регламенту существуют, то отправить уведомления исполнителям для связанных заданий по этапам.
        foreach (var task in tasks.Where(c => c.Value.Any()))
        {
          var newTask = SimpleTasks.CreateAsSubtask(task.Key);
          newTask.AssignmentType = Sungero.Workflow.SimpleTask.AssignmentType.Notice;
          newTask.Subject = string.Format(ProcessingRuleDocBases.Resources.CheckReturnDocumentSubject, doc.Name);
          var activeText = contract == null ?
            ProcessingRuleDocBases.Resources.CheckReturnDocumentActiveText : ProcessingRuleDocBases.Resources.CheckReturnContractActiveText;
          newTask.ActiveText = string.Format(activeText, doc.Name);
          newTask.MaxDeadline = task.Key.MaxDeadline;
          var performers = new List<Sungero.CoreEntities.IUser>(task.Value);
          performers.AddRange(additionalPerformers);
          foreach (var approver in performers.Distinct())
          {
            var routeStep = newTask.RouteSteps.AddNew();
            routeStep.AssignmentType = Sungero.Workflow.SimpleTaskRouteSteps.AssignmentType.Notice;
            routeStep.Performer = approver;
            routeStep.Deadline = task.Key.MaxDeadline;
          }
          newTask.Start();
        }
      }
      else
      {
        if (!additionalPerformers.Any())
          additionalPerformers.Add(doc.Author);
        // Если задач по регламенту не найдено, то отправить уведомление автору документа.
        this.SendSimpleTask(new List<IEntity>() { doc }, additionalPerformers);
      }
    }
    
    /// <summary>
    /// Возвращает словарь с задачами по согласованию с регламентом для указанного документа.
    /// </summary>
    /// <param name="doc">Документ для которого ищутся задачи.</param>
    /// <returns>Словарь с задачами по согласованию с регламентом для указанного документа. Key - задача с согласованием по регламенту, Value - список исполнителей связанных заданий по этапам.</returns>
    private Dictionary<ITask, List<Sungero.CoreEntities.IUser>> GetApprovalTasks(IOfficialDocument doc)
    {
      var res = new Dictionary<ITask, List<Sungero.CoreEntities.IUser>>();
      
      // Поиск задач по регламенту со статусом в работе.
      var approvalTasks = ApprovalTasks.GetAll().Where(c => c.Status == Sungero.Workflow.Task.Status.InProcess);
      foreach (var task in approvalTasks)
      {
        // Если во вложениях к задаче есть нужный документ, то попытаться определить исполнителей для этой задачи.
        if (task.DocumentGroup.All.Select(c => OfficialDocuments.As(c)).Any(c => c != null && OfficialDocuments.Equals(doc, c)))
        {
          // Поиск связанных с задачей заданий по этапу "Контроль возврата" и добавление исполнитей в список.
          var assignments = new List<IAssignment>();
          assignments.AddRange(ApprovalCheckReturnAssignments.GetAll().Where(c => Tasks.Equals(c.Task, task)));
          var performers = new List<Sungero.CoreEntities.IUser>();
          if (assignments.Any())
          {
            performers.AddRange(assignments.Select(c => c.Performer).Distinct());
          }
          else
          {
            // Если задание по этапу не найдено, то попробовать взять изполнителя с закладки "Выдача" в карточке документа.
            var track = doc.Tracking.LastOrDefault(c => c.Action == Sungero.Docflow.OfficialDocumentTracking.Action.Endorsement && !c.ReturnDate.HasValue);
            if (track != null && !performers.Contains(track.DeliveredTo))
              performers.Add(track.DeliveredTo);
          }
          if (performers.Any())
            res.Add(task, performers);
        }
      }
      return res;
    }
    
    /// <summary>
    /// Отправляет простую задачу в соответствии с настроенными параметрами отправки.
    /// </summary>
    /// <param name="attachments">Вложения.</param>
    /// <param name="additionalPerformers">Дополнительные согласующие.</param>
    protected override void SendSimpleTask(IList<IEntity> attachments, IList<IUser> additionalPerformers)
    {
      var doc = OfficialDocuments.As(attachments.First());
      if (doc == null)
        throw new ArgumentException(string.Format(ProcessingRuleDocBases.Resources.IncorrectTypeOfCreatedEntity, _obj.Name), "attachment");
      
      var performers = _obj.Performers.Select(p => Users.As(p.Performer)).ToList();
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
        if (deadline.HasValue && _obj.DeadlineInDays.HasValue)
          deadline = Calendar.AddWorkingHours(deadline.Value, _obj.DeadlineInHours.Value);

        var task = Sungero.Workflow.SimpleTasks.Create(string.Format(ProcessingRuleDocBases.Resources.CheckReturnDocumentSubject, doc.Name), performers.ToArray());
        
        if (task.Subject.Length > 250)
          task.Subject = task.Subject.Substring(0, 250);
        
        task.Deadline = deadline;
        task.AssignmentType = _obj.TaskType.HasValue && _obj.TaskType == DCTSIntegration.ProcessingRuleBase.TaskType.Assignment ?
          Sungero.Workflow.SimpleTaskRouteSteps.AssignmentType.Assignment :
          Sungero.Workflow.SimpleTaskRouteSteps.AssignmentType.Notice;

        // Добавить вложения.
        if (attachments != null)
          foreach (var ent in attachments)
            task.Attachments.Add(ent);
        
        task.Start();
      }
    }
    
    /// <summary>
    /// Запустить асинхронный обработчик, выполняющий занесение тела существующего документа.
    /// </summary>
    /// <param name="temporaryDocument">Документ.</param>
    [Remote]
    public static void CreateAsyncEntryExistingDocumentHandler(DirRX.DCTSIntegration.ITemporaryDocument temporaryDocument)
    {
      var asyncEntryExistingDocumentHandler = DirRX.DCTSIntegration.AsyncHandlers.EntryExistingDocument.Create();
      asyncEntryExistingDocumentHandler.TempDocId = temporaryDocument.Id;
      asyncEntryExistingDocumentHandler.ExecuteAsync();
    }
    
    /// <summary>
    /// Признак того, что правило соответствует параметрам отбора.
    /// </summary>
    /// <param name="entity">Сущность существующая в БД с которой ассоциировать правило.</param>
    /// <param name="line">Линия.</param>
    /// <param name="captureServices">Модуль ввода.</param>
    /// <returns>Признак того, что правило соответствует параметрам отбора. true - соотвествует, иначе  - false.</returns>
    public override bool IsMatchesParams(IEntity entity, string line, Enumeration captureServices)
    {
      return OfficialDocuments.Is(entity) && _obj.Line.ToLower() == line.ToLower() && _obj.CaptureService == captureServices;
    }
    
    /// <summary>
    /// Производит полный процесс обработки сущности.
    /// </summary>
    /// <param name="files">Словарь с захваченными файлами. Ключ - файл в формате Base64, значение - наименование файла.</param>
    /// <param name="pars">Xml-данные DCS с дополнительной информацией.</param>
    /// <param name="isCaptureByMail">Признак захвата через почту.</param>
    /// <param name="existEntity">Существующая сущность с которым ассоциировано данное правило (новая сущность не создается).</param>
    protected override void FullProcessEntity(Dictionary<string, string> files, Dictionary<string, string> pars, bool isCaptureByMail, IEntity existEntity)
    {
      var doc = OfficialDocuments.As(existEntity);
      if (doc == null)
        throw new ArgumentException(string.Format(ProcessingRuleDocBases.Resources.IncorrectTypeOfCreatedEntity, _obj.Name), "entity");
      
      var fileInfo = files.FirstOrDefault(f => f.Value.ToUpper() != ProcessingRuleDocBases.Resources.NameTXTFileBodyMail &&
                                      f.Value.ToUpper() != ProcessingRuleDocBases.Resources.NameHTMLFileBodyMail);
      var note = GetVersionNote();
      
      var temporaryDocument = DirRX.DCTSIntegration.Functions.TemporaryDocument.CreateTemporaryDocument(_obj, doc, note);
      using (var stream = new System.IO.MemoryStream())
      {
        var file = Convert.FromBase64String(fileInfo.Key);
        stream.Write(file, 0, file.Length);
        temporaryDocument.Data.Write(stream);
      }
      temporaryDocument.Extension = Path.GetExtension(fileInfo.Value);
      temporaryDocument.Save();
      
      Functions.ProcessingRuleExistingDoc.CreateAsyncEntryExistingDocumentHandler(temporaryDocument);
    }
  }
}