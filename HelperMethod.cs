using BTIS.DotNetLogger.Standard;
using BTIS.Utility.Standard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using WC_Audits.Domain.DTO;
using WC_Audits.Domain.Enums;
using WC_Audits.Domain.External;
using WC_Audits.Domain.External.WCSubmission;
using WC_Audits.Domain.ResponseModel;
using WC_Audits.Domain.ViewModels;
using WC_Audits.Repository.AuditChecklistRepo;
using WC_Audits.Service.Utility;
using IHostingEnvironment = Microsoft.AspNetCore.Hosting.IWebHostEnvironment;

namespace WC_Audits.Service.Helper
{
    /// <summary>
    /// Helper Class
    /// </summary>
    public class HelperMethod : IHelperMethod
    {
        #region  --Inject Dependency--
        private readonly ITokenUtility _tokenUtil;
        private readonly ICorrelationIdProvider _correlationIdProvider;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IAuditChecklistRepository _auditChecklistRepository;
        private readonly IHostingEnvironment _hostingEnvironment;
        private readonly List<EmailContent> _emailContentJson;
        private readonly IConfiguration _configuration;
        private readonly IInternalClient _internalClient;
        private readonly ILogger _logger;
        private readonly SmartHttpClient _httpClient;

        public HelperMethod(ITokenUtility tokenUtility,
            IHostingEnvironment hostingEnvironment,
            List<EmailContent> emailContentJson,
            IConfiguration configuration,
        IInternalClient internalClient,
        ILogger<HelperMethod> logger, ICorrelationIdProvider correlationIdProvider,
        IHttpContextAccessor httpContextAccessor,
        IAuditChecklistRepository auditChecklistRepository)
        {
            _correlationIdProvider = correlationIdProvider;
            _httpContextAccessor = httpContextAccessor;
            _auditChecklistRepository = auditChecklistRepository;
            _httpClient = new SmartHttpClient(_correlationIdProvider, _httpContextAccessor);
            _tokenUtil = tokenUtility;
            _hostingEnvironment = hostingEnvironment;
            _emailContentJson = emailContentJson;
            _configuration = configuration;
            _internalClient = internalClient;
            _logger = logger;
        }
        #endregion

        #region General Methods
        /// <summary>
        /// This method maps the properties of ResponseView model.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="status"></param>
        /// <param name="statusDescription"></param>
        /// <param name="response"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        public static ResponseViewModel<T> ResponseMapping<T>(int status, string statusDescription, T response, List<Error> error)
        {
            ResponseViewModel<T> res = new ResponseViewModel<T>
            {
                Status = status,
                StatusDescription = statusDescription,
                Response = response,
                Error = error
            };
            return res;
        }


        /// <summary>
        /// This method is used to Authorize for those endpoints which are available only for BTIS.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public Task<ResponseViewModel<object>> EndpointAuthorization(HttpRequest request)
        {
            _ = new ResponseViewModel<object>();
            // Checking if token is present or not in request.
            var tokenString = _tokenUtil.GetTokenFromHeader(request.Headers).ToString();
            ResponseViewModel<object>? response;
            // Checking if token is valid.
            if (!_tokenUtil.IsValidToken(tokenString))
            {
                // If the token is not authenticate.
                response = ResponseMapping<object>((int)HttpStatusCode.Unauthorized, "Unauthorized - Invalid Token", null, null);
                return Task.FromResult(response);
            }
            response = ResponseMapping<object>((int)HttpStatusCode.OK, "Success", null, null);
            return Task.FromResult(response);
        }
        #endregion

