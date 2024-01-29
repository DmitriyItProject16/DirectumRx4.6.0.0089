using System;
using Sungero.Core;

namespace Sungero.MobileApps.Constants
{
  public static class Module
  {
    /// <summary>
    /// Очередь сообщений для сервера мобильных приложений. 
    /// </summary>
    [Public]
    public const string MobileAppQueueName = "mobile_app_events";
  }
}