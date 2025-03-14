using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Interfaces;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.GclReplacements.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using WiserTaskScheduler.Core.Helpers;
using WiserTaskScheduler.Core.Models;
using WiserTaskScheduler.Core.Models.OAuth;
using WiserTaskScheduler.Modules.Body.Interfaces;
using WiserTaskScheduler.Modules.Body.Models;

namespace WiserTaskScheduler.Modules.Body.Services
{
    /// <summary>
    /// A service to prepare bodies.
    /// </summary>
    public class BodyService : IBodyService, IScopedService
    {
        private readonly IServiceProvider serviceProvider;

        public BodyService(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public string GenerateBody(BodyModel bodyModel, List<int> rows, JObject resultSets, HashSettingsModel hashSettings, int forcedIndex = -1, OAuthModel token = null)
        {
            // Ensure the rows list has at least 3 items.
            if (rows.Count < 2) rows.Add(0);
            if (rows.Count < 3) rows.Add(0);
            
            var finalBody = new StringBuilder();

            // Create a scope to get the string replacements service. It's needed to evaluate logic snippets.
            using var scope = serviceProvider.CreateScope();
            var stringReplacementsService = scope.ServiceProvider.GetRequiredService<IStringReplacementsService>();

            foreach (var bodyPart in bodyModel.BodyParts)
            {
                if (bodyPart.InnerBody != null)
                {
                    if (bodyPart.SingleItem)
                    {
                        finalBody.Append(GenerateBody(bodyPart.InnerBody, rows, resultSets, hashSettings, rows[0], token:token));
                    }
                    else
                    {
                        var array = ResultSetHelper.GetCorrectObject<JArray>(bodyPart.UseResultSet, rows, resultSets);
                        for (var i = 0; i < array.Count; i++)
                        {
                            rows[1] = i; // Used as '[j]' in the key if the result set is based on another result set.
                            finalBody.Append(GenerateBody(bodyPart.InnerBody, rows, resultSets, hashSettings, rows[0], token:token));
                            rows[2]++; // Used as '[k]' in the key if the result set is based on another result set that was already based on another result set.
                        }
                    }

                    continue;
                }
                
                var body = bodyPart.Text;
                
                // Replace fixed replacements, like accesstoken
                if (token!=null) body = body.Replace("[{Oauth.accessToken}]", token.AccessToken);
                
                // If an item-id is set, get the template from the database.
                if (bodyPart.WiserTemplateItemId > 0)
                {
                    // Create a scope to get the string wiserItems service. It's needed to get the template
                    var wiserItemsService = scope.ServiceProvider.GetRequiredService<IWiserItemsService>();

                    var template = wiserItemsService.GetItemDetailsAsync(bodyPart.WiserTemplateItemId, languageCode: bodyPart.LanguageCode, detailKey: bodyPart.PropertyName, skipPermissionsCheck: true);
                    body = template.Result.GetDetailValue(bodyPart.PropertyName);
                }

                // If the part needs a result set, apply it.
                if (!String.IsNullOrWhiteSpace(bodyPart.UseResultSet))
                {
                    var keyParts = bodyPart.UseResultSet.Split('.');
                    var remainingKey = keyParts.Length > 1 ? bodyPart.UseResultSet.Substring(keyParts[0].Length + 1) : "";
                    var tuple = ReplacementHelper.PrepareText(body, (JObject)resultSets[keyParts[0]], remainingKey, hashSettings);
                    body = tuple.Item1;
                    var parameterKeys = tuple.Item2;

                    if (parameterKeys.Count > 0)
                    {
                        // Replace body with values from first row.
                        if (bodyPart.SingleItem)
                        {
                            body = ReplacementHelper.ReplaceText(body, rows, parameterKeys, ResultSetHelper.GetCorrectObject<JObject>(bodyPart.UseResultSet, rows, resultSets), hashSettings);
                        }
                        // Replace and combine body with values for each row.
                        else
                        {
                            body = GenerateBodyCollection(body, bodyModel.ContentType, parameterKeys, ResultSetHelper.GetCorrectObject<JArray>(bodyPart.UseResultSet, rows, resultSets), hashSettings, bodyPart.ForceIndex ? forcedIndex : -1);
                        }
                    }
                }

                if (bodyPart.EvaluateLogicSnippets)
                {
                    // Evaluate [if]...[endif] snippets in the body.
                    body = stringReplacementsService.EvaluateTemplate(body);
                }

                finalBody.Append(body);
            }

            return finalBody.ToString();
        }

        /// <summary>
        /// Replace values in the body for each row and return the combined result.
        /// </summary>
        /// <param name="body">The body text to use for each row.</param>
        /// <param name="contentType">The content type that is being send in the request.</param>
        /// <param name="parameterKeys">The keys of the parameters that need to be replaced.</param>
        /// <param name="usingResultSet">The result set to get the values from.</param>
        /// <param name="hashSettings">The settings to use for hashing.</param>
        /// <param name="forcedIndex">The index a body part uses if it is set to use the forced index.</param>
        /// <returns></returns>
        private string GenerateBodyCollection(string body, string contentType, List<ParameterKeyModel> parameterKeys, JArray usingResultSet, HashSettingsModel hashSettings, int forcedIndex)
        {
            var separator = String.Empty;

            // Add a separator between each row result based on content type.
            switch (contentType)
            {
                case "application/json":
                    separator = ",";
                    break;
            }

            var bodyCollection = new StringBuilder();

            var rows = new List<int> { 0, 0 };
            var keyWithSecondLayer = parameterKeys.FirstOrDefault(parameterKey => parameterKey.Key.Contains("[j]"))?.Key;

            var startIndex = forcedIndex >= 0 ? forcedIndex : 0;
            var endIndex = forcedIndex >= 0 ? forcedIndex + 1 : usingResultSet.Count;

            // Perform the query for each row in the result set that is being used.
            for (var i = startIndex; i < endIndex; i++)
            {
                rows[0] = i;

                if (keyWithSecondLayer == null)
                {
                    var bodyWithValues = ReplacementHelper.ReplaceText(body, rows, parameterKeys, (JObject) usingResultSet[i], hashSettings);
                    bodyCollection.Append($"{(i > 0 ? separator : "")}{bodyWithValues}");
                    continue;
                }

                var secondLayerArray = ResultSetHelper.GetCorrectObject<JArray>($"{keyWithSecondLayer.Substring(0, keyWithSecondLayer.IndexOf("[j]"))}", rows, (JObject) usingResultSet[i]);

                for (var j = 0; j < secondLayerArray.Count; j++)
                {
                    rows[1] = j;
                    var bodyWithValues = ReplacementHelper.ReplaceText(body, rows, parameterKeys, (JObject)usingResultSet[i], hashSettings);
                    bodyCollection.Append($"{(i > startIndex || j > 0 ? separator : "")}{bodyWithValues}");
                }
            }

            // Add collection syntax based on content type.
            switch (contentType)
            {
                case "application/json":
                    return $"[{bodyCollection}]";
                default:
                    return bodyCollection.ToString();
            }
        }
    }
}
