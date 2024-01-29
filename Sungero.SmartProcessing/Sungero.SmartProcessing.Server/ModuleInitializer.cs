using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Domain.Initialization;

namespace Sungero.SmartProcessing.Server
{
  public partial class ModuleInitializer
  {

    public override void Initializing(Sungero.Domain.ModuleInitializingEventArgs e)
    {
      // Выдача прав всем пользователям.
      var allUsers = Roles.AllUsers;
      if (allUsers != null)
      {
        // Справочники.
        InitializationLogger.Debug("Init: Grant rights on databooks to all users.");
        GrantRightsOnDatabooks(allUsers);
      }
      
      AddLowerFMeasureLimitParam();
    }
    
    /// <summary>
    /// Выдать права всем пользователям на справочники.
    /// </summary>
    /// <param name="allUsers">Группа "Все пользователи".</param>
    public static void GrantRightsOnDatabooks(IRole allUsers)
    {
      InitializationLogger.Debug("Init: Grant rights on databooks to all users.");
      SmartProcessing.RepackingSessions.AccessRights.Grant(allUsers, DefaultAccessRightsTypes.Change);
      SmartProcessing.RepackingSessions.AccessRights.Save();
    }
    
    /// <summary>
    /// Добавить в таблицу параметров минимальное значение F1-меры для публикации модели.
    /// </summary>
    public static void AddLowerFMeasureLimitParam()
    {
      if (Docflow.PublicFunctions.Module.GetDocflowParamsValue(Constants.Module.LowerFMeasureLimitParamName) == null)
        Docflow.PublicFunctions.Module.InsertDocflowParam(Constants.Module.LowerFMeasureLimitParamName,
                                                          Constants.Module.DefaultLowerFMeasureLimit.ToString());
    }
    
  }

}
