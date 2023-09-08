using BTIS.DotNetLogger.Standard;
using BTIS.Utility.Standard;
using ExternalSubmissionData.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WCSubmission.Helper;
using WCSubmission.Models;
using WCSubmission.Models.InstantQuote;
using WCSubmission.Models.Renewals;
using WCSubmission.Services.Interfaces;
using WCSubmission.Utility;

namespace WCSubmission.Services
{
    /// <summary>
    /// 
    /// </summary>
    public class Service : IService
    {
        private readonly ILogger _logger;
        private readonly ITokenUtility _tokenUtil;
        private readonly ICorrelationIdProvider _correlationIdProvider;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly SmartHttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly string env = Environment.GetEnvironmentVariable("RUNNINGENVIRONMENT") ?? "Localhost";
        private readonly List<EmailContent> emailContentJson;
        private readonly IHostingEnvironment _env;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="tokenUtility"></param>
        /// <param name="correlationIdProvider"></param>
        /// <param name="httpContextAccessor"></param>
        /// <param name="configuration"></param>
        public Service(ILogger<Service> logger, ITokenUtility tokenUtility, ICorrelationIdProvider correlationIdProvider, IHttpContextAccessor httpContextAccessor, IConfiguration configuration, List<EmailContent> emailContents)
        {
            _correlationIdProvider = correlationIdProvider;
            _httpContextAccessor = httpContextAccessor;
            _httpClient = new SmartHttpClient(_correlationIdProvider, _httpContextAccessor);
            _logger = logger;
            _tokenUtil = tokenUtility;
            _configuration = configuration;
            emailContentJson = emailContents;
        }

        /// <summary>
        /// Service to get token.
        /// </summary>
        /// <param name="userKey"></param>
        /// <returns></returns>
        public async Task<UserKeyDetails> GetToken(string userKey)
        {
            var authUrl = string.Format(_configuration["AuthenticationURL"] + "/userkey/token?userKey={0}", userKey);
            _logger.LogInformation($"Calling token endpoint with url: {authUrl}");
            var res = await _httpClient.GetAsync(authUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            _logger.LogInformation($"Response from token endpoint : {responseString}");
            var userKeyResponse = JsonConvert.DeserializeObject<UserKeyDetails>(responseString);
            return userKeyResponse;
        }

        /// <summary>
        /// Service to get userInfo response from policyholder
        /// </summary>
        /// <param name="type"></param>
        /// <param name="userInfoParam"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> IsPolicyHolderUser(string type, string userInfoParam)
        {
            var url = $"{_configuration["PolicyHolderUrl"]}api/PolicyHolder/selfreport/IsPolicyHolderUser/{type}/{userInfoParam}";
            //var url = $"http://localhost:55727/api/PolicyHolder/selfreport/IsPolicyHolderUser/{type}/{userInfoParam}";
            _logger.LogDebug($"Calling IsPolicyHolderUser GET method to get the userdetails from policyholder userinfo collection with url: {url}");
            var res = await _httpClient.GetAsync(url);
            _logger.LogDebug($"Response from IsPolicyHolderUser GET method. Response Status:{res.StatusCode}");
            return res;
        }

        /// <summary>
        /// Service to send forget password mail to MPR user.
        /// </summary>
        /// <param name="email"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> SendForgetPasswordMail(string email)
        {
            var url = $"{_configuration["PolicyHolderUrl"]}api/PolicyHolder/forgetpassword/sendemail/{email}";
            //var url = $"http://localhost:55727/api/PolicyHolder/forgetpassword/sendemail/{email}";
            _logger.LogDebug($"Calling sendemail GET method to send forgetpassword mail to MPR user: {url}");
            var res = await _httpClient.GetAsync(url);
            _logger.LogDebug($"Response from sendemail GET method. Response Status:{res.StatusCode}");
            return res;

        }

        /// <summary>
        /// Service to get agency contact name.
        /// </summary>
        /// <param name="contactId"></param>
        /// <returns></returns>
        public async Task<Models.AgencyContact> GetAgencyContact(int contactId)
        {
            var agencyUrl = string.Format(_configuration["AgencyURL"] + "agencycontact/{0}", contactId);
            _logger.LogInformation($"Calling agency contact endpoint with url: {agencyUrl}");
            var res = await _httpClient.GetAsync(agencyUrl);
            var responseString = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"error: Response from agency contact endpoint:{responseString}");
                return new Models.AgencyContact();
            }

            _logger.LogInformation($"Response from agency contact endpoint:{responseString}");
            var contactInfo = JsonConvert.DeserializeObject<Models.AgencyContact>(responseString);
            return contactInfo;
        }