        public async Task<List<FailedEmails>> PrepareEmailAndSend(EmailOperations operationType, List<wcsubmission> submissions, string doneBy = "System")
        {
            List<FailedEmails> failedEmails = new();
            var emailList = new List<string>();
            // Get the email receipients.
            var emailContent = _emailContentJson.FirstOrDefault(a => a.EmailType == operationType.ToString());

            var agencyContacts = new List<AgencyContact>();
            var contactIds = submissions.Select(a => Convert.ToInt32(a.ContactId)).Distinct().ToArray();
            agencyContacts = await _internalClient.AgencyContact(contactIds);
       
           
            // Get the email template.
            var fileName = $"{operationType}.html";
            string commonEmailBody = string.Empty;
            try
            {
                //commonEmailBody = await File.ReadAllTextAsync(Path.Combine(_hostingEnvironment.WebRootPath, "EmailTemplates", fileName));
                commonEmailBody = await _internalClient.FetchEmailTemplate(fileName);
            }
            catch (Exception)
            {
                throw;
                // Write code to change the subject is no templates available for this email type.
            }

            //var footerURL = $"{_configuration["WCAuditsUrl"]}EmailTemplates/wc-footer-new.png";
            //var logoURL = $"{_configuration["WCAuditsUrl"]}EmailTemplates/btis_amynta.png";
            var attachmentUrl = $"{_configuration["WCAuditsUrl"]}Attachments/clear_spring_wc_auditform.pdf";
            //commonEmailBody = commonEmailBody.Replace("[FooterImg]", footerURL).Replace("[BTISLogo]", logoURL);
            foreach (var submission in submissions)
            {
                string submissionId = submission.SubmissionId.Contains("-") ? submission.SubmissionId.Substring(0, submission.SubmissionId.IndexOf("-")) : submission.SubmissionId;
                FailedEmails failedEmail = new();
                try
                {
                    DateTime expDateAfter30Days = Convert.ToDateTime(submission?.Submission?.ProposedExpirationDate).AddDays(30);
                    var agencyContact = agencyContacts.Where(ai => ai.Id == Convert.ToInt32(submission.ContactId)).FirstOrDefault();
                    var insuredName = submission.Submission.Applicant.LegalEntity == 7 ?
                        $"{submission.Submission.Applicant.InsuredFirstName} {submission.Submission.Applicant.InsuredLastName}" :
                        submission.Submission.Applicant.LegalEntityName;

                    if(operationType == EmailOperations.Warning || operationType == EmailOperations.AuditPastDue)
                    {

                       commonEmailBody = await PrepareWarningEmailBody(submission.PolicyNumber);
                    }

                    var submissionEmailBody = commonEmailBody
                        .Replace("[AgentPhoneNumber]", agencyContact?.Phone != null ? agencyContact?.Phone : String.Empty)
                        .Replace("[AgentEmail]", agencyContact?.Email != null ? agencyContact?.Email : String.Empty)
                        .Replace("[LegalName]", submission.Submission.Applicant.LegalEntityName)
                        .Replace("[DBA]", submission.Submission.Applicant.DBA)
                        .Replace("[BusinessName]", insuredName)
                        .Replace("[PolicyNumber]", submission.PolicyNumber)
                        .Replace("[EffectiveDate]", submission?.Submission?.ProposedEffectiveDate.Value.ToString("MM-dd-yyyy"))
                        .Replace("[ExpirationDate]", submission?.Submission?.ProposedExpirationDate.Value.ToString("MM-dd-yyyy"));
                        //.Replace("[30daysAfterExpiration]", expDateAfter30Days.ToString("MM-dd-yyyy"));



                    EmailDom emailDom = new()
                    {
                        Category = new Category
                        {
                            Program = "QMWC",
                            Type = emailContent.EmailType
                        },
                        Content = new Content
                        {
                            Subject = emailContent.EmailRecipients.Subject.Replace("[PolicyNumber]", submission.PolicyNumber),
                            Body = new Body
                            {
                                Html = submissionEmailBody
                            },
                            From = new From
                            {
                                Email = emailContent.EmailRecipients.From
                            },
                            Cc = operationType == EmailOperations.Warning || operationType == EmailOperations.AuditPastDue ?
                            new List<To>
                            {
                                new To
                                {
                                    Email = agencyContact?.Email
                                }
                            } : new List<To>(),

                            Attachments = operationType == EmailOperations.Welcome || operationType == EmailOperations.Warning || operationType == EmailOperations.AuditPastDue ?
                            new List<Attachment>()
                            {
                                new Attachment
                                {
                                    Content = attachmentUrl,
                                    Filename = $"clear_spring_wc_auditform.pdf"
                                }
                            } :
                            new List<Attachment>()

                        },
                        Metadata = new Metadata
                        {
                            AgencyId = Convert.ToInt32(submission.AgencyID),
                            ContactId = submission.ContactId
                        }
                    };
                    emailContent.EmailRecipients.To = emailContent?.EmailRecipients?.To ?? new List<string>();
                    if (emailContent?.EmailRecipients.To.Count == 0)
                        emailDom.Content.To.Add(new To { Email = submission.Submission.Contact.Email });
                    else
                    {
                        for (int i = 0; i < emailContent?.EmailRecipients?.To?.Count; i++)
                        {
                            emailDom.Content.To.Add(new To { Email = emailContent?.EmailRecipients?.To[i] });
                        }
                    }

                    emailContent.EmailRecipients.Cc = emailContent?.EmailRecipients?.Cc ?? new List<string>();
                    for (int i = 0; i < emailContent?.EmailRecipients?.Cc?.Count; i++)
                    {
                        emailDom.Content.Cc.Add(new To { Email = emailContent?.EmailRecipients?.Cc[i] });
                    }

                    emailContent.EmailRecipients.Bcc = emailContent?.EmailRecipients?.Bcc ?? new List<string>();
                    for (int i = 0; i < emailContent?.EmailRecipients?.Bcc?.Count; i++)
                    {
                        emailDom.Content.Bcc.Add(new To { Email = emailContent?.EmailRecipients?.Bcc[i] });
                    }
                    HttpResponseMessage emailPublished = new();
                    try
                    {
                        emailPublished = await _internalClient.EmailQueuePublisher(emailDom);
                        if (emailPublished.IsSuccessStatusCode)
                        {
                            // calling Audit trail service to save records to user.
                            var requestDOM = new
                            {
                                Event = $"{operationType} email sent.",
                                SubmissionId = submissionId,
                                DoneBY = doneBy,
                                LOB = "QMWC",
                                ContactId = 0
                            };
                            _logger.LogDebug("Calling AuditTrailService service method for tracking details.");
                            HttpResponseMessage auditTrailRes = new();
                            try
                            {
                                auditTrailRes = await _internalClient.AuditTrailService(requestDOM);
                                if (!auditTrailRes.IsSuccessStatusCode)
                                {
                                    string errorMessage = string.Format("Audit Email : Error from audit trail for. SubmissionId:{0}, Status:{1} and Response:{2}", submission.SubmissionId, auditTrailRes.StatusCode.ToString(), auditTrailRes);
                                    _logger.LogError($"{errorMessage}");
                                    string response = await PrepareErrorEmailAndSend(errorMessage, operationType);
                                    failedEmail.SubmissionId = submissionId;
                                    failedEmail.ErrorMessage = $"Save AuditTrailService failed for Audit {operationType}";
                                    failedEmails.Add(failedEmail);
                                    continue; // continue for next record
                                }
                            }
                            catch (Exception ex)
                            {
                                string errorMessage = string.Format("Audit Email : Exception occured while calling audit trail for. OperationType:{0}, SubmissionId:{1}, Info : Exception:{2}, Stack Trace{3}", operationType, submissionId, ex.Message, ex.StackTrace);
                                _logger.LogError($"{errorMessage}");
                                string response = await PrepareErrorEmailAndSend(errorMessage, operationType);
                                failedEmail.SubmissionId = submissionId;
                                failedEmail.ErrorMessage = $"{errorMessage}";
                                failedEmails.Add(failedEmail);
                            }
                        }
                        else
                        {
                            string errorMessage = string.Format("Audit Email : Error from EmailQueuePublisher for. OperationType:{0}, SubmissionId:{1}, Status:{2} and Response:{3}", operationType, submissionId, emailPublished.StatusCode.ToString(), emailPublished);
                            _logger.LogError($"{errorMessage}");
                            string response = await PrepareErrorEmailAndSend(errorMessage, operationType);
                            failedEmail.SubmissionId = submissionId;
                            failedEmail.ErrorMessage = $"{errorMessage}";
                            failedEmails.Add(failedEmail);
                            continue; // continue for next record
                        }
                    }
                    catch (Exception ex)
                    {
                        string errorMessage = $"Audit Email : Exception occured while sending {operationType} email send failed for {submissionId}. Info : Exception: {ex.Message}. Stack Trace: {ex.StackTrace}";
                        _logger.LogError($"{errorMessage}");
                        string response = await PrepareErrorEmailAndSend(errorMessage, operationType);
                        failedEmail.SubmissionId = submissionId;
                        failedEmail.ErrorMessage = $"{errorMessage}";
                        failedEmails.Add(failedEmail);
                    }
                }
                catch (Exception ex)
                {
                    string errorMessage = $"Audit Email : Exception occured while sending {operationType} email send failed for {submissionId}. Info : Exception: {ex.Message}. Stack Trace: {ex.StackTrace}";
                    _logger.LogError(errorMessage);
                    string response = await PrepareErrorEmailAndSend(errorMessage, operationType);
                    failedEmail.SubmissionId = submissionId;
                    failedEmail.ErrorMessage = $"EmailQueuePublisher failed for Audit {operationType}";
                    failedEmails.Add(failedEmail);
                }
            }
            return failedEmails;
        }

