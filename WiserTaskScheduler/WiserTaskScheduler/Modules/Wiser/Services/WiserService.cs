using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using WiserTaskScheduler.Core.Enums;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models;
using WiserTaskScheduler.Modules.Wiser.Interfaces;
using WiserTaskScheduler.Modules.Wiser.Models;
using HttpMethod = System.Net.Http.HttpMethod;

namespace WiserTaskScheduler.Modules.Wiser.Services
{
    /// <inheritdoc cref="IWiserService"/> />
    public class WiserService : IWiserService, ISingletonService
    {
        private readonly WiserSettings wiserSettings;
        private readonly ILogService logService;
        private readonly ILogger<WiserService> logger;
        private readonly LogSettings logSettings;

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        private string accessToken;
        private DateTime accessTokenExpireTime;
        private string refreshToken;

        // Semaphore is a locking system that can be used with async code.
        private static readonly SemaphoreSlim AccessTokenLock = new(1, 1);

        public WiserService(IOptions<WtsSettings> wtsSettings, ILogService logService, ILogger<WiserService> logger)
        {
            wiserSettings = wtsSettings.Value.Wiser;
            this.logService = logService;
            this.logger = logger;
            logSettings = wiserSettings?.LogSettings ?? new LogSettings();

            accessToken = "";
            accessTokenExpireTime = DateTime.MinValue;
            refreshToken = "";
        }

        /// <inheritdoc />
        public async Task<string> GetAccessTokenAsync()
        {
            // Lock to prevent multiple requests at once.
            await AccessTokenLock.WaitAsync();

            try
            {
                if (String.IsNullOrWhiteSpace(accessToken))
                {
                    await LoginAsync();
                }
                else if (accessTokenExpireTime <= DateTime.Now)
                {
                    if (String.IsNullOrWhiteSpace(refreshToken))
                    {
                        await LoginAsync();
                    }
                    else
                    {
                        await LoginAsync(true);
                    }
                }

                return accessToken;
            }
            finally
            {
                // Release the lock. This is in a finally to be 100% sure that it will always be released. Otherwise the application might freeze.
                AccessTokenLock.Release();
            }
        }

        /// <summary>
        /// DO NOT CALL THIS BY YOURSELF!
        /// Login to the Wiser API.
        /// This method is called when using <see cref="AccessToken"/> or <see cref="GetAccessTokenAsync"/> with a lock.
        /// </summary>
        private async Task LoginAsync(bool useRefreshToken = false)
        {
            var formData = new List<KeyValuePair<string, string>>()
            {
                new("subDomain", $"{wiserSettings.Subdomain}"),
                new("client_id", $"{wiserSettings.ClientId}"),
                new("client_secret", $"{wiserSettings.ClientSecret}"),
                new("isTestEnvironment", $"{wiserSettings.TestEnvironment}")
            };

            if (useRefreshToken)
            {
                formData.Add(new KeyValuePair<string, string>("grant_type", "refresh_token"));
                formData.Add(new KeyValuePair<string, string>("refresh_token", refreshToken));
            }
            else
            {
                formData.Add(new KeyValuePair<string, string>("grant_type", "password"));
                formData.Add(new KeyValuePair<string, string>("username", $"{wiserSettings.Username}"));
                formData.Add(new KeyValuePair<string, string>("password", $"{wiserSettings.Password}"));
            }

            var request = new HttpRequestMessage(HttpMethod.Post, $"{wiserSettings.WiserApiUrl}connect/token")
            {
                Content = new FormUrlEncodedContent(formData)
            };
            request.Headers.Add("Accept", "application/json");

            await logService.LogInformation(logger, LogScopes.RunBody, logSettings, $"URL: {request.RequestUri}\nHeaders: {request.Headers}\nBody: {String.Join(' ', formData)}", "WiserService");

            using var client = new HttpClient();
            try
            {
                var response = await client.SendAsync(request);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    await logService.LogCritical(logger, LogScopes.RunStartAndStop, logSettings, "Failed to login to the Wiser API.", "WiserService");
                    return;
                }

                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                var body = await reader.ReadToEndAsync();
                await logService.LogInformation(logger, LogScopes.RunBody, logSettings, $"Response body: {body}", "WiserService");
                var wiserLoginResponse = JsonConvert.DeserializeObject<WiserLoginResponseModel>(body);

                accessToken = wiserLoginResponse.AccessToken;
                accessTokenExpireTime = DateTime.Now.AddSeconds(wiserLoginResponse.ExpiresIn).AddMinutes(-1); // Refresh 1 minute before expire.
                refreshToken = wiserLoginResponse.RefreshToken;
            }
            catch (Exception e)
            {
                await logService.LogCritical(logger, LogScopes.RunStartAndStop, logSettings, $"Failed to login to the Wiser API.\n{e}", "WiserService");
            }
        }

        /// <inheritdoc />
        public async Task<List<TemplateSettingsModel>> RequestConfigurations()
        {
#if DEBUG
            var environment = Environments.Development;
#else
            var environment = Environments.Live;
#endif

            var configurationPaths = String.IsNullOrWhiteSpace(wiserSettings.ConfigurationPath) ? new[] { "" } : wiserSettings.ConfigurationPath.Split(";");
            using var client = new HttpClient();

            var configurations = new List<TemplateSettingsModel>();

            foreach (var configurationPath in configurationPaths)
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"{wiserSettings.WiserApiUrl}api/v3/templates/entire-tree-view?startFrom=SERVICES{(String.IsNullOrWhiteSpace(configurationPath) ? "" : $",{configurationPath}")}&environment={environment}");
                    request.Headers.Add("Authorization", $"Bearer {await GetAccessTokenAsync()}");

                    var response = await client.SendAsync(request);

                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        await logService.LogCritical(logger, LogScopes.RunStartAndStop, logSettings, "Failed to get configurations from the Wiser API.", "WiserService");
                        return null;
                    }

                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                    var body = await reader.ReadToEndAsync();

                    // The call to wiser configuration responds with an html document when Wiser is updating
                    // We check for both html tag and doctype so this document is more free to change
                    if (body.StartsWith("<html", StringComparison.InvariantCultureIgnoreCase) || body.StartsWith("<!DOCTYPE html", StringComparison.InvariantCultureIgnoreCase))
                    {
                        await logService.LogInformation(logger, LogScopes.RunStartAndStop, logSettings, "Unable to get configuration due to Wiser update.", "WiserService");
                        return null;
                    }

                    var templateTrees = JsonConvert.DeserializeObject<List<TemplateTreeViewModel>>(body);

                    configurations.AddRange(FlattenTree(templateTrees));
                }
                catch (Exception e)
                {
                    await logService.LogCritical(logger, LogScopes.RunStartAndStop, logSettings, $"Failed to get configurations from the Wiser API.\n{e}", "WiserService");
                    return null;
                }
            }

            return configurations;
        }

        private List<TemplateSettingsModel> FlattenTree(List<TemplateTreeViewModel> templateTrees)
        {
            var results = new List<TemplateSettingsModel>();

            foreach (var templateTree in templateTrees)
            {
                if (templateTree.HasChildren)
                {
                    results.AddRange(FlattenTree(templateTree.ChildNodes));
                }
                else
                {
                    results.Add(templateTree.TemplateSettings);
                }
            }

            return results;
        }
    }
}