using BTIS.DotNetLogger.NonWeb;
using Newtonsoft.Json;
using NLog;
using NLog.LayoutRenderers;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using WCUWReport.Helper;
using WCUWReport.Models;

namespace WCUWReport
{
    public class WCUWEmailConsumer
    {
        private static string url = ConsulHelper.GetConsulKeyAsync("Common/URL/RabbitMQURL").Result;
        private readonly ILogger _iLogger;
        private readonly ICorrelationIdProvider _iCorrelationIdprovider;
        private readonly HttpClient _httpClient;
        private readonly LogHelper logHelper = new LogHelper();
        private readonly string emailContent = ConsulHelper.GetConsulKeyAsync("Services/WorkersComp/EmailRecipients").Result;
        private readonly string quoteURL = ConsulHelper.GetConsulKeyAsync("Common/URL/WCSubmission").Result;
        private readonly string documentURL = ConsulHelper.GetConsulKeyAsync("Common/URL/Document").Result;
        private readonly string authUserKeyUrl = ConsulHelper.GetConsulKeyAsync("Common/URL/Authentication").Result + "/userkey";
        private readonly string postJson = ConsulHelper.GetConsulKeyAsync("Services/WCUWConsumer/UserKeyRequest").Result;
        private readonly string maxRetries = ConsulHelper.GetConsulKeyAsync("Services/WCUWConsumer/MaxRetries").Result;
        private List<EmailContent> emailContentJson;

        public WCUWEmailConsumer()
        {
            LayoutRenderer.Register("correlationid", typeof(ContextLessCorrelationIdLayoutRenderer));
            var config = logHelper.GetLogConfig();
            LogManager.Configuration = config;
            _iLogger = LogManager.GetCurrentClassLogger();
            _iCorrelationIdprovider = ContextLessCorrelationIdProvider.Instance;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-access-token", ConsulHelper.GetConsulKeyAsync("Common/NonExpiringToken").Result);
            emailContentJson = JsonConvert.DeserializeObject<List<EmailContent>>(emailContent);
        }

        //public void SendEmail()
        //{
        //    Email email = new Email();
        //    var res = _httpClient.GetByteArrayAsync("https://www.africau.edu/images/default/sample.pdf").Result;
            
        //    Email.TestEmail(res);

        //}

