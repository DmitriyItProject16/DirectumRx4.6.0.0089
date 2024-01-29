using System;
using System.Collections.Generic;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;
using Sungero.PowerOfAttorneyServiceExtensions;

namespace Sungero.PowerOfAttorneyCore.Server
{
  public class ModuleFunctions
  {
    
    /// <summary>
    /// Получить активные настройки подключения.
    /// </summary>
    /// <returns>Список активных настроек подключения.</returns>
    [Public]
    public virtual List<IPowerOfAttorneyServiceConnection> GetActiveServiceConnections()
    {
      return PowerOfAttorneyServiceConnections
        .GetAll(х => х.Status == Sungero.PowerOfAttorneyCore.PowerOfAttorneyServiceConnection.Status.Active &&
                х.ConnectionStatus == Sungero.PowerOfAttorneyCore.PowerOfAttorneyServiceConnection.ConnectionStatus.Connected)
        .ToList();
    }
    
    /// <summary>
    /// Создать подключение нашей организации к сервису доверенностей.
    /// </summary>
    /// <returns>Созданное подключение нашей организации к сервису доверенностей.</returns>
    [Remote]
    public static IPowerOfAttorneyServiceConnection CreateAttorneyServiceConnection()
    {
      // Создать подключение нашей организации к сервису доверенностей.
      return PowerOfAttorneyServiceConnections.Create();
    }
    
    /// <summary>
    /// Получить ИД НОР в сервисе доверенностей Контур.
    /// </summary>
    /// <param name="poaServiceConnection">Подключение к сервису доверенностей.</param>
    /// <returns>ИД НОР в сервисе доверенностей.</returns>
    [Public, Remote]
    public virtual string GetOrganizationIdFromService(IPowerOfAttorneyServiceConnection poaServiceConnection)
    {
      var konturApiConnector = this.GetKonturConnector(poaServiceConnection);
      
      try
      {
        return konturApiConnector.GetOrganizationId(poaServiceConnection.BusinessUnit.TIN);
      }
      catch (Exception ex)
      {
        Logger.Error("GetOrganizationIdFromService. Error connecting to power of attorney service.", ex);
        return string.Empty;
      }
    }
    
    /// <summary>
    /// Отправить доверенность на регистрацию в ФНС.
    /// </summary>
    /// <param name="businessUnit">Наша организация.</param>
    /// <param name="powerOfAttorneyXml">Тело xml-файла доверенности.</param>
    /// <param name="powerOfAttorneySignature">Тело утверждающей подписи.</param>
    /// <returns>Результат отправки: ИД операции регистрации в сервисе доверенностей или ошибка.</returns>
    [Public]
    public virtual PowerOfAttorneyCore.Structures.Module.IResponseResult SendPowerOfAttorneyForRegistration(Company.IBusinessUnit businessUnit,
                                                                                                            byte[] powerOfAttorneyXml,
                                                                                                            byte[] powerOfAttorneySignature)
    {
      var sendingResult = Structures.Module.ResponseResult.Create();
      var poaServiceConnection = this.GetPowerOfAttorneyServiceConnection(businessUnit);
      var konturApiConnector = this.GetKonturConnector(poaServiceConnection);
      
      // Если нет подключения к сервису доверенностей - соединение отсутствует.
      if (konturApiConnector == null)
      {
        sendingResult.ErrorType = Constants.Module.PowerOfAttorneyServiceErrors.ConnectionError;
        return sendingResult;
      }
      
      var organizationId = poaServiceConnection.OrganizationId;
      konturApiConnector.ResiliencySettings = new HttpResiliencySettings()
      {
        OperationTimeout = Constants.Module.PoARegisterOperationTimeout,
        RequestTimeout = Constants.Module.PoARegisterRequestTimeout
      };
      
      var response = konturApiConnector.SendPoAForRegistration(organizationId, powerOfAttorneyXml, powerOfAttorneySignature);
      
      if (response == null || response.ErrorType == Sungero.PowerOfAttorneyServiceExtensions.Model.ErrorType.NullResponseError)
      {
        Logger.ErrorFormat("SendPowerOfAttorneyForRegistration. Response is null (organizationId = {0}). {1}", organizationId, response.ErrorText);
        sendingResult.ErrorType = Constants.Module.NullResponseError;
        sendingResult.ErrorCode = null;
        return sendingResult;
      }
      
      var errorCode = response.Result?.Error?.Code;
      if (!response.IsSuccess || !string.IsNullOrEmpty(errorCode))
      {
        var errorDetailsFromService = this.GetResponseErrorDetails(response);
        
        Logger.ErrorFormat("SendPowerOfAttorneyForRegistration. Error while sending for registration (organizationId = {0}, errorCode = {1}, {2}).",
                           organizationId, errorCode, errorDetailsFromService);
        sendingResult.ErrorType = !string.IsNullOrEmpty(response.ErrorType.ToString()) ?
          response.ErrorType.ToString() :
          Constants.Module.PowerOfAttorneyServiceErrors.ProcessingError;
        sendingResult.ErrorCode = errorCode;
        return sendingResult;
      }
      
      Logger.DebugFormat("SendPowerOfAttorneyForRegistration. Sent power of attorney for registration successfully (organizationId = {0}, operationId = {1}).",
                         organizationId, response.Result.Id);
      sendingResult.OperationId = response.Result.Id;
      
      return sendingResult;
    }
    
