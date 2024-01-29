using System;
using Sungero.Core;

namespace Sungero.PowerOfAttorneyCore.Constants
{
  public static class Module
  {
    /// <summary>
    /// Версия API сервиса доверенностей Контур.
    /// </summary>
    public const string KonturPowerOfAttorneyServiceVersion = "v1";
    
    /// <summary>
    /// Таймауты на регистрацию доверенности и отзыва.
    /// </summary>
    public const int PoARegisterOperationTimeout = 5;
    public const int PoARegisterRequestTimeout = 5;
    
    /// <summary>
    /// Таймауты на проверку состояния.
    /// </summary>
    public const int CheckPoAStateOperationTimeout = 10;
    public const int CheckPoAStateRequestTimeout = 10;
    
    /// <summary>
    /// Таймауты на длительные/асинхронные Http операции по доверенности.
    /// </summary>
    public const int PoAAsyncActionsOperationTimeout = 60;
    public const int PoAAsyncActionsRequestTimeout = 10;
    
    /// <summary>
    /// Ошибки при работе с сервисом доверенностей.
    /// </summary>
    [Sungero.Core.Public]
    public static class PowerOfAttorneyServiceErrors
    {
      // Ошибка в настройке подключения к сервису доверенностей.
      [Sungero.Core.Public]
      public const string ConnectionError = "ConnectionError";
      
      // Ошибки соединения (408, 502, 503, 504).
      [Sungero.Core.Public]
      public const string NetworkError = "NetworkError";
      
      // Полученные данные не актуальны (не удалось получить ответ от сервиса за таймаут).
      [Sungero.Core.Public]
      public const string StateIsOutdated = "stateIsOutdated";
      
      // Полученные данные не актуальны (не удалось найти доверенность в сервисе за таймаут).
      [Sungero.Core.Public]
      public const string PoANotFound = "poaNotFound";
      
      // Не удалось найти доверенность в реестре ФНС.
      [Sungero.Core.Public]
      public const string PoaIsNotValidYet = "poaIsNotValidYet";
      
      // В результате выполнения запроса возникли ошибки.
      [Sungero.Core.Public]
      public const string ProcessingError = "ProcessingError";
    }
    
    /// <summary>
    /// В результате выполнения запроса response вернулся null.
    /// </summary>
    public const string NullResponseError = "NullResponseError";
    
    /// <summary>
    /// Статус обработки запроса - ошибка.
    /// </summary>
    public const string ErrorOperationStatus = "error";
  }
}