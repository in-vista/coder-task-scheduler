using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
using WiserTaskScheduler.Modules.ImportFiles.Enums;
using WiserTaskScheduler.Modules.ImportFiles.Interfaces;
using WiserTaskScheduler.Modules.ImportFiles.Models;

namespace WiserTaskScheduler.Modules.ImportFiles.Services
{
    public class ImportFilesService : IImportFilesService, IActionsService, IScopedService
    {
        private readonly ILogService logService;
        private readonly ILogger<ImportFilesService> logger;

        public ImportFilesService(ILogService logService, ILogger<ImportFilesService> logger)
        {
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
            var importFile = (ImportFileModel) action;

            if (importFile.Separator.Equals("\\t"))
            {
                importFile.Separator = '\t'.ToString();
            }

            if (importFile.SingleFile)
            {
                return await ImportFileAsync(importFile, ReplacementHelper.EmptyRows, resultSets, configurationServiceName, importFile.UseResultSet);
            }

            //When we want to import multiple files...
            var jArray = new JArray();

            if (String.IsNullOrWhiteSpace(importFile.UseResultSet) && !Directory.Exists(importFile.FilePath))
            {
                //When there is no resultset with filenames and also the FilePath contains an invalid directory
                await logService.LogError(logger, LogScopes.StartAndStop, importFile.LogSettings, $"The import in configuration '{configurationServiceName}', time ID '{importFile.TimeId}', order '{importFile.Order}' is set to not be a single file but no result set has been provided AND also no valid directory is given as FilePath. If the information is not dynamic set action to single file, otherwise provide a result set or a directory to use.", configurationServiceName, importFile.TimeId, importFile.Order);

                return new JObject
                {
                    {"Results", jArray}
                };
            }

            var rows = ResultSetHelper.GetCorrectObject<JArray>(importFile.UseResultSet, ReplacementHelper.EmptyRows, resultSets);

            for (var i = 0; i < rows.Count; i++)
            {
                var indexRows = new List<int> { i };
                jArray.Add(await ImportFileAsync(importFile, indexRows, resultSets, configurationServiceName, $"{importFile.UseResultSet}[{i}]"));
            }

            return new JObject
            {
                {"Results", jArray}
            };
        }

        /// <summary>
        /// Import a file.
        /// </summary>
        /// <param name="importFile">The information for the file to be imported.</param>
        /// <param name="rows">The indexes/rows of the array, passed to be used if '[i]' is used in the key.</param>
        /// <param name="resultSets">The result sets from previous actions in the same run.</param>
        /// <param name="configurationServiceName">The name of the configuration that contains this action.</param>
        /// <param name="useResultSet">The name of the result set to use, either the value as defined or added with an index.</param>
        /// <returns></returns>
        private async Task<JObject> ImportFileAsync(ImportFileModel importFile, List<int> rows, JObject resultSets, string configurationServiceName, string useResultSet)
        {
            var filePath = importFile.FilePath;

            // Replace the file path if a result set is used.
            if (!String.IsNullOrWhiteSpace(useResultSet))
            {
                var keyParts = useResultSet.Split('.');
                var usingResultSet = ResultSetHelper.GetCorrectObject<JObject>(useResultSet, ReplacementHelper.EmptyRows, resultSets);
                var remainingKey = keyParts.Length > 1 ? useResultSet.Substring(keyParts[0].Length + 1) : "";

                var tuple = ReplacementHelper.PrepareText(filePath, usingResultSet, remainingKey, importFile.HashSettings);

                filePath = ReplacementHelper.ReplaceText(tuple.Item1, rows, tuple.Item2, usingResultSet, importFile.HashSettings);
            }

            if (!File.Exists(filePath) && !Directory.Exists(filePath))
            {
                await logService.LogError(logger, LogScopes.RunStartAndStop, importFile.LogSettings, $"Failed to import file or directory '{filePath} ({importFile.FilePath})'. Path does not exists.", configurationServiceName, importFile.TimeId, importFile.Order);

                return new JObject
                {
                    {"Success", false}
                };
            }

            try
            {
                if (!String.IsNullOrEmpty(importFile.ProcessedFolder) && !Directory.Exists(importFile.ProcessedFolder))
                {
                    Directory.CreateDirectory(importFile.ProcessedFolder);
                }

                // Single file to import
                if (!Directory.Exists(filePath))
                {
                    return await ImportSingleFileAsync(importFile, filePath, configurationServiceName);
                }

                // Directory with files to import
                var jArray = new JArray();
                
                // Get all files sorted by creation date
                foreach(var file in Directory.GetFiles(filePath, importFile.SearchPattern, SearchOption.TopDirectoryOnly).OrderBy(file => File.GetCreationTime(file)).ToList())
                {
                   var result = await ImportSingleFileAsync(importFile, file, configurationServiceName);
                   if (result.ContainsKey("Success") && !(bool) result["Success"]) // Don't add failed imports to the result set.
                   {
                       continue;
                   }
                   jArray.Add(result);
                }

                return new JObject
                {
                    {"Results", jArray}
                };
            }
            catch (Exception e)
            {
                await logService.LogError(logger, LogScopes.RunStartAndStop, importFile.LogSettings, $"Failed to import file or directory '{importFile.FilePath}'. Error: {e}.", configurationServiceName, importFile.TimeId, importFile.Order);

                return new JObject
                {
                    {"Success", false}
                };
            }
        }

