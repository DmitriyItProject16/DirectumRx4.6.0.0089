using System;
using System.Collections.Generic;
using System.Linq;
using DirRX.DCTSIntegration.ProcessingRuleBase;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.DCTSIntegration.Shared
{
  
  partial class ProcessingRuleBaseFunctions
  {
    /// <summary>
    /// Установить обязательность свойств.
    /// </summary>
    public virtual void SetRequiredProperties()
    {
      var isAnyFill = _obj.TaskType.HasValue || !string.IsNullOrEmpty(_obj.TaskSubject) || _obj.Performers.Any();
      var isAssignment = _obj.TaskType.HasValue && _obj.TaskType.Value == DCTSIntegration.ProcessingRuleBase.TaskType.Assignment;
      
      _obj.State.Properties.DeadlineInDays.IsRequired = isAssignment && isAnyFill && !_obj.DeadlineInHours.HasValue;
      _obj.State.Properties.DeadlineInHours.IsRequired = isAssignment && isAnyFill && !_obj.DeadlineInDays.HasValue;
      _obj.State.Properties.TaskType.IsRequired = isAnyFill;
      _obj.State.Properties.TaskSubject.IsRequired = isAnyFill;
      _obj.State.Properties.Performers.IsRequired = isAnyFill;
    }
    
    /// <summary>
    /// Установить видимость свойств.
    /// </summary>
    public virtual void SetPropertiesVisibility()
    {
      var isAssignment = _obj.TaskType.HasValue && _obj.TaskType.Value == DCTSIntegration.ProcessingRuleBase.TaskType.Assignment;
      _obj.State.Properties.DeadlineInDays.IsVisible = isAssignment;
      _obj.State.Properties.DeadlineInHours.IsVisible = isAssignment;
    }
    
    /// <summary>
    /// Установить доступность свойств в зависимости от модуля ввода.
    /// </summary>
    public virtual void SetPropertiesAvailability()
    {
    }
  }
}