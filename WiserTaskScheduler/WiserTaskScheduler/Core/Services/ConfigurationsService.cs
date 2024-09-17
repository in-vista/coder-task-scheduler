using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using WiserTaskScheduler.Core.Enums;
using WiserTaskScheduler.Core.Helpers;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models;

namespace WiserTaskScheduler.Core.Services
{
    /// <summary>
    /// A service for a configuration.
    /// </summary>
    public class ConfigurationsService : IConfigurationsService, IScopedService
    {
        private readonly ILogService logService;
        private readonly ILogger<ConfigurationsService> logger;
        private readonly IActionsServiceFactory actionsServiceFactory;
        private readonly IErrorNotificationService errorNotificationService;
        private readonly IDatabaseHelpersService databaseHelpersService;
        private readonly WtsSettings wtsSettings;

        private readonly SortedList<int, ActionModel> actions;
        private readonly Dictionary<string, IActionsService> actionsServices;

        private string configurationServiceName;
        private int timeId;
        private string serviceFailedNotificationEmails;

        private HashSet<string> tablesToOptimize;

        /// <inheritdoc />
        public LogSettings LogSettings { get; set; }

        /// <inheritdoc />
        public string Name { get; set; }

        /// <inheritdoc />
        public bool HasAction => actions.Any();

        /// <summary>
        /// Creates a new instance of <see cref="ConfigurationsService"/>.
        /// </summary>
        /// <param name="logService">The service to use for logging.</param>
        /// <param name="logger"></param>
        /// <param name="actionsServiceFactory"></param>
        /// <param name="errorNotificationService"></param>
        /// <param name="databaseHelpersService"></param>
        public ConfigurationsService(ILogService logService, ILogger<ConfigurationsService> logger, IActionsServiceFactory actionsServiceFactory, IErrorNotificationService errorNotificationService, IDatabaseHelpersService databaseHelpersService, IOptions<WtsSettings> wtsSettings)
        {
            this.logService = logService;
            this.logger = logger;
            this.actionsServiceFactory = actionsServiceFactory;
            this.errorNotificationService = errorNotificationService;
            this.databaseHelpersService = databaseHelpersService;
            this.wtsSettings = wtsSettings.Value;

            actions = new SortedList<int, ActionModel>();
            actionsServices = new Dictionary<string, IActionsService>();

            tablesToOptimize = new HashSet<string>();
        }

        /// <inheritdoc />
        public async Task ExtractActionsFromConfigurationAsync(int timeId, ConfigurationModel configuration)
        {
            configurationServiceName = configuration.ServiceName;
            this.timeId = timeId;
            serviceFailedNotificationEmails = configuration.ServiceFailedNotificationEmails;
            var allActions = GetAllActionsFromConfiguration(configuration);

            foreach (ActionModel action in allActions.Where(action => action.TimeId == timeId))
            {
                action.LogSettings ??= LogSettings;
                actions.Add(action.Order, action);

                if (!actionsServices.ContainsKey(action.GetType().ToString()))
                {
                    var actionsService = actionsServiceFactory.GetActionsServiceForAction(action);
                    await actionsService.InitializeAsync(configuration, tablesToOptimize);
                    actionsServices.Add(action.GetType().ToString(), actionsService);
                }
            }

            if (!actions.Any())
            {
                await logService.LogWarning(logger, LogScopes.StartAndStop, LogSettings, $"{configurationServiceName} has no actions for time ID '{timeId}'. Please make sure the time ID of the run scheme and actions are filled in correctly.", configurationServiceName, timeId);
                return;
            }

            await logService.LogInformation(logger, LogScopes.StartAndStop, LogSettings, $"{configurationServiceName} has {actions.Count} action(s) for time ID '{timeId}'.", configurationServiceName, timeId);
        }

