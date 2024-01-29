using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.MobileApps.MobileAppSetting;

namespace Sungero.MobileApps
{
  partial class MobileAppSettingSharedHandlers
  {
    public virtual void EmployeeChanged(Sungero.MobileApps.Shared.MobileAppSettingEmployeeChangedEventArgs e)
    {
      _obj.VisibleFolders.Clear();
    }
  }
}