        private async Task<JObject> ImportSingleFileAsync(ImportFileModel importFile, string filePath, string configurationServiceName)
        {
            try
            {
                await logService.LogInformation(logger, LogScopes.RunBody, importFile.LogSettings, $"Importing file {filePath}.", configurationServiceName, importFile.TimeId, importFile.Order);

                JObject importResult;
                switch (importFile.FileType)
                {
                    case FileTypes.CSV:
                        importResult = await ImportCsvFileAsync(importFile, filePath, configurationServiceName);
                        break;
                    case FileTypes.XML:
                        importResult = await ImportXmlFileAsync(importFile, filePath);
                        break;
                    default:
                        throw new NotImplementedException($"File type '{importFile.FileType}' is not yet implemented to be imported.");
                }

                if (String.IsNullOrEmpty(importFile.ProcessedFolder))
                {
                    return importResult;
                }

                // Move file if successfully imported.
                var destinationFileName = new StringBuilder(Path.GetFileNameWithoutExtension(filePath));
                if (importFile.AddSuffixToFileNameAfterProcessing)
                {
                    destinationFileName.Append($"_{DateTime.Now:yyyyMMddHHmmss}");
                }

                destinationFileName.Append(Path.GetExtension(filePath));

                File.Move(filePath, Path.Combine(importFile.ProcessedFolder, destinationFileName.ToString()));
                return importResult;
            }
            catch (Exception e)
            {
                await logService.LogError(logger, LogScopes.RunStartAndStop, importFile.LogSettings, $"Failed to import file '{importFile.FilePath}'.\n{e}", configurationServiceName, importFile.TimeId, importFile.Order);

                return new JObject
                {
                    {"Success", false}
                };
            }
        }


        /// <summary>
        /// Import a CSV file.
        /// </summary>
        /// <param name="importFile">The information for the file to be imported.</param>
        /// <param name="filePath">The path to the CSV file to import.</param>
        /// <param name="configurationServiceName">The name of the configuration that contains this action.</param>
        /// <returns></returns>
        private async Task<JObject> ImportCsvFileAsync(ImportFileModel importFile, string filePath, string configurationServiceName)
        {
            var lines = await File.ReadAllLinesAsync(filePath);
            var jArray = new JArray();

            if (!importFile.HasFieldNames)
            {
                var firstColumnLength = -1;

                for (var i = 0; i < lines.Length; i++)
                {
                    if (String.IsNullOrWhiteSpace(lines[i]))
                    {
                        await logService.LogWarning(logger, LogScopes.RunBody, importFile.LogSettings, $"Did not import line {i} due to empty row in file {filePath}", configurationServiceName, importFile.TimeId, importFile.Order);
                        continue;
                    }

                    JArray columns;
                    if (!string.IsNullOrEmpty(importFile.EscapeCharacter))
                    {
                        columns = new JArray(lines[i].Split(importFile.Separator).Select(column => column.Trim(importFile.EscapeCharacter[0])));
                    }
                    else
                    {
                        columns = new JArray(lines[i].Split(importFile.Separator));
                    }
                    
                    // Use the first row to determine the number of columns to be expected in each row.
                    if (firstColumnLength == -1)
                    {
                        firstColumnLength = columns.Count;
                    }
                    else if(columns.Count != firstColumnLength)
                    {
                        await logService.LogWarning(logger, LogScopes.RunBody, importFile.LogSettings, $"Did not import line {i} due to missing columns in file {filePath}", configurationServiceName, importFile.TimeId, importFile.Order);
                        continue;
                    }

                    var row = new JObject
                    {
                        {"Columns", columns }
                    };
                    jArray.Add(row);
                }

                return new JObject
                {
                    {"Success", true},
                    {"FilePath",filePath},
                    {"Results", jArray}
                };
            }

            var fieldNames = lines[0].Split(importFile.Separator);
            if (!string.IsNullOrEmpty(importFile.EscapeCharacter))
            {
                fieldNames = (string[])(fieldNames.Select(field => field.Trim(importFile.EscapeCharacter[0])).ToArray());
            }

            //Read all lines
            for (var i = 1; i < lines.Length; i++)
            {
                var row = new JObject();

                if (String.IsNullOrWhiteSpace(lines[i]))
                {
                    await logService.LogWarning(logger, LogScopes.RunBody, importFile.LogSettings, $"Did not import line {i} due to empty row in file {filePath}", configurationServiceName, importFile.TimeId, importFile.Order);
                    continue;
                }

                string[] columns;
                //Get values
                if (!string.IsNullOrEmpty(importFile.EscapeCharacter))
                {
                    var line = lines[i];
                    var values = new List<string>();
                    while (line.Length > 0)
                    {
                        string value = null;
                        //If an escape character is given, then we need to handle this, so don't split on separator, but go to next escape character
                        var index =  line.StartsWith(importFile.EscapeCharacter, StringComparison.Ordinal) ? 
                            line.TrimStart(importFile.EscapeCharacter[0]).IndexOf(importFile.EscapeCharacter, StringComparison.Ordinal)+2 : 
                            line.IndexOf(importFile.Separator, StringComparison.Ordinal);
                        value = index >= 0 ? line.Substring(0, index) : line;
                        values.Add(value.Trim(importFile.EscapeCharacter[0]));
                        if (line.Length <= value.Length + importFile.Separator.Length)
                        {
                            if (line.EndsWith(importFile.Separator))
                                values.Add("");
                            line = "";
                        }
                        else
                            line = line.Substring(value.Length + importFile.Separator.Length);
                    }
                    columns = values.ToArray();
                }
                else
                {
                    columns = lines[i].Split(importFile.Separator);
                    if (!string.IsNullOrEmpty(importFile.EscapeCharacter))
                    {
                        columns = (string[])(columns.Select(column => column.Trim(importFile.EscapeCharacter[0])).ToArray());
                    }
                }

                if (columns.Length < fieldNames.Length)
                {
                    await logService.LogWarning(logger, LogScopes.RunBody, importFile.LogSettings, $"Did not import line {i} due to missing values in line. File: {filePath}", configurationServiceName, importFile.TimeId, importFile.Order);
                    continue;
                }

                for (var j = 0; j < fieldNames.Length; j++)
                {
                    row.Add(fieldNames[j], columns[j]);
                }

                jArray.Add(row);
            }

            var fieldArray = new JArray();
            foreach (var fieldName in fieldNames)
            {
                fieldArray.Add(fieldName);
            }

            return new JObject
            {
                {"Success", true},
                {"FilePath",filePath},
                {"Fields",  fieldArray},
                {"Results", jArray}
            };
        }

