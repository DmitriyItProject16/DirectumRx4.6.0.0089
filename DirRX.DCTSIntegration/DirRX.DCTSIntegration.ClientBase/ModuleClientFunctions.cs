using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using System.Reflection;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Docflow;

namespace DirRX.DCTSIntegration.Client
{
  public class ModuleFunctions
  {
    
    /// <summary>
    /// Отобразить информацию, используя Reflection.
    /// </summary>
    public virtual void Reflection()
    {
      var type = Type.GetType("Sungero.Docflow.IOfficialDocument, Sungero.Domain.Interfaces");
      if (type != null)
      {
        var list = new List<string>();
        foreach (var prop in type.GetProperties())
        {
          list.Add(string.Format("{0}: {1}", prop.Name, prop.PropertyType));
        }
        Dialogs.ShowMessage(string.Join(Environment.NewLine, list.ToArray()));
      }
    }
    
    /// <summary>
    /// Отладка почтовых отправлений.
    /// </summary>
    public virtual void Function2()
    {
      //Functions.Module.Remote.ProcessingDCTSKit("MailToSenderLine", @"C:\tmp\Mail\InstanceInfos.xml", @"C:\tmp\Mail\DeviceInfo.xml", @"C:\tmp\Mail\InputFiles.xml", @"C:\tmp\Mail\InputFiles");
    }
    
    /// <summary>
    /// Отладка файловой системы.
    /// </summary>
    public virtual void Function1()
    {
      /*Functions.Module.Remote.ProcessingDCTSKit("FileSystemToSenderLine",
                     @"C:\tmp\Senders\AnyToProgramSender_Temp\12020e15-2bf5-43d6-8779-9485c7852b8e\InstanceInfos.xml",
                     @"C:\tmp\Senders\AnyToProgramSender_Temp\12020e15-2bf5-43d6-8779-9485c7852b8e\DeviceInfo.xml",
                     @"C:\tmp\Senders\AnyToProgramSender_Temp\12020e15-2bf5-43d6-8779-9485c7852b8e\InputFiles.xml",
                     @"C:\tmp\Senders\AnyToProgramSender_Temp\12020e15-2bf5-43d6-8779-9485c7852b8e\InputFiles");
       */
    }
  }
}