        /// <summary>
        /// Get all the provided action sets if they exist in a single list.
        /// </summary>
        /// <returns></returns>
        private List<ActionModel> GetAllActionsFromConfiguration(ConfigurationModel configuration)
        {
            var actionSets = new List<ActionModel[]>
            {
                configuration.QueryGroup,
                configuration.Queries,
                configuration.HttpApiGroup,
                configuration.HttpApis,
                configuration.GenerateFileGroup,
                configuration.GenerateFiles,
                configuration.ImportFileGroup,
                configuration.ImportFiles,
                configuration.CleanupItemGroup,
                configuration.CleanupItems,
                configuration.CommunicationGroup,
                configuration.Communications,
                configuration.WiserImportGroup,
                configuration.WiserImports,
                configuration.ServerMonitorsGroup,
                configuration.ServerMonitor,
                configuration.FtpGroup,
                configuration.Ftps,
                configuration.CleanupWiserHistoryGroup,
                configuration.CleanupWiserHistories,
                configuration.GenerateCommunicationGroup,
                configuration.GenerateCommunications,
                configuration.DocumentStoreReadersGroup,
                configuration.DocumentStoreReader,
                configuration.SlackMessageGroup,
                configuration.SlackMessages
            };

            var allActions = new List<ActionModel>();

            if (actions == null)
            {
                return allActions;
            }

            foreach (var actionSet in actionSets)
            {
                if (actionSet != null)
                {
                    allActions.AddRange(actionSet);
                }
            }

            if (configuration.BranchQueueModel != null)
            {
                allActions.Add(configuration.BranchQueueModel);
            }

            return allActions;
        }

        /// <inheritdoc />
        public async Task<bool> IsValidConfigurationAsync(ConfigurationModel configuration)
        {
            var conflicts = 0;

            // Check for duplicate run scheme time ids.
            var runSchemeTimeIds = new List<int>();
            
            foreach (var runScheme in configuration.GetAllRunSchemes())
            {
                runSchemeTimeIds.Add(runScheme.TimeId);
            }

            var duplicateTimeIds = runSchemeTimeIds.GroupBy(id => id).Where(id => id.Count() > 1).Select(id => id.Key).ToList();

            if (duplicateTimeIds.Count > 0)
            {
                conflicts++;
                await logService.LogError(logger, LogScopes.StartAndStop, LogSettings, $"Configuration '{configuration.ServiceName}' has duplicate run scheme time ids: {String.Join(", ", duplicateTimeIds)}", configuration.ServiceName);
            }

            // Check for duplicate order in a single time id.
            var allActions = GetAllActionsFromConfiguration(configuration);

            foreach (var timeId in runSchemeTimeIds)
            {
                var duplicateOrders = allActions.Where(action => action.TimeId == timeId).GroupBy(action => action.Order).Where(action => action.Count() > 1).Select(action => action.Key).ToList();

                if (duplicateOrders.Count > 0)
                {
                    conflicts++;
                    await logService.LogError(logger, LogScopes.StartAndStop, LogSettings, $"Configuration '{configuration.ServiceName}' has duplicate orders within run scheme {timeId}. Orders: {String.Join(", ", duplicateOrders)}", configuration.ServiceName, timeId);
                }
            }

            return conflicts == 0;
        }

        /// <inheritdoc />
        public async Task ExecuteAsync()
        {
            var resultSets = new JObject();
            var currentOrder = 0;
            var stopwatch = new Stopwatch();
            tablesToOptimize.Clear();

            try
            {
                foreach (var action in actions)
                {
                    stopwatch.Reset();
                    
                    currentOrder = action.Value.Order;
                    
                    if (await SkipAction(resultSets, action.Value))
                    {
                        continue;
                    }
                    
                    stopwatch.Start();
                    var resultSet = await actionsServices[action.Value.GetType().ToString()].Execute(action.Value, resultSets, configurationServiceName);

                    if (!String.IsNullOrWhiteSpace(action.Value.ResultSetName))
                    {
                        resultSets.Add(action.Value.ResultSetName, resultSet);
                    }
                    
                    stopwatch.Stop();
                    await logService.LogInformation(logger, LogScopes.RunStartAndStop, LogSettings, $"Action finished in {stopwatch.Elapsed}", configurationServiceName, timeId, action.Value.Order);
                }

                if (tablesToOptimize.Any())
                {
                    await logService.LogInformation(logger, LogScopes.RunStartAndStop, LogSettings, $"Optimizing tables: {String.Join(',', tablesToOptimize)}", configurationServiceName, timeId);
                    await databaseHelpersService.OptimizeTablesAsync(tablesToOptimize.ToArray());
                }
            }
            catch (Exception e)
            {
                await logService.LogCritical(logger, LogScopes.StartAndStop, LogSettings, $"Aborted {configurationServiceName} due to exception in time ID '{timeId}' and order '{currentOrder}', will try again next time. Exception {e}", configurationServiceName, timeId, currentOrder);
                await errorNotificationService.NotifyOfErrorByEmailAsync(serviceFailedNotificationEmails, $"Service '{configurationServiceName}' with time ID '{timeId}' of '{wtsSettings.Name}' failed.", $"Wiser Task Scheduler '{wtsSettings.Name}' failed during the executing of service '{configurationServiceName}' with time ID '{timeId}' and has therefore been aborted. Please check the logs for more details. A new attempt will be made during the next run.", LogSettings, LogScopes.RunStartAndStop, configurationServiceName);
            }
        }