        /// <summary>
        /// Import an XML file.
        /// </summary>
        /// <param name="importFile">The ImportFileModel object.</param>
        /// <param name="filePath">The path to the CSV file to import.</param>
        /// <returns></returns>
        private async Task<JObject> ImportXmlFileAsync(ImportFileModel importFile, string filePath)
        {
            var xml = await File.ReadAllTextAsync(filePath);
            xml = Regex.Replace(xml, "\\sxmlns[^\"]+\"[^\"]+\"", ""); // Remove namespaces from the xml, because this gives problemns with the xpath expressions.
            var xmlDocument = new XmlDocument();
            xmlDocument.LoadXml(xml);

            if (importFile?.XmlMapping == null || importFile.XmlMapping.Any() == false) // No mapping supplied, return the whole document.
            {
                return JObject.Parse(JsonConvert.SerializeXmlNode(xmlDocument));
            }

            // Mapping supplied, return the mapped values.
            var result = new JArray();
            var expressionWithIndex = importFile.XmlMapping.FirstOrDefault(mapping => mapping.XPathExpression.Contains("[j]"));
            if (expressionWithIndex != null)
            {
                var indexedKey = expressionWithIndex.XPathExpression[..expressionWithIndex.XPathExpression.IndexOf("[j]", StringComparison.Ordinal)];
                var nodes = xmlDocument.SelectNodes(indexedKey)?.Count ?? 0;
                if (nodes == 0)
                {
                    return new JObject
                    {
                        {"Success", true},
                        {"FilePath",filePath},
                        {"Results", result}
                    };
                }

                // Add object for each node.
                for (var i = 0; i < nodes; i++)
                {
                    result.Add(new JObject());
                }
            }
            else
            {
                // Add single object to result.
                result.Add(new JObject());
            }

            for (var i = 0; i < result.Count; i++)
            {
                foreach (var xmlMap in importFile.XmlMapping)
                {
                    // Array indexers in XPath are 1-based, so add 1 to 'i' to select the correct item.
                    var xPathExpression = xmlMap.XPathExpression.Replace("[j]", $"[{i + 1}]");
                    var xmlNode = xmlDocument.SelectSingleNode(xPathExpression);
                    ((JObject)result[i]).Add(xmlMap.ResultSetName, xmlNode?.InnerText ?? "");
                }
            }

            return new JObject
            {
                {"Success", true},
                {"FilePath",filePath},
                {"Results", result}
            };
        }
    }
}
