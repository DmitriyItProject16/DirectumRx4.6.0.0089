using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;

namespace Sungero.PowerOfAttorneyCore.Structures.Module
{
  /// <summary>
  /// Информация об отозванной доверенности.
  /// </summary>
  [Public]
  partial class PowerOfAttorneyRevocationInfo
  {
    /// <summary>
    /// Дата подписания отзыва.
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Причина отзыва.
    /// </summary>
    public string Reason { get; set; }
    
    /// <summary>
    /// Тип ошибки.
    /// </summary>
    public string ErrorType { get; set; }
    
    /// <summary>
    /// Код ошибки.
    /// </summary>
    public string ErrorCode { get; set; }
  }
  
  /// <summary>
  /// Состояние валидации доверенности.
  /// </summary>
  [Public]
  partial class PowerOfAttorneyValidationState
  {
    /// <summary>
    /// Состояние операции валидации.
    /// </summary>
    public string OperationStatus { get; set; }

    /// <summary>
    /// Результат валидации.
    /// </summary>
    public string Result { get; set; }
    
    /// <summary>
    /// Код ответа запроса.
    /// </summary>
    public int StatusCode { get; set; }
    
    /// <summary>
    /// Ошибки валидации.
    /// </summary>
    public List<Sungero.PowerOfAttorneyCore.Structures.Module.IValidationOperationError> Errors { get; set; }
  }
  
  /// <summary>
  /// Состояние валидации доверенности.
  /// </summary>
  [Public]
  partial class ValidationOperationError
  {
    /// <summary>
    /// Тип ошибки.
    /// </summary>
    public string Type { get; set; }
    
    /// <summary>
    /// Код ошибки.
    /// </summary>
    public string Code { get; set; }
    
    /// <summary>
    /// Сообщение.
    /// </summary>
    public string Message { get; set; }
  }
  
  /// <summary>
  /// Результат отправки запроса в ФНС.
  /// </summary>
  [Public]
  partial class ResponseResult
  {
    /// <summary>
    /// ИД операции в сервисе доверенностей.
    /// </summary>
    public string OperationId { get; set; }

    /// <summary>
    /// Тип ошибки.
    /// </summary>
    public string ErrorType { get; set; }
    
    /// <summary>
    /// Код ошибки.
    /// </summary>
    public string ErrorCode { get; set; }
    
    /// <summary>
    /// Код ответа запроса.
    /// </summary>
    public int StatusCode { get; set; }
    
    /// <summary>
    /// Статус регистрации.
    /// </summary>
    public string State { get; set; }
    
    /// <summary>
    /// Элемент очереди на регистрацию в ФНС.
    /// </summary>
    public Sungero.Docflow.IPowerOfAttorneyQueueItem QueueItem { get; set; }
  }
  
  /// <summary>
  /// Представитель МЧД.
  /// </summary>
  [Public]
  partial class Agent
  {
    /// <summary>
    /// Имя.
    /// </summary>
    public string Name { get; set; }
    
    /// <summary>
    /// Фамилия.
    /// </summary>
    public string Surname { get; set; }
    
    /// <summary>
    /// Отчество.
    /// </summary>
    public string Middlename { get; set; }

    /// <summary>
    /// СНИЛС.
    /// </summary>
    public string INILA { get; set; }

    /// <summary>
    /// ИНН.
    /// </summary>
    public string TIN { get; set; }
    
    /// <summary>
    /// ИНН юр. лица.
    /// </summary>
    public string TINUl { get; set; }
    
    /// <summary>
    /// КПП.
    /// </summary>
    public string TRRC { get; set; }
  }
  
}