    /// <summary>
    /// Получить статус регистрации.
    /// </summary>
    /// <param name="businessUnit">Наша организация.</param>
    /// <param name="operationId">ИД операции.</param>
    /// <returns>Статус регистрации доверенности.</returns>
    [Public]
    public virtual PowerOfAttorneyCore.Structures.Module.IResponseResult GetRegistrationState(Company.IBusinessUnit businessUnit, string operationId)
    {
      var sendingResult = Structures.Module.ResponseResult.Create();
      
      var serviceConnection = this.GetPowerOfAttorneyServiceConnection(businessUnit);
      if (serviceConnection == null)
      {
        sendingResult.ErrorType = Constants.Module.PowerOfAttorneyServiceErrors.ConnectionError;
        return sendingResult;
      }
      
      var konturApiConnector = this.GetKonturConnector(serviceConnection);
      konturApiConnector.ResiliencySettings = new HttpResiliencySettings()
      {
        OperationTimeout = Constants.Module.PoAAsyncActionsOperationTimeout,
        RequestTimeout = Constants.Module.PoAAsyncActionsRequestTimeout
      };
      var response = konturApiConnector.GetPoARegistrationState(serviceConnection.OrganizationId, operationId);
      
      if (response == null || response.ErrorType == Sungero.PowerOfAttorneyServiceExtensions.Model.ErrorType.NullResponseError)
      {
        Logger.ErrorFormat("GetRegistrationState. Response is null (organizationId = {0}).", serviceConnection.OrganizationId);
        sendingResult.ErrorType = Constants.Module.NullResponseError;
        sendingResult.ErrorCode = null;
        return sendingResult;
      }
      
      sendingResult.StatusCode = response.StatusCode;
      var errorCode = response.Result?.Error?.Code;
      if (!response.IsSuccess || !string.IsNullOrEmpty(errorCode))
      {
        var errorDetailsFromService = this.GetResponseErrorDetails(response);
        
        sendingResult.ErrorType = !string.IsNullOrEmpty(response.ErrorType.ToString()) ?
          response.ErrorType.ToString() :
          Constants.Module.PowerOfAttorneyServiceErrors.ProcessingError;
        sendingResult.ErrorCode = errorCode;
        sendingResult.State = Constants.Module.ErrorOperationStatus;
        
        Logger.ErrorFormat("GetRegistrationState. Error while getting registration status (organizationId = {0}, error code = {1}, " +
                           "error type = {2}, {3}).",
                           serviceConnection.OrganizationId, sendingResult.ErrorCode, sendingResult.ErrorType, errorDetailsFromService);
        return sendingResult;
      }
      
      sendingResult.OperationId = response.Result.Id;
      sendingResult.State = response.Result.Status;
      
      return sendingResult;
    }
    
