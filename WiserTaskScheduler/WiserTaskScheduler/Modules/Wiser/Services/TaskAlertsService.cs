using System;
using System.Collections.Generic;
using System.Data;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Enums;
using GeeksCoreLibrary.Core.Interfaces;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.Communication.Interfaces;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using GeeksCoreLibrary.Modules.GclReplacements.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WiserTaskScheduler.Core.Enums;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models;
using WiserTaskScheduler.Modules.Wiser.Interfaces;
using HttpMethod = System.Net.Http.HttpMethod;

namespace WiserTaskScheduler.Modules.Wiser.Services;

/// <inheritdoc cref="ITaskAlertsService" />
public class TaskAlertsService : ITaskAlertsService, IScopedService
{
    private const string EntityType = "agendering";
    private const int ModuleId = 708;
    private const string DateField = "agendering_date";
    private const string ContentField = "content";
    private const string UserIdField = "userid";
    private const string UsernameField = "username";
    private const string SenderNameField = "placed_by";
    private const string SenderIdField = "placed_by_id";

    private readonly IWiserItemsService wiserItemsService;
    private readonly IWiserService wiserService;
    private readonly ILogService logService;
    private readonly ILogger<TaskAlertsService> logger;
    private readonly IDatabaseConnection databaseConnection;
    private readonly IStringReplacementsService stringReplacementsService;
    private readonly ICommunicationsService gclCommunicationsService;
    private readonly WtsSettings wtsSettings;

    public TaskAlertsService(IWiserItemsService wiserItemsService,
                             IWiserService wiserService,
                             IOptions<WtsSettings> wtsSettings,
                             ILogService logService,
                             ILogger<TaskAlertsService> logger,
                             IDatabaseConnection databaseConnection,
                             IStringReplacementsService stringReplacementsService,
                             ICommunicationsService gclCommunicationsService)
    {
        this.wiserItemsService = wiserItemsService;
        this.wiserService = wiserService;
        this.logService = logService;
        this.logger = logger;
        this.databaseConnection = databaseConnection;
        this.stringReplacementsService = stringReplacementsService;
        this.gclCommunicationsService = gclCommunicationsService;
        this.wtsSettings = wtsSettings.Value;
    }

    /// <inheritdoc />
    public async Task<WiserItemModel> SendMessageToUserAsync(ulong receiverId, string receiverName, string message, ActionModel action, string configurationServiceName, IDictionary<string, object> replaceData, ulong senderId = 0, string senderName = "WTS")
    {
        if (senderId == 0) senderId = receiverId;
        message = await stringReplacementsService.DoAllReplacementsAsync(stringReplacementsService.DoReplacements(message, replaceData));

        // Create and save the task alert in the database.
        var taskAlert = new WiserItemModel
        {
            EntityType = EntityType,
            ModuleId = ModuleId,
            PublishedEnvironment = Environments.Live,
            Details = new List<WiserItemDetailModel>
            {
                new() {Key = DateField, Value = DateTime.Now.ToString("yyyy-MM-dd")},
                new() {Key = ContentField, Value = message},
                new() {Key = UserIdField, Value = receiverId},
                new() {Key = UsernameField, Value = receiverName},
                new() {Key = SenderNameField, Value = senderName},
                new() {Key = SenderIdField, Value = senderId}
            }
        };

        await wiserItemsService.SaveAsync(taskAlert, username: senderName, userId: senderId);

        // Push the task alert to the user to give a signal within Wiser if it is open.
        var accessToken = await wiserService.GetAccessTokenAsync();

        try
        {
            var client = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Post, $"{wtsSettings.Wiser.WiserApiUrl}api/v3/pusher/message");
            request.Headers.Add(HttpRequestHeader.Authorization.ToString(), $"Bearer {accessToken}");
            request.Content = JsonContent.Create(new {userId = receiverId});
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                await logService.LogError(logger, LogScopes.RunBody, action.LogSettings, $"Failed to send task alert via pusher, server returned status '{response.StatusCode}' with reason '{response.ReasonPhrase}'.", configurationServiceName, action.TimeId, action.Order);
            }
        }
        catch (Exception exception)
        {
            await logService.LogError(logger, LogScopes.RunBody, action.LogSettings, $"Failed to send task alert via pusher due to exception:\n{exception}.", configurationServiceName, action.TimeId, action.Order);
        }

        return taskAlert;
    }

    /// <inheritdoc />
    public async Task NotifyUserByEmailAsync(ulong receiverId, string receiverName, ActionModel action, string configurationServiceName, string subject, string content, IDictionary<string, object> replaceData, string senderEmail = null, string senderName = null)
    {
        databaseConnection.AddParameter("userId", receiverId);
        var userDataTable = await databaseConnection.GetAsync($"SELECT `value` AS receiver FROM {WiserTableNames.WiserItemDetail} WHERE item_id = ?userId AND `key` = 'email_address'");

        if (userDataTable.Rows.Count == 0)
        {
            await logService.LogError(logger, LogScopes.RunBody, action.LogSettings, $"Could not find email address for user '{receiverId}'", configurationServiceName, action.TimeId, action.Order);

            // If there is no email address to send the notification to skip it.
            return;
        }

        var receiver = userDataTable.Rows[0].Field<string>("receiver");
        subject = await stringReplacementsService.DoAllReplacementsAsync(stringReplacementsService.DoReplacements(subject, replaceData));
        content = await stringReplacementsService.DoAllReplacementsAsync(stringReplacementsService.DoReplacements(content, replaceData));
        await gclCommunicationsService.SendEmailAsync(receiver, subject, content, receiverName, sendDate: DateTime.Now, sender: senderEmail, senderName: senderName);
    }
}