using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.MobileApps.MobileAppSetting;

namespace Sungero.MobileApps
{
  partial class MobileAppSettingServerHandlers
  {
    public override void BeforeSave(Sungero.Domain.BeforeSaveEventArgs e)
    {
      var isUnique = Functions.MobileAppSetting.IsUnique(_obj);      
      
      if (!isUnique)
        e.AddError(Sungero.MobileApps.MobileAppSettings.Resources.NonUniqueUserSetting);
    }
    
    public override void AfterDelete(Sungero.Domain.AfterDeleteEventArgs e)
    {
      Functions.MobileAppSetting.SendMobileAppSettingChanged(_obj);
    }
    
    public override void AfterSave(Sungero.Domain.AfterSaveEventArgs e)
    {
      Functions.MobileAppSetting.SendMobileAppSettingChanged(_obj);
    }
  }

}