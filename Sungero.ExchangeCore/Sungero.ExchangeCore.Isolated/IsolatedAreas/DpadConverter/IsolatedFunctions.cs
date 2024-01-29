using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Sungero.Core;
using Sungero.ExchangeCore.Structures.Module;

namespace Sungero.ExchangeCore.Isolated.DpadConverter
{
  public class IsolatedFunctions
  {
    /// <summary>
    /// Проставить штамп с подписями на документ.
    /// </summary>
    /// <param name="stream">Документ, на который ставится штамп.</param>
    /// <param name="signatureStamp">Штамп.</param>
    /// <returns>Документ со штампом.</returns>
    [Public]
    public virtual Stream AddSignatureStamp(Stream stream, Structures.Module.IDpadSignatureStamp signatureStamp)
    {
      Logger.DebugFormat("Execute. AddSignatureStamp DocumentId: {0}.", signatureStamp.PageStampInfo.DocumentId);
      var dpadConverter = this.CreateDpadConverter();
      var dpadSignatureStamp = dpadConverter.GetSignatureStamp(signatureStamp);
      NpoComputer.DpadCP.Converter.Stamps.Stamper.AddDirectumStamps(stream, dpadSignatureStamp);
      Logger.DebugFormat("Done. AddSignatureStamp DocumentId: {0}.", signatureStamp.PageStampInfo.DocumentId);
      return stream;
    }
    
    /// <summary>
    /// Поставить временный штамп на документ.
    /// </summary>
    /// <param name="stream">Документ, на который ставится штамп.</param>
    /// <returns>Документ со штампом.</returns>
    [Public]
    public virtual Stream AddTempStamp(Stream stream)
    {
      NpoComputer.DpadCP.Converter.Stamps.Stamper.AddTempStamp(stream);
      return stream;
    }
    
    /// <summary>
    /// Поставить постраничный штамп на документ.
    /// </summary>
    /// <param name="stream">Документ, на который ставится штамп.</param>
    /// <param name="pageStamp">Информация о постраничном штампе.</param>
    /// <returns>Документ со штампом.</returns>
    [Public]
    public virtual Stream AddPaginationStamp(Stream stream, Structures.Module.IDpadPageStampInfo pageStamp)
    {
      Logger.DebugFormat("Execute. AddPaginationStamp DocumentId: {0}.", pageStamp.DocumentId);
      var documentInfo = new NpoComputer.DpadCP.Converter.Stamps.DocumentInfo()
      {
        DocumentId = pageStamp.DocumentId,
        Title = pageStamp.Title
      };
      NpoComputer.DpadCP.Converter.Stamps.Stamper.AddPaginationStamp(stream, documentInfo);
      Logger.DebugFormat("Done. AddPaginationStamp DocumentId: {0}.", pageStamp.DocumentId);
      return stream;
    }
    
    /// <summary>
    /// Сгенерировать PDF на основании переданных титулов документа.
    /// </summary>
    /// <param name="sellerTitle">Титул продавца.</param>
    /// <param name="buyerTitle">Титул покупателя.</param>
    /// <param name="signatureStamp">Штамп с информацией о подписях.</param>
    /// <returns>Поток с содержимым PDF.</returns>
    [Public]
    public virtual Stream GeneratePdfForDocumentTitles(Stream sellerTitle, Stream buyerTitle,
                                                       Structures.Module.IDpadSignatureStamp signatureStamp)
    {
      Logger.DebugFormat("Execute. GeneratePdfForDocumentTitles DocumentId: {0}.", signatureStamp?.PageStampInfo.DocumentId);
      var dpadConverter = this.CreateDpadConverter();
      var dpadSignaturesStamp = signatureStamp != null ? dpadConverter.GetSignatureStamp(signatureStamp) : null;
      NpoComputer.DpadCP.Converter.IFormalizedDocument document;
      
      if (buyerTitle != null)
        document = NpoComputer.DpadCP.Converter.FormalizedDocumentFactory.CreateDocument(sellerTitle, buyerTitle);
      else
        document = NpoComputer.DpadCP.Converter.FormalizedDocumentFactory.CreateDocument(sellerTitle);
      
      System.IO.Stream result = null;
      
      if (dpadSignaturesStamp != null)
        result = document.ConvertToPdfWithStamp(dpadSignaturesStamp);
      else
        result = document.ConvertToPdf();
      
      Logger.DebugFormat("Done. GeneratePdfForDocumentTitles DocumentId: {0}.", signatureStamp?.PageStampInfo.DocumentId);
      return result;
    }
    
    /// <summary>
    /// Создать экземпляр класса Dpad конвертера.
    /// </summary>
    /// <returns>Экземпляр класса Dpad конвертера.</returns>
    public virtual DpadConverter CreateDpadConverter()
    {
      return new DpadConverter();
    }
  }
}