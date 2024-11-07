using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Extensions;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.Objects.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using WiserTaskScheduler.Core.Enums;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models.OAuth;

namespace WiserTaskScheduler.Core.Services
{
    public class OAuthService : IOAuthService, ISingletonService
    {
        private const string LogName = "OAuthService";

        private readonly GclSettings gclSettings;
        private readonly ILogService logService;
        private readonly ILogger<OAuthService> logger;
        private readonly IServiceProvider serviceProvider;

        private OAuthConfigurationModel configuration;

        // Semaphore is a locking system that can be used with async code.
        private static readonly SemaphoreSlim OauthApiLock = new(1, 1);

        public OAuthService(IOptions<GclSettings> gclSettings, ILogService logService, ILogger<OAuthService> logger, IServiceProvider serviceProvider)
        {
            this.gclSettings = gclSettings.Value;
            this.logService = logService;
            this.logger = logger;
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public async Task SetConfigurationAsync(OAuthConfigurationModel configuration)
        {
            this.configuration = configuration;

            if (this.configuration.OAuths == null || !this.configuration.OAuths.Any())
            {
                await logService.LogWarning(logger, LogScopes.StartAndStop, this.configuration.LogSettings, "An OAuth configuration has been added but it does not contain OAuths to setup. Consider removing the OAuth configuration.", LogName);
                return;
            }

            using var scope = serviceProvider.CreateScope();
            var objectsService = scope.ServiceProvider.GetRequiredService<IObjectsService>();

            // Check if there is already information stored in the database to use.
            foreach (var oAuth in this.configuration.OAuths)
            {
                oAuth.AccessToken = (await objectsService.GetSystemObjectValueAsync($"WTS_{oAuth.ApiName}_AccessToken"))?.DecryptWithAes(gclSettings.DefaultEncryptionKey);
                oAuth.TokenType = await objectsService.GetSystemObjectValueAsync($"WTS_{oAuth.ApiName}_TokenType");
                if (string.IsNullOrEmpty(oAuth.RefreshToken))
                {
                    oAuth.RefreshToken = (await objectsService.GetSystemObjectValueAsync($"WTS_{oAuth.ApiName}_RefreshToken"))?.DecryptWithAes(gclSettings.DefaultEncryptionKey);
                }
                var expireTime = await objectsService.GetSystemObjectValueAsync($"WTS_{oAuth.ApiName}_ExpireTime");

                // Try to parse the DateTime. If it fails, then set it to DateTime.MinValue to prevent errors.
                if (!String.IsNullOrWhiteSpace(expireTime) && DateTime.TryParse(expireTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expireTimeParsed))
                {
                    oAuth.ExpireTime = expireTimeParsed;
                }
                else
                {
                    oAuth.ExpireTime = DateTime.MinValue;
                }

                oAuth.LogSettings ??= this.configuration.LogSettings;
            }
        }

        /// <inheritdoc />
        public async Task<string> GetAccessTokenAsync(string apiName, bool retryAfterWrongRefreshToken = true)
        {
            var oAuthApi = configuration.OAuths.SingleOrDefault(oAuth => oAuth.ApiName.Equals(apiName));

            if (oAuthApi == null)
            {
                return null;
            }

            // Check if a new access token needs to be requested and request it.
            OAuthState result;

            // Lock to prevent multiple requests at once.
            await OauthApiLock.WaitAsync();

            try
            {
                if (!String.IsNullOrWhiteSpace(oAuthApi.AccessToken) && oAuthApi.ExpireTime > DateTime.Now)
                {
                    result = OAuthState.CurrentToken;
                }
                else
                {
                    var formData = new List<KeyValuePair<string, string>>();

                    // Setup correct authentication.
                    OAuthState failState;
                    if (String.IsNullOrWhiteSpace(oAuthApi.AccessToken) || String.IsNullOrWhiteSpace(oAuthApi.RefreshToken))
                    {
                        failState = OAuthState.FailedLogin;

                        if (oAuthApi.OAuthJwt == null)
                        {
                            switch (oAuthApi.GrantType)
                            {
                                case OAuthGrantType.RefreshToken:
                                    formData.Add(new KeyValuePair<string, string>("refresh_token", oAuthApi.RefreshToken));
                                    formData.Add(new KeyValuePair<string, string>("grant_type", "refresh_token"));
                                    break;
                                    
                                case OAuthGrantType.AuthCode:
                                    throw new NotImplementedException("OAuthGrantType.AuthCode is not supported yet");
                                    break;

                                case OAuthGrantType.AuthCodeWithPKCE:
                                    throw new NotImplementedException(
                                        "OAuthGrantType.AuthCodeWithPKCE is not supported yet");
                                    break;

                                case OAuthGrantType.Implicit:
                                    throw new NotImplementedException("OAuthGrantType.Implicit is not supported yet");
                                    break;

                                case OAuthGrantType.PasswordCredentials:
                                    await logService.LogInformation(logger, LogScopes.RunBody, oAuthApi.LogSettings,
                                        $"Requesting new access token for '{apiName}' on endpoint '{oAuthApi.Endpoint}' using username and password.",
                                        LogName);

                                    formData.Add(new KeyValuePair<string, string>("grant_type", "password"));
                                    formData.Add(new KeyValuePair<string, string>("username", oAuthApi.Username));
                                    formData.Add(new KeyValuePair<string, string>("password", oAuthApi.Password));

                                    break;

                                case OAuthGrantType.ClientCredentials:
                                    await logService.LogInformation(logger, LogScopes.RunBody, oAuthApi.LogSettings,
                                        $"Requesting new access token for '{apiName}' on endpoint '{oAuthApi.Endpoint}' using client credentials.",
                                        LogName);
                                    
                                    formData.Add(new KeyValuePair<string, string>("grant_type", "client_credentials"));

                                    if (oAuthApi.SendClientCredentialsInBody)
                                    {
                                        formData.Add(new KeyValuePair<string, string>("client_id", oAuthApi.ClientId));
                                        formData.Add(new KeyValuePair<string, string>("client_secret", oAuthApi.ClientSecret));
                                    }
                                    break;
                            }
                        }
                    }
                    else
                    {
                        await logService.LogInformation(logger, LogScopes.RunBody, oAuthApi.LogSettings, $"Requesting new access token for '{apiName}' using refresh token.", LogName);

                        failState = OAuthState.FailedRefreshToken;
                        if (oAuthApi.OAuthJwt == null)
                        {
                            formData.Add(new KeyValuePair<string, string>("grant_type", "refresh_token"));
                            formData.Add(new KeyValuePair<string, string>("refresh_token", oAuthApi.RefreshToken));
                        }
                    }

                    string jwtToken = null;
                    if (oAuthApi.OAuthJwt != null)
                    {
                        var claims = new Dictionary<string, object>
                        {
                            { "exp", DateTimeOffset.Now.AddSeconds(oAuthApi.OAuthJwt.ExpirationTime).ToUnixTimeSeconds() },
                            { "iss", oAuthApi.OAuthJwt.Issuer },
                            { "sub", oAuthApi.OAuthJwt.Subject },
                            { "aud", oAuthApi.OAuthJwt.Audience }
                        };

                        // Add the custom claims.
                        foreach (var claim in oAuthApi.OAuthJwt.Claims)
                        {
                            // Ignore the reserved claims.
                            if (claim.Name.InList("exp", "iss", "sub", "aud"))
                            {
                                continue;
                            }

                            // If a data type is specified, then try to convert the value to that type.
                            Type type = null;
                            if (!String.IsNullOrWhiteSpace(claim.DataType))
                            {
                                type = Type.GetType(claim.DataType, false, true) ?? Type.GetType($"System.{claim.DataType}", false, true);
                            }

                            if (type != null)
                            {
                                try
                                {
                                    claims[claim.Name] = Convert.ChangeType(claim.Value, type);
                                }
                                catch (Exception exception)
                                {
                                    // If the conversion fails, then log it and use the string value instead.
                                    await logService.LogWarning(logger, LogScopes.RunBody, oAuthApi.LogSettings, $"Failed to convert claim value to specified type. Using string instead. Exception: {exception}", LogName);
                                    claims[claim.Name] = claim.Value;
                                }
                            }
                            else
                            {
                                // No data type specified, so just use the string value.
                                claims[claim.Name] = claim.Value;
                            }
                        }

                        // Load the certificate and create the token.
                        var certificate = new X509Certificate2(oAuthApi.OAuthJwt.CertificateLocation, oAuthApi.OAuthJwt.CertificatePassword);
                        jwtToken = Jose.JWT.Encode(claims, certificate.GetRSAPrivateKey(), Jose.JwsAlgorithm.RS256);
                    }

                    if (oAuthApi.FormKeyValues != null)
                    {
                        foreach (var keyValue in oAuthApi.FormKeyValues)
                        {
                            var value = keyValue.Value;
                            if (value.Equals("[{jwt_token}]"))
                            {
                                value = jwtToken ?? String.Empty;
                            }

                            formData.Add(new KeyValuePair<string, string>(keyValue.Key, value));
                        }
                    }

                    var request = new HttpRequestMessage(HttpMethod.Post, oAuthApi.Endpoint)
                    {
                        Content = new FormUrlEncodedContent(formData)
                    };
                    
                    //Add custom headers
                    request.Headers.Add("Accept", "application/json");
                    if (oAuthApi.Headers != null)
                    {
                        foreach (var header in oAuthApi.Headers)
                        {
                            request.Headers.Add(header.Name, header.Value);
                        }
                    }

                    if (!request.Headers.Contains("Authorization") && 
                        ((!string.IsNullOrEmpty(oAuthApi.ClientId) && !string.IsNullOrEmpty(oAuthApi.ClientSecret) && 
                          (oAuthApi.GrantType == OAuthGrantType.RefreshToken)
                        || (!oAuthApi.SendClientCredentialsInBody && oAuthApi.GrantType == OAuthGrantType.ClientCredentials))))
                    {

                        var authString = $"{oAuthApi.ClientId}:{oAuthApi.ClientSecret}";
                        var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(authString);
                        request.Headers.Add("Authorization", "Basic " + System.Convert.ToBase64String(plainTextBytes));
                    }

                    using var client = new HttpClient();
                    var response = await client.SendAsync(request);

                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                    var json = await reader.ReadToEndAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        await logService.LogError(logger, LogScopes.RunBody, oAuthApi.LogSettings, $"Failed to get access token for {oAuthApi.ApiName}. Received: {response.StatusCode}\n{json}", LogName);
                        result = failState;
                    }
                    else
                    {
                        await logService.LogInformation(logger, LogScopes.RunBody, oAuthApi.LogSettings, $"Response from oAuth request: '{json}'", LogName);
                        var body = JObject.Parse(json);

                        oAuthApi.AccessToken = (string) body["access_token"];
                        oAuthApi.TokenType = (string) body["token_type"];
                        oAuthApi.RefreshToken = (string) body["refresh_token"];

                        if (body["expires_in"].Type == JTokenType.Integer)
                        {
                            oAuthApi.ExpireTime = DateTime.Now.AddSeconds((int) body["expires_in"]);
                        }
                        else
                        {
                            oAuthApi.ExpireTime = DateTime.Now.AddSeconds(Convert.ToInt32((string) body["expires_in"]));
                        }

                        oAuthApi.ExpireTime -= oAuthApi.ExpireTimeOffset;

                        await logService.LogInformation(logger, LogScopes.RunBody, oAuthApi.LogSettings, $"A new access token has been retrieved for {oAuthApi.ApiName} and is valid till {oAuthApi.ExpireTime}", LogName);

                        result = OAuthState.NewToken;
                    }
                }
            }
            finally
            {
                // Release the lock. This is in a finally to be 100% sure that it will always be released. Otherwise the application might freeze.
                OauthApiLock.Release();
            }

            if (result == OAuthState.FailedLogin)
            {
                return null;
            }

            if (result == OAuthState.FailedRefreshToken)
            {
                if (!retryAfterWrongRefreshToken)
                {
                    return null;
                }

                //Retry to get the token with the login credentials if the refresh token was invalid.
                await RequestWasUnauthorizedAsync(apiName);
                return await GetAccessTokenAsync(apiName, false);
            }

            if (result == OAuthState.CurrentToken)
            {
                return $"{oAuthApi.TokenType} {oAuthApi.AccessToken}";
            }

            await SaveToDatabaseAsync(oAuthApi);

            return $"{oAuthApi.TokenType} {oAuthApi.AccessToken}";
        }