    /// <summary>
    /// Получить статус регистрации отзыва МЧД.
    /// </summary>
    /// <param name="businessUnit">Наша организация.</param>
    /// <param name="operationId">ИД операции.</param>
    /// <returns>Статус регистрации отзыва.</returns>
    [Public]
    public virtual PowerOfAttorneyCore.Structures.Module.IResponseResult GetRevocationState(Company.IBusinessUnit businessUnit, string operationId)
    {
      var sendingResult = Structures.Module.ResponseResult.Create();
      
      var serviceConnection = this.GetPowerOfAttorneyServiceConnection(businessUnit);
      if (serviceConnection == null)
      {
        sendingResult.ErrorType = Constants.Module.PowerOfAttorneyServiceErrors.ConnectionError;
        return sendingResult;
      }
      
      var konturApiConnector = this.GetKonturConnector(serviceConnection);
      konturApiConnector.ResiliencySettings = new HttpResiliencySettings()
      {
        OperationTimeout = Constants.Module.PoAAsyncActionsOperationTimeout,
        RequestTimeout = Constants.Module.PoAAsyncActionsRequestTimeout
      };
      var response = konturApiConnector.GetRevocationState(serviceConnection.OrganizationId, operationId);
      
      if (response == null || response.ErrorType == Sungero.PowerOfAttorneyServiceExtensions.Model.ErrorType.NullResponseError)
      {
        Logger.ErrorFormat("GetRevocationState. Response is null (organizationId = {0}).", serviceConnection.OrganizationId);
        sendingResult.ErrorType = Constants.Module.NullResponseError;
        sendingResult.ErrorCode = null;
        return sendingResult;
      }
      
      var errorCode = response.Result?.Error?.Code;
      if (!response.IsSuccess || !string.IsNullOrEmpty(errorCode))
      {
        var errorDetailsFromService = this.GetResponseErrorDetails(response);
        
        Logger.ErrorFormat("GetRevocationState. Error while getting registration status (organizationId = {0}, errorCode = {1}, {2}).",
                           serviceConnection.OrganizationId, errorCode, errorDetailsFromService);
        sendingResult.ErrorType = !string.IsNullOrEmpty(response.ErrorType.ToString()) ? response.ErrorType.ToString() : Constants.Module.PowerOfAttorneyServiceErrors.ProcessingError;
        sendingResult.ErrorCode = errorCode;
        
        return sendingResult;
      }
      
      sendingResult.OperationId = response.Result.Id;
      sendingResult.State = response.Result.Status;
      
      return sendingResult;
    }
    