        public async Task<bool> WCUWConsumer()
        {
            try
            {
                Email email = new Email();

                var factory = new ConnectionFactory() { HostName = "amqp://localhost" };
                factory.Uri = new Uri(url.Replace("amqp://", "amqps://"));
                factory.AutomaticRecoveryEnabled = true;
                factory.TopologyRecoveryEnabled = true;
              
                using (var connection = factory.CreateConnection("WC UW email listener"))
                using (var channel = connection.CreateModel())
                {
                    var queueName = await ConsulHelper.GetConsulKeyAsync("Services/WCUWConsumer/QueueName"); // test_queue
                    string deadQueue = $"{queueName}_dead"; // tets_queue_dead
                    string submissionId = string.Empty;
                    var gatewayURL = await ConsulHelper.GetConsulKeyAsync("Common/URL/WC_Gateway");
                    var agency = await ConsulHelper.GetConsulKeyAsync("Common/URL/Agency");
                    var proposalURL = await ConsulHelper.GetConsulKeyAsync("Common/URL/PIEWC");
                    var uwDashboardURL = await ConsulHelper.GetConsulKeyAsync("Services/WCUWConsumer/UWDashboardURL");
                    var excludeAgencies = JsonConvert.DeserializeObject<List<string>>(await ConsulHelper.GetConsulKeyAsync("Services/WorkersComp/ExcludeAgencies")) ?? new List<string>();
                    var excludeContacts = JsonConvert.DeserializeObject<List<int>>(await ConsulHelper.GetConsulKeyAsync("Services/WorkersComp/ExcludedContacts")) ?? new List<int>();

                    channel.ExchangeDeclare("Quote.exchange.direct", ExchangeType.Direct, true, false, null);
                    channel.QueueDeclare(queueName, true, false, false,
                         new Dictionary<string, object>
                        {
                        {"x-dead-letter-exchange", "wcuw.exchange.deadqueue"},
                        });
                    channel.QueueBind(queueName, "Quote.exchange.direct", queueName, null);
                    channel.BasicQos(0, 1, false);

                    // Do a simple poll of the queue.
                    var consumer = new EventingBasicConsumer(channel);
                    consumer.Received += async (model, ea) =>
                    {
                        var flag = true;
                        var data = ea.Body;
                        if (ea.BasicProperties.Headers != null && ea.BasicProperties.Headers.Count > 0)
                        {
                            var deathProperties = (List<object>)ea.BasicProperties.Headers["x-death"];
                            var lastRetry = (Dictionary<string, object>)deathProperties[1];
                            var count = Convert.ToInt32(lastRetry["count"].ToString());
                            if (count > Convert.ToInt32(maxRetries))
                            {
                                // send email to itprod
                                submissionId = Encoding.UTF8.GetString(data);
                                EmailContent UWEmailContent = emailContentJson.FirstOrDefault(s => s.EmailType == EmailType.UWErrors.ToString());
                                Email.EmailReject(submissionId, UWEmailContent);
                                channel.BasicAck(ea.DeliveryTag, false);
                                flag = false;
                            }
                        }
                        if (flag == true)
                        {
                            // The message is null if the queue was empty
                            if (data != null)
                            {
                                try
                                {
                                    // convert the message back from byte[] to a string
                                    submissionId = Encoding.UTF8.GetString(data);

                                    var getQuoteURL = quoteURL + $"Submission/{submissionId}";
                                    // Calling get Submission api of WCSubmission service.
                                    _iLogger.Debug($"Calling get Submission api of WCSubmission service for submissionID: {submissionId}, URL: {getQuoteURL}");
                                    var response = await _httpClient.GetAsync(getQuoteURL);
                                    var responseString = await response.Content.ReadAsStringAsync();
                                    _iLogger.Debug($"Response from SubmissionAPI. Code:{response.StatusCode}, response:{responseString}");
                                    if (response.StatusCode != HttpStatusCode.OK)
                                    {
                                        _iLogger.Error($"Error: Response from Submission Status={response.StatusCode}, response = {responseString}");
                                        // PublishToDeadQueue(channel, deadQueue, submissionId);
                                        channel.BasicReject(ea.DeliveryTag, false);
                                    }
                                    else
                                    {
                                        var quoteResponse = JsonConvert.DeserializeObject<ResponseViewModel<WCSubmission>>(responseString);

                                        // Agency details.
                                        var agencyUrl = agency + $"agency/{quoteResponse?.Response?.AgencyID.ToString()}";
                                        _iLogger.Debug($"Calling agency endpoint with url: {agencyUrl}");
                                        var agencyString = await _httpClient.GetAsync(agencyUrl);
                                        var agencyResponse = await agencyString.Content.ReadAsStringAsync();
                                        _iLogger.Debug($"Response from agency endpoint. Code:{agencyString.StatusCode}, response:{agencyResponse}");
                                        if (agencyString.StatusCode != HttpStatusCode.OK)
                                        {
                                            _iLogger.Error($"Error: Response from Agency API Status={agencyString.StatusCode}, response = {agencyResponse}");
                                            //PublishToDeadQueue(channel, deadQueue, submissionId);
                                            channel.BasicReject(ea.DeliveryTag, false);
                                        }
                                        else
                                        {
                                            var agencyDetails = JsonConvert.DeserializeObject<AgencyDetails>(agencyResponse);

                                            // Agency contact details
                                            var contactUrl = string.Format(agency + "agencycontact/{0}", quoteResponse?.Response?.ContactId.ToString());
                                            _iLogger.Debug($"Calling agency contact endpoint with url: {contactUrl}");
                                            var contactString = await _httpClient.GetAsync(contactUrl);
                                            var agencyContactResponse = await contactString.Content.ReadAsStringAsync();
                                            _iLogger.Debug($"Response from agencycontact endpoint. Code:{contactString.StatusCode}, response:{agencyContactResponse}");
                                            if (contactString.StatusCode != HttpStatusCode.OK)
                                            {
                                                _iLogger.Error($"Error: Response from Contact API Status={contactString.StatusCode}, response = {agencyContactResponse}");
                                                //PublishToDeadQueue(channel, deadQueue, submissionId);
                                                channel.BasicReject(ea.DeliveryTag, false);
                                            }
                                            else
                                            {
                                                var contactDetails = JsonConvert.DeserializeObject<ContactDetails>(agencyContactResponse);

                                                var getDocumentUrl = string.Format(documentURL, 1, "pdf") + submissionId;
                                                _iLogger.Debug($"Calling get document endpoint {getDocumentUrl} with submissionId: {submissionId}.");
                                                var documentRes = await _httpClient.GetAsync(getDocumentUrl);
                                                _iLogger.Debug($"Response from document endpoint. Code:{documentRes.StatusCode}");
                                                if (documentRes.StatusCode != HttpStatusCode.OK)
                                                {
                                                    _iLogger.Error($"Error: Response from get Document API Status={documentRes.StatusCode} , response = {await documentRes.Content.ReadAsStringAsync()}");
                                                    //PublishToDeadQueue(channel, deadQueue, submissionId);
                                                    channel.BasicReject(ea.DeliveryTag, false);
                                                }
                                                else
                                                {
                                                    var resByte = documentRes.StatusCode == HttpStatusCode.OK ? await documentRes.Content.ReadAsByteArrayAsync() : null;

                                                    #region Generating PIE proposal doc.
                                                    byte[] proposalDoc = null;
                                                    byte[] instantQuoteDoc = null;
                                                    if ((quoteResponse.Response.FinalCarrier ?? "")?.ToLower() == "pie")
                                                    {
                                                        var finalurl = quoteURL + $"Submission/{submissionId}/Carrier/{Carriers.PIE.ToString()}/Response";

                                                        // Calling get carrier response api of WCSubmission service.
                                                        _iLogger.Debug($"Calling get carrier response endpoint {finalurl} of WCSubmission service for submissionID={submissionId}");
                                                        var carrierResponse = await _httpClient.GetAsync(finalurl);
                                                        string quoteID = string.Empty;
                                                        if (carrierResponse?.StatusCode == HttpStatusCode.OK)
                                                        {
                                                            var carrierResponseString = await carrierResponse?.Content?.ReadAsStringAsync();
                                                            var carrierResponseDetails = JsonConvert.DeserializeObject<ResponseViewModel<CarrierResponse>>(carrierResponseString);
                                                            if (carrierResponseDetails?.Status == 200)
                                                            {
                                                                var carrierDetails = JsonConvert.DeserializeObject<ResponseViewModel<WCResponse>>(carrierResponseDetails?.Response?.Response?.ToString());
                                                                quoteID = carrierDetails?.Response?.submissionid;
                                                            }
                                                        }
                                                        if (!string.IsNullOrEmpty(quoteID))
                                                        {
                                                            var getProposalURL = proposalURL + $"/api/PIE/Quote/" + $"{quoteID}/Proposal";
                                                            _iLogger.Debug($"Calling get proposal api of Pie for pieId: {quoteID}, URL: {getProposalURL}");
                                                            // Calling get proposal endpoint
                                                            var proposalResponse = await _httpClient.GetAsync(getProposalURL);
                                                            var content = await proposalResponse.Content.ReadAsStringAsync();
                                                            _iLogger.Debug($"Response from proposal API. Code:{proposalResponse.StatusCode}, response:{content}");
                                                            if (proposalResponse.IsSuccessStatusCode)
                                                            {
                                                                var obj = JsonConvert.DeserializeObject<ResponseViewModel<byte[]>>(content);
                                                                proposalDoc = obj.Response;
                                                            }
                                                        }
                                                    }

                                                    if ((quoteResponse.Response.FinalCarrier ?? "")?.ToLower() == "pie")
                                                    {
                                                        var url = gatewayURL + $"gateway/Submission/{submissionId}/{quoteResponse.Response.FinalCarrier}/Application?iscoverletter=true";
                                                        var resDocument = await _httpClient.GetAsync(url);
                                                        if (resDocument.StatusCode == HttpStatusCode.OK)
                                                        {
                                                            instantQuoteDoc = await resDocument.Content.ReadAsByteArrayAsync();
                                                        }
                                                    }
                                                    #endregion

                                                    var userKeyVm = new UserKeyVm();
                                                    string uwDashboardLink = string.Empty;

                                                    if (agencyDetails?.id > 0)
                                                    {
                                                        var content = new StringContent(postJson, Encoding.UTF8, "application/json");
                                                        var res = await _httpClient.PostAsync(authUserKeyUrl, content);
                                                        var resContent = await res.Content.ReadAsStringAsync();
                                                        if (res.StatusCode != HttpStatusCode.OK)
                                                            _iLogger.Error($"Error: Response from Authentication user key endpoint. Status={res.StatusCode}, response = {resContent}");
                                                        else
                                                        {
                                                            userKeyVm.userKey = JsonConvert.DeserializeObject<UserKeyVm>(resContent)?.userKey;
                                                        }
                                                    }

                                                    if (!string.IsNullOrEmpty(userKeyVm?.userKey))
                                                    {
                                                        //{userKey}/{submissionID}/{source}
                                                        uwDashboardLink = uwDashboardURL.Replace("{userKey}", userKeyVm.userKey).Replace("{source}", userKeyVm.Source);
                                                    }
                                                    if (excludeAgencies.Contains(quoteResponse.Response.AgencyID) || excludeContacts.Contains(quoteResponse.Response.ContactId))
                                                    {
                                                        EmailContent uwemailContent = emailContentJson.FirstOrDefault(s => s.EmailType == EmailType.TestEmail.ToString());
                                                        email.UWEmail(quoteResponse?.Response, agencyDetails, contactDetails, resByte, proposalDoc, quoteURL, instantQuoteDoc, uwemailContent, uwDashboardLink);
                                                    }
                                                    else
                                                    {
                                                        EmailContent uwemailContent = emailContentJson.FirstOrDefault(s => s.EmailType == EmailType.UWEmail.ToString());
                                                        email.UWEmail(quoteResponse?.Response, agencyDetails, contactDetails, resByte, proposalDoc, quoteURL, instantQuoteDoc, uwemailContent, uwDashboardLink);
                                                    }
                                                    
                                                    channel.BasicAck(ea.DeliveryTag, false);
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // PublishToDeadQueue(channel, deadQueue, submissionId);
                                    channel.BasicReject(ea.DeliveryTag, false);

                                    // Calling EmailError method to send exception details via email.
                                    EmailContent uwemailContent = emailContentJson.FirstOrDefault(s => s.EmailType == EmailType.UWErrors.ToString());
                                    Email.EmailError(ex, uwemailContent);
                                    var error = $"Error: {ex.Message}{Environment.NewLine}InnerException: {ex.InnerException}{Environment.NewLine}StackTrace: {ex.StackTrace}";
                                    _iLogger.Error(error);
                                }
                            }
                        }
                    };

                    channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer, consumerTag: "uwEmail");

                    while (true)
                    {
                        await Task.Delay(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                // Calling EmailError method to send exception details via email.
                EmailContent UWEmailContent = emailContentJson.FirstOrDefault(s => s.EmailType == EmailType.UWErrors.ToString());
                Email.EmailError(ex, UWEmailContent);
                var error = $"Error: {ex.Message}{Environment.NewLine}InnerException: {ex.InnerException}{Environment.NewLine}StackTrace: {ex.StackTrace}";
                _iLogger.Error(error);
                return false;
            }
        }

        //private static void PublishToDeadQueue(IModel channel, string deadQueue, string submissionId)
        //{
        //    // Publishing failed submission to dead queue.
        //    var failedSubmission = Encoding.UTF8.GetBytes(submissionId);
        //    // the data put on the queue must be a byte array
        //    bool durable = true;
        //    bool exclusive = false;
        //    bool autoDelete = false;
        //    channel.QueueDeclare(deadQueue, durable, exclusive, autoDelete, null);

        //    // publish to the "default exchange", with the queue name as the routing key
        //    var exchangeName = "";
        //    var routingKey = deadQueue;
        //    channel.BasicPublish(exchangeName, routingKey, null, failedSubmission);
        //}

        public async Task<bool> WCPIEUWConsumer()
        {
            try
            {
                var factory = new ConnectionFactory() { HostName = "localhost" };
                factory.Uri = new Uri(url.Replace("amqp://", "amqps://"));
                factory.AutomaticRecoveryEnabled = true;
                factory.TopologyRecoveryEnabled = true;
                Email email = new Email();
                using (var connection = factory.CreateConnection("PIE UW email listener"))
                using (var channel = connection.CreateModel())
                {
                    var pieQueueName = await ConsulHelper.GetConsulKeyAsync("Services/WCUWConsumer/PIEQueueName");
                    string pieDeadQueue = $"{pieQueueName}_dead";
                    string submissionId = string.Empty;
                    var pieDocUploadURL = await ConsulHelper.GetConsulKeyAsync("Services/WCUWConsumer/PIEDocumentUpload");

                    channel.ExchangeDeclare("Quote.exchange.direct", ExchangeType.Direct, true, false, null);
                    channel.QueueDeclare(pieQueueName, true, false, false,
                         new Dictionary<string, object>
                        {
                        {"x-dead-letter-exchange", "wcuw.exchange.deadqueue"},
                        });
                    channel.QueueBind(pieQueueName, "Quote.exchange.direct", pieQueueName, null);
                    channel.BasicQos(0, 1, false);


                    // Do a simple poll of the queue.
                    var consumer = new EventingBasicConsumer(channel);
                    consumer.Received += async (model, ea) =>
                    {
                        var pieflag = true;
                        var pieData = ea.Body;
                        if (ea.BasicProperties.Headers != null && ea.BasicProperties.Headers.Count > 0)
                        {
                            var deathProperties = (List<object>)ea.BasicProperties.Headers["x-death"];
                            var lastRetry = (Dictionary<string, object>)deathProperties[1];
                            var count = Convert.ToInt32(lastRetry["count"].ToString());
                            if (count > Convert.ToInt32(maxRetries))
                            {
                                // send email to itprod
                                submissionId = Encoding.UTF8.GetString(pieData);
                                EmailContent UWEmailContent = emailContentJson.FirstOrDefault(s => s.EmailType == EmailType.UWErrors.ToString());
                                Email.EmailReject(submissionId, UWEmailContent);
                                channel.BasicAck(ea.DeliveryTag, false);
                                pieflag = false;
                            }
                        }
                        if (pieflag == true)
                        {
                            // The message is null if the queue was empty
                            if (pieData != null)
                            {
                                try
                                {
                                    // convert the message back from byte[] to a string
                                    submissionId = Encoding.UTF8.GetString(pieData);

                                    var getCarrierSpecResUrl = quoteURL + $"Submission/{submissionId}/Carrier/{Carriers.PIE.ToString()}/Response";

                                    // Calling get carrier specific response endpoint of WCSubmission service.
                                    _iLogger.Debug($"Calling get carrier specific response endpoint {getCarrierSpecResUrl} of WCSubmission service for submissionID: {submissionId}");
                                    var carrResponse = await _httpClient.GetAsync(getCarrierSpecResUrl);
                                    var responseString = await carrResponse?.Content?.ReadAsStringAsync();
                                    _iLogger.Debug($"Response from carrier response endpoint. Code:{carrResponse.StatusCode}, response: {responseString}");

                                    if (carrResponse.StatusCode != HttpStatusCode.OK)
                                    {
                                        _iLogger.Error($"Error: Response from carrier specific response endpoint Status={carrResponse.StatusCode}, response = {responseString}");
                                        //PublishToDeadQueue(channel, pieDeadQueue, submissionId);
                                        channel.BasicReject(ea.DeliveryTag, false);
                                    }
                                    else
                                    {
                                        var carrierResponseDetails = JsonConvert.DeserializeObject<ResponseViewModel<CarrierResponse>>(responseString);

                                        var carrierResponse = JsonConvert.DeserializeObject<ResponseViewModel<WCResponse>>(carrierResponseDetails?.Response?.Response?.ToString());

                                        // Checking carrier specific endpoint response.
                                        if (carrierResponse?.Status == (int)HttpStatusCode.OK)
                                        {
                                            // Calling get document endpoint.
                                            var getDocumentUrl = string.Format(documentURL, 11, "pdf") + submissionId;
                                            _iLogger.Debug($"Calling get document endpoint {getDocumentUrl} with submissionId: {submissionId}.");
                                            var documentRes = await _httpClient.GetAsync(getDocumentUrl);
                                            _iLogger.Debug($"Response from document endpoint. Code:{documentRes.StatusCode}");
                                            if (documentRes.StatusCode != HttpStatusCode.OK)
                                            {
                                                _iLogger.Error($"Error: Response from get Document API Status={documentRes.StatusCode} , response = {await documentRes.Content.ReadAsStringAsync()}");
                                                //PublishToDeadQueue(channel, pieDeadQueue, submissionId);
                                                channel.BasicReject(ea.DeliveryTag, false);
                                            }
                                            else
                                            {
                                                var resByte = await documentRes.Content.ReadAsByteArrayAsync();
                                                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "multipart/form-data;");
                                                var multipartContent = new MultipartFormDataContent();
                                                var byteArrayContent = new ByteArrayContent(resByte);
                                                byteArrayContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
                                                multipartContent.Add(byteArrayContent, "File", "document.pdf");

                                                // Calling PIE document upload endpoint.
                                                var finalUrl = pieDocUploadURL + $"{carrierResponse?.Response?.submissionid}";
                                                _iLogger.Debug($"Calling PIE document upload endpoint for quoteID: {carrierResponse?.Response?.submissionid}, URL: {finalUrl}");
                                                var docUploadRes = await _httpClient.PostAsync(finalUrl, multipartContent);
                                                _iLogger.Debug($"Response from document upload endpoint. Code:{docUploadRes.StatusCode}, response:{await docUploadRes.Content.ReadAsStringAsync()}");
                                                if (docUploadRes.StatusCode != HttpStatusCode.OK)
                                                {
                                                    _iLogger.Error($"Error: Response from document upload endpoint. Status={docUploadRes.StatusCode}, response = {await docUploadRes.Content.ReadAsStringAsync()}");
                                                    //PublishToDeadQueue(channel, pieDeadQueue, submissionId);
                                                    channel.BasicReject(ea.DeliveryTag, false);
                                                }
                                                else
                                                {
                                                    EmailContent pieEmailContent = emailContentJson.FirstOrDefault(s => s.EmailType == EmailType.PIEEmail.ToString());
                                                    email.PIEEmail(submissionId, resByte, pieEmailContent);
                                                    _iLogger.Debug("Acknowledging the message");
                                                    channel.BasicAck(ea.DeliveryTag, false);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            _iLogger.Debug("Acknowledging the message");
                                            channel.BasicAck(ea.DeliveryTag, false);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _iLogger.Error("Error: Publishing submissionId in dead queue due to exception");
                                    //PublishToDeadQueue(channel, pieDeadQueue, submissionId);
                                    channel.BasicReject(ea.DeliveryTag, false);

                                    // Calling EmailError method to send exception details via email.
                                    EmailContent uwemailContent = emailContentJson.FirstOrDefault(s => s.EmailType == EmailType.UWErrors.ToString());
                                    Email.EmailError(ex, uwemailContent);
                                    var error = $"Error: {ex.Message}{Environment.NewLine}InnerException: {ex.InnerException}{Environment.NewLine}StackTrace: {ex.StackTrace}";
                                    _iLogger.Error(error);
                                }
                            }
                        }
                    };

                    channel.BasicConsume(queue: pieQueueName, autoAck: false, consumer: consumer, consumerTag: "PIEDocUpload");

                    while (true)
                    {
                        await Task.Delay(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                // Calling EmailError method to send exception details via email.
                EmailContent UWEmailContent = emailContentJson.FirstOrDefault(s => s.EmailType == EmailType.UWErrors.ToString());
                Email.EmailError(ex, UWEmailContent);
                var error = $"Error: {ex.Message}{Environment.NewLine}InnerException: {ex.InnerException}{Environment.NewLine}StackTrace: {ex.StackTrace}";
                _iLogger.Error(error);
                return false;
            }
        }
    }
}
