using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Commons.EntityRecognitionInfo;
using Sungero.Core;
using Sungero.CoreEntities;

namespace Sungero.Commons
{
  partial class EntityRecognitionInfoClientHandlers
  {

    public override void Refresh(Sungero.Presentation.FormRefreshEventArgs e)
    {
      var entityParams = ((Domain.Shared.IExtendedEntity)_obj).Params;
      if (!entityParams.ContainsKey(PublicConstants.EntityRecognitionInfo.CanChangeTrainingStatusParamName))
      {
        var isUserAdministrator = Users.Current.IncludedIn(Roles.Administrators);
        if (isUserAdministrator)
          _obj.State.Properties.ClassifierTrainingStatus.IsEnabled = true;
        
        entityParams[PublicConstants.EntityRecognitionInfo.CanChangeTrainingStatusParamName] = isUserAdministrator;
      }
      else
      {
        object canChangeTrainingStatus = null;
        if (entityParams.TryGetValue(PublicConstants.EntityRecognitionInfo.CanChangeTrainingStatusParamName, out canChangeTrainingStatus))
        {
          if ((bool)canChangeTrainingStatus)
            _obj.State.Properties.ClassifierTrainingStatus.IsEnabled = true;
        }
      }
    }

  }
}