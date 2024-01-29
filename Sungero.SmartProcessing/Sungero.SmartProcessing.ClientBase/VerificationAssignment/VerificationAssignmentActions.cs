using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow;
using Sungero.SmartProcessing.VerificationAssignment;

namespace Sungero.SmartProcessing.Client
{
  partial class VerificationAssignmentActions
  {
    public virtual void Repacking(Sungero.Domain.Client.ExecuteActionArgs e)
    {
      if (!Docflow.PublicFunctions.Module.Remote.IsModuleAvailableByLicense(Commons.PublicConstants.Module.IntelligenceGuid))
      {
        Dialogs.NotifyMessage(VerificationAssignments.Resources.NoLicenseToRepacking);
        return;
      }
      
      if (_obj.State.IsChanged)
      {
        e.AddWarning(VerificationAssignments.Resources.SaveAssignmentBeforeRepacking);
        return;
      }

      var documents = Functions.VerificationAssignment.GetDocumentsSuitableForRepacking(_obj);
      var documentsAndVersions = documents.Select(x => Structures.RepackingSession.RepackingDocument.Create(x, x.LastVersion)).ToList();
      if (!documentsAndVersions.Any())
      {
        Dialogs.NotifyMessage(VerificationAssignments.Resources.NoDocumentsSuitableForRepacking);
        return;
      }

      var activeRepackingSession = Functions.RepackingSession.Remote.GetActiveSessionByAssignmentId(_obj.Id);

      if (activeRepackingSession == null)
      {
        if (Functions.Module.TryLockRepackingSessionDocuments(documentsAndVersions))
        {
          var repackingSession = SmartProcessing.Functions.Module.Remote.CreateRepackingSession(_obj.Id, documentsAndVersions);
          var url = Functions.RepackingSession.Remote.GetUrl(repackingSession);
          Hyperlinks.Open(url);
        }
        else
        {
          e.AddError(VerificationAssignments.Resources.AttachedDocumentsLocked);
          return;
        }
      }
      else
      {
        if (Functions.Module.TryLockRepackingSessionDocuments(documentsAndVersions))
        {
          activeRepackingSession.Status = SmartProcessing.RepackingSession.Status.Closed;
          activeRepackingSession.Save();
          
          var repackingSession = SmartProcessing.Functions.Module.Remote.CreateRepackingSession(_obj.Id, documentsAndVersions);
          var url = Functions.RepackingSession.Remote.GetUrl(repackingSession);
          Hyperlinks.Open(url);
        }
        else
          Dialogs.NotifyMessage(VerificationAssignments.Resources.RepackingIsInProgress);
      }
      e.Params.AddOrUpdate(Sungero.SmartProcessing.Constants.VerificationAssignment.ShowRepackingResultsNotificationParamName, true);
    }

    public virtual bool CanRepacking(Sungero.Domain.Client.CanExecuteActionArgs e)
    {
      return _obj.Status == Sungero.Workflow.Assignment.Status.InProcess;
    }

    public virtual void ShowInvalidDocuments(Sungero.Domain.Client.ExecuteActionArgs e)
    {
      var attachments = _obj.AllAttachments
        .Where(a => Content.ElectronicDocuments.Is(a))
        .Select(a => Content.ElectronicDocuments.As(a))
        .Distinct()
        .Cast<Docflow.IOfficialDocument>().ToList();
      
      var invalidDocuments = attachments
        .Where(x => Sungero.Docflow.PublicFunctions.OfficialDocument.HasEmptyRequiredProperties(x))
        .ToList();
      if (invalidDocuments.Count() > 0)
        invalidDocuments.Show();
    }

    public virtual bool CanShowInvalidDocuments(Sungero.Domain.Client.CanExecuteActionArgs e)
    {
      return true;
    }

    public virtual void DeleteDocuments(Sungero.Domain.Client.ExecuteActionArgs e)
    {
      if (RepackingSessions.GetAll(s => s.AssignmentId == _obj.Id && s.Status == SmartProcessing.RepackingSession.Status.Active).Any())
      {
        e.AddError(Sungero.SmartProcessing.VerificationAssignments.Resources.DeleteDocumentsDialogImpossible);
        return;
      }
      
      var documents = Functions.VerificationAssignment.GetOrderedDocuments(_obj)
        .Where(d => d.LifeCycleState != Docflow.OfficialDocument.LifeCycleState.Obsolete);
      var notSuitableDocuments = Functions.VerificationAssignment.GetInaccesssibleDocuments(_obj, documents);
      SmartProcessing.Client.ModuleFunctions.DeleteDocumentsDialogInWeb(_obj, documents.Except(notSuitableDocuments).ToList());
    }