    /// <summary>
    /// Проверить состояние эл. доверенности.
    /// </summary>
    /// <param name="businessUnit">Наша организация.</param>
    /// <param name="unifiedRegistrationNumber">Единый рег. номер доверенности.</param>
    /// <param name="agent">Представитель.</param>
    /// <param name="powerOfAttorneyXml">Тело xml-файла доверенности.</param>
    /// <param name="powerOfAttorneySignature">Тело утверждающей подписи.</param>
    /// <returns>Результат валидации доверенности.</returns>
    [Public]
    public virtual PowerOfAttorneyCore.Structures.Module.IPowerOfAttorneyValidationState CheckPowerOfAttorneyState(Company.IBusinessUnit businessUnit,
                                                                                                                   string unifiedRegistrationNumber,
                                                                                                                   PowerOfAttorneyCore.Structures.Module.IAgent agent,
                                                                                                                   byte[] powerOfAttorneyXml,
                                                                                                                   byte[] powerOfAttorneySignature)
    {
      var validationResult = Structures.Module.PowerOfAttorneyValidationState.Create();
      validationResult.Errors = new List<Sungero.PowerOfAttorneyCore.Structures.Module.IValidationOperationError>();
      var serviceConnection = this.GetPowerOfAttorneyServiceConnection(businessUnit);
      var konturApiConnector = this.GetKonturConnector(serviceConnection);
      var organizationId = serviceConnection?.OrganizationId;
      // Если нет подключения к сервису доверенностей - соединение отсутствует.
      if (konturApiConnector == null)
      {
        var error = Structures.Module.ValidationOperationError.Create();
        error.Type = Constants.Module.PowerOfAttorneyServiceErrors.ConnectionError;
        validationResult.Errors.Add(error);
        return validationResult;
      }
      
      // Подготовка данных для запроса состояния в сервисе.
      var poaContent = Convert.ToBase64String(powerOfAttorneyXml);
      var signatureContent = Convert.ToBase64String(powerOfAttorneySignature);
      var poaFiles = PowerOfAttorneyServiceExtensions.Model.PoAFiles.Create(poaContent, signatureContent);
      var principal = PowerOfAttorneyServiceExtensions.Model.Principal.Create(businessUnit.TIN, businessUnit.TRRC);
      var representative = PowerOfAttorneyServiceExtensions.Model.Representative.CreateRepresentative(agent.TIN, agent.INILA, agent.TINUl, agent.TRRC,
                                                                                                      agent.Name, agent.Surname, agent.Middlename);
      
      konturApiConnector.ResiliencySettings = new HttpResiliencySettings()
      {
        OperationTimeout = Constants.Module.CheckPoAStateOperationTimeout,
        RequestTimeout = Constants.Module.CheckPoAStateRequestTimeout
      };
      
      var response = konturApiConnector.ValidatePoA(organizationId, principal, representative, null, poaFiles);
      
      if (response == null || response.ErrorType == Sungero.PowerOfAttorneyServiceExtensions.Model.ErrorType.NullResponseError)
      {
        Logger.ErrorFormat("CheckPowerOfAttorneyState. Response is null (FPoA unified registration number = {0}).", unifiedRegistrationNumber);
        var error = Structures.Module.ValidationOperationError.Create();
        error.Type = Constants.Module.NullResponseError;
        error.Message = Constants.Module.NullResponseError;
        validationResult.Errors.Add(error);
        return validationResult;
      }

      if (response.Result == null || !response.IsSuccess)
      {
        var error = Structures.Module.ValidationOperationError.Create();
        error.Type = !string.IsNullOrEmpty(response.ErrorType.ToString()) ? response.ErrorType.ToString() : Constants.Module.PowerOfAttorneyServiceErrors.ProcessingError;
        error.Message = response.ErrorText;
        
        Logger.ErrorFormat("CheckPowerOfAttorneyState. Error while validating power of attorney (FPoA unified registration number = {0}, error type = {1}, error message: {2}).",
                           unifiedRegistrationNumber, error.Type, error.Message);
        
        validationResult.Errors.Add(error);
        return validationResult;
      }
      
      // Если получили неактуальный ответ.
      if (!response.Result.SystemSyncInfo.IsActual)
      {
        // Если есть время последнего обновления состояния в ФНС, и ФНС доступен - указываем время последнего обновления,
        // иначе - указываем, что состояние ранее не обновлялось или ФНС недоступен.
        Logger.ErrorFormat("CheckPowerOfAttorneyState. State is outdated, state - {1}, sync date - {2}. Power of attorney not found or FTS service is unavailable (FPoA unified registration number = {0}).",
                           unifiedRegistrationNumber, response.Result.PoAValidationResult.Status, response.Result.SystemSyncInfo.SyncedAt);
        var error = Structures.Module.ValidationOperationError.Create();
        error.Type = Constants.Module.PowerOfAttorneyServiceErrors.ProcessingError;
        error.Code = DateTime.Compare(response.Result.SystemSyncInfo.SyncedAt, DateTime.MinValue) != 0 ?
          Constants.Module.PowerOfAttorneyServiceErrors.StateIsOutdated :
          Constants.Module.PowerOfAttorneyServiceErrors.PoANotFound;
        validationResult.Errors.Add(error);
      }
      
      // Если есть ошибки - доверенность не валидна.
      if (response.Result.PoAValidationResult?.Errors != null)
      {
        foreach (var validationError in response.Result.PoAValidationResult.Errors)
        {
          Logger.DebugFormat("CheckPowerOfAttorneyState. Error while validating power of attorney (FPoA unified registration number = {0}, state = {1}, error code = {2}, message: {3}).",
                             unifiedRegistrationNumber, response.Result.PoAValidationResult.Status, validationError.Code, validationError.Message);
          
          var error = Structures.Module.ValidationOperationError.Create();
          error.Type = Constants.Module.PowerOfAttorneyServiceErrors.ProcessingError;
          error.Code = validationError.Code;
          error.Message = validationError.Message;
          validationResult.Errors.Add(error);
        }
      }
      
      validationResult.Result = response.Result.PoAValidationResult?.Status;
      Logger.DebugFormat("CheckPowerOfAttorneyState. Validating power of attorney done. State - {1}, sync date - {2}. (FPoA unified registration number = {0}).",
                         unifiedRegistrationNumber, validationResult.Result, response.Result.SystemSyncInfo.SyncedAt);
      return validationResult;
    }
    
