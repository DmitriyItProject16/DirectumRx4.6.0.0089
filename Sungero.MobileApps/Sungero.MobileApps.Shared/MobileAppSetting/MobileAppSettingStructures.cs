using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;

namespace Sungero.MobileApps.Structures.MobileAppSetting
{
  /// <summary>
  /// Информация для обработки запроса обновления настройки мобильного приложения.
  /// </summary>
  [Public]
  partial class MobileAppSettingChangedEventArgs
  {
    public int UserId { get; set; }
  }
  
  /// <summary>
  /// ИД и отображаемое имя папки.
  /// </summary>
  partial class FolderNameWithId
  {
    public int Id { get; set; }
    
    public string FolderName { get; set; }
  }
}