    public virtual bool CanDeleteDocuments(Sungero.Domain.Client.CanExecuteActionArgs e)
    {
      if (ClientApplication.ApplicationType != ApplicationType.Web)
        return false;
      
      if (_obj.Status != Sungero.Workflow.AssignmentBase.Status.InProcess)
        return false;
      
      return _obj.AllAttachments.Any(a => Content.ElectronicDocuments.Is(a));
    }

    public virtual void SendForExecution(Sungero.Domain.Client.ExecuteActionArgs e)
    {
      // Проверить заполненность обязательных полей во всех документах комплекта.
      var attachments = _obj.AllAttachments.Select(a => Content.ElectronicDocuments.As(a)).Distinct().ToList();

      if (attachments.Cast<Docflow.IOfficialDocument>().Any(x => Sungero.Docflow.PublicFunctions.OfficialDocument.HasEmptyRequiredProperties(x)))
      {
        e.AddError(VerificationAssignments.Resources.InvalidDocumentWhenSendInWork,
                   _obj.Info.Actions.ShowInvalidDocuments);
        return;
      }
      
      // Определить главный документ.
      var suitableDocuments = Docflow.PublicFunctions.OfficialDocument.GetSuitableDocuments(attachments,
                                                                                            Docflow.OfficialDocuments.Info.Actions.SendActionItem);
      var probablyMainDocument = Content.ElectronicDocuments.As(Functions.Module.GetLeadingDocument(suitableDocuments.Cast<Docflow.IOfficialDocument>().ToList()));
      var mainDocument = Docflow.PublicFunctions.OfficialDocument.ChooseMainDocument(suitableDocuments,
                                                                                     new List<Content.IElectronicDocument> { probablyMainDocument });
      if (mainDocument == null)
        return;
      var mainOfficialDocument = Docflow.OfficialDocuments.As(mainDocument);
      
      // Создать задачу.
      var actionItemTask = Sungero.RecordManagement.PublicFunctions.Module.Remote.CreateActionItemExecution(mainOfficialDocument);
      
      // Добавить вложения, которые не были добавлены при создании задачи.
      foreach (var attachment in attachments.Where(att => !actionItemTask.Attachments.Any(x => Equals(x, att))))
      {
        if (Docflow.PublicFunctions.OfficialDocument.NeedToAttachDocument(attachment, mainOfficialDocument))
          actionItemTask.OtherGroup.All.Add(attachment);
      }
      
      actionItemTask.Show();
    }

    public virtual bool CanSendForExecution(Sungero.Domain.Client.CanExecuteActionArgs e)
    {
      var attachments = _obj.AllAttachments.Select(a => Content.ElectronicDocuments.As(a)).Distinct().ToList();
      var suitableDocuments = Docflow.PublicFunctions.OfficialDocument.GetSuitableDocuments(attachments,
                                                                                            Docflow.OfficialDocuments.Info.Actions.SendActionItem);
      return suitableDocuments.Any();
    }

    public virtual void SendForReview(Sungero.Domain.Client.ExecuteActionArgs e)
    {
      // Проверить заполненность обязательных полей во всех документах комплекта.
      var attachments = _obj.AllAttachments.Select(a => Content.ElectronicDocuments.As(a)).Distinct().ToList();

      if (attachments.Cast<Docflow.IOfficialDocument>().Any(x => Sungero.Docflow.PublicFunctions.OfficialDocument.HasEmptyRequiredProperties(x)))
      {
        e.AddError(VerificationAssignments.Resources.InvalidDocumentWhenSendInWork,
                   _obj.Info.Actions.ShowInvalidDocuments);
        return;
      }
      
      // Определить главный документ.
      var suitableDocuments = Docflow.PublicFunctions.OfficialDocument.GetSuitableDocuments(attachments,
                                                                                            Docflow.OfficialDocuments.Info.Actions.SendForReview);
      var probablyMainDocument = Content.ElectronicDocuments.As(Functions.Module.GetLeadingDocument(suitableDocuments.Cast<Docflow.IOfficialDocument>().ToList()));
      var mainDocument = Docflow.PublicFunctions.OfficialDocument.ChooseMainDocument(suitableDocuments,
                                                                                     new List<Content.IElectronicDocument> { probablyMainDocument });
      if (mainDocument == null)
        return;
      var mainOfficialDocument = Docflow.OfficialDocuments.As(mainDocument);
      
      // Если по главному документу ранее были запущены задачи, то вывести соответствующий диалог.
      if (!Docflow.PublicFunctions.OfficialDocument.NeedCreateReviewTask(mainOfficialDocument))
        return;

      var task = RecordManagement.PublicFunctions.Module.Remote.CreateDocumentReview(mainOfficialDocument);
      var reviewTask = RecordManagement.DocumentReviewTasks.As(task);
      
      // Добавить вложения, которые не были добавлены при создании задачи.
      foreach (var attachment in attachments.Where(att => !reviewTask.Attachments.Any(x => Equals(x, att))))
      {
        if (Docflow.PublicFunctions.OfficialDocument.NeedToAttachDocument(attachment, mainOfficialDocument))
          reviewTask.OtherGroup.All.Add(attachment);
      }
      
      reviewTask.Show();
    }