    /// <summary>
    /// Получить дату подписания и причину отзыва доверенности.
    /// </summary>
    /// <param name="serviceConnection">Подключение к сервису доверенностей.</param>
    /// <param name="unifiedRegistrationNumber">Единый регистрационный номер доверенности.</param>
    /// <returns>Дата подписания и причина отзыва доверенности.</returns>
    [Public, Remote]
    public virtual Structures.Module.IPowerOfAttorneyRevocationInfo GetPowerOfAttorneyRevocationInfo(IPowerOfAttorneyServiceConnection serviceConnection, string unifiedRegistrationNumber)
    {
      var konturApiConnector = this.GetKonturConnector(serviceConnection);
      var organizationId = serviceConnection?.OrganizationId;
      
      var revocationInfo = Structures.Module.PowerOfAttorneyRevocationInfo.Create();
      konturApiConnector.ResiliencySettings = new HttpResiliencySettings()
      {
        OperationTimeout = Constants.Module.PoAAsyncActionsOperationTimeout,
        RequestTimeout = Constants.Module.PoAAsyncActionsRequestTimeout
      };
      var response = konturApiConnector.GetPoARevocationInfo(organizationId, unifiedRegistrationNumber);
      
      if (response == null || response.ErrorType == Sungero.PowerOfAttorneyServiceExtensions.Model.ErrorType.NullResponseError)
      {
        Logger.ErrorFormat("GetPowerOfAttorneyRevocationInfo. Response is null (organizationId = {0}).", organizationId);
        return null;
      }
      
      if (response.IsSuccess && response.Result != null)
      {
        revocationInfo = Structures.Module.PowerOfAttorneyRevocationInfo.Create();
        revocationInfo.Date = response.Result.Sign.SignedAt;
        revocationInfo.Reason = response.Result.Reason;
      }
      else
      {
        Logger.ErrorFormat("GetPowerOfAttorneyRevocationInfo. Error while getting revocation info (FPoA unified registration number = {0}, business unit id = {1}).",
                           unifiedRegistrationNumber, serviceConnection.BusinessUnit?.Id);
        return null;
      }

      return revocationInfo;
    }
    
