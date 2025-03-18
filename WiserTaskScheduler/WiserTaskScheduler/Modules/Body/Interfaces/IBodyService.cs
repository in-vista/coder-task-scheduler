using System.Collections.Generic;
using GeeksCoreLibrary.Core.Models;
using Newtonsoft.Json.Linq;
using WiserTaskScheduler.Core.Models.OAuth;
using WiserTaskScheduler.Modules.Body.Models;

namespace WiserTaskScheduler.Modules.Body.Interfaces
{
    /// <summary>
    /// A service to prepare bodies.
    /// </summary>
    public interface IBodyService
    {
        /// <summary>
        /// Generate the body based on a <see cref="BodyModel"/>.
        /// </summary>
        /// <param name="bodyModel">The <see cref="BodyModel"/> to generate the body from.</param>
        /// <param name="rows">The indexes/rows of the array, passed to be used if '[i]' is used in the key.</param>
        /// <param name="resultSets">The result sets to use to generate the body.</param>
        /// <param name="hashSettings">The settings to use for hashing.</param>
        /// <param name="forcedIndex">The index a body part uses if it is set to use the forced index.</param>
        /// <returns>Returns the generated body.</returns>
        string GenerateBody(BodyModel bodyModel, List<int> rows, JObject resultSets, HashSettingsModel hashSettings, int forcedIndex = -1, OAuthModel token = null);
    }
}
