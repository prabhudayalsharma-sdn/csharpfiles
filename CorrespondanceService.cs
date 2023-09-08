using AutoMapper;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using WC_Audits.Domain.DTO;
using WC_Audits.Domain.ResponseModel;
using WC_Audits.Domain.ViewModels;
using WC_Audits.Repository.CorrespondanceRepo;
using WC_Audits.Service.Helper;

namespace WC_Audits.Service.CorrespondanceService
{
    public class CorrespondanceService : ICorrespondanceService
    {
        #region DependencyInjection
        #region fields
        private readonly ICorrespondanceRepository _correspondanceRepository;
        private readonly ILogger<CorrespondanceService> _logger;
        private readonly IMapper _mapper;
        #endregion

        #region ctor
        public CorrespondanceService(ICorrespondanceRepository correspondanceRepository,
            ILogger<CorrespondanceService> logger,
            IMapper mapper)
        {
            _correspondanceRepository = correspondanceRepository;
            _logger = logger;
            _mapper = mapper;
        }
        #endregion
        #endregion

        #region methods
        public async Task<ResponseViewModel<CorrespondanceVM>> GetCorrespondanceVMInfo(string submissionID)
        {
            var response = new ResponseViewModel<CorrespondanceVM>();

            try
            {
                // Checking submissionID.
                if (string.IsNullOrEmpty(submissionID))
                {
                    _logger.LogWarning($"Bad request. Passed SubmissionId: {submissionID}");
                    response.Error.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Invalid submissionID passed.",
                        Message = "Bad request"
                    });
                    response = HelperMethod.ResponseMapping<CorrespondanceVM>((int)HttpStatusCode.BadRequest, "Bad request.", null, response.Error);
                    return response;
                }
                //Calling GetCorrespondanceInfoBySubmissionID repository method. 
                _logger.LogDebug($"Calling GetCorrespondanceInfoBySubmissionId repository method with input SubmissionId = {submissionID}");
                var result = await _correspondanceRepository.GetCorrespondanceInfoBySubmissionId(submissionID);
                if (result == null)
                {
                    _logger.LogWarning($"Correspondance details not found from GetCorrespondanceInfoBySubmissionId method for SubmissionId = {submissionID}");
                    response.Error.Add(new Error
                    {
                        Code = (int)HttpStatusCode.NotFound,
                        Description = "Correspondance details not found.",
                        Message = "Please provide correct submissionID."
                    });
                    response = HelperMethod.ResponseMapping<CorrespondanceVM>((int)HttpStatusCode.NotFound, "Not found.", null, response.Error);
                    return response;
                }

                //Mapping from entity to VM
                _logger.LogDebug($"Mapping from entity to VM");
                var correspondanceToReturn = _mapper.Map<CorrespondanceVM>(result);

                response = HelperMethod.ResponseMapping<CorrespondanceVM>((int)HttpStatusCode.OK, "Data fetched successfully.", correspondanceToReturn, null);
                return response;

            }
            catch (Exception ex)
            {

                // If there is an exception while calling service.
                _logger.LogError($"Error while getting Correspondance info : Exception: {ex.Message}. Stack Trace: {ex.StackTrace}");
                response.Error.Add(new Error
                {
                    Code = (int)HttpStatusCode.InternalServerError,
                    Description = ex.Message == null || ex.Message == "" ? "Something went wrong" : ex.Message,
                    Message = ex.Message,
                    More_Info = ex.StackTrace
                });
                response = HelperMethod.ResponseMapping<CorrespondanceVM>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", null, response.Error);
                return response;
            }
        }

        public async Task<ResponseViewModel<CorrespondanceVM>> SaveCorrespondanceDetails(CorrespondanceVM correspondanceVM)
        {
            var response = new ResponseViewModel<CorrespondanceVM>();
            try
            {
                if (correspondanceVM == null)
                {
                    _logger.LogWarning($"Bad request. Invalid object passed to POST correspondance endpoint.");
                    response.Error.Add(new Error
                    {
                        Code = (int)HttpStatusCode.BadRequest,
                        Description = "Bad request. Invalid object passed to POST correspondance endpoint.",
                        Message = "Bad request. Invalid object passed to POST auditInfo endpoint."
                    });
                    response = HelperMethod.ResponseMapping<CorrespondanceVM>((int)HttpStatusCode.BadRequest, "Bad request.", null, response.Error);
                    return response;

                }

                //Mapping from VM to Entity.
                _logger.LogDebug("Mapping from VM to Entity");
                var correspondance = _mapper.Map<Correspondance>(correspondanceVM);

                //Calling SaveCorrespondanceDetails repo method to save auditInfo details
                _logger.LogDebug($"Calling repo method to save the auditInfo {correspondance}");
                await _correspondanceRepository.AddCorrespondanceDetails(correspondance);

                //Mapping from Entity to VM
                _logger.LogDebug("Mapping from Entity to VM");
                var correspondanceToReturn = _mapper.Map<CorrespondanceVM>(correspondance);

                response = HelperMethod.ResponseMapping<CorrespondanceVM>((int)HttpStatusCode.OK, "Successful.", correspondanceToReturn, null);
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
                response = HelperMethod.ResponseMapping<CorrespondanceVM>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", null, response.Error);
                return response;
            }
        }

        /// <summary>
        /// This method is used to delete Correspondance details based on policyNumber.
        /// </summary>
        /// <param name="policyNumber"></param>
        /// <returns></returns>
        public async Task<ResponseViewModel<bool>> DeleteCorrespondence(string policyNumber)
        {
            var response = new ResponseViewModel<bool>();

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
                    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.BadRequest, "Bad request.", false, response.Error);
                    return response;
                }

                //Calling GetCorrespondanceInfoByPolicyNumber repository method to get the correspondance details based on passed policynumber.
                _logger.LogDebug($"Calling GetCorrespondanceInfoByPolicyNumber repository method to get the correspondance details based on passed policynumber = {policyNumber}");
                var correspondance = await _correspondanceRepository.GetCorrespondanceInfoByPolicyNumber(policyNumber);

                if (correspondance.Count > 0)
                {
                    //Calling DeleteByPolicyNumber repository method to delete correspondence based on passed policynumber.
                    _logger.LogDebug($"Calling DeleteByPolicyNumber repository method to delete Correspondance based on passed policynumber = {policyNumber}");
                    var result = await _correspondanceRepository.DeleteByPolicyNumber(policyNumber);
                    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.OK, "Correspondance deleted successfully.", result, null);
                    return response;
                }
                else
                {
                    _logger.LogWarning($"Correspondence details not found from GetCorrespondanceInfoByPolicyNumber method for policyNumber: {policyNumber}");
                    response.Error.Add(new Error
                    {
                        Code = (int)HttpStatusCode.NotFound,
                        Description = "Correspondence details not found",
                        Message = "Please provide correct PolicyNumber."
                    });
                    response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.NotFound, "Non found", false, response.Error);
                    return response;
                }
            }
            catch (Exception ex)
            {

                // If there is an exception while calling service.
                _logger.LogError($"Error while deleting correspondence : Exception: {ex.Message}. Stack Trace: {ex.StackTrace}");
                response.Error.Add(new Error
                {
                    Code = (int)HttpStatusCode.InternalServerError,
                    Description = ex.Message == null || ex.Message == "" ? "Something went wrong" : ex.Message,
                    Message = ex.Message,
                    More_Info = ex.StackTrace
                });
                response = HelperMethod.ResponseMapping<bool>((int)HttpStatusCode.InternalServerError, "Exception occurs while processing request.", false, response.Error);
                return response;
            }
        }
        #endregion
    }
}
