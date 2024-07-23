using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Domain.Initialization;
using Sungero.Docflow;
using Sungero.Parties;
using Sungero.Company;
using Init = DirRX.DCTSIntegration.Constants.Module.Initialize;

namespace DirRX.DCTSIntegration.Server
{
  public partial class ModuleInitializer
  {

    public override void Initializing(Sungero.Domain.ModuleInitializingEventArgs e)
    {
      CreateRules();
    }
    
    #region Создание правил
    
    public void CreateRules()
    {
      var department = GetDepartment(DirRX.DCTSIntegration.Resources.DepartmentSampleName);
      if (department == null)
        department = CreateDepartment(DirRX.DCTSIntegration.Resources.DepartmentSampleName);
      
      var incomingLetter = Sungero.Docflow.PublicFunctions.DocumentKind.GetNativeDocumentKind(Init.IncomingLetterKind);
      var simpleDoc = Sungero.Docflow.PublicFunctions.DocumentKind.GetNativeDocumentKind(Init.SimpleDocumentKind);
      var correspondent = CreateCompany(DirRX.DCTSIntegration.Resources.Correspondent);
      
      InitializationLogger.Debug("Create rule: IncommingLetter");
      CreateRuleIncommingLetter(DirRX.DCTSIntegration.Resources.IncommingLetterRuleName, DirRX.DCTSIntegration.Resources.MailLineToSystem,
                                DirRX.DCTSIntegration.ProcessingRuleBase.CaptureService.Mail, incomingLetter, true, correspondent, true, department);
      
      InitializationLogger.Debug("Create rule: Fax");
      CreateRuleSimpleDoc(DirRX.DCTSIntegration.Resources.FaxRuleName, DirRX.DCTSIntegration.Resources.FaxLineToSystem,
                          DirRX.DCTSIntegration.ProcessingRuleBase.CaptureService.FileSystem, simpleDoc, DirRX.DCTSIntegration.Resources.NamePattern, false);

      InitializationLogger.Debug("Create rule: Scan");
      CreateRuleSimpleDoc(DirRX.DCTSIntegration.Resources.ScanRuleName, DirRX.DCTSIntegration.Resources.ScanLineToSystem, DirRX.DCTSIntegration.ProcessingRuleBase.CaptureService.FileSystem,
                          simpleDoc, string.Empty, true);
      
      InitializationLogger.Debug("Create rule: Return");
      CreateRuleExistDoc(DirRX.DCTSIntegration.Resources.ReturnRuleName, DirRX.DCTSIntegration.Resources.ScanLineToSystem, DirRX.DCTSIntegration.ProcessingRuleBase.CaptureService.FileSystem, true);
    }
    
    /// <summary>
    /// Создать правило обработки входящего письма.
    /// </summary>
    /// <param name="name">Имя.</param>
    /// <param name="line">Линия.</param>
    /// <param name="captureService">Модуль ввода.</param>
    /// <param name="documentKind">Вид документа.</param>
    /// <param name="fillFromSubject">Заполнять из темы письма.</param>
    /// <param name="correspondent">Корреспондент.</param>
    /// <param name="isAutoCalcCor">Определять по эл. почте.</param>
    /// <param name="department">Подразделение.</param>
    /// <returns>Правило.</returns>
    private IProcessingRuleIncomingLetter CreateRuleIncommingLetter(string name, string line, Enumeration? captureService, IDocumentKind documentKind, bool? fillFromSubject,
                                                                    ICounterparty correspondent, bool? isAutoCalcCor, IDepartment department)
    {
      var rule = ProcessingRuleIncomingLetters.GetAll().FirstOrDefault(p => p.Name == name);
      try
      {
        if (rule == null)
        {
          rule = ProcessingRuleIncomingLetters.Create();
          rule.Name = name;
          rule.Line = line;
          rule.CaptureService = captureService;
          rule.DocumentKind = documentKind;
          rule.FillFromSubject = fillFromSubject;
          rule.Correspondent = correspondent;
          rule.IsAutoCalcCorrespondent = isAutoCalcCor;
          rule.Department = department;
          rule.Save();
          InitializationLogger.DebugFormat("ProcessingRuleIncomingLetter \"{0}\" created", name);
        }
      }
      catch (Exception ex)
      {
        InitializationLogger.DebugFormat("Error: ProcessingRuleIncomingLetter \"{0}\" {1}{2}", name, Environment.NewLine, ex.Message);
      }
      return rule;
    }
    