    /// <summary>
    /// Отправить запрос на проверку состояния эл. доверенности.
    /// </summary>
    /// <param name="serviceConnection">Подключение к сервису доверенностей.</param>
    /// <param name="businessUnit">НОР - доверитель.</param>
    /// <param name="unifiedRegistrationNumber">Единый рег. номер доверенности.</param>
    /// <param name="agent">Представитель.</param>
    /// <param name="powerOfAttorneyXml">Тело xml-файла доверенности.</param>
    /// <param name="powerOfAttorneySignature">Тело утверждающей подписи.</param>
    /// <returns>ИД операции в сервисе доверенностей.</returns>
    [Public]
    public virtual PowerOfAttorneyCore.Structures.Module.IResponseResult EnqueuePoAValidation(IPowerOfAttorneyServiceConnection serviceConnection,
                                                                                              Company.IBusinessUnit businessUnit, string unifiedRegistrationNumber,
                                                                                              PowerOfAttorneyCore.Structures.Module.IAgent agent,
                                                                                              byte[] powerOfAttorneyXml, byte[] powerOfAttorneySignature)
    {
      var principal = PowerOfAttorneyServiceExtensions.Model.Principal.Create(businessUnit.TIN, businessUnit.TRRC);
      var representative = PowerOfAttorneyServiceExtensions.Model.Representative.CreateRepresentative(agent.TIN, agent.INILA, agent.TINUl, agent.TRRC,
                                                                                                      agent.Name, agent.Surname, agent.Middlename);
      var poaIdentity = PowerOfAttorneyServiceExtensions.Model.PoAIdentity.Create(unifiedRegistrationNumber, businessUnit.TIN);
      
      var poaContent = Convert.ToBase64String(powerOfAttorneyXml, Base64FormattingOptions.InsertLineBreaks);
      var signatureContent = Convert.ToBase64String(powerOfAttorneySignature, Base64FormattingOptions.InsertLineBreaks);
      var poaFiles = PowerOfAttorneyServiceExtensions.Model.PoAFiles.Create(poaContent, signatureContent);
      
      var organizationId = serviceConnection?.OrganizationId;
      var connector = this.GetKonturConnector(serviceConnection);
      var sendingResult = Structures.Module.ResponseResult.Create();
      connector.ResiliencySettings = new HttpResiliencySettings()
      {
        OperationTimeout = Constants.Module.PoAAsyncActionsOperationTimeout,
        RequestTimeout = Constants.Module.PoAAsyncActionsRequestTimeout
      };
      var response = connector.EnqueuePoAValidation(organizationId, principal, representative, poaIdentity, poaFiles);
      
      if (response == null || response.ErrorType == Sungero.PowerOfAttorneyServiceExtensions.Model.ErrorType.NullResponseError)
      {
        Logger.ErrorFormat("EnqueuePoAValidation. Response is null (organizationId = {0}).", organizationId);
        sendingResult.ErrorType = Constants.Module.NullResponseError;
        sendingResult.ErrorCode = null;
        return sendingResult;
      }
      
      var errorCode = response.Result?.Error?.Code;
      
      if (!response.IsSuccess || !string.IsNullOrEmpty(errorCode))
      {
        var errorDetailsFromService = this.GetResponseErrorDetails(response);
        sendingResult.ErrorType = !string.IsNullOrEmpty(response.ErrorType.ToString()) ? 
          response.ErrorType.ToString() : 
          Constants.Module.PowerOfAttorneyServiceErrors.ProcessingError;
        sendingResult.ErrorCode = errorCode;
        Logger.ErrorFormat("EnqueueValidatePoA. Error while enqueueing power of attorney validation " +
                           "(FPoA unified registration number = {0}, business unit id = {1}, error code = {2}, " +
                           "error type = {3}, {4}).",
                           unifiedRegistrationNumber, businessUnit.Id, sendingResult.ErrorCode, 
                           sendingResult.ErrorType, errorDetailsFromService);

        return sendingResult;
      }
      
      sendingResult.OperationId = response.Result.Id;
      
      return sendingResult;
    }
    
    /// <summary>
    /// Получить состояние валидации эл. доверенности в сервисе.
    /// </summary>
    /// <param name="serviceConnection">Подключение к сервису доверенностей.</param>
    /// <param name="operationId">ИД операции в сервисе доверенностей.</param>
    /// <returns>Состояние валидации доверенности.</returns>
    [Public]
    public virtual Structures.Module.IPowerOfAttorneyValidationState GetPoAValidationState(IPowerOfAttorneyServiceConnection serviceConnection, string operationId)
    {
      var validationState = Structures.Module.PowerOfAttorneyValidationState.Create();
      validationState.Errors = new List<Sungero.PowerOfAttorneyCore.Structures.Module.IValidationOperationError>();
      var organizationId = serviceConnection?.OrganizationId;
      var connector = this.GetKonturConnector(serviceConnection);
      connector.ResiliencySettings = new HttpResiliencySettings()
      {
        OperationTimeout = Constants.Module.PoAAsyncActionsOperationTimeout,
        RequestTimeout = Constants.Module.PoAAsyncActionsRequestTimeout
      };
      
      var response = connector.GetPoAValidationOperationState(organizationId, operationId);
      
      if (response == null || response.ErrorType == Sungero.PowerOfAttorneyServiceExtensions.Model.ErrorType.NullResponseError)
      {
        Logger.ErrorFormat("GetPoAValidationState. Response is null (organizationId = {0}).", organizationId);
        var error = Structures.Module.ValidationOperationError.Create();
        error.Type = Constants.Module.NullResponseError;
        error.Code = null;
        
        validationState.Errors.Add(error);
        return validationState;
      }
      
      if (!response.IsSuccess || response.Result == null || response.Result.Status == Constants.Module.ErrorOperationStatus)
      {
        validationState.OperationStatus = response.Result?.Status;
        Logger.ErrorFormat("GetPoAValidationState. Error while getting power of attorney validation state (operation id = {0}, business unit id = {1}, error code = {2}).",
                           operationId, serviceConnection.BusinessUnit?.Id, response.Result?.Error?.Code);
        var error = Structures.Module.ValidationOperationError.Create();
        error.Type = !string.IsNullOrEmpty(response.ErrorType.ToString()) ? 
          response.ErrorType.ToString() : 
          Constants.Module.PowerOfAttorneyServiceErrors.ProcessingError;
        error.Code = response.Result?.Error?.Code;
        
        validationState.Errors.Add(error);
        return validationState;
      }

      validationState.OperationStatus = response.Result.Status;
      validationState.Result = response.Result.ValidationResult?.Status;
      
      // Если есть ошибки - доверенность не валидна.
      if (response.Result.ValidationResult?.Errors != null)
      {
        foreach (var validationError in response.Result.ValidationResult.Errors)
        {
          Logger.DebugFormat("GetPoAValidationState. Error while validating power of attorney (organizationId = {0}, error code = {1}, message: {2}).",
                             response.Result.ValidationResult.Status, validationError.Code, validationError.Message);
          
          var error = Structures.Module.ValidationOperationError.Create();
          error.Type = Constants.Module.PowerOfAttorneyServiceErrors.ProcessingError;
          error.Code = validationError.Code;
          error.Message = validationError.Message;
          validationState.Errors.Add(error);
        }
      }
      
      return validationState;
    }
    