        public async Task<string> PrepareErrorEmailAndSend(string errorMessage, EmailOperations operationType)
        {
            var auditEmailConfig = _emailContentJson.FirstOrDefault(a => a.EmailType == operationType.ToString());

            EmailDom emailDom = new EmailDom
            {
                Category = new Category
                {
                    Program = "QMWC",
                    Type = "Audit exception"
                },
                Content = new Content
                {
                    Subject = "WC Audit Error",
                    Body = new Body
                    {
                        Html = errorMessage
                    },
                    From = new From
                    {
                        Email = auditEmailConfig.EmailRecipients.From,
                    }
                },

            };
            auditEmailConfig.EmailRecipients.To = auditEmailConfig.EmailRecipients?.To ?? new List<string>();
            for (int i = 0; i < auditEmailConfig.EmailRecipients?.To?.Count(); i++)
            {
                emailDom.Content.To.Add(new To { Email = auditEmailConfig.EmailRecipients?.To[i] });
            }
            auditEmailConfig.EmailRecipients.Cc = auditEmailConfig.EmailRecipients?.Cc ?? new List<string>();
            for (int i = 0; i < auditEmailConfig.EmailRecipients?.Cc?.Count(); i++)
            {
                emailDom.Content.Cc.Add(new To { Email = auditEmailConfig.EmailRecipients?.Cc[i] });
            }
            auditEmailConfig.EmailRecipients.Bcc = auditEmailConfig.EmailRecipients?.Bcc ?? new List<string>();
            for (int i = 0; i < auditEmailConfig.EmailRecipients?.Bcc?.Count(); i++)
            {
                emailDom.Content.Bcc.Add(new To { Email = auditEmailConfig.EmailRecipients?.Bcc[i] });
            }
            var response = _internalClient.EmailErrorAsync(emailDom).Result;
            return response.StatusCode.ToString();
        }

