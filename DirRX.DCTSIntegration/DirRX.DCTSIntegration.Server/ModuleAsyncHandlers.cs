using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration.Server
{
  public class ModuleAsyncHandlers
  {

    public virtual void EntryExistingDocument(DirRX.DCTSIntegration.Server.AsyncHandlerInvokeArgs.EntryExistingDocumentInvokeArgs args)
    {
      var tempDocId = args.TempDocId;
      var temporaryDocument = this.GetTemporaryDocument(tempDocId);
      if (temporaryDocument == null)
        throw new ArgumentException(string.Format(DCTSIntegration.Resources.NotFoundTemporaryDoc, tempDocId), "tempDocId");
      
      var doc = temporaryDocument.Entity;
      var rule = temporaryDocument.Rule;
      
      Logger.DebugFormat("TryGrantRightsByRule: start entry existing document {0}, temporary document {1}, rule {2}", doc.Id, tempDocId, rule.Id);
      
      var file = temporaryDocument.Data.Read();
      if (file.Length != 0)
      {
        if (!Locks.GetLockInfo(doc).IsLocked)
        {
          doc.CreateVersionFrom(file, temporaryDocument.Extension);
          doc.LastVersion.Note = temporaryDocument.Note;
          // TODO Если понадобится, то тут можно сделать метод, который позволит реализовать дополнительную функциональность при обработке существующего документа.
          doc.Save();
          
          DirRX.DCTSIntegration.Functions.ProcessingRuleExistingDoc.SendTasks(rule, doc);
          DirRX.DCTSIntegration.TemporaryDocuments.Delete(temporaryDocument);
          Logger.DebugFormat("TryGrantRightsByRule: success entry existing document {0}, temporary document {1}, rule {2}", doc.Id, tempDocId, rule.Id);
        }
        else
        {
          Logger.DebugFormat("TryGrantRightsByRule: cannot entry existing document {0}, temporary document {1}, rule {2}", doc.Id, tempDocId, rule.Id);
          args.Retry = true;
        }
      }
    }
    
    public virtual DirRX.DCTSIntegration.ITemporaryDocument GetTemporaryDocument(int tempDocId)
    {
      return DirRX.DCTSIntegration.TemporaryDocuments.GetAll().Where(n => n.Id == tempDocId).FirstOrDefault();
    }

  }
}