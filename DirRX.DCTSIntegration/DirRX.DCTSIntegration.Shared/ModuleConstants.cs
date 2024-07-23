using System;

namespace DirRX.DCTSIntegration.Constants
{
  public static class Module
  {
    /// <summary>Маска для подстановки даты в строку.</summary>
    public const string DatePattern = @"<Дата>";
    
    /// <summary>Маска для подстановки даты и времени в строку.</summary>
    public const string DateTimePattern = @"<ДатаВремя>";
    
    /// <summary>Маска для подстановки контрагента в строку.</summary>
    public const string CounterpartyPattern = @"<Контрагент>";
    
    /// <summary>Маска для подстановки вида документа в строку.</summary>
    public const string DocKindPattern = @"<ВидДокумента>";
    
    public static class Initialize
    {
      /// <summary> Guid справочника "Виды документов". </summary>
      public static readonly Guid DocumentKindTypeGuid = Guid.Parse("14a59623-89a2-4ea8-b6e9-2ad4365f358c");
      
      /// <summary> Guid вида документов "Входящие письма". </summary>
      public static readonly Guid IncomingLetterKind = Guid.Parse("0002C3CB-43E1-4A01-A4FE-35ABC8994D66");
      
      /// <summary> Guid вида документов "Простой документ". </summary>
      public static readonly Guid SimpleDocumentKind = Guid.Parse("3981CDD1-A279-4A51-85D5-58DB391603C2");
      
    }

  }
}