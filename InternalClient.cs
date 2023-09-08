using BTIS.DotNetLogger.Standard;
using BTIS.Utility.Standard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net;
using System.Text;
using WC_Audits.Domain.Enums;
using WC_Audits.Domain.External;
using WC_Audits.Domain.External.WCSubmission;
using WC_Audits.Domain.ResponseModel;
using WC_Audits.Domain.ViewModels;

namespace WC_Audits.Service.Helper
{
    public class InternalClient:IInternalClient
    {
        private readonly ILogger _logger;
        private readonly ICorrelationIdProvider _correlationIdProvider;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly SmartHttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly List<EmailContent> emailContentJson;
        public InternalClient(ILogger<InternalClient> logger,
             ICorrelationIdProvider correlationIdProvider,
             IHttpContextAccessor httpContextAccessor,
             IConfiguration configuration, List<EmailContent> emailContents)
        {
            _correlationIdProvider = correlationIdProvider;
            _httpContextAccessor = httpContextAccessor;
            _httpClient = new SmartHttpClient(_correlationIdProvider, _httpContextAccessor);
            _logger = logger;
            _configuration = configuration;
            emailContentJson = emailContents;
        }
        public async Task<HttpResponseMessage> EmailQueuePublisher(EmailDom emailDom)
        {
            var queuePublisherEndpoint = $"{_configuration["PublisherURL"]}email";

            // Writing logic to not send test submissions to IR.
            var excludeAgencies = JsonConvert.DeserializeObject<List<string>>(_configuration["ExcludeAgencies"] ?? "" ) ?? new List<string>();
            var excludeContacts = JsonConvert.DeserializeObject<List<string>>(_configuration["ExcludeContacts"] ?? "") ?? new List<string>();
            if (excludeAgencies.Contains(emailDom.Metadata.AgencyId.ToString()) || excludeContacts.Contains(emailDom.Metadata.ContactId.ToString()))
            {
                EmailContent testEmail = emailContentJson.FirstOrDefault(s => s.EmailType == CommonEmailTypes.TestEmail.ToString());
                emailDom.Content.From.Email = testEmail.EmailRecipients.From;
                emailDom.Content.Cc = new List<To>();
                testEmail.EmailRecipients.Cc = testEmail?.EmailRecipients?.Cc ?? new List<string>();
                for (int i = 0; i < testEmail?.EmailRecipients?.Cc?.Count(); i++)
                {
                    emailDom.Content.Cc.Add(new To { Email = testEmail?.EmailRecipients?.Cc[i] });
                }

                var bccList = testEmail?.EmailRecipients?.Bcc ?? new List<string>();
                emailDom.Content.Bcc = new List<To>();
                for (int i = 0; i < bccList?.Count(); i++)
                {
                    emailDom.Content.Bcc.Add(new To { Email = testEmail?.EmailRecipients?.Bcc[i] });
                }
            }
            var postBody = JsonConvert.SerializeObject(emailDom);
            // Calling Email QueuePublisher endpoint.
            _logger.LogInformation($"Calling Email QueuePublisher endpoint. Endpoint:{queuePublisherEndpoint}, Body: {postBody}");
            var content = new StringContent(postBody.ToString(), Encoding.UTF8, "application/json");
            var respns = await _httpClient.PostAsync(queuePublisherEndpoint, content);
            _logger.LogInformation($"Response from QueuePublisher endpoint. Status:{respns.StatusCode}");
            return respns;
        }

        /// <summary>
        /// call wcreporting AuditEmailProcess method to get audit process submisisons.
        /// </summary>
        /// <param name="operationType"></param>
        /// <param name="submissionId"></param>
        /// <returns>wcsubmission list</returns>
        public async Task<HttpResponseMessage> AuditEmailProcess(string operationType, string submissionId)
        {
            var url = _configuration["WCReporting"] + $"Audits/AuditEmailProcess/{operationType}";
            if (!string.IsNullOrEmpty(submissionId))
                url = url + $"?submissionId={submissionId}";

            _logger.LogInformation($"Calling AuditEmailProcess endpoint with url: {url}");
            var res = await _httpClient.GetAsync(url);
            _logger.LogInformation($"Response from AuditEmailProcess endpoint. Status:{res.StatusCode}");
            return res;
        }
        //public async Task<HttpResponseMessage> QueuePublisher(EmailDom emailDom)
        //{
        //    var queuePublisherEndpoint = $"{_configuration["PublisherURL"]}email";

