using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Sungero.Core;
using Sungero.ExchangeCore.Structures.Module;

namespace Sungero.ExchangeCore.Isolated.DpadConverter
{
  public class DpadConverter
  {
    /// <summary>
    /// Получить данные штампа с подписями.
    /// </summary>
    /// <param name="stamp">Штамп.</param>
    /// <returns>Dpad штамп.</returns>
    public virtual NpoComputer.DpadCP.Converter.Stamps.SignatureStampGenerator GetSignatureStamp(Structures.Module.IDpadSignatureStamp stamp)
    {
      var documentInfo = new NpoComputer.DpadCP.Converter.Stamps.DocumentInfo()
      {
        DocumentId = stamp.PageStampInfo.DocumentId,
        Title = stamp.PageStampInfo.Title
      };
      
      var dpadStamp = new NpoComputer.DpadCP.Converter.Stamps.SignatureStampGenerator(documentInfo);
      dpadStamp.StampWidth = stamp.StampWidth;
      
      foreach (var signature in stamp.Signatures)
      {
        var dpadSignature = new NpoComputer.Dpad.Converter.SignatureInfo();
        dpadSignature.CertificateIssuer = signature.CertificateIssuer;
        dpadSignature.CertificateSerialNumber = signature.CertificateSerialNumber;
        dpadSignature.FormalizedPoATitle = signature.FormalizedPoATitle;
        dpadSignature.FormalizedPoAUnifiedNumber = signature.FormalizedPoAUnifiedNumber;
        dpadSignature.OrganizationInfo = signature.OrganizationInfo;
        dpadSignature.PersonInfo = signature.PersonInfo;
        dpadSignature.SignIcon = this.GetDpadSignIcon(signature.SignIcon);
        dpadSignature.SigningDate = signature.SigningDate;
        dpadSignature.Status = signature.Status;
        dpadStamp.Signatures.Add(dpadSignature);
      }
      return dpadStamp;
    }
    
    /// <summary>
    /// Получить иконку подписания Dpad.
    /// </summary>
    /// <param name="signIcon">Значение иконки подписания.</param>
    /// <returns>Иконка подписания Dpad.</returns>
    public virtual NpoComputer.DpadCP.Converter.SignIcon GetDpadSignIcon(int signIcon)
    {
      switch (signIcon)
      {
          case 0: return NpoComputer.DpadCP.Converter.SignIcon.None;
          case 1: return NpoComputer.DpadCP.Converter.SignIcon.Warning;
          case 2: return NpoComputer.DpadCP.Converter.SignIcon.Sign;
          default: throw new Exception(string.Format("Unexpected SignIcon value: {0}.", signIcon));
      }
    }
  }
}