        /// <summary>
        /// Check if the action needs to be skipped due to constraints when it is allowed to run.
        /// </summary>
        /// <param name="resultSets">The result sets of the previous actions within the run to reference.</param>
        /// <param name="action">The action to perform the check on.</param>
        /// <returns></returns>
        private async Task<bool> SkipAction(JObject resultSets, ActionModel action)
        {
            if (!String.IsNullOrWhiteSpace(action.OnlyWithStatusCode))
            {
                var parts = action.OnlyWithStatusCode.Split(",");

                try
                {
                    var defaultValue = String.Empty;
                    var defaultValueIndex = parts[0].IndexOf('?');
                    if (defaultValueIndex >= 0)
                    {
                        defaultValue = parts[0][defaultValueIndex..];
                        parts[0] = parts[0][..defaultValueIndex];
                    }

                    var statusCode = ReplacementHelper.GetValue($"{parts[0]}.StatusCode{defaultValue}", ReplacementHelper.EmptyRows, resultSets, false);
                    
                    if (statusCode != parts[1])
                    {
                        await logService.LogInformation(logger, LogScopes.RunStartAndStop, LogSettings, $"Skipped action because status code was '{statusCode}'.", configurationServiceName, action.TimeId, action.Order);
                        return true;
                    }
                }
                catch (Exception e)
                {
                    await logService.LogError(logger, LogScopes.RunStartAndStop, LogSettings, $"Failed to validate status code, skipping action. Exception: {e}", configurationServiceName, action.TimeId, action.Order);
                    return true;
                }
            }

            if (!String.IsNullOrWhiteSpace(action.OnlyWithSuccessState))
            {
                var parts = action.OnlyWithSuccessState.Split(",");

                try
                {
                    var defaultValue = String.Empty;
                    var defaultValueIndex = parts[0].IndexOf('?');
                    if (defaultValueIndex >= 0)
                    {
                        defaultValue = parts[0][defaultValueIndex..];
                        parts[0] = parts[0][..defaultValueIndex];
                    }

                    var state = ReplacementHelper.GetValue($"{parts[0]}.Success{defaultValue}", ReplacementHelper.EmptyRows, resultSets, false);
                    
                    if (state != parts[1])
                    {
                        await logService.LogInformation(logger, LogScopes.RunStartAndStop, LogSettings, $"Skipped action because success state was '{state}'.", configurationServiceName, action.TimeId, action.Order);
                        return true;
                    }
                }
                catch (Exception e)
                {
                    await logService.LogError(logger, LogScopes.RunStartAndStop, LogSettings, $"Failed to validate action success, skipping action. Exception: {e}", configurationServiceName, action.TimeId, action.Order);
                    return true;
                }
            }
            
            if (!String.IsNullOrWhiteSpace(action.OnlyWithValue))
            {
                var parts = action.OnlyWithValue.Split(",");

                try
                {
                    var defaultValue = String.Empty;
                    var defaultValueIndex = parts[0].IndexOf('?');
                    if (defaultValueIndex >= 0)
                    {
                        defaultValue = parts[0][defaultValueIndex..];
                        parts[0] = parts[0][..defaultValueIndex];
                    }
                    
                    var state = ReplacementHelper.GetValue($"{parts[0]}{defaultValue}", ReplacementHelper.EmptyRows, resultSets, false);

                    if (parts.Length < 2)
                    {
                        await logService.LogInformation(logger, LogScopes.RunStartAndStop, LogSettings, $"Skipped action because no value was provided to check state against (did you forget a comma?).", configurationServiceName, action.TimeId, action.Order);
                        return true;
                    }
                    
                    if (state != parts[1])
                    {
                        await logService.LogInformation(logger, LogScopes.RunStartAndStop, LogSettings, $"Skipped action because success state was '{state}' and not {parts[1]}.", configurationServiceName, action.TimeId, action.Order);
                        return true;
                    }
                }
                catch (Exception e)
                {
                    await logService.LogError(logger, LogScopes.RunStartAndStop, LogSettings, $"Failed to validate action value state, skipping action. Exception: {e}", configurationServiceName, action.TimeId, action.Order);
                    return true;
                }
            }
            
            return false;
        }
    }
}
