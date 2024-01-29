using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Domain.Initialization;

namespace Sungero.PowerOfAttorneyCore.Server
{
  public partial class ModuleInitializer
  {

    public override void Initializing(Sungero.Domain.ModuleInitializingEventArgs e)
    {
      InitializationLogger.Debug("Init: Grant rights on databooks to all users.");
      PowerOfAttorneyCore.PowerOfAttorneyServiceConnections.AccessRights.Grant(Roles.AllUsers, DefaultAccessRightsTypes.Read);
      PowerOfAttorneyCore.PowerOfAttorneyServiceConnections.AccessRights.Save();
    }
  }
}