        public async Task<string> PrepareAuditSubmittedEmailAndSend(string emailType, string policyNumber)
        {
            var auditEmailConfig = _emailContentJson.FirstOrDefault(a => a.EmailType == emailType);

            EmailDom emailDom = new EmailDom
            {
                Category = new Category
                {
                    Program = "QMWC",
                    Type = "Audit Submitted"
                },
                Content = new Content
                {
                    Subject = "WC Audit Submitted",
                    Body = new Body
                    {
                        Html = $"Audit has been added for Policynumber : {policyNumber}"
                    },
                    From = new From
                    {
                        Email = auditEmailConfig.EmailRecipients.From,
                    },

                    Attachments = new List<Attachment>()

                },

            };

            auditEmailConfig.EmailRecipients.To = auditEmailConfig.EmailRecipients?.To ?? new List<string>();
            for (int i = 0; i < auditEmailConfig.EmailRecipients?.To?.Count(); i++)
            {
                emailDom.Content.To.Add(new To { Email = auditEmailConfig.EmailRecipients?.To[i] });
            }
            auditEmailConfig.EmailRecipients.Cc = auditEmailConfig.EmailRecipients?.Cc ?? new List<string>();
            for (int i = 0; i < auditEmailConfig.EmailRecipients?.Cc?.Count(); i++)
            {
                emailDom.Content.Cc.Add(new To { Email = auditEmailConfig.EmailRecipients?.Cc[i] });
            }
            auditEmailConfig.EmailRecipients.Bcc = auditEmailConfig.EmailRecipients?.Bcc ?? new List<string>();
            for (int i = 0; i < auditEmailConfig.EmailRecipients?.Bcc?.Count(); i++)
            {
                emailDom.Content.Bcc.Add(new To { Email = auditEmailConfig.EmailRecipients?.Bcc[i] });
            }
            var response = _internalClient.EmailQueuePublisher(emailDom).Result;
            return response.StatusCode.ToString();

        }


