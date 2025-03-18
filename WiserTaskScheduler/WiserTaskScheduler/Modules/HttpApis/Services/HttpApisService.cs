using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Xml;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WiserTaskScheduler.Core.Enums;
using WiserTaskScheduler.Core.Helpers;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models;
using WiserTaskScheduler.Core.Models.OAuth;
using WiserTaskScheduler.Modules.Body.Interfaces;
using WiserTaskScheduler.Modules.HttpApis.Interfaces;
using WiserTaskScheduler.Modules.HttpApis.Models;

namespace WiserTaskScheduler.Modules.HttpApis.Services
{
    /// <summary>
    /// A service for a HTTP API action.
    /// </summary>
    public class HttpApisService : IHttpApisService, IActionsService, IScopedService
    {
        private readonly IOAuthService oAuthService;
        private readonly IBodyService bodyService;
        private readonly ILogService logService;
        private readonly ILogger<HttpApisService> logger;

        private bool retryOAuthUnauthorizedResponse;

        /// <summary>
        /// Creates a new instance of <see cref="HttpApisService"/>.
        /// </summary>
        /// <param name="oAuthService"></param>
        /// <param name="bodyService"></param>
        /// <param name="logService">The service to use for logging.</param>
        /// <param name="logger"></param>
        public HttpApisService(IOAuthService oAuthService, IBodyService bodyService, ILogService logService, ILogger<HttpApisService> logger)
        {
            this.oAuthService = oAuthService;
            this.bodyService = bodyService;
            this.logService = logService;
            this.logger = logger;
        }

        /// <inheritdoc />
        public Task InitializeAsync(ConfigurationModel configuration, HashSet<string> tablesToOptimize)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<JObject> Execute(ActionModel action, JObject resultSets, string configurationServiceName)
        {
            var httpApi = (HttpApiModel) action;
            var jArray = new JArray();

            await logService.LogInformation(logger, LogScopes.RunStartAndStop, httpApi.LogSettings, $"Executing HTTP API in time id: {httpApi.TimeId}, order: {httpApi.Order}", configurationServiceName, httpApi.TimeId, httpApi.Order);

            if (httpApi.SingleRequest)
            {
                if (String.IsNullOrWhiteSpace(httpApi.NextUrlProperty))
                {
                    return await ExecuteRequest(httpApi, resultSets, httpApi.UseResultSet, ReplacementHelper.EmptyRows, configurationServiceName);
                }

                var url = httpApi.Url;
                do
                {
                    var result = await ExecuteRequest(httpApi, resultSets, httpApi.UseResultSet, ReplacementHelper.EmptyRows, configurationServiceName, url);
                    url = ReplacementHelper.GetValue($"Body.{httpApi.NextUrlProperty}?", ReplacementHelper.EmptyRows, result, false);
                    jArray.Add(result);
                } while (!String.IsNullOrWhiteSpace(url));

                return new JObject
                {
                    {"Results", jArray}
                };
            }
            
            if (String.IsNullOrWhiteSpace(httpApi.UseResultSet))
            {
                await logService.LogError(logger, LogScopes.StartAndStop, httpApi.LogSettings, $"The HTTP API in configuration '{configurationServiceName}', time ID '{httpApi.TimeId}', order '{httpApi.Order}' is set to not be a single request but no result set has been provided. If the information is not dynamic set action to single request, otherwise provide a result set to use.", configurationServiceName, httpApi.TimeId, httpApi.Order);
                
                return new JObject
                {
                    {"Results", jArray}
                };
            }

            var rows = ResultSetHelper.GetCorrectObject<JArray>(httpApi.UseResultSet, ReplacementHelper.EmptyRows, resultSets);

            for (var i = 0; i < rows.Count; i++)
            {
                var indexRows = new List<int> {i};

                if (String.IsNullOrWhiteSpace(httpApi.NextUrlProperty))
                {
                    jArray.Add(await ExecuteRequest(httpApi, resultSets, $"{httpApi.UseResultSet}[{i}]", indexRows, configurationServiceName, forcedIndex: i));
                }
                else
                {
                    var url = httpApi.Url;
                    do
                    {
                        var result = await ExecuteRequest(httpApi, resultSets, $"{httpApi.UseResultSet}[{indexRows[0]}]", indexRows, configurationServiceName, url, i);
                        url = ReplacementHelper.GetValue($"Body.{httpApi.NextUrlProperty}?", ReplacementHelper.EmptyRows, result, false);
                        jArray.Add(result);
                    } while (!String.IsNullOrWhiteSpace(url));
                }
            }

            return new JObject
            {
                {"Results", jArray}
            };
        }

