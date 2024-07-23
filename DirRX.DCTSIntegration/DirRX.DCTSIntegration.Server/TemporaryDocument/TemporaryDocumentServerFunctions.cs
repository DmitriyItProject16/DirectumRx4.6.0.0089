using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using DirRX.DCTSIntegration.TemporaryDocument;

namespace DirRX.DCTSIntegration.Server
{
  partial class TemporaryDocumentFunctions
  {
    /// <summary>
    /// Создать временный документ.
    /// </summary>
    /// <param name="rule">Правило обработки существующего документа.</param>
    /// <param name="doc">Документ.</param>
    /// <param name="note">Примечание.</param>
    /// <returns>Временный документ.</returns>
    [Public, Remote]
    public static DirRX.DCTSIntegration.ITemporaryDocument CreateTemporaryDocument(DirRX.DCTSIntegration.IProcessingRuleExistingDoc rule, Sungero.Docflow.IOfficialDocument doc, string note)
    {
      var temporaryDocument = DirRX.DCTSIntegration.TemporaryDocuments.Create();
      temporaryDocument.Rule = rule;
      temporaryDocument.Entity = doc;
      temporaryDocument.Note = note;
      temporaryDocument.DocId = doc.Id;
      temporaryDocument.Save();
      
      return temporaryDocument;
    }
  }
}