        public async Task PrepareAuditChecklistEmailAndSend(EmailOperations operationType, wcsubmission wcsubmission, StringBuilder stringBuilder, string doneBy = "Underwriter")
        {
            var auditEmailConfig = _emailContentJson.FirstOrDefault(a => a.EmailType == operationType.ToString());

            var agencydetails = _internalClient.GetAgencyContact(wcsubmission.ContactId).Result;

            string commonEmailBody = string.Empty;

            //commonEmailBody = await File.ReadAllTextAsync(Path.Combine(_hostingEnvironment.WebRootPath, "EmailTemplates", "PremiumAudit.html"));
            commonEmailBody = await _internalClient.FetchEmailTemplate(Constant.PremiumAudit);

            //var footerURL = $"{_configuration["WCAuditsUrl"]}EmailTemplates/wc-footer-new.png";
            //var logoURL = $"{_configuration["WCAuditsUrl"]}EmailTemplates/btis_amynta.png";
            var attachmentUrl = $"{_configuration["WCAuditsUrl"]}Attachments/clear_spring_wc_auditform.pdf";


            commonEmailBody = commonEmailBody.
                Replace("[LegalName]", wcsubmission?.Submission?.Applicant?.LegalEntityName).
                Replace("[dba]", wcsubmission?.Submission?.Applicant?.DBA).
                Replace("[policy number]", wcsubmission?.PolicyNumber).
                Replace("[current policy eff date]", $"{wcsubmission?.Submission?.ProposedEffectiveDate.Value.ToString("MM-dd-yyyy")} to {wcsubmission?.Submission?.ProposedExpirationDate.Value.ToString("MM-dd-yyyy")}"). //need to confirm the value with senior dev
                Replace("[missing items]", stringBuilder.ToString()).
                Replace("[DUE DATE]", DateTime.Now.AddDays(Constant.ChecklistDueDateDays).ToShortDateString()).
                Replace("[AgentEmail]", agencydetails?.Email != null ? agencydetails?.Email : String.Empty);

            EmailDom emailDom = new EmailDom
            {
                Category = new Category
                {
                    Program = "QMWC",
                    Type = auditEmailConfig.EmailType
                },
                Content = new Content
                {
                    Subject = auditEmailConfig.EmailRecipients.Subject.Replace("[PolicyNumber]", wcsubmission?.PolicyNumber),
                    Body = new Body
                    {
                        Html = commonEmailBody
                    },
                    From = new From
                    {
                        Email = auditEmailConfig.EmailRecipients.From,
                    },
                    To = new List<To>
                    {
                        new To
                        {
                            Email = wcsubmission.Submission.Contact.Email,
                        }
                    },
                    Attachments = new List<Attachment>()
                    {
                        new Attachment
                        {
                            Content = attachmentUrl,
                            Filename = $"clear_spring_wc_auditform.pdf"
                        }
                    }
                },

                //},
                Metadata = new Metadata
                {
                    AgencyId = Convert.ToInt32(wcsubmission?.AgencyID),
                    ContactId = wcsubmission.ContactId
                }
            };

            auditEmailConfig.EmailRecipients.To = auditEmailConfig.EmailRecipients?.To ?? new List<string>();
            for (int i = 0; i < auditEmailConfig.EmailRecipients?.To?.Count(); i++)
            {
                emailDom.Content.To.Add(new To { Email = auditEmailConfig.EmailRecipients?.To[i] });
            }
            auditEmailConfig.EmailRecipients.Cc = auditEmailConfig.EmailRecipients?.Cc ?? new List<string>();
            for (int i = 0; i < auditEmailConfig.EmailRecipients?.Cc?.Count(); i++)
            {
                emailDom.Content.Cc.Add(new To { Email = auditEmailConfig.EmailRecipients?.Cc[i] });
            }
            auditEmailConfig.EmailRecipients.Bcc = auditEmailConfig.EmailRecipients?.Bcc ?? new List<string>();
            for (int i = 0; i < auditEmailConfig.EmailRecipients?.Bcc?.Count(); i++)
            {
                emailDom.Content.Bcc.Add(new To { Email = auditEmailConfig.EmailRecipients?.Bcc[i] });
            }

            try
            {
                var response = _internalClient.EmailQueuePublisher(emailDom).Result;

                if (response.IsSuccessStatusCode)
                {
                    var requestDOM = new
                    {
                        Event = $"{operationType} email sent.",
                        wcsubmission.SubmissionId,
                        DoneBY = doneBy,
                        LOB = "QMWC",
                        ContactId = wcsubmission.ContactId
                    };
                    _logger.LogDebug("Calling AuditTrailService service method for tracking details.");

                    var auditTrailRes = await _internalClient.AuditTrailService(requestDOM);
                    if (!auditTrailRes.IsSuccessStatusCode)
                    {
                        string errorMessage = string.Format("Audit Email : Error from audit trail for. SubmissionId:{0}, Status:{1} and Response:{2}", wcsubmission.SubmissionId, auditTrailRes.StatusCode.ToString(), auditTrailRes);
                        _logger.LogError($"{errorMessage}");
                        string res = await PrepareErrorEmailAndSend(errorMessage, EmailOperations.PublishFailEmail);
                        _logger.LogError("AuditChecklist Process : ErrorEmail send response: " + res);

                    }
                }
                else
                {
                    string errorMessage = string.Format("Audit Email : Error from EmailQueuePublisher for. OperationType:{0}, SubmissionId:{1}, Status:{2} and Response:{3}", operationType, wcsubmission.SubmissionId, response.StatusCode.ToString(), response);
                    _logger.LogError($"{errorMessage}");
                    string res = await PrepareErrorEmailAndSend(errorMessage, EmailOperations.PublishFailEmail);
                    _logger.LogError("AuditChecklist Process : ErrorEmail send response: " + res);

                }


            }
            catch (Exception ex)
            {
                var error = $"Error: {ex.Message}{Environment.NewLine}InnerException: {ex.InnerException}{Environment.NewLine}StackTrace: {ex.StackTrace}";
                _logger.LogError($"{error}");
                string res = await PrepareErrorEmailAndSend(error, EmailOperations.PublishFailEmail);
                _logger.LogError("AuditChecklist Process : ErrorEmail send response: " + res);
            }

        }

