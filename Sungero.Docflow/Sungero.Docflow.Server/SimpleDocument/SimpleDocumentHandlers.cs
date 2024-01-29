using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow.SimpleDocument;

namespace Sungero.Docflow
{
  partial class SimpleDocumentConvertingFromServerHandler
  {

    public override void ConvertingFrom(Sungero.Domain.ConvertingFromEventArgs e)
    {
      base.ConvertingFrom(e);
      
      // Очистить статус LifeCycleState, значения которого нет в целевом документе - ограничение платформы.
      if (!PublicFunctions.OfficialDocument.IsSupportedLifeCycleState(_source))
        e.Without(_info.Properties.LifeCycleState);
    }
    
  }
}