        //    var postBody = JsonConvert.SerializeObject(emailDom);
        //    // Calling Email QueuePublisher endpoint.
        //    _logger.LogInformation($"Calling Email QueuePublisher endpoint. Endpoint:{queuePublisherEndpoint}, Body: {postBody}");
        //    var content = new StringContent(postBody.ToString(), Encoding.UTF8, "application/json");
        //    var respns = await _httpClient.PostAsync(queuePublisherEndpoint, content);
        //    _logger.LogInformation($"Response from QueuePublisher endpoint. Status:{respns.StatusCode}");
        //    return respns;

        //}

        public async Task<HttpResponseMessage> AuditTrailService(object auditRequest)
        {
            var auditEventUrl = string.Format($"{_configuration["AuditTrail"]}/audit_trail");
            _logger.LogDebug($"Calling auditTrail Service with url: {auditEventUrl}");
            var jsonObject = JsonConvert.SerializeObject(auditRequest);
            var content = new StringContent(jsonObject.ToString(), Encoding.UTF8, "application/json");
            var res = await _httpClient.PostAsync(auditEventUrl, content);
            var responseString = await res.Content.ReadAsStringAsync();
            _logger.LogDebug($"Response from auditTrail endpoint:{responseString}");
            return res;
        }

        public async Task<HttpResponseMessage> EmailErrorAsync(EmailDom errorMessage)
        {
            string errorEmailUrl = $"{_configuration["ErrorEmailURL"]}Email/Send";
            var jsonObject = JsonConvert.SerializeObject(errorMessage);
            var data = new StringContent(jsonObject, Encoding.UTF8, "application/json");
            var response = _httpClient.PostAsync(errorEmailUrl, data).Result;
            _logger.LogInformation($"Response from errorEmailUrl. Status:{response.StatusCode}");
            return response;
        }
        public async Task<List<AgencyContact>> AgencyContact(int[] contactIds)
        {
            var agencyInfo = new List<AgencyContact>();
            try
            {
                var batchSize = 20;
                int numberOfBatches = (int)Math.Ceiling((double)contactIds.Count() / batchSize);

                for (int i = 0; i < numberOfBatches; i++)
                {
                    var currentIds = contactIds.Skip(i * batchSize).Take(batchSize);
                    var tasks = currentIds.Select(id => GetAgencyContact(id));
                    var taskOutput = await Task.WhenAll(tasks);
                    agencyInfo.AddRange(taskOutput);
                }
                return agencyInfo;
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Exception while getting agency info, response. Exception:{ex.Message}");
                return new List<AgencyContact>();
            }
        }

        /// <summary>
        /// Service Method to get the submission data.
        /// </summary>
        /// <param name="submissionId"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> GetSubmission(string submissionId)
        {
            var url = string.Format(_configuration["BaseURL"] + "WC/v1/wcsubmission/api/Endorsements/{0}", submissionId);
            // Calling Endorsements "GET" method to get the submission info.
            _logger.LogDebug($"Calling Endorsements GET method to get the submission info with url: {url}");
            var res = await _httpClient.GetAsync(url);
            _logger.LogDebug($"Response from Endorsements GET method. Response Status:{res.StatusCode}");
            return res;
        }