    /// <summary>
    /// Отправить запрос отзыва доверенности в ФНС.
    /// </summary>
    /// <param name="businessUnit">Наша организация.</param>
    /// <param name="revocationXml">Тело xml-файла отзыва доверенности.</param>
    /// <param name="revocationSignature">Тело утверждающей подписи.</param>
    /// <returns>Результат отправки отзыва эл. доверенности.</returns>
    [Public]
    public virtual PowerOfAttorneyCore.Structures.Module.IResponseResult SendPowerOfAttorneyRevocation(Company.IBusinessUnit businessUnit,
                                                                                                       byte[] revocationXml,
                                                                                                       byte[] revocationSignature)
    {
      var sendingResult = Structures.Module.ResponseResult.Create();
      var serviceConnection = this.GetPowerOfAttorneyServiceConnection(businessUnit);
      var konturApiConnector = this.GetKonturConnector(serviceConnection);
      
      // Если нет подключения к сервису доверенностей - соединение отсутствует.
      if (konturApiConnector == null)
      {
        sendingResult.ErrorType = Constants.Module.PowerOfAttorneyServiceErrors.ConnectionError;
        Logger.ErrorFormat("SendPowerOfAttorneyRevocation. Error while get api connector.");
        return sendingResult;
      }
      
      var organizationId = serviceConnection?.OrganizationId;
      konturApiConnector.ResiliencySettings = new HttpResiliencySettings()
      {
        OperationTimeout = Constants.Module.PoARegisterOperationTimeout,
        RequestTimeout = Constants.Module.PoARegisterRequestTimeout
      };
      
      var response = konturApiConnector.SendRevocation(organizationId, revocationXml, revocationSignature);
      
      if (response == null || response.ErrorType == Sungero.PowerOfAttorneyServiceExtensions.Model.ErrorType.NullResponseError)
      {
        Logger.ErrorFormat("SendPowerOfAttorneyRevocation. Response is null (organizationId = {0}).", organizationId);
        sendingResult.ErrorType = Constants.Module.NullResponseError;
        sendingResult.ErrorCode = null;
        return sendingResult;
      }
      
      var errorCode = response.Result?.Error?.Code;
      if (!response.IsSuccess || !string.IsNullOrEmpty(errorCode))
      {
        var errorDetailsFromService = this.GetResponseErrorDetails(response);
        
        Logger.ErrorFormat("SendPowerOfAttorneyRevocation. Error while sending power of attorney revocation (organizationId = {0}, errorCode = {1}, {2}).",
                           organizationId, errorCode, errorDetailsFromService);
        sendingResult.ErrorType = !string.IsNullOrEmpty(response.ErrorType.ToString()) ?
          response.ErrorType.ToString() :
          Constants.Module.PowerOfAttorneyServiceErrors.ProcessingError;
        sendingResult.ErrorCode = errorCode;
        return sendingResult;
      }
      
      Logger.DebugFormat("SendPowerOfAttorneyRevocation. Power of attorney revocation sent successfully (organizationId = {0}, operationId = {1}).",
                         organizationId, response.Result.Id);
      sendingResult.OperationId = response.Result.Id;
      
      return sendingResult;
    }
    
