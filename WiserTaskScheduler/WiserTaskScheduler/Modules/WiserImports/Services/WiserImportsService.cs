using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Interfaces;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.Communication.Interfaces;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using GeeksCoreLibrary.Modules.GclReplacements.Interfaces;
using GeeksCoreLibrary.Modules.Imports.Models;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WiserTaskScheduler.Core.Enums;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models;
using WiserTaskScheduler.Modules.Wiser.Interfaces;
using WiserTaskScheduler.Modules.WiserImports.Interfaces;
using WiserTaskScheduler.Modules.WiserImports.Models;

namespace WiserTaskScheduler.Modules.WiserImports.Services;

public class WiserImportsService : IWiserImportsService, IActionsService, IScopedService
{
    private const string DefaultSubject = "Import[if({name}!)] with the name '{name}'[endif] from {date:DateTime(dddd\\, dd MMMM yyyy,en-US)} did [if({errorCount}=0)]finish successfully[else](partially) go wrong[endif]";
    private const string DefaultContent = "<p>The import started on {startDate:DateTime(HH\\:mm\\:ss)} and finished on {endDate:DateTime(HH\\:mm\\:ss)}. The import took a total of {hours} hour(s), {minutes} minute(s) and {seconds} second(s).</p>[if({errorCount}!0)] <br /><br />The following errors occurred during the import: {errors:Raw}[endif]";

    private readonly IServiceProvider serviceProvider;
    private readonly ILogService logService;
    private readonly ILogger<WiserImportsService> logger;
    private readonly JsonSerializerSettings serializerSettings = new() {NullValueHandling = NullValueHandling.Ignore};
    private readonly FileExtensionContentTypeProvider fileExtensionContentTypeProvider = new();

    private string connectionString;

    public WiserImportsService(IServiceProvider serviceProvider, ILogService logService, ILogger<WiserImportsService> logger)
    {
        this.serviceProvider = serviceProvider;
        this.logService = logService;
        this.logger = logger;
    }

