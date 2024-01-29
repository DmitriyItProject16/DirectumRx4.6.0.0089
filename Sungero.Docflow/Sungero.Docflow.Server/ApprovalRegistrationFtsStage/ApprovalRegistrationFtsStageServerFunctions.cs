using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow.ApprovalRegistrationFtsStage;

namespace Sungero.Docflow.Server
{
  partial class ApprovalRegistrationFtsStageFunctions
  {
    public override Sungero.Docflow.Structures.ApprovalFunctionStageBase.ExecutionResult Execute(IApprovalTask approvalTask)
    {
      Logger.DebugFormat("ApprovalRegistrationFtsStage. Start execute registration document in fts for task id: {0}, start id: {1}.", approvalTask.Id, approvalTask.StartId);
      
      var result = base.Execute(approvalTask);
      
      var mainDocument = approvalTask.DocumentGroup.OfficialDocuments.SingleOrDefault();
      if (mainDocument == null)
      {
        Logger.ErrorFormat("ApprovalRegistrationFtsStage. Primary document not found. task id: {0}, start id: {1}", approvalTask.Id, approvalTask.StartId);
        return this.GetErrorResult(Sungero.Docflow.Resources.PrimaryDocumentNotFoundError);
      }
      
      var errorMessage = this.GetValidationErrorRegistrationFts(mainDocument);
      if (!string.IsNullOrEmpty(errorMessage))
        return this.GetErrorResult(errorMessage);
      
      try
      {
        var sendingError = this.GetSendingToFtsError(mainDocument, approvalTask.Id);
        if (!string.IsNullOrEmpty(sendingError))
          return this.GetErrorResult(sendingError);
      }
      catch (Exception ex)
      {
        Logger.ErrorFormat("ApprovalRegistrationFtsStage. Registration in fts error: {0}. Document Id {1}", ex, ex.Message, mainDocument.Id);
        return this.GetRetryResult(string.Empty);
      }
      
      Logger.DebugFormat("ApprovalRegistrationFtsStage. Done execute registration in fts for task id {0}, document id: {1}", approvalTask.Id, mainDocument.Id);
      
      return result;
    }
    
    /// <summary>
    /// Проверить возможность регистрации в реестре ФНС.
    /// </summary>
    /// <param name="mainDocument">Документ.</param>
    /// <returns>Сообщение ошибки регистрации.</returns>
    public virtual string GetValidationErrorRegistrationFts(IOfficialDocument mainDocument)
    {
      if (!FormalizedPowerOfAttorneys.Is(mainDocument) && !PowerOfAttorneyRevocations.Is(mainDocument))
      {
        Logger.DebugFormat("ApprovalRegistrationFtsStage. Can register in fts only fpoa or revocation. Document Id {0}", mainDocument.Id);
        return Sungero.Docflow.ApprovalRegistrationFtsStages.Resources.RegistrationFtsDocumentKindError;
      }
      
      if (!Sungero.PowerOfAttorneyCore.PublicFunctions.Module.HasPowerOfAttorneyServiceConnection(mainDocument.BusinessUnit))
      {
        Logger.DebugFormat("ApprovalRegistrationFtsStage. Power of attorney service connection not found. BusinessUnit Id {0}", mainDocument.BusinessUnit.Id);
        return Sungero.Docflow.ApprovalRegistrationFtsStages.Resources.RegistrationFtsConnectionNotFoundError;
      }
      
      if (!mainDocument.HasVersions)
      {
        Logger.DebugFormat("ApprovalRegistrationFtsStage. Version is not found. Document Id {0}", mainDocument.Id);
        return Sungero.Docflow.ApprovalRegistrationFtsStages.Resources.RegistrationFtsVersionNotFoundError;
      }
      
      var signature = Functions.OfficialDocument.GetSignatureFromOurSignatory(mainDocument, mainDocument.LastVersion.Id);
      if (signature == null)
      {
        Logger.DebugFormat("ApprovalRegistrationFtsStage. Signature is not found. Document Id {0}", mainDocument.Id);
        return Sungero.Docflow.ApprovalRegistrationFtsStages.Resources.RegistrationFtsSignatureError;
      }
      
      if (!signature.IsValid)
      {
        Logger.DebugFormat("ApprovalRegistrationFtsStage. Signature is not valid. Document Id {0}", mainDocument.Id);
        return Sungero.Docflow.ApprovalRegistrationFtsStages.Resources.RegistrationFtsSignatureError;
      }
      
      var fpoa = FormalizedPowerOfAttorneys.As(mainDocument);
      if (fpoa != null && fpoa.FtsListState == Docflow.FormalizedPowerOfAttorney.FtsListState.Registered)
      {
        Logger.DebugFormat("ApprovalRegistrationFtsStage. Formalized power of attorney is already registered. Document Id {0}", fpoa.Id);
        return Sungero.Docflow.FormalizedPowerOfAttorneys.Resources.ConflictDifferentSignatureErrorMessage;
      }
      
      var revocation = PowerOfAttorneyRevocations.As(mainDocument);
      if (revocation != null && revocation.FtsListState == Docflow.PowerOfAttorneyRevocation.FtsListState.Registered)
      {
        Logger.DebugFormat("ApprovalRegistrationFtsStage. Power of attorney revocation is already registered. Document Id {0}", revocation.Id);
        return Sungero.Docflow.PowerOfAttorneyRevocations.Resources.RepeatedRegistrationErrorMessage;
      }
      
      return string.Empty;
    }
    