        /// <summary>
        /// Execute the HTTP API request.
        /// </summary>
        /// <param name="httpApi">The HTTP API action to execute.</param>
        /// <param name="resultSets">The result sets from previous actions in the same run.</param>
        /// <param name="useResultSet">The result set to use for this execution.</param>
        /// <param name="rows">The indexes/rows of the array, passed to be used if '[i]' is used in the key.</param>
        /// <param name="overrideUrl">The url to use instead of the provided url, used for continuous calls with a next URL.</param>
        /// <returns></returns>
        private async Task<JObject> ExecuteRequest(HttpApiModel httpApi, JObject resultSets, string useResultSet, List<int> rows, string configurationServiceName, string overrideUrl = "", int forcedIndex = -1)
        {
            var extraValuesToObfuscate = new List<string>();
            var url = String.IsNullOrWhiteSpace(overrideUrl) ? httpApi.Url : overrideUrl;

            // If a result set needs to be used apply it on the url.
            if (!String.IsNullOrWhiteSpace(useResultSet))
            {
                var keyParts = useResultSet.Split('.');
                var usingResultSet = ResultSetHelper.GetCorrectObject<JObject>(httpApi.SingleRequest ? keyParts[0] : useResultSet, ReplacementHelper.EmptyRows, resultSets);
                var remainingKey = keyParts.Length > 1 ? useResultSet.Substring(keyParts[0].Length + 1) : "";
                var tuple = ReplacementHelper.PrepareText(url, usingResultSet, remainingKey, httpApi.HashSettings, htmlEncode: true);
                url = tuple.Item1;
                var parameterKeys = tuple.Item2;
                url = ReplacementHelper.ReplaceText(url, rows, parameterKeys, usingResultSet, httpApi.HashSettings, htmlEncode: true);
            }

            await logService.LogInformation(logger, LogScopes.RunBody, httpApi.LogSettings, $"URL to call for this service = {url}, method: {httpApi.Method}", configurationServiceName, httpApi.TimeId, httpApi.Order, extraValuesToObfuscate);
            var request = new HttpRequestMessage(new HttpMethod(httpApi.Method), url);

            foreach (var header in httpApi.Headers)
            {
                if (String.IsNullOrWhiteSpace(header.UseResultSet))
                {
                    request.Headers.Add(header.Name, header.Value);
                }
                // If a result set is used for the header apply it to the value.
                else
                {
                    var keyParts = header.UseResultSet.Split('.');
                    var usingResultSet = ResultSetHelper.GetCorrectObject<JObject>(httpApi.SingleRequest ? keyParts[0] : header.UseResultSet, ReplacementHelper.EmptyRows, resultSets);
                    var remainingKey = keyParts.Length > 1 ? header.UseResultSet.Substring(keyParts[0].Length + 1) : "";
                    var tuple = ReplacementHelper.PrepareText(header.Value, usingResultSet, remainingKey, httpApi.HashSettings);
                    var headerValue = tuple.Item2.Count > 0 ? ReplacementHelper.ReplaceText(tuple.Item1, rows, tuple.Item2, usingResultSet, httpApi.HashSettings) : tuple.Item1;
                    request.Headers.Add(header.Name, headerValue);
                }
            }

            OAuthModel token = null;
            if (!String.IsNullOrWhiteSpace(httpApi.OAuth))
            {
                token = await oAuthService.GetAccessTokenAsync(httpApi.OAuth);
                if (token != null)
                {
                    request.Headers.Add("Authorization", $"{token.TokenType} {token.AccessToken}"); //bijvoorbeeld: Bearer <accesstoken>
                }
                else
                {
                    await logService.LogWarning(logger, LogScopes.RunBody, httpApi.LogSettings, $"OAuth '{httpApi.OAuth}' not found for configuration '{configurationServiceName}' with time ID '{httpApi.TimeId}' and order '{httpApi.Order}'. Add the '{httpApi.OAuth}' to the OAuth configuration or remove usage.", configurationServiceName, httpApi.TimeId, httpApi.Order, extraValuesToObfuscate);
                }
            }

            // If an authorization header has been added through custom headers or OAuth add it to the list of extra values to obfuscate.
            if (request.Headers.Contains("Authorization"))
            {
                foreach (var value in request.Headers.GetValues("Authorization"))
                {
                    extraValuesToObfuscate.Add(value);
                }
            }
            
            // If an x-api-key header has been added through custom headers add it to the list of extra values to obfuscate.
            if (request.Headers.Contains("X-API-Key"))
            {
                foreach (var value in request.Headers.GetValues("X-API-Key"))
                {
                    extraValuesToObfuscate.Add(value);
                }
            }
            
            await logService.LogInformation(logger, LogScopes.RunBody, httpApi.LogSettings, $"Headers: {request.Headers}", configurationServiceName, httpApi.TimeId, httpApi.Order, extraValuesToObfuscate);

            if (httpApi.Body != null)
            {
                var body = bodyService.GenerateBody(httpApi.Body, rows, resultSets, httpApi.HashSettings, forcedIndex, token);

                await logService.LogInformation(logger, LogScopes.RunBody, httpApi.LogSettings, $"Body:\n{body}", configurationServiceName, httpApi.TimeId, httpApi.Order, extraValuesToObfuscate);
                request.Content = new StringContent(body)
                {
                    Headers = {ContentType = new MediaTypeHeaderValue(httpApi.Body.ContentType)}
                };
            }

            
            var httpHandler = new HttpClientHandler();

            if (httpApi.IgnoreSSLValidationErrors)
            {
                httpHandler.ClientCertificateOptions = ClientCertificateOption.Manual;
                httpHandler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
            }

            using var client = new HttpClient(httpHandler);

            if (httpApi.Timeout >= 0)
            {
                client.Timeout = TimeSpan.FromSeconds(httpApi.Timeout);
            }
            
            using var response = await client.SendAsync(request);

            // If the request was unauthorized retry the request if it has an OAuth API name set and it hasn't retried before.
            if (response.StatusCode == HttpStatusCode.Unauthorized && !String.IsNullOrWhiteSpace(httpApi.OAuth) && retryOAuthUnauthorizedResponse)
            {
                retryOAuthUnauthorizedResponse = false;
                await oAuthService.RequestWasUnauthorizedAsync(httpApi.OAuth);

                await logService.LogWarning(logger, LogScopes.RunBody, httpApi.LogSettings, $"Request to {url} return \"Unauthorized\" on OAuth token. Retrying once with new OAuth token.", configurationServiceName, httpApi.TimeId, httpApi.Order, extraValuesToObfuscate);
                return await ExecuteRequest(httpApi, resultSets, useResultSet, rows, overrideUrl);
            }

            retryOAuthUnauthorizedResponse = true;

            var resultSet = new JObject
            {
                {"Url", url},
                {"StatusCode", ((int) response.StatusCode).ToString()}
            };

            // Add all headers to the result set.
            ExtractHeadersIntoResultSet(resultSet, response.Headers);
            // Add all content headers to the result set.
            ExtractHeadersIntoResultSet(resultSet, response.Content.Headers);

            // Determine content type.
            string contentType = null;
            if (!String.IsNullOrWhiteSpace(httpApi.ResultContentType))
            {
                contentType = httpApi.ResultContentType;
            }
            else if (resultSet.ContainsKey("Content-Type"))
            {
                contentType = (string) resultSet["Content-Type"]?[0];
            }
            
            // Make sure contentType is not null.
            contentType ??= String.Empty;

            var responseBody = await response.Content.ReadAsStringAsync();
            if (contentType.Contains("json"))
            {
                if (responseBody.StartsWith("{"))
                {
                    resultSet.Add("Body", JObject.Parse(responseBody));
                }
                else if(responseBody.StartsWith("["))
                {
                    resultSet.Add("Body", JArray.Parse(responseBody));
                }
            }
            else if (contentType.Contains("xml"))
            {
                var xml = new XmlDocument();
                xml.LoadXml(responseBody);
                resultSet.Add("Body", JObject.Parse(JsonConvert.SerializeXmlNode(xml)));
            }
            else
            {
                resultSet.Add("Body", responseBody);
            }

            // Always add the body as plain text.
            resultSet.Add("BodyPlainText", responseBody);
            
            var useResultSetKeyParts = useResultSet.Split('.');
            var usedResultSet = String.IsNullOrWhiteSpace(useResultSet) ? null : ResultSetHelper.GetCorrectObject<JObject>(httpApi.SingleRequest ? useResultSetKeyParts[0] : useResultSet, ReplacementHelper.EmptyRows, resultSets);
            resultSet.Add("UsedResultSet", usedResultSet);

            await logService.LogInformation(logger, LogScopes.RunBody, httpApi.LogSettings, $"Status: {resultSet["StatusCode"]}, Result body:\n{responseBody}", configurationServiceName, httpApi.TimeId, httpApi.Order, extraValuesToObfuscate);

            return resultSet;
        }

        private void ExtractHeadersIntoResultSet(JObject resultSet, HttpHeaders headers)
        {
            foreach (var header in headers)
            {
                var headerProperties = new JArray();

                foreach (var value in header.Value)
                {
                    headerProperties.Add(value);
                }

                resultSet.Add(header.Key, headerProperties);
            }
        }
    }
}