        private async Task<string> PrepareWarningEmailBody(string policyNumber)
        {
            var commonEmailBody = string.Empty;

            var auditChecklist = _auditChecklistRepository.GetAuditChecklistByPolicyNumber(policyNumber).Result;

            if(auditChecklist != null && (auditChecklist.AuditWorksheet.Completed == false || auditChecklist?.PayrollVerification.Completed == false || auditChecklist?.SubContract.Completed == false) )
            {
                var strBuilder = new StringBuilder();

                //AuditWorksheet
                if (auditChecklist.AuditWorksheet.Completed == false)
                {
                    var pagesToPrint = auditChecklist?.AuditWorksheet?.Pages?.Where(p => p.Checked != true).ToList();

                    if (pagesToPrint?.Count > 0)
                    {
                        foreach (var item in pagesToPrint)
                        {
                            strBuilder.Append($"<li>{auditChecklist?.AuditWorksheet?.Name} - {item?.PageName}</li>");
                        }
                    }

                    if (!string.IsNullOrEmpty(auditChecklist?.AuditWorksheet?.Comment))
                    {
                        strBuilder.Append($"<li>{auditChecklist?.AuditWorksheet?.Name} - {auditChecklist?.AuditWorksheet.Comment}</li>");
                    }
                }

                //Payroll Varification
                if (auditChecklist?.PayrollVerification.Completed == false)
                {
                    if (auditChecklist.PayrollVerification.SelectedOption == Constant.NoPayroll)
                    {
                        var formData = auditChecklist?.PayrollVerification?.FormsInfos?.Where(f => f.Checked != true).ToList();

                        if (formData?.Count > 0)
                        {
                            foreach (var item in formData)

                            {
                                strBuilder.Append($"<li>No Employee {auditChecklist?.PayrollVerification.Name} - {item?.FormName}</li>");
                            }

                        }

                        if (!string.IsNullOrEmpty(auditChecklist?.PayrollVerification?.Comment))
                        {
                            strBuilder.Append($"<li>No Employee {auditChecklist.PayrollVerification.Name} - {auditChecklist.PayrollVerification?.Comment}</li>");
                        }

                    }
                    else
                    {
                        var quarterlyInfos = auditChecklist?.PayrollVerification?.QuarterlyInfos?.Where(q => q.Checked != true).ToList();

                        if (quarterlyInfos?.Count > 0)
                        {
                            foreach (var item in quarterlyInfos)
                            {
                                strBuilder.Append($"<li>{auditChecklist?.PayrollVerification?.Name} - {auditChecklist?.PayrollVerification?.SelectedOption}s: Period of {item?.FromDate} - {item?.ToDate}</li>");

                            }

                        }

                        if (!string.IsNullOrEmpty(auditChecklist?.PayrollVerification?.Comment))
                        {
                            strBuilder.Append($"<li>{auditChecklist.PayrollVerification.Name} - {auditChecklist.PayrollVerification?.Comment}</li>");
                        }

                    }

                }

                //SubContractor
                if (auditChecklist?.SubContract.Completed == false)
                {
                    var reqInfolist = auditChecklist.SubContract.RequiredInfos?.Where(i => i.Checked != true).ToList();

                    if (reqInfolist?.Count > 0)
                    {
                        foreach (var item in reqInfolist)
                        {
                            strBuilder.Append($"<li>{auditChecklist?.SubContract?.Name} - {item?.Name}</li>");
                        }
                    }

                    if (!string.IsNullOrEmpty(auditChecklist.SubContract.Comment))
                    {
                        strBuilder.Append($"<li>{auditChecklist.SubContract.Name} - {auditChecklist.SubContract.Comment}</li>");
                    }
                }


                //commonEmailBody = await File.ReadAllTextAsync(Path.Combine(_hostingEnvironment.WebRootPath, "EmailTemplates", "CheckListWarning.html"));
                commonEmailBody = await _internalClient.FetchEmailTemplate(Constant.CheckListWarning);


                commonEmailBody = commonEmailBody.Replace("[missing items]", strBuilder.ToString());

            }
            else
            {
                //commonEmailBody = await File.ReadAllTextAsync(Path.Combine(_hostingEnvironment.WebRootPath, "EmailTemplates", "Warning.html"));
                commonEmailBody = await _internalClient.FetchEmailTemplate(Constant.Warning);

            }

            return commonEmailBody;

        }

    }
}