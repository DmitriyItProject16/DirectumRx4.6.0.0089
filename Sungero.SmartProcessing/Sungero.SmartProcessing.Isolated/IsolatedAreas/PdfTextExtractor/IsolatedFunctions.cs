using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sungero.Core;
using Sungero.SmartProcessing.Structures.Module;

namespace Sungero.SmartProcessing.Isolated.PdfTextExtractor
{
  public class IsolatedFunctions
  {
    /// <summary>
    /// Получить текст из метаданных.
    /// </summary>
    /// <param name="documentBody">Тело документа.</param>
    /// <returns>Метаданные.</returns>
    [Public]
    public virtual IExtractedText GetTextFromMetadata(Stream documentBody)
    {
      var extractedText = ExtractedText.Create();
      try
      {
        var pdfTextExtractor = new PdfTextExtractor();
        extractedText.Text = pdfTextExtractor.GetTextFromMetadata(documentBody);
      }
      catch (Exception ex)
      {
        extractedText.ErrorMessage = "Cannot extract text";
        Logger.Error("Cannot extract text", ex);
      }
      
      return extractedText;
    }
  }
}