        /// <summary>
        /// Service to get agency name.
        /// </summary>
        /// <param name="agencyId"></param>
        /// <returns></returns>
        public async Task<string> GetAgencyName(int agencyId)
        {
            var agencyUrl = string.Format(_configuration["AgencyURL"] + "agency/{0}", agencyId);
            _logger.LogInformation($"Calling agency endpoint with url: {agencyUrl}");
            var res = await _httpClient.GetAsync(agencyUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            _logger.LogInformation($"Response from agency endpoint, StatusCode: {res.StatusCode}, Response :{responseString}");
            var agencyResponse = JToken.Parse(responseString);
            var agencyName = agencyResponse.SelectToken("name").ToString();
            return agencyName;
        }

        /// <summary>
        /// Service to publish submissionId.
        /// </summary>
        /// <param name="submissionId"></param>
        public async Task PublishQuote(string submissionId)
        {
            try
            {
                var factory = new ConnectionFactory() { HostName = "localhost" };
                factory.Uri = new Uri(_configuration["RabbitMQURL"].Replace("amqp://", "amqps://"));
                factory.AutomaticRecoveryEnabled = true;
                factory.TopologyRecoveryEnabled = true;
                using (var connection = factory.CreateConnection($"WC UW: {env} env"))
                using (var channel = connection.CreateModel())
                {
                    var exchangeName = "Quote.exchange.direct";
                    var newExchangeName = "wcuw.exchange.deadqueue";
                    var queueName = _configuration["UWEmailQueueName"];
                    var deadQueueName = queueName + "_dead";
                    var timeToLive = Convert.ToInt32(_configuration["TimeToLive"]);

                    // Ensure that the queue exists before we publish to it
                    channel.ExchangeDeclare(exchangeName, ExchangeType.Direct, true, false, null);
                    channel.QueueDeclare(queueName, true, false, false,
                         new Dictionary<string, object>
                            {
                                {"x-dead-letter-exchange", newExchangeName}
                            });
                    channel.QueueBind(queueName, exchangeName, queueName);

                    // New exchange declare for dead Queue with 2 argument of exchange and message time to live of 24 hours
                    channel.ExchangeDeclare(newExchangeName, ExchangeType.Direct, true, false, null);
                    channel.QueueDeclare(deadQueueName, true, false, false,
                        new Dictionary<string, object>
                            {
                                {"x-dead-letter-exchange", exchangeName},
                                {"x-message-ttl", timeToLive}
                        });
                    channel.QueueBind(deadQueueName, newExchangeName, queueName, null);

                    // The data put on the queue must be a byte array
                    var data = Encoding.UTF8.GetBytes(submissionId);
                    channel.BasicPublish(exchangeName, queueName, null, data);
                }
            }
            catch (Exception ex)
            {
                await SendMail($"{env} environment: Error while publishing {submissionId} in UW queue", $"Exception:{ex.Message}, stack trace:{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Method to call externalSubmssion service to store submission details into AmtrustWorkersCompSupplement table.
        /// </summary>
        /// <param name="amtrustWorkersComp"></param>
        public async Task<ExternalSubmissionData.Client.ResponseViewModel<ExternalSubmissionData.Client.AmtrustWorkersCompSupplement>> AmtrustSuppliment(ExternalSubmissionData.Client.AmtrustWorkersCompSupplement amtrustWorkersComp)
        {
            _logger.LogInformation("Calling externalSubmission service to save submission details into AmtustWorkersCompSupplement table.");
            var client = new ExternalSubmissionData.Client.AmtustWorkersCompSupplementsClient(_configuration["BaseURL"]);
            return await client.Post("QMWC", amtrustWorkersComp);
        }

        /// <summary>
        /// Method to get AmtrustWorkersCompSupplement details for passed submissionId.
        /// </summary>
        /// <param name="submissionId"></param>
        /// <returns></returns>
        public async Task<ExternalSubmissionData.Client.AmtrustWorkersCompSupplementModel> GetAmtrustSupplement(string submissionId)
        {
            _logger.LogInformation("Calling externalSubmission service to get submission details from AmtustWorkersCompSupplement table.");
            var submissionNumber = Convert.ToInt32(Regex.Replace(submissionId, @"^[A-Za-z]+", ""));
            var client = new ExternalSubmissionData.Client.AmtustWorkersCompSupplementsClient(_configuration["BaseURL"]);
            var res = await client.Get("QMWC", new[] { submissionNumber });
            if (res.Count() > 0)
            {
                return res.FirstOrDefault();
            }
            return null;
        }

        /// <summary>
        /// Method to get AmtrustWorkersCompSupplement details for passed multiple submissionId.
        /// </summary>
        /// <param name="submissionIdList"></param>
        /// <returns></returns>
        public async Task<List<ExternalSubmissionData.Client.AmtrustWorkersCompSupplementModel>> GetAmtrustSupplementByMultiIds(string[] submissionIdList)
        {
            _logger.LogInformation("Calling externalSubmission service to get submission details from AmtustWorkersCompSupplement table.");
            var submissionNumbers = submissionIdList.Select(s => Convert.ToInt32(Regex.Replace(s, @"^[A-Za-z]+", ""))).ToArray();
            var client = new ExternalSubmissionData.Client.AmtustWorkersCompSupplementsClient(_configuration["BaseURL"]);
            var res = await client.Get("QMWC", submissionNumbers);
            if (res.Count() > 0)
            {
                return res.ToList();
            }
            return null;
        }

        /// <summary>
        /// Method to update submission status in  AmtrustWorkersCompSupplement table.
        /// </summary>
        /// <param name="amtrustWorkersComp"></param>
        public async Task<ExternalSubmissionData.Client.ResponseViewModel<ExternalSubmissionData.Client.AmtrustWorkersCompSupplement>> UpdateAmtrustSuppliment(ExternalSubmissionData.Client.AmtrustWorkersCompSupplement amtrustWorkersComp)
        {
            _logger.LogInformation("Calling externalSubmission service to update submission details into AmtustWorkersCompSupplement table.");
            var client = new ExternalSubmissionData.Client.AmtustWorkersCompSupplementsClient(_configuration["BaseURL"]);
            return await client.Put("QMWC", amtrustWorkersComp.SubmissionId.ToString(), amtrustWorkersComp);
        }

        /// <summary>
        /// Method to call publisher endpoint to publish submissionID.
        /// </summary>
        /// <param name="submissionID"></param>
        public void PublishSubmission(string submissionID)
        {
            var endpointAddress = _configuration["WCSubmissionUrl"] + $"UnderWriter/UWEmail/{submissionID}";
            _logger.LogInformation($"Calling publisher service to publish submissionId with url: {endpointAddress}");
            _httpClient.PostAsync(endpointAddress, null).ConfigureAwait(false);
        }

        /// <summary>
        /// This method is being used to send emails like proposal for a given submissionId.
        /// </summary>
        /// <param name="submissionID"></param>
        /// <param name="emailtype"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> SendEmail(string submissionID,string emailtype)
        {
            var endpointAddress = _configuration["WCSubmissionUrl"] + $"UnderWriter/Email/{submissionID}/Type/{emailtype}";
            _logger.LogInformation($"Calling Email endpoint with url: {endpointAddress}");
            return await _httpClient.PostAsync(endpointAddress, null);
        }

        /// <summary>
        /// Service to publish PIE carrier submissionId.
        /// </summary>
        /// <param name="submissionId"></param>
            public async Task PublishPIEQuote(string submissionId)
        {
            try
            {
                var factory = new ConnectionFactory() { HostName = "localhost" };
                factory.Uri = new Uri(_configuration["RabbitMQURL"].Replace("amqp://", "amqps://"));
                factory.AutomaticRecoveryEnabled = true;
                factory.TopologyRecoveryEnabled = true;
                using (var connection = factory.CreateConnection($"PIE UW: {env} env"))
                using (var channel = connection.CreateModel())
                {
                    var exchangeName = "Quote.exchange.direct";
                    var newExchangeName = "wcuw.exchange.deadqueue";
                    var queueName = _configuration["PieQueueName"];
                    var deadQueueName = queueName + "_dead";
                    var timeToLive = Convert.ToInt32(_configuration["TimeToLive"]);

                    // Ensure that the queue exists before we publish to it
                    channel.ExchangeDeclare(exchangeName, ExchangeType.Direct, true, false, null);
                    channel.QueueDeclare(queueName, true, false, false,
                        new Dictionary<string, object>
                            {
                                {"x-dead-letter-exchange", newExchangeName}
                            });
                    channel.QueueBind(queueName, exchangeName, queueName);

                    // New exchange declare for dead Queue with 2 argument of exchange and message time to live of 24 hours
                    channel.ExchangeDeclare(newExchangeName, ExchangeType.Direct, true, false, null);
                    channel.QueueDeclare(deadQueueName, true, false, false,
                        new Dictionary<string, object>
                            {
                                {"x-dead-letter-exchange", exchangeName},
                                {"x-message-ttl", timeToLive}
                        });
                    channel.QueueBind(deadQueueName, newExchangeName, queueName, null);

                    // The data put on the queue must be a byte array
                    var data = Encoding.UTF8.GetBytes(submissionId);
                    channel.BasicPublish(exchangeName, queueName, null, data);
                }
            }
            catch (Exception ex)
            {
                await SendMail($"{env} environment: Error while publishing {submissionId} in PIE UW queue", $"Exception:{ex.Message}, stack trace:{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="submissionID"></param>
        /// <param name="carrier"></param>
        public async Task<Models.ResponseViewModel<PaymentPlanInfo>> GetPaymentPlan(string submissionID, string carrier)
        {
            var endpointAddress = _configuration["WCSubmissionUrl"] + $"PaymentPlan/{submissionID}/Carrier/{carrier}";
            _logger.LogInformation($"Calling GetPaymentPlan endpoint to get payment plan with url: {endpointAddress}");
            var res = await _httpClient.GetAsync(endpointAddress);
            var finalRes = await res.Content.ReadAsStringAsync();
            _logger.LogInformation($"Response from GetPaymentPlan endpoint Status code: {res.StatusCode}, response: {finalRes}");

            var resDetails = JsonConvert.DeserializeObject<Models.ResponseViewModel<PaymentPlanInfo>>(finalRes);
            if (resDetails.Status == 200)
                return resDetails;
            else
                return null;
        }

        /// <summary>
        /// This service method is used to call Email QueuePublisher endpoint.
        /// </summary>
        /// <param name="emailDom">Email request</param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> EmailQueuePublisher(EmailDom emailDom)
        {
            var queuePublisherEndpoint = $"{_configuration["PublisherURL"]}email";
            
            // Writing logic to not send test submissions to IR.
            var excludeAgencies = JsonConvert.DeserializeObject<List<string>>(_configuration["ExcludeAgencies"]) ?? new List<string>();
            var excludeContacts = JsonConvert.DeserializeObject<List<string>>(_configuration["ExcludeContacts"]) ?? new List<string>();
            if (excludeAgencies.Contains(emailDom.Metadata.AgencyId.ToString()) || excludeContacts.Contains(emailDom.Metadata.ContactId.ToString()))
            {
                EmailContent testEmail = emailContentJson.FirstOrDefault(s => s.EmailType == EmailType.TestEmail.ToString());
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
        /// Method to call agency service
        /// </summary>
        /// <param name="agencyId"></param>
        /// <returns></returns>
        public async Task<AgencyDetails> GetAgency(int agencyId)
        {
            var agencyUrl = _configuration["AgencyURL"] + $"agency/{agencyId}";
            _logger.LogInformation($"Calling agency endpoint with url: {agencyUrl}");
            var agencyString = await _httpClient.GetAsync(agencyUrl);
            var agencyResponse = await agencyString.Content.ReadAsStringAsync();

            if (agencyString.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError($"Error: Response from Agency API Status={agencyString.StatusCode}, response = {agencyResponse}");
                return null;
            }
            else
            {
                _logger.LogInformation($"Response from agency endpoint. Code:{agencyString.StatusCode}, response:{agencyResponse}");
                var agencyDetails = JsonConvert.DeserializeObject<AgencyDetails>(agencyResponse);
                return agencyDetails;
            }
        }

        /// <summary>
        /// Method to call agency service for multiple agency id
        /// </summary>
        /// <param name="agencyIds"></param>
        /// <returns></returns>
        public async Task<List<AgencyDetails>> GetAgencyByMultiIds(int[] agencyIds)
        {
            var agencies = new List<AgencyDetails>();
            var taskList = new List<Task<HttpResponseMessage>>();

            _logger.LogInformation($"Calling agency endpoint with url: {_configuration["AgencyURL"]}agency/ , for agencyIds: {JsonConvert.SerializeObject(agencyIds)}");
            foreach (int id in agencyIds)
            {
                var agencyServiceURL = _configuration["AgencyURL"] + $"agency/{id}";
                taskList.Add(_httpClient.GetAsync(agencyServiceURL));
            }

            await Task.WhenAll(taskList);
            taskList.ForEach(task =>
            {
                var agencystring = task.Result;
                var agencyResponse = agencystring.Content.ReadAsStringAsync().Result;

                if (agencystring.StatusCode != HttpStatusCode.OK)
                    _logger.LogError($"error: response from agency api status={agencystring.StatusCode}, response = {agencyResponse}");
                else
                {
                    _logger.LogInformation($"Response from agency endpoint. Code:{agencystring.StatusCode}, response:{agencyResponse}");
                    agencies.Add(JsonConvert.DeserializeObject<AgencyDetails>(agencyResponse));
                }
            });

            return agencies;
        }

        /// <summary>
        /// Method to get UwComment based on SubmissionId
        /// </summary>
        /// <param name="submissionIdList">List of Submission Id</param>
        /// <returns></returns>
        public async Task<List<UWCommentVM>> GetUWCommentBySubIds(string[] submissionIdList)
        {
            _logger.LogInformation($"Calling externalSubmission service to UWComment for submissionId : {JsonConvert.SerializeObject(submissionIdList).ToString()}");
            var submissionNumbers = submissionIdList.Select(s => Convert.ToInt32(Regex.Replace(s, @"^[A-Za-z]+", ""))).ToArray();
            var url = $"{_configuration["ESBaseURL"]}/Underwriter/QMWC";
            var content = new StringContent(JsonConvert.SerializeObject(submissionNumbers).ToString(), Encoding.UTF8, "application/json");
            var result = await _httpClient.PostAsync(url, content);
            var response = await result.Content.ReadAsStringAsync();

            if (result.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError($"Error: Response from UWComment API Status={result.StatusCode}, response = {response}");
                return null;
            }

            _logger.LogInformation($" Response from UWComment API Status={result.StatusCode}, response = {response}");
            var uwCommentList = JsonConvert.DeserializeObject<Models.ResponseViewModel<List<UWCommentVM>>>(response);
            return uwCommentList.Response;
        }

        /// <summary>
        /// Get States code list by states ids
        /// </summary>
        /// <param name="stateIds"></param>
        /// <returns></returns>
        public async Task<List<StateVm>> GetStateByMultiIds(int[] stateIds)
        {
            var states = new List<StateVm>();
            var taskList = new List<Task<HttpResponseMessage>>();

            _logger.LogInformation($"Calling lookup state endpoint with url: {_configuration["LookupURL"]}states/ , for stateIds: {JsonConvert.SerializeObject(stateIds)}");
            foreach (int id in stateIds)
            {
                var getStateUrl = _configuration["LookupURL"] + $"states/{id}";
                taskList.Add(_httpClient.GetAsync(getStateUrl));
            }

            await Task.WhenAll(taskList);
            int taskIndex = 0;

            foreach (int id in stateIds)
            {
                var taskRslt = taskList[taskIndex].Result;
                var response = taskRslt.Content.ReadAsStringAsync().Result;

                if (taskRslt.StatusCode != HttpStatusCode.OK)
                    _logger.LogError($"error: response from lookup state api status={taskRslt.StatusCode}, response = {response}");
                else
                {
                    _logger.LogInformation($"Response from lookup state api status={taskRslt.StatusCode}, response = {response}");
                    states.Add(new StateVm() { Id = id, StateName = JsonConvert.DeserializeObject<string>(response) });
                }

                taskIndex++;
            }

            return states;
        }

        /// <summary>
        /// Get all entity type
        /// </summary>
        /// <returns>List of entity values</returns>
        public async Task<List<KeyValueObjVm>> GetAllEntityValue()
        {
            var lookupUrl = string.Format($"{_configuration["GatewayURL"]}gateway/lookup/entity_types");
            _logger.LogInformation($"Calling wc gateway lookup entity type endpoint with url: {lookupUrl}");
            var res = await _httpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from wc gateway lookup entity endpoint:{responseString}");
                return new List<KeyValueObjVm>();
            }

            _logger.LogInformation($"Response from wc gateway lookup entity endpoint:{responseString}");
            var entityTypes = JsonConvert.DeserializeObject<List<KeyValueObjVm>>(responseString);
            return entityTypes;
        }

        /// <summary>
        /// Get all industry experience key value
        /// </summary>
        /// <returns>List of entity values</returns>
        public async Task<List<KeyValueObjVm>> GetAllIndustryExpeValue()
        {
            var lookupUrl = string.Format($"{_configuration["GatewayURL"]}gateway/lookup/industry_experience");
            _logger.LogInformation($"Calling wc gateway lookup industry experience endpoint with url: {lookupUrl}");
            var res = await _httpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from wc gateway lookup industry experience endpoint:{responseString}");
                return new List<KeyValueObjVm>();
            }

            _logger.LogInformation($"Response from wc gateway lookup industry experience endpoint:{responseString}");
            var entityTypes = JsonConvert.DeserializeObject<List<KeyValueObjVm>>(responseString);
            return entityTypes;
        }

        ///<summary>
        ///This service method is used to call Appetite endpoint to get available carriers for multiple submission.
        ///</summary>
        public async Task<List<SubAvailableCarriersVm>> GetAppetiteByMultiSubmission(List<WCSubmissionV2> quoteSubmision)
        {
            var taskList = new List<Task<HttpResponseMessage>>();
            List<SubAvailableCarriersVm> availableCarriers = new List<SubAvailableCarriersVm>();
            var gatewayURL = $"{_configuration["GatewayURL"]}gateway/carrier/appetite";
            int taskIndex = 0;

            for (int i = 0; i < quoteSubmision.Count; i++)
            {
                var contentbody = JsonConvert.SerializeObject(quoteSubmision[0]);
                _logger.LogInformation($"Calling Gateway Appetite endpoint. URL:{gatewayURL}, body: wcubmission dom");
                var contentReq = new StringContent(contentbody.ToString(), Encoding.UTF8, "application/json");
                taskList.Add(_httpClient.PostAsync(gatewayURL, contentReq));
            }

            foreach (var subId in quoteSubmision.Select(s => s.SubmissionId))
            {
                var appRes = await taskList[0];
                var appContent = await appRes.Content.ReadAsStringAsync();

                if (appRes.StatusCode == HttpStatusCode.OK)
                {
                    _logger.LogInformation($"Response from Gateway Appetite endpoint. Code:{appRes.StatusCode} and response={appContent}");
                    var carriers = JsonConvert.DeserializeObject<List<string>>(appContent);
                    availableCarriers.Add(new SubAvailableCarriersVm() { SubId = subId, AvailableCarriers = carriers });
                }
                else
                {
                    _logger.LogError($"error: Response from Gateway Appetite endpoint. Code:{appRes.StatusCode} and response={appContent}");
                }

                taskIndex++;
            }

            return availableCarriers;
        }

        /// <summary>
        /// Method to get AmtrustSupplement for passed PolicyNumber.
        /// </summary>
        /// <param name="policyNumber"></param>
        /// <returns></returns>
        public async Task<List<ExternalSubmissionData.Client.AmtrustWorkersCompSupplementModel>> GetAmtrustSupplementByPolicyNum(string policyNumber)
        {
            _logger.LogInformation($"Calling externalSubmission service to get submission details from AmtustWorkersCompSupplement table for polcyNumber:{policyNumber}.");
            var client = new ExternalSubmissionData.Client.AmtustWorkersCompSupplementsClient(_configuration["BaseURL"]);
            var res = await client.Get("QMWC", policyNumber);
            _logger.LogInformation($"Reponse from  externalSubmission service for polcyNumber {policyNumber} is: {JsonConvert.SerializeObject(res)}.");

            if (res != null && res.Count() > 0)
                return res?.ToList();

            return null;
        }

        /// <summary>
        /// This method call wc ui submission endpoint to get browser info for multiple submissionId
        /// </summary>
        /// <param name="subIds">array of submission id</param>        
        /// <returns></returns>
        public async Task<List<SubmissionBrowserInfo>> GetBrowserInfoBySubIds(string[] subIds)
        {
            var browserInfoList = new List<SubmissionBrowserInfo>();
            var taskList = new List<Task<HttpResponseMessage>>();
            _httpContextAccessor.HttpContext.Request.Headers.Remove("x-access-token");
            _httpContextAccessor.HttpContext.Request.Headers.Add("x-access-token", _configuration["NonExpireyToken"]);
            var httpClient = new SmartHttpClient(_correlationIdProvider, _httpContextAccessor);

            _logger.LogInformation($"Calling wcui submission endpoint. URL:{_configuration["WCUIUrl"]}, for submissionIds:{JsonConvert.SerializeObject(subIds)}");
            foreach (string id in subIds)
                taskList.Add(httpClient.GetAsync(_configuration["WCUIUrl"] + $"/{id}"));

            await Task.WhenAll(taskList);
            int taskIndex = 0;

            foreach (string id in subIds)
            {

                var taskRslt = await taskList[taskIndex];
                var response = await taskRslt.Content.ReadAsStringAsync();

                if (taskRslt.StatusCode != HttpStatusCode.OK)
                    _logger.LogError($"error: response from wcui submission endpoint status={taskRslt.StatusCode}, response = {response}, for submissionId: {id}");
                else
                {
                    _logger.LogDebug($"Response from wcui submission endpoint status={taskRslt.StatusCode}, response = {response}, for submissionId: {id}");
                    JToken jToken = JToken.Parse(response);
                    var browserInfo = jToken["data"]["browserInfo"].ToObject<SubmissionBrowserInfo>();
                    browserInfo.SubmissionId = id;
                    browserInfoList.Add(browserInfo);
                }

                taskIndex++;
            }

            return browserInfoList;
        }

        /// <summary>
        /// This service method is used to save carrier wise premium.
        /// </summary>
        /// <param name="request">CarrierPremium request</param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> SaveCarrierPremium(CarrierPremium request)
        {
            var url = $"{_configuration["ESBaseURL"]}/Submission/CarrierPremium";
            var postBody = JsonConvert.SerializeObject(request);

            // Calling ExternalSubmissionDataV2's CarrierPremium endpoint.
            _logger.LogInformation($"Calling ExternalSubmissionDataV2's CarrierPremium endpoint. URL:{url}, Body: {postBody}");
            var content = new StringContent(postBody.ToString(), Encoding.UTF8, "application/json");
            var respns = await _httpClient.PostAsync(url, content);
            var responseString = await respns.Content.ReadAsStringAsync();
            _logger.LogInformation($"Response from CarrierPremium endpoint. Status:{respns.StatusCode}. Body:{responseString}");
            return respns;
        }

        ///<summary>
        ///This service method is used to call Appetite endpoint.
        ///</summary>
        public async Task<List<string>> GetAppetiteResponse(wcsubmission quoteResponse, List<string> carrierResAvailable)
        {
            List<string> availableCarriersNotSelected = new List<string>();
            var gatewayURL = $"{_configuration["GatewayURL"]}gateway/carrier/appetite";
            var contentbody = JsonConvert.SerializeObject(quoteResponse?.Submission);
            _logger.LogInformation($"Calling Gateway Appetite endpoint. URL:{gatewayURL}, body:{contentbody}");
            var contentReq = new StringContent(contentbody.ToString(), Encoding.UTF8, "application/json");
            var appRes = await _httpClient.PostAsync(gatewayURL, contentReq);
            var appContent = await appRes.Content.ReadAsStringAsync();
            _logger.LogInformation($"Response from Gateway Appetite endpoint. Code:{appRes.StatusCode} and response={appContent}");
            if (appRes.StatusCode == HttpStatusCode.OK)
            {
                var carriers = JsonConvert.DeserializeObject<List<string>>(appContent);
                availableCarriersNotSelected = carriers.Except(carrierResAvailable).ToList();
            }
            return availableCarriersNotSelected;
        }

        /// <summary>
        /// Method to get AmtrustSupplement for passed PolicyNumber.
        /// </summary>
        /// <param name="policyNumber"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public async Task<List<AmtrustWorkersCompSupplement>> GetAmtrustSupplementByPolicy(string policyNumber, string token)
        {
            var externalSubDatayUrl = $"{_configuration["ESBaseURL"]}/AmtustWorkersCompSupplements/QMWC/{policyNumber}";
            _logger.LogInformation($"Calling externalSubmission service endpoint with url: {externalSubDatayUrl}");
            _httpContextAccessor.HttpContext.Request.Headers.Remove("x-access-token");
            _httpContextAccessor.HttpContext.Request.Headers.Add("x-access-token", token);
            var _httpClient = new SmartHttpClient(_correlationIdProvider, _httpContextAccessor);
            var esdString = await _httpClient.GetAsync(externalSubDatayUrl);
            var esdResponse = await esdString.Content.ReadAsStringAsync();

            if (esdString.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError($"Error: Response from externalSubmission service endpoint Status={esdString.StatusCode}, response = {esdResponse}");
                return null;
            }
            else
            {
                _logger.LogInformation($"Response from externalSubmission service endpoint. Code:{esdString.StatusCode}, response:{esdResponse}");
                var subDetails = JsonConvert.DeserializeObject<Models.ResponseViewModel<List<AmtrustWorkersCompSupplement>>>(esdResponse);
                return subDetails.Response;
            }
        }

        ///<summary>
        ///This service method is used to call WC ui Submission endpoint.
        ///</summary>
        public async Task<List<string>> GetWcUiResponse(string submissionId)
        {
            List<string> availableCarriers = new List<string>();

            var gatewayURL = _configuration["WCUIUrl"] + $"/{submissionId}";
            _logger.LogDebug($"Calling WCUI submission endpoint. URL:{gatewayURL}");
            _httpContextAccessor.HttpContext.Request.Headers.Remove("x-access-token");
            _httpContextAccessor.HttpContext.Request.Headers.Add("x-access-token", _configuration["NonExpireyToken"]);
            var _httpClient = new SmartHttpClient(_correlationIdProvider, _httpContextAccessor);
            var appRes = await _httpClient.GetAsync(gatewayURL);
            var appContent = await appRes.Content.ReadAsStringAsync();
            _logger.LogDebug($"Response from WCUI submission endpoint. Code:{appRes.StatusCode} and response={appContent}");

            if (appRes.StatusCode == HttpStatusCode.OK)
            {
                var submissionRes = JObject.Parse(appContent);
                var postTitles = submissionRes.SelectTokens("data.value.quoteResponse[*].Carrier")?.Select(s => s.ToString()).ToList();
                availableCarriers = postTitles;

                if (!availableCarriers.Any())
                    _logger.LogWarning($"Response from WCUI submission endpoint response={appContent}, not contain quoteResponse[*].carrier values");

            }

            return availableCarriers;
        }

        /// <summary>
        /// Method to get WC carriers.
        /// </summary>
        public async Task<List<ExternalSubmissionData.Client.AmtrustWorkerscompCarriers>> GetWCCarriers()
        {
            _logger.LogInformation("Calling externalSubmission service to get AmtrustWorkersCompCarriers table.");
            var client = new ExternalSubmissionData.Client.AmtustWorkersCompSupplementsClient(_configuration["BaseURL"]);
            var res = await client.Get("QMWC");
            return res?.ToList();
        }

        /// <summary>
        /// Service to call wc submission API's Put endpoint.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="wcUniqueId"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> UpdateSubmission(WCUISubmission request, string wcUniqueId)
        {
            var submissionUrl = string.Format(_configuration["WCUIUrl"] + $"/{wcUniqueId}");
            var postBody = JsonConvert.SerializeObject(request);
            var content = new StringContent(postBody.ToString(), Encoding.UTF8, "application/json");
            _logger.LogDebug($"Calling WC submission API with Url: {submissionUrl} and Body: {postBody}");
            var res = await _httpClient.PutAsync(submissionUrl, content);
            var responseString = await res.Content.ReadAsStringAsync();
            _logger.LogDebug($"Response from WC submission API. Response Status:{res.StatusCode}, Body:{responseString}");
            return res;
        }

        /// <summary>
        /// Service Method to get titles based on Legal entities.
        /// </summary>
        /// <param name="entityType"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> GetEntityTitles(int entityType)
        {
            var url = $"{_configuration["WCLookup"]}legalentity/{entityType}/titles";
            // Calling lookup endpoint to get titles based on Legal entities.
            _logger.LogDebug($"Calling lookup endpoint with url: {url}");
            var res = await _httpClient.GetAsync(url);
            var responseString = await res.Content.ReadAsStringAsync();
            _logger.LogDebug($"Response from lookup endpoint. Response Status:{res.StatusCode}, Body:{responseString}");
            return res;
        }

        /// <summary>
        /// Service Method to get Limits
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> GetLimit(string state)
        {
            var url = $"{_configuration["WCLookup"]}lookup/limits?stateName={state}&effectiveDate={DateTime.Now}";
            // Calling lookup endpoint to get limit.
            _logger.LogDebug($"Calling lookup endpoint with url: {url}");
            var res = await _httpClient.GetAsync(url);
            var responseString = await res.Content.ReadAsStringAsync();
            _logger.LogDebug($"Response from lookup endpoint. Response Status:{res.StatusCode}, Body:{responseString}");
            return res;
        }

        /// <summary>
        /// Service Method to get Legal entities.
        /// </summary>
        /// <returns></returns>
        public async Task<HttpResponseMessage> GetEntity()
        {
            var url = $"{_configuration["WCLookup"]}lookup/entity_types";
            // Calling lookup endpoint to get legal entity type.
            _logger.LogDebug($"Calling lookup endpoint with url: {url}");
            var res = await _httpClient.GetAsync(url);
            var responseString = await res.Content.ReadAsStringAsync();
            _logger.LogDebug($"Response from lookup endpoint. Response Status:{res.StatusCode}, Body:{responseString}");
            return res;
        }

        /// <summary>
        /// Service to call wc submission API's Post endpoint.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public async Task<string> Submission(WCUISubmission request)
        {
            var submissionUrl = _configuration["WCUIUrl"];
            var postBody = JsonConvert.SerializeObject(request);
            var content = new StringContent(postBody.ToString(), Encoding.UTF8, "application/json");
            _logger.LogDebug($"Calling WC submission API with Url: {submissionUrl} and Body: {postBody}");
            var res = await _httpClient.PostAsync(submissionUrl, content);
            var responseString = await res.Content.ReadAsStringAsync();
            _logger.LogDebug($"Response from WC submission API. Response Status:{res.StatusCode}, Body:{responseString}");
            try
            {
                var response = JToken.Parse(responseString);
                var wcUniqueId = response["wcUniqueId"]?.Value<string>();
                return wcUniqueId;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Email send method.
        /// </summary>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <param name="cc"></param>
        /// <param name="subject"></param>
        /// <param name="body"></param>
        public async Task SendMail(string subject, string body)
        {
            _logger.LogDebug("Entered in Send email method.");
            MailMessage mail = new MailMessage();
            //List<EmailContent> emailContentJson = JsonConvert.DeserializeObject<List<EmailContent>>(_configuration["EmailRecipients"]);
            EmailContent publishFailEmailContent = emailContentJson.FirstOrDefault(s => s.EmailType == EmailType.PublishFailEmail.ToString());
            //set the addresses
            mail.From = new MailAddress(publishFailEmailContent?.EmailRecipients.From);
            for (int i = 0; i < publishFailEmailContent?.EmailRecipients.To?.Count; i++)
            {
                mail.To.Add(publishFailEmailContent?.EmailRecipients?.To?[i]?.ToString());
            }

            mail.IsBodyHtml = true;
            for (int i = 0; i < publishFailEmailContent?.EmailRecipients?.Cc?.Count; i++)
            {
                mail.CC.Add(publishFailEmailContent?.EmailRecipients?.Cc?[i]?.ToString());
            }

            for (int i = 0; i < publishFailEmailContent?.EmailRecipients?.Bcc?.Count; i++)
            {
                mail.Bcc.Add(publishFailEmailContent?.EmailRecipients?.Bcc?[i]?.ToString());
            }

            //set the content
            mail.Subject = subject;
            mail.Body = body;

            //send the message

            _logger.LogDebug($"SMTP Host URL:{_configuration["SmtpServer"]}");
            SmtpClient smtp = new SmtpClient(_configuration["SmtpServer"]);
            await smtp.SendMailAsync(mail);
            _logger.LogDebug("Exiting Send email method after sending email.");
        }

        /// <summary>
        /// Method service to call auditTrail api to save the details after queue publish. 
        /// </summary>
        /// <param name="auditRequest"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> AuditTrailService(object auditRequest )
        {
            var auditEventUrl = string.Format($"{_configuration["GatewayURL"]}gateway/Submission/auditEvent");
            _logger.LogInformation($"Calling wc gateway auditTrail Service with url: {auditEventUrl}");
            var jsonObject = JsonConvert.SerializeObject(auditRequest);
            var content = new StringContent(jsonObject.ToString(), Encoding.UTF8, "application/json");
            var res = await _httpClient.PostAsync(auditEventUrl, content);
            var responseString = await res.Content.ReadAsStringAsync();
            _logger.LogInformation($"Response from wc gateway lookup auditTrail endpoint:{responseString}");
            return res;
        }

        /// <summary>
        /// Service to call PolicyNumberGateway SellPolicy API endpoint.
        /// </summary>
        /// <param name="request">PolicyIssuanceRequest</param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> SellPolicy(PolicyIssuanceRequest request)
        {
            var url = _configuration["PolicyNumberGateway"] + "/api/PolicyNumber/Issue";
            var postBody = JsonConvert.SerializeObject(request);
            _logger.LogInformation($"Calling PolicyNumberGateway SellPolicy API endpoint. Endpoint:{url}, Body: {postBody}");
            var content = new StringContent(postBody.ToString(), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            _logger.LogInformation($"Response from PolicyNumberGatewaySellPolicy endpoint. Status:{response.StatusCode}");
            return response;
        }

        // This method is used to send issued email
        private async Task CancellationEmail(string submissionID, string agencyId, int contactId, WCSubmissionV2 submission)
        {
            try
            {
                EmailContent issuedEmailContent = emailContentJson.FirstOrDefault(s => s.EmailType == EmailType.IssuedEmail.ToString());
                // Calling agencyContact endpoint to get the contactDetails.
                var agentDetail = await GetAgencyContact(contactId);
                var footerURL = $"{_configuration["WCSubmissionUrl"]}EmailTemplate/wc-footer-new.png";
                var logoURL = $"{_configuration["WCSubmissionUrl"]}EmailTemplate/btis_amynta.png";
                var emailBody = await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(_env.WebRootPath, "EmailTemplate", "CancellationEmail.html"));
                var agentReplyTo = _configuration["AgentReplyTo"];
                emailBody = emailBody.Replace("[FooterImg]", footerURL)
                            .Replace("[BTISLogo]", logoURL);

                EmailDom emailDom = new EmailDom
                {
                    Category = new Category
                    {
                        Program = "QMWC",
                        Type = " Cancellation Email"
                    },
                    Content = new Content
                    {
                        Subject = string.Format(issuedEmailContent?.EmailRecipients?.Subject, submissionID),
                        Body = new Body
                        {
                            Html = emailBody
                        },
                        From = new From
                        {
                            Email = issuedEmailContent?.EmailRecipients?.From
                        },
                        To = new List<To>
                        {
                            new To
                            {
                                 Email = submission?.Contact?.Email? .ToString(),
                                 Name= submission.Contact?.FirstName + " " + submission.Contact.FirstName
                            }
                        },
                        //Attachments = new List<Models.Attachment>
                        //{
                        //    new Models.Attachment
                        //    {
                        //        Content = documentURL,
                        //        Filename= $"Policy Document {submissionID}.pdf"
                        //    }
                        //}
                    },
                    Metadata = new Metadata
                    {
                        AgencyId = Convert.ToInt32(agencyId),
                        ContactId = contactId
                    }
                };
               
                emailDom.Content.Cc.Add(new To { Email = agentDetail?.Email });
                issuedEmailContent.EmailRecipients.Bcc = issuedEmailContent?.EmailRecipients?.Bcc ?? new List<string>();
                for (int i = 0; i < issuedEmailContent?.EmailRecipients?.Bcc?.Count(); i++)
                {
                    emailDom.Content.Bcc.Add(new To { Email = issuedEmailContent?.EmailRecipients?.Bcc[i] });
                }

                _logger.LogDebug("Calling EmailQueuePublisher service method for sending agent email");
                await EmailQueuePublisher(emailDom);


            }
            catch (Exception ex)
            {
                _logger.LogError($"Error while sending pie email to agent: Exception: {ex.Message}. Stack Trace: {ex.StackTrace}");
            }
        }

        public async Task<wcsubmission> GetSubmissionLatestId(string submissionID, string transactionType, DateTime effectiveDate, string cancellationReason, string cancellationType, bool MPRFlag)
        {
            var endorsementUrl = _configuration["WCSubmissionUrl"] + "api/Endorsements/Submission/" + submissionID + "/Endorsement/" + transactionType + "?effectiveDate=" + effectiveDate + "&CancellationReason=" + cancellationReason + "&CancellationType=" + cancellationType + "&IsMPR=" + MPRFlag;
            var result = await _httpClient.PostAsync(endorsementUrl, null);
            var response = await result.Content.ReadAsStringAsync();
            if (!result.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from generic endorsement endpoint:{response}");
                return new wcsubmission();
            }
            var submission = JsonConvert.DeserializeObject<Models.ResponseViewModel<wcsubmission>>(response);
            return submission.Response;
        }

        /// <summary>
        /// Get all active rules.
        /// </summary>
        /// <returns>List of all active rules</returns>
        public async Task<List<RenewalRules>> GetRenewalRules()
        {
            var lookupUrl = string.Format($"{_configuration["GatewayURL"]}gateway/lookup/renewalEngine");
            _logger.LogInformation($"Calling wc gateway renewal rules endpoint with url: {lookupUrl}");
            var res = await _httpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from wc gateway renewal rules endpoint:{responseString}");
                return new List<RenewalRules>();
            }

            _logger.LogInformation($"Response from wc gateway lookup entity endpoint:{responseString}");
            var rules = JsonConvert.DeserializeObject<List<RenewalRules>>(responseString);
            return rules;
        }

        /// <summary>
        /// Get classcode by state.
        /// </summary>
        /// <param name="state">submission id</param>
        /// <returns>Return IQClassCodes dom based on state passed </returns>s
        public async Task <IQClassCodes> GetClassCodeByState(string state)
        {

            var url = $"{_configuration["GatewayURL"]}gateway/IQ/classCodeListByState?state={state}";

            _logger.LogInformation($"Calling excludedListByState endpoint with url: {url}");
            var res = await _httpClient.GetAsync(url);
            var responseString = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from excludedListByState endpoint:{responseString}");
                return new IQClassCodes();
            }

            _logger.LogInformation($"Response from excludedListByState endpoint:{responseString}");
            var data = JsonConvert.DeserializeObject<IQClassCodes>(responseString);
            return data;


        }
        /// <summary>
        /// This method is being used to call Configuration lookup gateway.
        /// </summary>
        /// <param name="state"></param>
        /// <param name="effectiveDate"></param>
        /// <returns>Configuration_Lookup</returns>
        public async Task<Configuration_Lookup> GetConfigurationByState(string state, DateTime effectiveDate)
        {
            string effDate = effectiveDate.ToString("yyyy-MM-dd");
            var lookupUrl = _configuration["WCGatewayLookup"] + $"configuration_lookup/state?state={state}&effectiveDate={effDate}";
            _logger.LogInformation($"Calling config lookup service endpoint with url: {lookupUrl}");
            var res = await _httpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from wc gateway lookup configuration_lookup :{responseString}");
                return new Configuration_Lookup();
            }
            _logger.LogInformation($"Response from wc gateway lookup  configuration_lookup endpoint:{responseString}");
            var configuration_Lookup = JsonConvert.DeserializeObject<Configuration_Lookup>(responseString);
            return configuration_Lookup;
        }

        /// <summary>
        /// This method is being used to call Agency Broker Fee lookup gateway.
        /// </summary>
        /// <param name="agencyId"></param>
        /// <param name="effectiveDate"></param>
        /// <returns>Configuration_Lookup</returns>
        public async Task<AgencyBrokerFee> GetAgencyBrokerFee(string agencyId, DateTime effectiveDate)
        {
            string effDate = effectiveDate.ToString("yyyy-MM-dd");
            var lookupUrl = _configuration["WCGatewayLookup"] + $"agencyBrokerFee/agencyid?AgencyId={agencyId}&effectiveDate={effDate}";
            _logger.LogInformation($"Calling Agency Broker Fee endpoint with url: {lookupUrl}");
            var res = await _httpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            _logger.LogInformation($"Response from Agency Broker Fee endpoint, StatusCode: {res.StatusCode}, Response :{responseString}");
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from wc gateway lookup Agency Broker Fee :{responseString}");
                return null;
            }
            _logger.LogInformation($"Response from wc gateway lookup Agency Broker Fee endpoint:{responseString}");
            var agencyBrokerFee = JsonConvert.DeserializeObject<Models.ResponseViewModel<AgencyBrokerFee>>(responseString);
            return agencyBrokerFee.Response;
        }

        public async Task<ExperienceMod> GetExperienceMod(string fein, string effectiveDate)
        {
            var lookupUrl = _configuration["WCLookup"] + $"fromFein/{fein}/getXMOD?effectiveDate={effectiveDate}";
            _logger.LogInformation($"Calling Experience Mod endpoint with url: {lookupUrl}");
            var res = await _httpClient.GetAsync(lookupUrl);
            var responseString = await res.Content.ReadAsStringAsync();
            _logger.LogInformation($"Response from  Experience Mod endpoint, StatusCode: {res.StatusCode}, Response :{responseString}");
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError($"Error: Response from wc gateway lookup Experience Mod :{responseString}");
                return null;
            }
            _logger.LogInformation($"Response from wc gateway lookup Experience Mod endpoint:{responseString}");
            var expMod = JsonConvert.DeserializeObject<Models.ExperienceMod>(responseString);
            return expMod;
        }

    }
}