        /// <summary>
        /// Service Method to post endorsement with NonCoOp endorsement type
        /// </summary>
        /// <param name="submissionID"></param>
        /// <param name="premiumInfo"></param>
        /// <returns></returns>
        public async Task AuditEndorsement(string[] submissionIds)
        {
            //var submissionEndorsementInfo = new List<string>();
            List<wcsubmission> wc = new List<wcsubmission>();
            try
            {
                var batchSize = 20;
                int numberOfBatches = (int)Math.Ceiling((double)submissionIds.Count() / batchSize);
                
                for (int i = 0; i < numberOfBatches; i++)
                {
                    var currentIds = submissionIds.Skip(i * batchSize).Take(batchSize);

                    var tasks = currentIds.Select(id => PostNonCoOpEndorsementData(id));
                    var taskOutput = await Task.WhenAll(tasks);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Exception while getting submissionIds, response. Exception:{ex.Message}");
            }
        }

        private async Task<wcsubmission> PostNonCoOpEndorsementData(string submissionID)
        {
            // call EndorsementSubMapping(id) method
            var endorsementResponse = await EndorsementSubMapping(submissionID);
            // response data of EndorsementSubMapping passed in serial body
            if (endorsementResponse == null)
            {
                return new wcsubmission();
            }
            #region // Post Endorsement endpoint with NonCoOpt Endorsement Type
            //var resBody = await EndorsementSubMapping(submissionID);
            var endorsementUrl = _configuration["WCSubmissionUrl"] + "api/Endorsements/" + submissionID;
            var postBody = JsonConvert.SerializeObject(endorsementResponse);

            // Calling Post Endorsement endpoint with NonCoOpt Endorsement Type
            _logger.LogInformation($"Calling Post Endorsement endpoint with NonCoOp Endorsement Type. Endpoint:{endorsementUrl}, Body: {postBody}");
            var content = new StringContent(postBody.ToString(), Encoding.UTF8, "application/json");
            var result = await _httpClient.PostAsync(endorsementUrl, content);

            var response = await result.Content.ReadAsStringAsync();
            if (!result.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from wc gateway lookup industry experience endpoint:{response}");
                return new wcsubmission();
            }
            var submission = JsonConvert.DeserializeObject<ResponseViewModel<wcsubmission>>(response);
            return submission.Response;
            #endregion
        }

        // <summary> This method is used to mapping Endorsement request body </summary>
        /// <param name="submissionID">SubmissionId</param>
        /// <param name="premiumInfo">SubmissionId</param>
        /// <returns></returns>
        private async Task<WCEndorsementSubmission> EndorsementSubMapping(string submissionID)
        {
            var res = await GetSubmission(submissionID);
            var resString = await res.Content.ReadAsStringAsync();
            var wcsubmission = JsonConvert.DeserializeObject<ResponseViewModel<WCEndorsementSubmission>>(resString);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from Get Endorsement method endpoint : {resString}");
                return new WCEndorsementSubmission();
            }

            WCEndorsementSubmission requestEndorsement = new WCEndorsementSubmission
            {
                _id = null,
                Type = new List<string>() { "PremiumInfo", "NoSpecificWaiver", "NoSchModifier", "NonCoOp" },
                SchModifier = wcsubmission.Response.SchModifier,
                WcSubmission = new WCSubmissionV2()
            };
            requestEndorsement.WcSubmission = wcsubmission.Response.WcSubmission;
            requestEndorsement.EndorsementDate = wcsubmission.Response.EndorsementDate;
            requestEndorsement.PremiumInfo = new PremiumInfo();
            requestEndorsement.PremiumInfo = wcsubmission.Response.PremiumInfo;
            requestEndorsement.SpecificWaiver = new List<SpecificWaiver>();
            requestEndorsement.SpecificWaiver = wcsubmission.Response.SpecificWaiver;
            requestEndorsement.EndorsementOwner = new List<EndorsementOwner>();
            requestEndorsement.EndorsementOwner = wcsubmission.Response.EndorsementOwner;
            requestEndorsement.EndorsementClassification = new List<EndorsementClassification>();
            requestEndorsement.EndorsementClassification = wcsubmission.Response.EndorsementClassification;
            requestEndorsement.IsFlatCancellation = wcsubmission.Response.IsFlatCancellation;
            return requestEndorsement;
        }