    /// <inheritdoc />
    public Task InitializeAsync(ConfigurationModel configuration, HashSet<string> tablesToOptimize)
    {
        connectionString = configuration.ConnectionString;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<JObject> Execute(ActionModel action, JObject resultSets, string configurationServiceName)
    {
        var wiserImport = (WiserImportModel) action;
        var connectionStringToUse = wiserImport.ConnectionString ?? connectionString;
        var databaseName = GetDatabaseNameForLogging(connectionStringToUse);

        await logService.LogInformation(logger, LogScopes.RunStartAndStop, wiserImport.LogSettings, $"Starting the import for '{databaseName}'.", configurationServiceName, wiserImport.TimeId, wiserImport.Order);

        using var scope = serviceProvider.CreateScope();
        await using var databaseConnection = scope.ServiceProvider.GetRequiredService<IDatabaseConnection>();
        await databaseConnection.ChangeConnectionStringsAsync(connectionStringToUse, connectionStringToUse);

        var importDataTable = await GetImportsToProcessAsync(databaseConnection);
        if (importDataTable.Rows.Count == 0)
        {
            await logService.LogInformation(logger, LogScopes.RunStartAndStop, wiserImport.LogSettings, $"Finished the import for '{databaseName}' due to no imports to process.", configurationServiceName, wiserImport.TimeId, wiserImport.Order);

            return new JObject()
            {
                {"Database", databaseName},
                {"Success", 0},
                {"WithWarnings", 0},
                {"Total", 0}
            };
        }

        var stopwatch = new Stopwatch();

        var wiserItemsService = scope.ServiceProvider.GetRequiredService<IWiserItemsService>();
        var taskAlertsService = scope.ServiceProvider.GetRequiredService<ITaskAlertsService>();

        var successfulImports = 0;
        var importsWithWarnings = 0;

        foreach (DataRow row in importDataTable.Rows)
        {
            var importRow = new ImportRowModel(row);
            var usernameForLogs = $"{importRow.Username} (Import)";

            if (String.IsNullOrWhiteSpace(importRow.RawData))
            {
                await logService.LogError(logger, LogScopes.RunBody, wiserImport.LogSettings, $"Import for '{databaseName}' with ID '{importRow.Id}' failed because there is no data to import.", configurationServiceName, wiserImport.TimeId, wiserImport.Order);
                continue;
            }

            stopwatch.Restart();
            var startDate = DateTime.Now;
            databaseConnection.AddParameter("id", importRow.Id);
            databaseConnection.AddParameter("startDate", startDate);
            await databaseConnection.ExecuteAsync($"UPDATE {WiserTableNames.WiserImport} SET started_on = ?startDate WHERE id = ?id");

            var errors = new List<string>();
            var destinationLinksToKeep = new Dictionary<int, Dictionary<ulong, List<ulong>>>();
            var itemParentIdsToKeep = new Dictionary<int, Dictionary<ulong, List<ulong>>>();
            var sourceLinksToKeep = new Dictionary<int, Dictionary<ulong, List<ulong>>>();

            // The IDs of the new items that other rows in this import link to, by their import key.
            Dictionary<string, ulong> savedImportKeys = new();

            var importData = JsonConvert.DeserializeObject<List<ImportDataModel>>(importRow.RawData, serializerSettings);
            foreach (var import in importData)
            {
                await ResolveImportKeysAsync(wiserImport, import, savedImportKeys, errors, configurationServiceName);

                try
                {
                    import.Item = await wiserItemsService.SaveAsync(import.Item, import.Item.ParentItemId, username: usernameForLogs, userId: importRow.UserId);
                    if (!string.IsNullOrWhiteSpace(import.ImportKey))
                    {
                        savedImportKeys[import.ImportKey] = import.Item.Id;
                    }
                }
                catch (Exception e)
                {
                    await logService.LogError(logger, LogScopes.RunBody, wiserImport.LogSettings, $"Failed to import an item due to exception:\n{e}\n\nItem details:\n{JsonConvert.SerializeObject(import.Item, serializerSettings)}", configurationServiceName, wiserImport.TimeId, wiserImport.Order);

                    // No need to import links and files if the item failed to be imported.
                    continue;
                }

                await ImportLinksAsync(wiserImport, import, databaseConnection, wiserItemsService, usernameForLogs, errors, destinationLinksToKeep, itemParentIdsToKeep, sourceLinksToKeep, importRow, configurationServiceName);
                await ImportFilesAsync(wiserImport, import, databaseConnection, wiserItemsService, usernameForLogs, errors, importRow, configurationServiceName);
            }

            // If there have been no errors the import was successful. We can safely cleanup any links that need to be deleted.
            if (!errors.Any())
            {
                await CleanupLinks(wiserImport, databaseConnection, wiserItemsService, errors, destinationLinksToKeep, itemParentIdsToKeep, sourceLinksToKeep, usernameForLogs, importRow, configurationServiceName, importData.First().Item.EntityType);
                successfulImports++;
            }
            else
            {
                importsWithWarnings++;
            }

            var endDate = DateTime.Now;
            stopwatch.Stop();

            databaseConnection.AddParameter("id", importRow.Id);
            databaseConnection.AddParameter("finishedOn", DateTime.Now);
            databaseConnection.AddParameter("success", !errors.Any());
            databaseConnection.AddParameter("errors", String.Join(Environment.NewLine, errors));
            await databaseConnection.ExecuteAsync($"UPDATE {WiserTableNames.WiserImport} SET finished_on = ?finishedOn, success = ?success, errors = ?errors WHERE id = ?id");

            var replaceData = new Dictionary<string, object>
            {
                {"name", importRow.ImportName},
                {"date", DateTime.Now},
                {"errorCount", errors.Count},
                {"startDate", startDate},
                {"endDate", endDate},
                {"hours", stopwatch.Elapsed.Hours},
                {"minutes", stopwatch.Elapsed.Minutes},
                {"seconds", stopwatch.Elapsed.Seconds},
                {"errors", $"<ul><li><pre>{String.Join("</pre></li><li><pre>", errors)}</pre></li></ul>"}
            };

            WiserItemModel template = null;
            if (wiserImport.TemplateId > 0)
            {
                template = await wiserItemsService.GetItemDetailsAsync(wiserImport.TemplateId, userId: importRow.UserId);
            }

            var subject = template?.GetDetailValue("subject");
            var content = template?.GetDetailValue("template");
            if (String.IsNullOrWhiteSpace(subject))
            {
                subject = DefaultSubject;
            }
            if (String.IsNullOrWhiteSpace(content))
            {
                content = DefaultContent;
            }

            await taskAlertsService.NotifyUserByEmailAsync(importRow.UserId, importRow.Username, wiserImport, configurationServiceName, subject, content, replaceData, template?.GetDetailValue("sender_email"), template?.GetDetailValue("sender_name"));
            await taskAlertsService.SendMessageToUserAsync(importRow.UserId, importRow.Username, subject, wiserImport, configurationServiceName, replaceData, importRow.UserId, usernameForLogs);
        }

        return new JObject()
        {
            {"Database", databaseName},
            {"Success", successfulImports},
            {"WithWarnings", importsWithWarnings},
            {"Total", successfulImports + importsWithWarnings}
        };
    }

    /// <summary>
    /// Get the name of the database that is used for the imports to include in the logging.
    /// </summary>
    /// <param name="connectionStringToUse">The connection string to get the database name from.</param>
    /// <returns>Returns the name of the database or 'Unknown' if it failed to retrieve a value.</returns>
    private string GetDatabaseNameForLogging(string connectionStringToUse)
    {
        var connectionStringParts = connectionStringToUse.Split(';');
        foreach (var part in connectionStringParts)
        {
            if (part.StartsWith("database"))
            {
                return part.Split('=')[1];
            }
        }

        return "Unknown";
    }

    /// <summary>
    /// Get all imports that need to be processed that have been created by the current machine/server.
    /// Only imports from the same machine/server are processed to ensure any files uploaded to be included are present.
    /// </summary>
    /// <param name="databaseConnection">The database connection to use.</param>
    /// <returns>Returns a <see cref="DataTable"/> containing the rows of the imports that need to be processed.</returns>
    private async Task<DataTable> GetImportsToProcessAsync(IDatabaseConnection databaseConnection)
    {
        databaseConnection.AddParameter("serverName", Environment.MachineName);

        // Ensure import times are based on server time and not database time to prevent difference in time zones to have imports be processed to early/late.
        databaseConnection.AddParameter("now", DateTime.Now);
        return await databaseConnection.GetAsync($@"
SELECT 
    id,
    name,
    user_id,
    customer_id,
    added_by,
    data,
    sub_domain
FROM {WiserTableNames.WiserImport}
WHERE server_name = ?serverName
AND started_on IS NULL
AND start_on <= ?now
ORDER BY added_on ASC");
    }

    /// <summary>
    /// Sets the destination of the links that refer to a new item from the same import via <see cref="ItemLinkImportModel.DestinationImportKey"/>,
    /// to the ID that item got when it was saved. For links that use the parent_item_id, the item is placed under that item.
    /// Links to an item that wasn't saved (for example because it failed) are removed from the import and added to the errors.
    /// </summary>
    /// <param name="wiserImport">The WTS information for handling the imports.</param>
    /// <param name="import">The data to import.</param>
    /// <param name="savedImportKeys">The IDs of the items that were saved in this import, by their import key.</param>
    /// <param name="errors">The list of errors.</param>
    /// <param name="configurationServiceName">The name of the configuration the import is executed within.</param>
    private async Task ResolveImportKeysAsync(WiserImportModel wiserImport, ImportDataModel import, Dictionary<string, ulong> savedImportKeys, List<string> errors, string configurationServiceName)
    {
        List<ItemLinkImportModel> unresolvedLinks = [];
        foreach (ItemLinkImportModel link in import.Links.Where(link => !string.IsNullOrWhiteSpace(link.DestinationImportKey)))
        {
            if (!savedImportKeys.TryGetValue(link.DestinationImportKey, out ulong destinationItemId))
            {
                string error = $"Can't link item '{import.Item.Title}' to the item with import key '{link.DestinationImportKey}', because that item hasn't been imported.";
                await logService.LogError(logger, LogScopes.RunBody, wiserImport.LogSettings, error, configurationServiceName, wiserImport.TimeId, wiserImport.Order);
                errors.Add(error);
                unresolvedLinks.Add(link);
                continue;
            }

            link.DestinationItemId = destinationItemId;
            if (link.UseParentItemId)
            {
                import.Item.ParentItemId = destinationItemId;
            }
        }

        import.Links.RemoveAll(unresolvedLinks.Contains);
    }

    /// <summary>
    /// Import the links to Wiser.
    /// </summary>
    /// <param name="wiserImport">The WTS information for handling the imports.</param>
    /// <param name="import">The data to import.</param>
    /// <param name="databaseConnection">The connection to the database.</param>
    /// <param name="wiserItemsService">The WiserItemsService to use to import the data.</param>
    /// <param name="usernameForLogs">The username to use for the logs in Wiser.</param>
    /// <param name="errors">The list of errors.</param>
    /// <param name="destinationLinksToKeep">The list of destination links to keep.</param>
    /// <param name="itemParentIdsToKeep">The list of item parent ids to keep.</param>
    /// <param name="sourceLinksToKeep">The list of source links to keep.</param>
    /// <param name="importRow">The information of the import itself.</param>
    /// <param name="configurationServiceName">The name of the configuration the import is executed within.</param>
    private async Task ImportLinksAsync(WiserImportModel wiserImport, ImportDataModel import, IDatabaseConnection databaseConnection, IWiserItemsService wiserItemsService, string usernameForLogs, List<string> errors, Dictionary<int, Dictionary<ulong, List<ulong>>> destinationLinksToKeep, Dictionary<int, Dictionary<ulong, List<ulong>>> itemParentIdsToKeep, Dictionary<int, Dictionary<ulong, List<ulong>>> sourceLinksToKeep, ImportRowModel importRow, string configurationServiceName)
    {
        foreach (var link in import.Links)
        {
            try
            {
                if (link.ItemId == 0 && link.DestinationItemId == 0)
                {
                    await logService.LogError(logger, LogScopes.RunBody, wiserImport.LogSettings, $"Found link with neither an item_id nor a destination_item_id for item '{import.Item.Id}', but it has not data, so there is nothing we can import.", configurationServiceName, wiserImport.TimeId, wiserImport.Order);
                    continue;
                }

                // Set the missing link to the new item.
                if (link.ItemId == 0)
                {
                    link.ItemId = import.Item.Id;
                }
                else if (link.DestinationItemId == 0)
                {
                    link.DestinationItemId = import.Item.Id;
                }

                // Sorting starts with 1, so set value to 1 if not set.
                if (link.Ordering == 0)
                {
                    link.Ordering = 1;
                }

                if (!link.UseParentItemId)
                {
                    databaseConnection.AddParameter("itemId", link.ItemId);
                    databaseConnection.AddParameter("destinationItemId", link.DestinationItemId);
                    databaseConnection.AddParameter("type", link.Type);
                    var linkPrefix = await wiserItemsService.GetTablePrefixForLinkAsync(link.Type);
                    var existingLinkDataTable = await databaseConnection.GetAsync($"SELECT id FROM {linkPrefix}{WiserTableNames.WiserItemLink} WHERE item_id = ?itemId AND destination_item_id = ?destinationItemId AND type = ?type");

                    if (existingLinkDataTable.Rows.Count > 0)
                    {
                        link.Id = Convert.ToUInt64(existingLinkDataTable.Rows[0]["id"]);
                    }
                    else
                    {
                        link.Id = await wiserItemsService.AddItemLinkAsync(link.ItemId, link.DestinationItemId, link.Type, link.Ordering, usernameForLogs, importRow.UserId);
                    }
                }

                // If the user wants to delete existing links, make a list with links that we need to keep, so we can delete the rest at the end of the import.
                if (link.DeleteExistingLinks)
                {
                    // If the current item is the source item, then we want to delete all other links of the given destination, so that only the imported items will remain linked to that destination.
                    if (link.ItemId == import.Item.Id)
                    {
                        var listToUse = link.UseParentItemId ? itemParentIdsToKeep : destinationLinksToKeep;

                        if (!listToUse.ContainsKey(link.Type))
                        {
                            listToUse.Add(link.Type, new Dictionary<ulong, List<ulong>>());
                        }

                        if (!listToUse[link.Type].ContainsKey(link.DestinationItemId))
                        {
                            listToUse[link.Type].Add(link.DestinationItemId, new List<ulong>());
                        }

                        listToUse[link.Type][link.DestinationItemId].Add(link.ItemId);
                    }

                    // Else if the current item is the destination item, then we want to delete all other links of the given source, so that this item will only remain linked the the items from this import.
                    else if (link.DestinationItemId == import.Item.Id)
                    {
                        if (!sourceLinksToKeep.ContainsKey(link.Type))
                        {
                            sourceLinksToKeep.Add(link.Type, new Dictionary<ulong, List<ulong>>());
                        }

                        if (!sourceLinksToKeep[link.Type].ContainsKey(link.ItemId))
                        {
                            sourceLinksToKeep[link.Type].Add(link.ItemId, new List<ulong>());
                        }

                        sourceLinksToKeep[link.Type][link.ItemId].Add(link.DestinationItemId);
                    }
                }

                if (!link.UseParentItemId && link.Details != null && link.Details.Any())
                {
                    foreach (var linkDetail in link.Details)
                    {
                        linkDetail.IsLinkProperty = true;
                        linkDetail.ItemLinkId = link.Id;
                        linkDetail.Changed = true;
                    }

                    import.Item.Details.AddRange(link.Details);
                    await wiserItemsService.SaveAsync(import.Item, username: usernameForLogs, userId: importRow.UserId);
                }
            }
            catch (Exception e)
            {
                await logService.LogError(logger, LogScopes.RunBody, wiserImport.LogSettings, $"Error while trying to import an item link due to exception:\n{e}\n\nItem link details:\n{JsonConvert.SerializeObject(link, serializerSettings)}", configurationServiceName, wiserImport.TimeId, wiserImport.Order);
                errors.Add(e.Message);
            }
        }
    }

    /// <summary>
    /// Import the files to Wiser.
    /// </summary>
    /// <param name="wiserImport">The WTS information for handling the imports.</param>
    /// <param name="import">The data to import.</param>
    /// <param name="databaseConnection">The connection to the database.</param>
    /// <param name="wiserItemsService">The WiserItemsService to use to import the data.</param>
    /// <param name="usernameForLogs">The username to use for the logs in Wiser.</param>
    /// <param name="errors">The list of errors.</param>
    /// <param name="importRow">The information of the import itself.</param>
    /// <param name="configurationServiceName">The name of the configuration the import is executed within.</param>
    private async Task ImportFilesAsync(WiserImportModel wiserImport, ImportDataModel import, IDatabaseConnection databaseConnection, IWiserItemsService wiserItemsService, string usernameForLogs, List<string> errors, ImportRowModel importRow, string configurationServiceName)
    {
        var basePath = $@"C:\temp\WTS Import\{importRow.CustomerId}\{importRow.Id}\";
        foreach (var file in import.Files)
        {
            try
            {
                var fileLocation = Path.Combine(basePath, file.FileName);
                if (!File.Exists(fileLocation))
                {
                    var errorMessage = $"Could not import file '{file.FileName}' for item '{import.Item.Id}' because it was not found on the hard-disk of the server.";
                    errors.Add(errorMessage);
                    await logService.LogError(logger, LogScopes.RunBody, wiserImport.LogSettings, errorMessage, configurationServiceName, wiserImport.TimeId, wiserImport.Order);

                    continue;
                }

                file.ItemId = import.Item.Id;
                file.Content = await File.ReadAllBytesAsync(fileLocation);
                if (fileExtensionContentTypeProvider.TryGetContentType(file.FileName, out var contentType))
                {
                    file.ContentType = contentType;
                }

                file.Id = await wiserItemsService.AddItemFileAsync(file, usernameForLogs, importRow.UserId);
            }
            catch (Exception e)
            {
                await logService.LogError(logger, LogScopes.RunBody, wiserImport.LogSettings, $"Error while trying to import an item file:\n{e}\n\nFile details:\n{JsonConvert.SerializeObject(file, serializerSettings)}", configurationServiceName, wiserImport.TimeId, wiserImport.Order);
                errors.Add(e.Message);
            }
        }
    }

    /// <summary>
    /// Cleanup all links that have not been included in this import if it is requested.
    /// </summary>
    /// <param name="wiserImport">The WTS information for handling the imports.</param>
    /// <param name="databaseConnection">The connection to the database.</param>
    /// <param name="wiserItemsService">The WiserItemsService to use to import the data.</param>
    /// <param name="errors">The list of errors.</param>
    /// <param name="destinationLinksToKeep">The list of destination links to keep.</param>
    /// <param name="itemParentIdsToKeep">The list of item parent ids to keep.</param>
    /// <param name="sourceLinksToKeep">The list of source links to keep.</param>
    /// <param name="usernameForLogs">The username to use for the logs in Wiser.</param>
    /// <param name="importRow">The information of the import itself.</param>
    /// <param name="configurationServiceName">The name of the configuration the import is executed within.</param>
    private async Task CleanupLinks(WiserImportModel wiserImport, IDatabaseConnection databaseConnection, IWiserItemsService wiserItemsService, List<string> errors, Dictionary<int, Dictionary<ulong, List<ulong>>> destinationLinksToKeep, Dictionary<int, Dictionary<ulong, List<ulong>>> itemParentIdsToKeep, Dictionary<int, Dictionary<ulong, List<ulong>>> sourceLinksToKeep, string usernameForLogs, ImportRowModel importRow, string configurationServiceName, string entityType)
    {
        foreach (var destinationLink in destinationLinksToKeep)
        {
            var linkType = destinationLink.Key;
            foreach (var destination in destinationLink.Value)
            {
                var destinationItemId = destination.Key;
                var linkSettings = await wiserItemsService.GetLinkTypeSettingsAsync(linkType, sourceEntityType: entityType);
                var linkPrefix = wiserItemsService.GetTablePrefixForLink(linkSettings);

                try
                {
                    databaseConnection.AddParameter("type", linkType);
                    databaseConnection.AddParameter("destinationItemId", destinationItemId);
                    var destinationLinksToDelete = await databaseConnection.GetAsync($"SELECT `id`, item_id AS sourceId, destination_item_id AS destinationId FROM {linkPrefix}{WiserTableNames.WiserItemLink} WHERE type = ?type AND destination_item_id = ?destinationItemId AND item_id NOT IN ({String.Join(",", destination.Value)})");

                    if (destinationLinksToDelete.Rows.Count > 0)
                    {
                        var linkIds = new HashSet<ulong>();
                        var destinationIds = new HashSet<ulong>();
                        var sourceIds = new HashSet<ulong>();

                        foreach (DataRow row in destinationLinksToDelete.Rows)
                        {
                            linkIds.Add(Convert.ToUInt64(row["id"]));
                            destinationIds.Add(Convert.ToUInt64(row["destinationId"]));
                            sourceIds.Add(Convert.ToUInt64(row["sourceId"]));
                        }

                        await wiserItemsService.RemoveItemLinksByIdAsync(linkIds.ToList(), linkSettings.SourceEntityType, sourceIds.ToList(), linkSettings.DestinationEntityType, destinationIds.ToList(), usernameForLogs, importRow.UserId);
                    }
                }
                catch (Exception e)
                {
                    await logService.LogError(logger, LogScopes.RunBody, wiserImport.LogSettings, $"Error while trying to delete item links (destination) of type '{linkType}' due to exception:\n{e}", configurationServiceName, wiserImport.TimeId, wiserImport.Order);
                    errors.Add(e.Message);
                }
            }
        }

        foreach (var parentItem in itemParentIdsToKeep)
        {
            var linkType = parentItem.Key;

            foreach (var parent in parentItem.Value)
            {
                var parentItemId = parent.Key;
                var linkSettings = await wiserItemsService.GetLinkTypeSettingsAsync(linkType, sourceEntityType: entityType);

                try
                {
                    var linkIds = new HashSet<ulong>();
                    var destinationIds = new HashSet<ulong>() {parentItemId};
                    var sourceIds = new HashSet<ulong>();
                    var linkedItems = await wiserItemsService.GetLinkedItemDetailsAsync(parentItemId, linkType, userId: importRow.UserId, entityType: linkSettings.SourceEntityType);

                    foreach (var linkedItem in linkedItems)
                    {
                        if (parent.Value.Contains(linkedItem.Id))
                        {
                            continue;
                        }

                        linkIds.Add(linkedItem.Id);
                        sourceIds.Add(linkedItem.Id);
                    }

                    if (linkIds.Any())
                    {
                        await wiserItemsService.RemoveParentLinkOfItemsAsync(linkIds.ToList(), linkSettings.SourceEntityType, sourceIds.ToList(), linkSettings.DestinationEntityType, destinationIds.ToList(), usernameForLogs, importRow.UserId);
                    }
                }
                catch (Exception e)
                {
                    await logService.LogError(logger, LogScopes.RunBody, wiserImport.LogSettings, $"Error while trying to delete item links (parent) of type '{linkType}' due to exception:\n{e}", configurationServiceName, wiserImport.TimeId, wiserImport.Order);
                    errors.Add(e.Message);
                }
            }
        }

        foreach (var sourceLink in sourceLinksToKeep)
        {
            var linkType = sourceLink.Key;
            foreach (var source in sourceLink.Value)
            {
                var sourceItemId = source.Key;
                var linkSettings = await wiserItemsService.GetLinkTypeSettingsAsync(linkType, destinationEntityType: entityType);
                var linkPrefix = wiserItemsService.GetTablePrefixForLink(linkSettings);

                try
                {
                    databaseConnection.AddParameter("type", linkType);
                    databaseConnection.AddParameter("sourceItemId", sourceItemId);
                    var sourceLinksToDelete = await databaseConnection.GetAsync($"SELECT `id`, item_id AS sourceId, destination_item_id AS destinationId FROM {linkPrefix}{WiserTableNames.WiserItemLink} WHERE type = ?type AND item_id = ?sourceItemId AND destination_item_id NOT IN ({String.Join(",", source.Value)})");

                    if (sourceLinksToDelete.Rows.Count > 0)
                    {
                        var linkIds = new HashSet<ulong>();
                        var destinationIds = new HashSet<ulong>();
                        var sourceIds = new HashSet<ulong>();

                        foreach (DataRow row in sourceLinksToDelete.Rows)
                        {
                            linkIds.Add(Convert.ToUInt64(row["id"]));
                            destinationIds.Add(Convert.ToUInt64(row["destinationId"]));
                            sourceIds.Add(Convert.ToUInt64(row["sourceId"]));
                        }

                        await wiserItemsService.RemoveItemLinksByIdAsync(linkIds.ToList(), linkSettings.SourceEntityType, sourceIds.ToList(), linkSettings.DestinationEntityType, destinationIds.ToList(), usernameForLogs, importRow.UserId);
                    }
                }
                catch (Exception e)
                {
                    await logService.LogError(logger, LogScopes.RunBody, wiserImport.LogSettings, $"Error while trying to delete item links (source) of type '{linkType}' due to exception:\n{e}", configurationServiceName, wiserImport.TimeId, wiserImport.Order);
                    errors.Add(e.Message);
                }
            }
        }
    }
}