using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleIncomingLetter;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration
{
  partial class ProcessingRuleIncomingLetterServerHandlers
  {

    public override void BeforeSave(Sungero.Domain.BeforeSaveEventArgs e)
    {
      base.BeforeSave(e);
      
      // Проверить что выбрано хотябы одно из свойств по заполнению содержания.
      if (string.IsNullOrWhiteSpace(_obj.SubjectPattern) && 
          (_obj.FillFromSubject.HasValue && !_obj.FillFromSubject.Value) && (_obj.FillFromFileName.HasValue && !_obj.FillFromFileName.Value))
        e.AddError(ProcessingRuleIncomingLetters.Resources.RequiredSubjectPattern);
    }

    public override void Created(Sungero.Domain.CreatedEventArgs e)
    {
      base.Created(e);
      
      if (!_obj.State.IsCopied)
      {
        _obj.FillFromSubject = false;
        _obj.FillFromFileName = false;
        _obj.IsAutoCalcCorrespondent = false;
      }
    }
  }

}