    public virtual bool CanSendForReview(Sungero.Domain.Client.CanExecuteActionArgs e)
    {
      var attachments = _obj.AllAttachments.Select(a => Content.ElectronicDocuments.As(a)).Distinct().ToList();
      var suitableDocuments = Docflow.PublicFunctions.OfficialDocument.GetSuitableDocuments(attachments,
                                                                                            Docflow.OfficialDocuments.Info.Actions.SendForReview);
      return suitableDocuments.Any();
    }

    public virtual void SendForFreeApproval(Sungero.Domain.Client.ExecuteActionArgs e)
    {
      // Проверить заполненность обязательных полей во всех документах комплекта.
      var attachments = _obj.AllAttachments.Select(a => Content.ElectronicDocuments.As(a)).Distinct().ToList();

      if (attachments.Cast<Docflow.IOfficialDocument>().Any(x => Sungero.Docflow.PublicFunctions.OfficialDocument.HasEmptyRequiredProperties(x)))
      {
        e.AddError(VerificationAssignments.Resources.InvalidDocumentWhenSendInWork,
                   _obj.Info.Actions.ShowInvalidDocuments);
        return;
      }
      
      // Определить главный документ.
      var suitableDocuments = Docflow.PublicFunctions.OfficialDocument.GetSuitableDocuments(attachments,
                                                                                            Docflow.OfficialDocuments.Info.Actions.SendForFreeApproval);
      var probablyMainDocument = Content.ElectronicDocuments.As(Functions.Module.GetLeadingDocument(suitableDocuments.Cast<Docflow.IOfficialDocument>().ToList()));
      var mainDocument = Docflow.PublicFunctions.OfficialDocument.ChooseMainDocument(suitableDocuments,
                                                                                     new List<Content.IElectronicDocument> { probablyMainDocument });
      if (mainDocument == null)
        return;
      var mainOfficialDocument = Docflow.OfficialDocuments.As(mainDocument);
      
      // Создать задачу.
      var freeApprovalTask = Sungero.Docflow.PublicFunctions.Module.Remote.CreateFreeApprovalTask(mainOfficialDocument);
      
      // Добавить вложения, которые не были добавлены при создании задачи.
      foreach (var attachment in attachments.Where(att => !freeApprovalTask.Attachments.Any(x => Equals(x, att))))
      {
        if (Docflow.PublicFunctions.OfficialDocument.NeedToAttachDocument(attachment, mainOfficialDocument))
          freeApprovalTask.OtherGroup.All.Add(attachment);
      }
      
      freeApprovalTask.Show();
    }

    public virtual bool CanSendForFreeApproval(Sungero.Domain.Client.CanExecuteActionArgs e)
    {
      var attachments = _obj.AllAttachments.Select(a => Content.ElectronicDocuments.As(a)).Distinct().ToList();
      var suitableDocuments = Docflow.PublicFunctions.OfficialDocument.GetSuitableDocuments(attachments,
                                                                                            Docflow.OfficialDocuments.Info.Actions.SendForFreeApproval);
      return suitableDocuments.Any();
    }

