using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Aspose.Pdf;
using Aspose.Pdf.Text;
using Newtonsoft.Json.Linq;
using PdfSharpCore.Pdf;
using Sungero.Core;

namespace Sungero.SmartProcessing.Isolated.PdfTextExtractor
{
  public class PdfTextExtractor
  {
    /// <summary>
    /// Получить текст из метаданных страниц pdf-документа.
    /// </summary>
    /// <param name="documentBody">Тело документа.</param>
    /// <returns>Метаданные.</returns>
    public string GetTextFromMetadata(Stream documentBody)
    {
      var docText = new System.Text.StringBuilder();
      using (var document = PdfSharpCore.Pdf.IO.PdfReader.Open(documentBody))
      {
        foreach (var page in document.Pages)
        {
          var pieceInfo = page.Elements.GetDictionary("/PieceInfo");
          // Метаданные в документе, который пришел из Ario должны быть на каждой странице.
          if (pieceInfo == null)
            return string.Empty;
          
          var ario = pieceInfo?.Elements.GetDictionary("/Ario");

          var privateData = ario?.Elements.GetDictionary("/Private");
          var value = (privateData?.Elements.GetReference("/PageData").Value as PdfSharpCore.Pdf.PdfDictionary)?.Stream?.ToString();
          if (!string.IsNullOrEmpty(value))
          {
            var bytes = new PdfSharpCore.Pdf.Internal.RawEncoding().GetBytes(value);
            var json = JObject.Parse(Encoding.UTF8.GetString(bytes));
            var text = json["Text"]?.ToString();
            docText.AppendLine(text);
          }
        }
      }

      return docText.ToString();
    }
    
  }
}