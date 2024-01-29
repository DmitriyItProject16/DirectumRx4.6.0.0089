using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;

namespace Sungero.Docflow
{
  partial class DocumentUsageReportServerHandlers
  {

    public override void BeforeExecute(Sungero.Reporting.Server.BeforeExecuteEventArgs e)
    {
      var reportSessionId = System.Guid.NewGuid().ToString();
      DocumentUsageReport.ReportSessionId = reportSessionId;
      DocumentUsageReport.ReportDate = Calendar.Now;
      DocumentUsageReport.DepartmentId = DocumentUsageReport.Department != null ? DocumentUsageReport.Department.Id : 0;
      
      Functions.Module.ExecuteSQLCommandFormat(Queries.Module.DropTable, new[] { Constants.DocumentUsageReport.EmployeeTableName });
      Functions.Module.ExecuteSQLCommandFormat(Queries.DocumentUsageReport.CreateEmployeeTable, new[] { Constants.DocumentUsageReport.EmployeeTableName });
      Functions.Module.ExecuteSQLCommandFormat(Queries.DocumentUsageReport.InsertIntoEmployeeTable,
                                               new object[] { Constants.DocumentUsageReport.EmployeeTableName, reportSessionId, DocumentUsageReport.DepartmentId });
      Functions.Module.ExecuteSQLCommandFormat(Queries.Module.DropTable, new[] { Constants.DocumentUsageReport.HistoryTableName });
      Functions.Module.ExecuteSQLCommandFormat(Queries.DocumentUsageReport.CreateHistoryTable, new[] { Constants.DocumentUsageReport.HistoryTableName });
      if (DocumentUsageReport.PeriodBegin.HasValue && DocumentUsageReport.PeriodEnd.HasValue)
      {
        var args = new object[]
        { 
          Constants.DocumentUsageReport.HistoryTableName,
          reportSessionId,
          DocumentUsageReport.PeriodBegin.Value.ToString(Constants.DocumentUsageReport.DateTimeFormat),
          DocumentUsageReport.PeriodEnd.Value.ToString(Constants.DocumentUsageReport.DateTimeFormat) 
        };
        Functions.Module.ExecuteSQLCommandFormat(Queries.DocumentUsageReport.InsertIntoHistoryTable, args);
      }
    }

  }
}