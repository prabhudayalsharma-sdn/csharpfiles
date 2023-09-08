using AutoMapper;
using Microsoft.Extensions.Logging;
using System.Net;
using WC_Audits.Domain.DTO;
using WC_Audits.Domain.ResponseModel;
using WC_Audits.Domain.ViewModels;
using WC_Audits.Repository.AuditInfoRepo;
using WC_Audits.Service.Helper;

namespace WC_Audits.Service.AuditService
{
    public class AuditInfoService : IAuditInfoService
    {
        #region DependencyInjection
        #region fields
        private readonly IAuditInfoRepository _auditInfoRepository;
        private readonly ILogger<AuditInfoService> _logger;
        private readonly IHelperMethod _helperMethod;
        private readonly IMapper _mapper;
        #endregion

        #region ctor
        public AuditInfoService(IAuditInfoRepository auditInfoRepository,
                                ILogger<AuditInfoService> logger,
                                IHelperMethod helperMethod,
                                IMapper mapper)
        {
            _auditInfoRepository = auditInfoRepository;
            _helperMethod = helperMethod;
            _logger = logger;
            _mapper = mapper;
        }
        #endregion
        #endregion


        #region methods

        public async Task<ResponseViewModel<AuditInfoVM>> SaveAuditInfo(AuditInfoVM auditInfoVM)
        {
            var response = new ResponseViewModel<AuditInfoVM>();
            try
            {
                if (auditInfoVM==null)
                {
                    _logger.LogWarning($"Bad request. Invalid object passed to POST auditInfo endpoint.");
                    response.Error.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Bad request. Invalid object passed to POST auditInfo endpoint.",
                        Message = "Bad request. Invalid object passed to POST auditInfo endpoint."
                    });
                    response = HelperMethod.ResponseMapping<AuditInfoVM>((int)HttpStatusCode.BadRequest, "Bad request.", null, response.Error);
                    return response;

                }

                //Mapping from VM to Entity.
                _logger.LogDebug("Mapping from VM to Entity");
                var auditInfo = _mapper.Map<AuditInfo>(auditInfoVM);

                //Calling SaveAuditInfo repo method to save auditInfo details
                _logger.LogDebug($"Calling repo method to save the auditInfo {auditInfo}");
                var info = await _auditInfoRepository.SaveAuditInfo(auditInfo);

                //Checking if Save and Submit button is clicked from UI
                if (auditInfoVM.IsAuditSubmitted)
                {
                    //Adding code to send mail for latest audit submitted.

                    await _helperMethod.PrepareAuditSubmittedEmailAndSend("AuditSubmitted", info.PolicyNumber);

                }

                //Mapping from Entity to VM
                _logger.LogDebug("Mapping from Entity to VM");
                var auditInfoToReturn = _mapper.Map<AuditInfoVM>(info);
                auditInfoToReturn.IsAuditSubmitted = auditInfoVM.IsAuditSubmitted;

                response = HelperMethod.ResponseMapping<AuditInfoVM>((int)HttpStatusCode.OK, "Successful.", auditInfoToReturn, null);
                return response;

            }
            catch (Exception ex)
            {

                // If there is an exception while calling service.
                _logger.LogError($"Error while saving renewal flags info : Exception: {ex.Message}. Stack Trace: {ex.StackTrace}");
                response.Error.Add(new Error
                {
                    Code = (int)HttpStatusCode.InternalServerError,
                    Description = ex.Message == null || ex.Message == "" ? "Something went wrong" : ex.Message,
                    Message = ex.Message,
                    More_Info = ex.StackTrace
                });
                response = HelperMethod.ResponseMapping<AuditInfoVM>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", null, response.Error);
                return response;
            }
        }

        public async Task<ResponseViewModel<AuditInfoVM>> GetAuditInfo(string policyNumber)
        {
            var response = new ResponseViewModel<AuditInfoVM>();

            try
            {
                // Checking policyNumber.
                if (string.IsNullOrEmpty(policyNumber))
                {
                    _logger.LogWarning($"Bad request. Passed policyNumber: {policyNumber}");
                    response.Error.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Invalid policyNumber passed.",
                        Message = "Bad request"
                    });
                    response = HelperMethod.ResponseMapping<AuditInfoVM>((int)HttpStatusCode.BadRequest, "Bad request.", null, response.Error);
                    return response;
                }
                //Calling GetAuditInfoByPolicyNumber repository method. 
                _logger.LogDebug($"Calling GetAuditInfoByPolicyNumber repository method with input policyNumber = {policyNumber}");
                var result = await _auditInfoRepository.GetAuditInfoByPolicyNumber(policyNumber);
                if (result == null)
                {
                    _logger.LogWarning($"AuditInfo details not found from GetAuditInfoByPolicyNumber method for policyNumber = {policyNumber}");
                    response.Error.Add(new Error
                    {
                        Code = (int)HttpStatusCode.NotFound,
                        Description = "AuditInfo details not found.",
                        Message = "Please provide correct policyNumber."
                    });
                    response = HelperMethod.ResponseMapping<AuditInfoVM>((int)HttpStatusCode.NotFound, "Not found.", null, response.Error);
                    return response;
                }

                //Mapping from entity to VM
                _logger.LogDebug($"Mapping from entity to VM");
                var auditProcessToReturn = _mapper.Map<AuditInfoVM>(result);

                response = HelperMethod.ResponseMapping<AuditInfoVM>((int)HttpStatusCode.OK, "Data fetched successfully.", auditProcessToReturn, null);
                return response;

            }
            catch (Exception ex)
            {

                // If there is an exception while calling service.
                _logger.LogError($"Error while saving renewal flags info : Exception: {ex.Message}. Stack Trace: {ex.StackTrace}");
                response.Error.Add(new Error
                {
                    Code = (int)HttpStatusCode.InternalServerError,
                    Description = ex.Message == null || ex.Message == "" ? "Something went wrong" : ex.Message,
                    Message = ex.Message,
                    More_Info = ex.StackTrace
                });
                response = HelperMethod.ResponseMapping<AuditInfoVM>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", null, response.Error);
                return response;
            }
        }
        #endregion

    }

}
