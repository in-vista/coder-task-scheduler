using System;
using System.Collections.Generic;
using System.Linq;
using WiserTaskScheduler.Core.Models;
using Newtonsoft.Json.Linq;

namespace WiserTaskScheduler.Core.Helpers
{
    public static class ResultSetHelper
    {
        /// <summary>
        /// Get the correct object based on the key.
        /// </summary>
        /// <typeparam name="T">The type of JToken that needs to be returned.</typeparam>
        /// <param name="key">The key, separated by comma, to the required object.</param>
        /// <param name="rows">The indexes/rows of the array, to be used if for example '[i]' is used in the key.</param>
        /// <param name="usingResultSet">The result set from where to start te search.</param>
        /// <param name="processedKey"></param>
        /// <returns></returns>
        public static T GetCorrectObject<T>(string key, List<int> rows, JObject usingResultSet, string processedKey = "") where T : JToken
        {
            if (String.IsNullOrWhiteSpace(key))
            {
                return usingResultSet as T;
            }
            
            if (usingResultSet == null)
            {
                throw new ResultSetException($"Failed to get correct object because no result set was given. The key being processed is '{key}', already processed '{processedKey}'. If the key is correct but the value is not always present it is recommended to use a default value.");
            }
            
            var currentPart = "";
            
            try
            {
                var keyParts = key.Split(".");
                currentPart = keyParts[0];

                // No next step left, return object as requested type.
                if (keyParts.Length == 1)
                {
                    if (!key.EndsWith(']'))
                    {
                        return usingResultSet.Properties()
                            .FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
                            ?.Value as T;
                        
                        //return usingResultSet[key] as T;
                    }

                    var arrayKey = key.Substring(0, key.IndexOf('['));
                    var indexKey = GetIndex(keyParts, rows);
                    return usingResultSet[arrayKey][indexKey] as T;
                }

                var remainingKey = key.Substring(key.IndexOf(".") + 1);

                // Object to step into is not an array.
                if (!currentPart.EndsWith("]"))
                {
                    switch (usingResultSet[keyParts[0]])
                    {
                        case JValue valueAsJValue:
                            return valueAsJValue as T;
                        case JObject valueAsJObject:
                            return GetCorrectObject<T>(remainingKey, rows, valueAsJObject, $"{processedKey}.{currentPart}");
                    }
                }
            
                var index = GetIndex(keyParts, rows);
                var bracketIndexOf = currentPart.IndexOf('[');
                var firstPartKey = currentPart;
                if (bracketIndexOf > -1)
                {
                    firstPartKey = firstPartKey[..bracketIndexOf];
                }

                if (usingResultSet[firstPartKey] is not JArray resultSetArray || index < 0 || index >= resultSetArray.Count)
                {
                    var fullKey = $"{processedKey}.{key}";
                    fullKey = fullKey.StartsWith('.') ? fullKey.Substring(1) : fullKey;
                    throw new ResultSetException($"Failed to get array from result set. The key being processed is '{fullKey}' at part '{currentPart}'. Already processed '{processedKey}'. If the key is correct but the value is not always present it is recommended to use a default value.");
                }

                var resultObject = resultSetArray[index] as JObject;

                return GetCorrectObject<T>(remainingKey, rows, resultObject, $"{processedKey}.{currentPart}");
            }
            catch (ResultSetException)
            {
                throw;
            }
            catch (Exception e)
            {
                var fullKey = $"{processedKey}.{key}";
                fullKey = fullKey.StartsWith('.') ? fullKey.Substring(1) : fullKey;
                
                throw new ResultSetException($"Something went wrong while processing the key in the result set. The key being processed is '{fullKey}' at part '{currentPart}'. Already processed '{processedKey}'. If the key is correct but the value is not always present it is recommended to use a default value.", e);
            }
        }

        private static int GetIndex(string[] keyParts, List<int> rows)
        {
            var indexLetter = keyParts[0][keyParts[0].Length - 2];
            var index = 0;
            
            // If an index letter is used get the correct value based on letter, starting from 'i'.
            if (Char.IsLetter(indexLetter))
            {
                var rowIndex = (int) indexLetter - 105;
                if (rowIndex >= 0 && rowIndex < rows.Count)
                {
                    index = rows[rowIndex];
                }
            }
            // If a specific value is used for the array index use that instead.
            else
            {
                var indexIdentifier = keyParts[0].Substring(keyParts[0].IndexOf('['));
                index = Int32.Parse(indexIdentifier.Substring(1, indexIdentifier.Length - 2));
            }

            return index;
        }
    }
}
