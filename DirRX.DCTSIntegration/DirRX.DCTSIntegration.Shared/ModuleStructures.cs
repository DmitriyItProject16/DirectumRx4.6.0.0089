using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration.Structures.Module
{

  /// <summary>
  /// Файлы принятые от DCS
  /// </summary>
  [Public]
  partial class DocumentPackage
  {
    /// <summary>
    /// Документ в формате Base64.
    /// </summary>
    public string FileBase64 { get; set; }
    
    /// <summary>
    /// Наименование файла.
    /// </summary>
    public string FileName { get; set; }
  }

}