    /// <summary>
    /// Получить коннектор к сервису доверенностей.
    /// </summary>
    /// <param name="businessUnit">Наша организация.</param>
    /// <returns>Коннектор к сервису доверенностей.</returns>
    public virtual PowerOfAttorneyServiceExtensions.KonturConnector GetKonturConnector(Company.IBusinessUnit businessUnit)
    {
      var serviceConnection = this.GetPowerOfAttorneyServiceConnection(businessUnit);
      if (serviceConnection == null)
        return null;
      return this.GetKonturConnector(serviceConnection);
    }
    
    /// <summary>
    /// Получить коннектор к сервису доверенностей.
    /// </summary>
    /// <param name="poaServiceConnection">Подключение к сервису доверенностей.</param>
    /// <returns>Коннектор к сервису доверенностей.</returns>
    public virtual PowerOfAttorneyServiceExtensions.KonturConnector GetKonturConnector(IPowerOfAttorneyServiceConnection poaServiceConnection)
    {
      if (poaServiceConnection == null)
      {
        Logger.Error("GetKonturConnector. Service connection for organization is not specified.");
        return null;
      }
      
      var serviceApiVersion = Constants.Module.KonturPowerOfAttorneyServiceVersion;
      return PowerOfAttorneyServiceExtensions.KonturConnector.Get(poaServiceConnection.ServiceApp.Uri,
                                                                  serviceApiVersion,
                                                                  poaServiceConnection.ServiceApp.APIKey);
    }
    
    /// <summary>
    /// Получить подключение к сервису доверенностей.
    /// </summary>
    /// <param name="businessUnit">Наша организация.</param>
    /// <returns>Подключение к сервису доверенностей.</returns>
    [Public]
    public virtual IPowerOfAttorneyServiceConnection GetPowerOfAttorneyServiceConnection(Company.IBusinessUnit businessUnit)
    {
      return this.GetPowerOfAttorneyServiceConnectionQuery(businessUnit).FirstOrDefault();
    }
    
    /// <summary>
    /// Проверить наличие настроенного подключения к сервису доверенностей.
    /// </summary>
    /// <param name="businessUnit">Наша организация.</param>
    /// <returns>True - если есть активное настроенное подключение.</returns>
    [Public]
    public virtual bool HasPowerOfAttorneyServiceConnection(Company.IBusinessUnit businessUnit)
    {
      return this.GetPowerOfAttorneyServiceConnectionQuery(businessUnit).Any();
    }
    
    /// <summary>
    /// Получить запрос с подключением к сервису доверенностей.
    /// </summary>
    /// <param name="businessUnit">Наша организация.</param>
    /// <returns>Запрос для получения подключения к сервису доверенностей.</returns>
    public virtual IQueryable<IPowerOfAttorneyServiceConnection> GetPowerOfAttorneyServiceConnectionQuery(Company.IBusinessUnit businessUnit)
    {
      return PowerOfAttorneyServiceConnections.GetAll()
        .Where(p => Equals(p.BusinessUnit, businessUnit) &&
               p.Status == PowerOfAttorneyCore.PowerOfAttorneyServiceConnection.Status.Active &&
               p.ConnectionStatus == PowerOfAttorneyCore.PowerOfAttorneyServiceConnection.ConnectionStatus.Connected);
    }
    
    /// <summary>
    /// Получить детали ошибки из ответа сервиса.
    /// </summary>
    /// <param name="response">Ответ сервиса.</param>
    /// <returns>Форматированная строка с деталями ошибки.</returns>
    private string GetResponseErrorDetails(PowerOfAttorneyServiceExtensions.Model.RequestResult<PowerOfAttorneyServiceExtensions.Model.OperationResponse> response)
    {
      var errorMessagesFromService = string.Empty;
      if (response.Result?.Error?.Details != null)
      {
        errorMessagesFromService = "details: ";
        foreach (var details in response.Result.Error.Details)
          errorMessagesFromService += details.Message + "\r\n";
      }
      
      return errorMessagesFromService;
    }
  }
}