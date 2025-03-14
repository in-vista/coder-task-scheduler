using System.Threading.Tasks;
using WiserTaskScheduler.Core.Models.OAuth;

namespace WiserTaskScheduler.Core.Interfaces
{
    public interface IOAuthService
    {
        /// <summary>
        /// Set the configuration to be used for OAuth calls.
        /// </summary>
        /// <param name="configuration">The configuration to set.</param>
        /// <returns></returns>
        Task SetConfigurationAsync(OAuthConfigurationModel configuration);

        /// <summary>
        /// Get the access token of the specified API.
        /// </summary>
        /// <param name="apiName">The name of the API to get the access token from.</param>
        /// <param name="retryAfterWrongRefreshToken">Retry to get an access token using login credentials if the refresh token didn't give a new access token.</param>
        /// <returns>Returns the access token to the API.</returns>
        Task<OAuthModel> GetAccessTokenAsync(string apiName, bool retryAfterWrongRefreshToken = true);

        /// <summary>
        /// Tells that the specified API gave an access token was invalid and caused the request to be "Unauthorized".
        /// API information will be cleared so the next request will request a new access token.
        /// </summary>
        /// <param name="apiName">The name of the API that gave an invalid access token.</param>
        /// <returns></returns>
        Task RequestWasUnauthorizedAsync(string apiName);
    }
}
