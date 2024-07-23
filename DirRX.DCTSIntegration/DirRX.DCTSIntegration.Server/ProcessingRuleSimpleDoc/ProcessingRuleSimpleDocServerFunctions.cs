using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DirRX.DCTSIntegration.ProcessingRuleSimpleDoc;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow;

namespace DirRX.DCTSIntegration.Server
{
  partial class ProcessingRuleSimpleDocFunctions
  {

    /// <summary>
    /// Возвращает тип документа.
    /// </summary>
    /// <returns>Тип документа.</returns>
    [Remote]
    public override Sungero.Docflow.IDocumentType GetDocumentType()
    {
      return DocumentTypes.GetAll().First(_ => _.DocumentTypeGuid == "09584896-81e2-4c83-8f6c-70eb8321e1d0");
    }    
    
    /// <summary>
    /// Создает официальный документ.
    /// </summary>
    /// <returns>Официальный документ.</returns>
    [Remote]
    public override IOfficialDocument CreateDocument()
    {
      return SimpleDocuments.Create();
    }        
  }
}