    /// <summary>
    /// Получить результат отправки эл. доверенности/заявления на отзыв в реестр ФНС.
    /// </summary>
    /// <param name="mainDocument">Документ.</param>
    /// <param name="approvalTaskId">ИД задачи на согласование.</param>
    /// <returns>Результат отправки эл. доверенности/заявления на отзыв в реестр ФНС.</returns>
    public virtual string GetSendingToFtsError(IOfficialDocument mainDocument, int approvalTaskId)
    {
      Sungero.PowerOfAttorneyCore.Structures.Module.IResponseResult sendingResult = null;
      
      if (FormalizedPowerOfAttorneys.Is(mainDocument))
      {
        Logger.DebugFormat("ApprovalRegistrationFtsStage. Send fpoa to registration in fts. Document Id {0}", mainDocument.Id);
        var formalizedPoa = FormalizedPowerOfAttorneys.As(mainDocument);
        sendingResult = Sungero.Docflow.PublicFunctions.FormalizedPowerOfAttorney.Remote.RegisterFormalizedPowerOfAttorneyWithService(formalizedPoa, approvalTaskId);
        if (!string.IsNullOrEmpty(sendingResult.ErrorCode))
          return formalizedPoa.FtsRejectReason;
      }
      
      if (PowerOfAttorneyRevocations.Is(mainDocument))
      {
        Logger.DebugFormat("ApprovalRegistrationFtsStage. Send revocation to registration in fts. Document Id {0}", mainDocument.Id);
        var poaRevocation = PowerOfAttorneyRevocations.As(mainDocument);
        if (poaRevocation.FormalizedPowerOfAttorney.FtsListState == Docflow.FormalizedPowerOfAttorney.FtsListState.Revoked)
          return Sungero.Docflow.PowerOfAttorneyRevocations.Resources.SendRevocationToRevokedFPoAError;
        sendingResult = Sungero.Docflow.PublicFunctions.PowerOfAttorneyRevocation.Remote.RegisterRevocationWithService(poaRevocation, approvalTaskId);
        if (!string.IsNullOrEmpty(sendingResult.ErrorCode))
          return poaRevocation.FtsRejectReason;
      }
      
      return string.Empty;
    }
    
    public override Sungero.Docflow.Structures.ApprovalFunctionStageBase.ExecutionResult CheckCompletionState(IApprovalTask approvalTask)
    {
      var result = base.CheckCompletionState(approvalTask);
      var mainDocument = approvalTask.DocumentGroup.OfficialDocuments.SingleOrDefault();
      
      if (FormalizedPowerOfAttorneys.Is(mainDocument) &&
          FormalizedPowerOfAttorneys.As(mainDocument).FtsListState == Sungero.Docflow.FormalizedPowerOfAttorney.FtsListState.Revoked)
        return this.GetErrorResult(FormalizedPowerOfAttorneys.Resources.FormalizedPowerOfAttorneySendForRegistrationError);
      
      if (!this.HasAnswerFromFts(mainDocument))
      {
        Logger.DebugFormat("ApprovalRegistrationFtsStage. Retry check registration. Approval task Id {0}, Document Id {1}.",
                           approvalTask.Id, mainDocument.Id);
        return this.GetRetryResult(string.Empty);
      }
      
      var ftsRegistrationError = this.GetFtsRegistrationError(mainDocument);
      if (string.IsNullOrEmpty(ftsRegistrationError))
      {
        Logger.DebugFormat("ApprovalRegistrationFtsStage. Registration is done. Approval task Id {0}, Document Id {1}.",
                           approvalTask.Id, mainDocument.Id);
        return this.GetSuccessResult();
      }
      else
      {
        Logger.DebugFormat("ApprovalRegistrationFtsStage. Registration error: {0}, Approval task Id {1}, Document Id {2}.",
                           ftsRegistrationError, approvalTask.Id, mainDocument.Id);
        return this.GetErrorResult(ftsRegistrationError);
      }
    }
    
    /// <summary>
    /// Проверить, что сервис дал ответ по регистрации документа в реестре ФНС.
    /// </summary>
    /// <param name="mainDocument">Документ.</param>
    /// <returns>True - статус регистрации в реестре ФНС "Зарегистрирован"/"Ошибка регистрации", иначе - false.</returns>
    public virtual bool HasAnswerFromFts(IOfficialDocument mainDocument)
    {
      var hasAnswerFromFts = false;
      if (FormalizedPowerOfAttorneys.Is(mainDocument))
      {
        var ftsListState = FormalizedPowerOfAttorneys.As(mainDocument).FtsListState;
        hasAnswerFromFts = ftsListState == Docflow.FormalizedPowerOfAttorney.FtsListState.Registered ||
          ftsListState == Docflow.FormalizedPowerOfAttorney.FtsListState.Rejected;
      }
      if (PowerOfAttorneyRevocations.Is(mainDocument))
      {
        var ftsListState = PowerOfAttorneyRevocations.As(mainDocument).FtsListState;
        hasAnswerFromFts = ftsListState == Docflow.PowerOfAttorneyRevocation.FtsListState.Registered ||
          ftsListState == Docflow.PowerOfAttorneyRevocation.FtsListState.Rejected;
      }
      
      return hasAnswerFromFts;
    }
    
    /// <summary>
    /// Получить ошибку регистрации в ФНС эл. доверенности/заявления на отзыв.
    /// </summary>
    /// <param name="mainDocument">Документ.</param>
    /// <returns>Ошибка регистрации в ФНС.</returns>
    public virtual string GetFtsRegistrationError(IOfficialDocument mainDocument)
    {
      var ftsRejectReason = string.Empty;
      if (FormalizedPowerOfAttorneys.Is(mainDocument))
        ftsRejectReason = FormalizedPowerOfAttorneys.As(mainDocument).FtsRejectReason;
      if (PowerOfAttorneyRevocations.Is(mainDocument))
        ftsRejectReason = PowerOfAttorneyRevocations.As(mainDocument).FtsRejectReason;
      
      return ftsRejectReason;
    }
  }
}