        /// <inheritdoc />
        public async Task RequestWasUnauthorizedAsync(string apiName)
        {
            var oAuthApi = configuration.OAuths.SingleOrDefault(oAuth => oAuth.ApiName.Equals(apiName));

            if (oAuthApi == null)
            {
                return;
            }

            oAuthApi.AccessToken = null;
            oAuthApi.TokenType = null;
            oAuthApi.RefreshToken = null;
            oAuthApi.ExpireTime = DateTime.MinValue;

            await SaveToDatabaseAsync(oAuthApi);
        }

        /// <summary>
        /// Save the state/information of the OAuth to the database.
        /// </summary>
        /// <param name="oAuthApi">The name of the API to save the information for.</param>
        private async Task SaveToDatabaseAsync(OAuthModel oAuthApi)
        {
            using var scope = serviceProvider.CreateScope();
            var objectsService = scope.ServiceProvider.GetRequiredService<IObjectsService>();

            await objectsService.SetSystemObjectValueAsync($"WTS_{oAuthApi.ApiName}_AccessToken", oAuthApi.AccessToken.EncryptWithAes(gclSettings.DefaultEncryptionKey));
            await objectsService.SetSystemObjectValueAsync($"WTS_{oAuthApi.ApiName}_TokenType", oAuthApi.TokenType);
            await objectsService.SetSystemObjectValueAsync($"WTS_{oAuthApi.ApiName}_RefreshToken", oAuthApi.RefreshToken.EncryptWithAes(gclSettings.DefaultEncryptionKey));
            await objectsService.SetSystemObjectValueAsync($"WTS_{oAuthApi.ApiName}_ExpireTime", oAuthApi.ExpireTime.ToString(CultureInfo.InvariantCulture));
        }
    }
}