    /// <summary>
    /// Создать правило обработки простого документа.
    /// </summary>
    /// <param name="name">Имя.</param>
    /// <param name="line">Линия.</param>
    /// <param name="captureService">Модуль ввода.</param>
    /// <param name="documentKind">Вид документа.</param>
    /// <param name="namePattern">Наименование образца для заполнения.</param>
    /// <param name="fillNameFromFileName">Заполнять из имени файла.</param>
    /// <returns>Правило.</returns>
    private IProcessingRuleSimpleDoc CreateRuleSimpleDoc(string name, string line, Enumeration? captureService, IDocumentKind documentKind, string namePattern, bool? fillNameFromFileName)
    {
      var rule = ProcessingRuleSimpleDocs.GetAll(p => p.Name == name).FirstOrDefault();
      try
      {
        if (rule == null)
        {
          rule = ProcessingRuleSimpleDocs.Create();
          rule.Name = name;
          rule.Line = line;
          rule.CaptureService = captureService;
          rule.DocumentKind = documentKind;
          rule.FillNameFromFileName = fillNameFromFileName;
          rule.NamePattern = namePattern;
          rule.Save();
          InitializationLogger.DebugFormat("ProcessingRuleSimpleDocs \"{0}\" created", name);
        }
      }
      catch (Exception ex)
      {
        InitializationLogger.DebugFormat("Error: ProcessingRuleSimpleDocs \"{0}\" {1}{2}", name, Environment.NewLine, ex.Message);
      }
      return rule;
    }
    
    /// <summary>
    /// Создать правило "Возврат документов".
    /// </summary>
    /// <param name="name">Имя.</param>
    /// <param name="line">Линия.</param>
    /// <param name="captureService">Модуль ввода.</param>
    /// <param name="sendNotice">Отравлять уведомления.</param>
    /// <returns>Правило.</returns>
    private IProcessingRuleExistingDoc CreateRuleExistDoc(string name, string line, Enumeration? captureService, bool? sendNotice)
    {
      
      var rule = ProcessingRuleExistingDocs.GetAll(p => p.Name == name).FirstOrDefault();
      try
      {
        if (rule == null)
        {
          rule = ProcessingRuleExistingDocs.Create();
          rule.Name = name;
          rule.Line = line;
          rule.CaptureService = captureService;
          rule.SendNotice = sendNotice;
          rule.Save();
          InitializationLogger.DebugFormat("ProcessingRuleExistingDoc \"{0}\" created", name);
        }
      }
      catch (Exception ex)
      {
        InitializationLogger.DebugFormat("Error: ProcessingRuleExistingDoc \"{0}\" {1}{2}", name, Environment.NewLine, ex.Message);
      }

      return rule;
    }
    
    #endregion
    
    #region Получение вспомогательных записей
    
    /// <summary>
    /// Получить подразделение по наименованию.
    /// </summary>
    /// <param name="name">Наименование.</param>
    /// <returns>Подразделение.</returns>
    public IDepartment GetDepartment(string name)
    {
      return Departments.GetAll(d => d.Name == name).FirstOrDefault();
    }
    
    /// <summary>
    /// Создать подразделение по наименованию.
    /// </summary>
    /// <param name="name">Наименование.</param>
    /// <returns>Подразделение.</returns>
    public IDepartment CreateDepartment(string name)
    {
      var department = Departments.Null;
      try
      {
        var businessUnit = BusinessUnits.GetAll().FirstOrDefault();
        department = Departments.Create();
        department.Name = name;
        department.BusinessUnit = businessUnit;
        department.Save();
      }
      catch (Exception ex)
      {
        InitializationLogger.DebugFormat("Error: CreateDepartment \"{0}\" {1}{2}", name, Environment.NewLine, ex.Message);
      }
      return department;
    }
    
    /// <summary>
    /// Создать компанию.
    /// </summary>
    /// <param name="name">Наименование.</param>
    /// <returns>Компания.</returns>
    public ICompany CreateCompany(string name)
    {
      var company = Companies.GetAll(c => c.Name == name).FirstOrDefault();
      try
      {
        if (company == null)
          company = Companies.Create();
        else
          return company;
        
        company.Name = name;
        company.Save();
      }
      catch (Exception ex)
      {
        InitializationLogger.DebugFormat("Error: company \"{0}\" {1}", name, ex.Message);
      }
      return company;
    }
    
    #endregion
    
  }
}
