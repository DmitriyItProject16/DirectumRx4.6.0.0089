using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Commons.EntityRecognitionInfo;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.Domain.Shared;
using Sungero.Metadata;

namespace Sungero.Commons.Server
{
  partial class EntityRecognitionInfoFunctions
  {
    /// <summary>
    /// Получить результат распознавания сущности.
    /// </summary>
    /// <param name="entity">Сущность.</param>
    /// <returns>Результат распознавания.</returns>
    [Public, Remote(IsPure = true)]
    public static IEntityRecognitionInfo GetEntityRecognitionInfo(Sungero.Domain.Shared.IEntity entity)
    {
      var typeGuid = entity.GetEntityMetadata().GetOriginal().NameGuid.ToString();
      
      return EntityRecognitionInfos
        .GetAll(x => x.EntityId == entity.Id &&
                string.Equals(x.EntityType, typeGuid, StringComparison.InvariantCultureIgnoreCase))
        .OrderByDescending(x => x.Id)
        .FirstOrDefault();
    }
    
    /// <summary>
    /// Клонировать заданную сущность.
    /// </summary>
    /// <param name="targetEntity">Сущность.</param>
    [Public, Remote]
    public virtual void Clone(Sungero.Domain.Shared.IEntity targetEntity)
    {
      var typeGuid = targetEntity.GetEntityMetadata()
        .GetOriginal()
        .NameGuid.ToString();
      if (EntityRecognitionInfos.GetAll().Any(x => x.EntityId == targetEntity.Id && x.EntityType == typeGuid))
        return;
      
      var clone = EntityRecognitionInfos.Copy(_obj);
      clone.EntityType = typeGuid;
      clone.Save();
    }
    
    /// <summary>
    /// Получить вероятность, с которой распознано свойство сущности.
    /// </summary>
    /// <param name="propertyName">Наименование свойства.</param>
    /// <returns>Вероятность, с которой распознано свойство сущности.
    /// null, если для свойства нет результатов распознавания.</returns>
    [Public]
    public virtual double? GetProbabilityByPropertyName(string propertyName)
    {
      if (string.IsNullOrEmpty(propertyName))
        return null;
      
      var factsByPropertyName = _obj.Facts.Where(x => x.PropertyName == propertyName);
      
      if (!factsByPropertyName.Any())
        return null;
      
      return factsByPropertyName.First().Probability;
    }
    
    /// <summary>
    /// Проложить связь между фактом и свойством документа.
    /// </summary>
    /// <param name="fact">Факт, который будет связан со свойством документа.</param>
    /// <param name="fieldName">Поле, которое будет связано со свойством документа. Если не указано, то будут связаны все поля факта.</param>
    /// <param name="propertyName">Имя свойства документа.</param>
    /// <param name="propertyValue">Значение свойства.</param>
    [Public]
    public virtual void LinkFactAndProperty(Structures.Module.IArioFact fact,
                                            string fieldName,
                                            string propertyName,
                                            object propertyValue)
    {
      this.LinkFactAndProperty(fact, fieldName, propertyName, propertyValue, null, null);
    }
    
    /// <summary>
    /// Проложить связь между фактом и свойством документа.
    /// </summary>
    /// <param name="fact">Факт, который будет связан со свойством документа.</param>
    /// <param name="fieldName">Поле, которое будет связано со свойством документа. Если не указано, то будут связаны все поля факта.</param>
    /// <param name="propertyName">Имя свойства документа.</param>
    /// <param name="propertyValue">Значение свойства.</param>
    /// <param name="probability">Вероятность.</param>
    [Public]
    public virtual void LinkFactAndProperty(Structures.Module.IArioFact fact,
                                            string fieldName,
                                            string propertyName,
                                            object propertyValue,
                                            double? probability)
    {
      this.LinkFactAndProperty(fact, fieldName, propertyName, propertyValue, probability, null);
    }
    
    /// <summary>
    /// Проложить связь между фактом и свойством документа.
    /// </summary>
    /// <param name="fact">Факт, который будет связан со свойством документа.</param>
    /// <param name="fieldName">Поле, которое будет связано со свойством документа. Если не указано, то будут связаны все поля факта.</param>
    /// <param name="propertyName">Имя свойства документа.</param>
    /// <param name="propertyValue">Значение свойства.</param>
    /// <param name="probability">Вероятность.</param>
    /// <param name="collectionRecordId">ИД записи свойства-коллекции.</param>
    [Public]
    public virtual void LinkFactAndProperty(Structures.Module.IArioFact fact,
                                            string fieldName,
                                            string propertyName,
                                            object propertyValue,
                                            double? probability = null,
                                            int? collectionRecordId = null)
    {
      var fieldNames = new List<string>() { fieldName };
      if (fieldName == null)
        fieldNames = null;
      this.LinkFactFieldsAndProperty(fact, fieldNames, propertyName, propertyValue, probability, collectionRecordId);
    }