        public async Task<AgencyContact> GetAgencyContact(int contactId)
        {
            var agencyUrl = string.Format(_configuration["AgencyURL"] + "agencycontact/{0}", contactId);
            _logger.LogInformation($"Calling agency contact endpoint with url: {agencyUrl}");
            var res = await _httpClient.GetAsync(agencyUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            _logger.LogDebug($"Response from agency contact endpoint. Code:{res.StatusCode} and response:{responseString}");
            if (res.StatusCode == HttpStatusCode.OK)
            {
                AgencyContact contactInfo = JsonConvert.DeserializeObject<AgencyContact>(responseString);
                return contactInfo;
            }
            return new AgencyContact();
        }


        public async Task<List<HttpResponseMessage>> UpdateLastEmailSent(string[] policyNumbers, string operationType)
        {
            var httpResponse = new List<HttpResponseMessage>();
            try
            {
                var batchSize = 20;
                int numberOfBatches = (int)Math.Ceiling((double)policyNumbers.Count() / batchSize);

                for (int i = 0; i < numberOfBatches; i++)
                {
                    var currentIds = policyNumbers.Skip(i * batchSize).Take(batchSize);
                    var tasks = currentIds.Select(policyNo => LastEmailSent(policyNo, operationType));
                    var taskOutput = await Task.WhenAll(tasks);
                    httpResponse.AddRange(taskOutput);
                }
                return httpResponse;
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Exception while updating LastEmailSent, response. Exception:{ex.Message}");
                return new List<HttpResponseMessage>();
            }
        }
        public async Task<HttpResponseMessage> LastEmailSent(string policyNumber, string operationType)
        {
            var url = _configuration["WCSubmissionUrl"] + $"api/Endorsements/policyNumber/{policyNumber}/operationType/{operationType}";
            var postBody = JsonConvert.SerializeObject(policyNumber);
            var content = new StringContent(postBody.ToString(), Encoding.UTF8, "application/json");
            _logger.LogDebug($"Calling update LastEmailSent property in wcsubmisison collection for policyNumber : {policyNumber}, opeartionType : {operationType} : url {url}");
            var res = await _httpClient.PutAsync(url, content);
            return res;
        }

        public async Task<HttpResponseMessage> UpdateLastEmailSent(List<string> policyNumbers, string operationType)
        {
            var url = _configuration["WCSubmissionUrl"] + $"api/Endorsements/policyNumber/{policyNumbers}/operationType/{operationType}";
            var postBody = JsonConvert.SerializeObject(policyNumbers);
            var content = new StringContent(postBody.ToString(), Encoding.UTF8, "application/json");
            _logger.LogDebug($"Calling update LastEmailSent property in wcsubmisison collection for policyNumber : {policyNumbers}, opeartionType : {operationType} : url {url}");
            var res = await _httpClient.PutAsync(url, content);
            return res;
        }

        /// <summary>
        /// Service Method to get the submission data.
        /// </summary>
        /// <param name="policyNumber"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> GetWcSubmissionByPolicyNumber(string policyNumber)
        {
            var url = $"{_configuration["BaseURL"]}WC/v1/wcsubmission/Submission/{policyNumber}/details";
            _logger.LogDebug($"Calling Submission GET endpoint to get the submission info with url: {url}");
            var res = await _httpClient.GetAsync(url);
            _logger.LogDebug($"Response from Submission GET method. Response Status:{res.StatusCode}");
            return res;
        }

        /// <summary>
        /// This method is being used to fetch email template as a string body.
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns>Email template based on filename Passed.</returns>
        public async Task<string> FetchEmailTemplate(string fileName)
        {
            try
            {
                var url = $"{_configuration["BaseCDNUrl"]}/resources/workerscomp/{fileName}";
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"An error occurred while fetching the email template: {ex.Message}");
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError($"The request to fetch the email template timed out: {ex.Message}");
                throw;
            }
        }


    }
}