    public virtual void SendForApproval(Sungero.Domain.Client.ExecuteActionArgs e)
    {
      // Проверить заполненность обязательных полей во всех документах комплекта.
      var attachments = _obj.AllAttachments.Select(a => Content.ElectronicDocuments.As(a)).Distinct().ToList();

      if (attachments.Cast<Docflow.IOfficialDocument>().Any(x => Sungero.Docflow.PublicFunctions.OfficialDocument.HasEmptyRequiredProperties(x)))
      {
        e.AddError(VerificationAssignments.Resources.InvalidDocumentWhenSendInWork,
                   _obj.Info.Actions.ShowInvalidDocuments);
        return;
      }
      
      // Определить главный документ.
      var suitableDocuments = Docflow.PublicFunctions.OfficialDocument.GetSuitableDocuments(attachments,
                                                                                            Docflow.OfficialDocuments.Info.Actions.SendForApproval);
      var probablyMainDocument = Content.ElectronicDocuments.As(Functions.Module.GetLeadingDocument(suitableDocuments.Cast<IOfficialDocument>().ToList()));
      var mainDocument = Docflow.PublicFunctions.OfficialDocument.ChooseMainDocument(suitableDocuments,
                                                                                     new List<Content.IElectronicDocument> { probablyMainDocument });
      if (mainDocument == null)
        return;
      var mainOfficialDocument = OfficialDocuments.As(mainDocument);
      
      // Если по главному документу ранее были запущены задачи, то вывести соответствующий диалог.
      if (!Docflow.PublicFunctions.OfficialDocument.NeedCreateApprovalTask(mainOfficialDocument))
        return;
      
      // Проверить наличие регламента.
      var availableApprovalRules = Docflow.PublicFunctions.ApprovalRuleBase.Remote.GetAvailableRulesByDocument(mainOfficialDocument);
      if (availableApprovalRules.Any())
      {
        var approvalTask = Docflow.PublicFunctions.Module.Remote.CreateApprovalTask(mainOfficialDocument);

        // Добавить вложения, которые не были добавлены при создании задачи.
        foreach (var attachment in attachments.Where(att => !approvalTask.Attachments.Any(x => Equals(x, att))))
        {
          if (Docflow.PublicFunctions.OfficialDocument.NeedToAttachDocument(attachment, mainOfficialDocument))
            approvalTask.OtherGroup.All.Add(attachment);
        }
        
        approvalTask.Show();
      }
      else
      {
        // Если по документу нет регламента, вывести сообщение.
        Dialogs.ShowMessage(Docflow.OfficialDocuments.Resources.NoApprovalRuleWarning, MessageType.Warning);
        return;
      }
    }

    public virtual bool CanSendForApproval(Sungero.Domain.Client.CanExecuteActionArgs e)
    {
      var attachments = _obj.AllAttachments.Select(a => Content.ElectronicDocuments.As(a)).Distinct().ToList();
      var suitableDocuments = Docflow.PublicFunctions.OfficialDocument.GetSuitableDocuments(attachments,
                                                                                            Docflow.OfficialDocuments.Info.Actions.SendForApproval);
      return suitableDocuments.Any();
    }

    public virtual void Forward(Sungero.Workflow.Client.ExecuteResultActionArgs e)
    {
      if (_obj.Addressee == null)
      {
        e.AddError(VerificationTasks.Resources.CantRedirectWithoutAddressee);
        e.Cancel();
      }
      
      if (_obj.Addressee == _obj.Performer)
      {
        e.AddError(VerificationTasks.Resources.ApproverAlreadyExistsFormat(_obj.Addressee.Person.ShortName));
        e.Cancel();
      }

      if (_obj.NewDeadline == null)
      {
        e.AddError(VerificationTasks.Resources.CantRedirectWithoutNewDeadline);
        e.Cancel();
      }

      if (!Sungero.Docflow.PublicFunctions.Module.ShowConfirmationDialog(e.Action.ConfirmationMessage, null, null,
                                                                         Constants.VerificationTask.VerificationAssignmentConfirmDialogID.ReAddress))
      {
        e.Cancel();
        return;
      }
      
      // Прокинуть исполнителя в задачу.
      var task = VerificationTasks.As(_obj.Task);
      task.Addressee = _obj.Addressee;
      task.Save();
    }

    public virtual bool CanForward(Sungero.Workflow.Client.CanExecuteResultActionArgs e)
    {
      return _obj.Status == Status.InProcess && Functions.VerificationTask.HasDocumentAndCanRead(VerificationTasks.As(_obj.Task));
    }

    public virtual void Complete(Sungero.Workflow.Client.ExecuteResultActionArgs e)
    {
      var attachments = _obj.AllAttachments
        .Where(a => Content.ElectronicDocuments.Is(a))
        .Select(a => Content.ElectronicDocuments.As(a))
        .Distinct()
        .Cast<Docflow.IOfficialDocument>().ToList();
      var haveErrors = false;
      if (attachments.Any(x => Sungero.Docflow.PublicFunctions.OfficialDocument.HasEmptyRequiredProperties(x)))
      {
        e.AddError(VerificationAssignments.Resources.InvalidDocumentWhenCompleted,
                   _obj.Info.Actions.ShowInvalidDocuments);
        haveErrors = true;
      }

      var session = Functions.RepackingSession.Remote.GetActiveSessionByAssignmentId(_obj.Id);
      if (session != null)
      {
        e.AddError(VerificationAssignments.Resources.CompleteWithActiveRepackingSession);
        haveErrors = true;
      }
      
      if (haveErrors)
        e.Cancel();
    }

    public virtual bool CanComplete(Sungero.Workflow.Client.CanExecuteResultActionArgs e)
    {
      return _obj.Addressee == null;
    }

  }

}