    /// <summary>
    /// Проложить связь между полями факта и свойством документа.
    /// </summary>
    /// <param name="fact">Факт, который будет связан со свойством документа.</param>
    /// <param name="fieldNames">Поля, которые будут связаны со свойством документа. Если не указано, то будут связаны все поля факта.</param>
    /// <param name="propertyName">Имя свойства документа.</param>
    /// <param name="propertyValue">Значение свойства.</param>
    /// <param name="probability">Вероятность.</param>
    /// <param name="collectionRecordId">ИД записи свойства-коллекции.</param>
    [Public]
    public virtual void LinkFactFieldsAndProperty(Structures.Module.IArioFact fact,
                                                  List<string> fieldNames,
                                                  string propertyName,
                                                  object propertyValue,
                                                  double? probability = null,
                                                  int? collectionRecordId = null)
    {
      var propertyStringValue = PublicFunctions.Module.GetValueAsString(propertyValue);
      if (string.IsNullOrWhiteSpace(propertyStringValue))
        return;
      
      var hasFieldNames = fieldNames != null;
      if (hasFieldNames && !fieldNames.Any())
        return;
      
      // Если значение определилось не из фактов,
      // для подсветки заносим это свойство и результату не доверяем.
      if (fact == null)
      {
        var calculatedFact = _obj.Facts.AddNew();
        calculatedFact.PropertyName = propertyName;
        calculatedFact.PropertyValue = propertyStringValue;
        calculatedFact.Probability = probability;
      }
      else
      {
        var propertyRelatedFields = _obj.Facts.Where(f => f.FactId == fact.Id)
          .Where(f => !hasFieldNames || fieldNames.Contains(f.FieldName));
        
        foreach (var field in propertyRelatedFields)
        {
          if (probability == null)
            probability = PublicFunctions.Module.GetFieldProbability(fact, field.FieldName);
          
          var factLabel = PublicFunctions.Module.GetFactLabel(fact, propertyName);
          
          field.PropertyName = propertyName;
          field.PropertyValue = propertyStringValue;
          field.Probability = probability;
          field.FactLabel = factLabel;
          field.CollectionRecordId = collectionRecordId;
        }
      }
    }
    
    /// <summary>
    /// Получить документ, связанный с результатом распознавания.
    /// </summary>
    /// <returns>Документ.</returns>
    /// <remarks>Вернуть как сущность, чтобы не добавлять зависимость от модуля, где определен официальный документ.</remarks>
    [Public, Remote(IsPure = true)]
    public virtual IEntity GetDocument()
    {
      if (_obj.EntityId.HasValue)
      {
        var document = Sungero.Docflow.PublicFunctions.Module.GetElectronicDocumentAsEntity(_obj.EntityId.Value);
        if (document != null && string.Equals(document.GetEntityMetadata().GetOriginal().NameGuid.ToString(),
                                              _obj.EntityType,
                                              StringComparison.InvariantCultureIgnoreCase))
          return document;
      }
      return null;
    }
    
    /// <summary>
    /// Обновить положение фактов для нового порядка страниц в документе.
    /// </summary>
    /// <param name="newPagesOrder">Новый порядок страниц в документе. Ключ - текущий номер страницы, Значение - новый номер.</param>
    /// <returns>Список Ид фактов, страницы которых не указаны в новом порядке.</returns>
    [Public]
    public virtual List<int> UpdatePagesInPositions(System.Collections.Generic.IDictionary<string, string> newPagesOrder)
    {
      var elementDelimiter = Docflow.PublicConstants.Module.PositionElementDelimiter;
      var positionsDelimiter = Docflow.PublicConstants.Module.PositionsDelimiter;
      var loggerTemplateForChange = "Commons. UpdatePagesInPositions. Position for field {0} in recognition info (ID = {1}) was changed from {2} to {3}.";

      var factsFromDeletedPages = new List<int>();
      var factFieldsWithPositions = _obj.Facts.Where(x => !string.IsNullOrWhiteSpace(x.PropertyName) && !string.IsNullOrWhiteSpace(x.Position) && x.FactId.HasValue);
      foreach (var factField in factFieldsWithPositions)
      {
        var positions = factField.Position.Split(positionsDelimiter).Select(x => x.Split(elementDelimiter)).ToList();
        var newPositions = new List<string>();
        foreach (var positionElements in positions)
        {
          if (newPagesOrder.ContainsKey(positionElements[0]))
          {
            positionElements[0] = newPagesOrder[positionElements[0]];
            newPositions.Add(string.Join(elementDelimiter.ToString(), positionElements));
          }
        }

        var newFactPosition = string.Join(positionsDelimiter.ToString(), newPositions);
        if (factField.Position != newFactPosition)
        {
          if (string.IsNullOrEmpty(newFactPosition))
            factsFromDeletedPages.Add(factField.FactId.Value);
          else
          {
            Logger.DebugFormat(loggerTemplateForChange, factField.FieldId, _obj.Id, factField.Position, newFactPosition);
            factField.Position = newFactPosition;
          }
        }
      }
      return factsFromDeletedPages.Distinct().ToList();
    }
    
    /// <summary>
    /// Очистить привязку к свойству у всех полей фактов.
    /// </summary>
    /// <param name="factsIds">Список с Ид фактов.</param>
    [Public]
    public virtual void ClearFactAndPropertyLink(List<int> factsIds)
    {
      var loggerTemplateForDelete = "Commons. ClearFactAndPropertyLink. Position and property link for field {0} in recognition info (ID = {1}) was deleted.";
      var deletedFactsFields = _obj.Facts.Where(x => x.FactId.HasValue && factsIds.Contains(x.FactId.Value));
      foreach (var factField in deletedFactsFields)
      {
        factField.PropertyName = string.Empty;
        factField.PropertyValue = string.Empty;
        factField.FactLabel = string.Empty;
        factField.Position = string.Empty;
        factField.CollectionRecordId = null;
        factField.Probability = null;
        Logger.DebugFormat(loggerTemplateForDelete, factField.FieldId, _obj.Id);
      }
    }
  }
}