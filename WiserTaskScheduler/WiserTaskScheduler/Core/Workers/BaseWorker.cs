using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WiserTaskScheduler.Core.Enums;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models;
using WiserTaskScheduler.Modules.RunSchemes.Interfaces;
using WiserTaskScheduler.Modules.RunSchemes.Models;
using WiserTaskScheduler.Modules.Wiser.Interfaces;

namespace WiserTaskScheduler.Core.Workers
{
    /// <summary>
    /// The <see cref="BaseWorker"/> performs shared functionality for all workers.
    /// </summary>
    public abstract class BaseWorker : BackgroundService
    {
        /// <summary>
        /// Gets the name of the worker.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Gets the delay between two runs of the worker.
        /// </summary>
        public RunSchemeModel RunScheme { get; private set; }

        private bool RunImmediately { get; set; }

        private bool SingleRun { get; set; }

        private string ConfigurationName { get; set; }

        private readonly ILogService logService;
        private readonly ILogger<BaseWorker> logger;
        private readonly IRunSchemesService runSchemesService;
        private readonly IWiserDashboardService wiserDashboardService;
        private readonly IErrorNotificationService errorNotificationService;
        private readonly WtsSettings wtsSettings;

        private bool defaultServiceIsCreated;
        private string serviceFailedNotificationEmails;

        /// <summary>
        /// Creates a new instance of <see cref="BaseWorker"/>.
        /// </summary>
        /// <param name="baseWorkerDependencyAggregate"></param>
        protected BaseWorker(IBaseWorkerDependencyAggregate baseWorkerDependencyAggregate)
        {
            logService = baseWorkerDependencyAggregate.LogService;
            logger = baseWorkerDependencyAggregate.Logger;
            runSchemesService = baseWorkerDependencyAggregate.RunSchemesService;
            wiserDashboardService = baseWorkerDependencyAggregate.WiserDashboardService;
            errorNotificationService = baseWorkerDependencyAggregate.ErrorNotificationService;
            wtsSettings = baseWorkerDependencyAggregate.WtsSettings;
        }

        /// <summary>
        /// Assigns the base values from a derived class.
        /// </summary>
        /// <param name="name">The name of the worker.</param>
        /// <param name="runScheme">The run scheme of the worker.</param>
        /// <param name="runImmediately">True to run the action immediately, false to run at first delayed time.</param>
        /// <param name="configurationName">The name of the configuration, default <see langword="null"/>. If set it will update the service information.</param>
        /// <param name="singleRun">The configuration is only run once, ignoring paused state and run time.</param>
        protected void Initialize(string name, RunSchemeModel runScheme, string serviceFailedNotificationEmails, bool runImmediately = false, string configurationName = null, bool singleRun = false)
        {
            if (!String.IsNullOrWhiteSpace(Name))
                return;

            Name = $"{name} ({Environment.MachineName})";;
            RunScheme = runScheme;
            RunImmediately = runImmediately;
            ConfigurationName = configurationName;
            SingleRun = singleRun;

            this.serviceFailedNotificationEmails = serviceFailedNotificationEmails;
        }

        /// <summary>
        /// Handles when the worker is allowed to execute its action.
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(ConfigurationName) && !defaultServiceIsCreated)
                {
                    var existingService = await wiserDashboardService.GetServiceAsync(Name, 0);
                    if (existingService == null)
                    {
                        await wiserDashboardService.CreateServiceAsync(Name, 0);
                        await wiserDashboardService.UpdateServiceAsync(Name, 0, state: "active", templateId: 0);
                        defaultServiceIsCreated = true;
                    }
                }

                var nxtRun = runSchemesService.GetDateTimeTillNextRun(RunScheme, DateTime.Now);
                await logService.LogInformation(logger, LogScopes.StartAndStop, RunScheme.LogSettings, $"{ConfigurationName ?? Name} started, first run on: {nxtRun}", ConfigurationName ?? Name, RunScheme.TimeId);
                await wiserDashboardService.UpdateServiceAsync(ConfigurationName ?? Name, RunScheme.TimeId, nextRun: nxtRun);

                if (!RunImmediately && !SingleRun)
                {
                    await WaitTillNextRun(stoppingToken);
                }

                var updateSucceeded = true;
                while (!stoppingToken.IsCancellationRequested)
                {
                    var state = "success";
                    var stopWatch = new Stopwatch();
                    bool? paused = false;
                    bool? alreadyRunning = false;
                    
                    // Only check running and pause state for custom configurations because the default services are always running for each instance of the WTS.
                    if (!String.IsNullOrWhiteSpace(ConfigurationName))
                    {
                        alreadyRunning = await wiserDashboardService.IsServiceRunning(ConfigurationName, RunScheme.TimeId);
                        paused = await wiserDashboardService.IsServicePaused(ConfigurationName, RunScheme.TimeId);
                        // Only save paused state if we know for sure it is paused and not currently running (if it is running, it will be set to paused by that thread after the execution).
                        if (paused.HasValue && paused.Value && alreadyRunning.HasValue && !alreadyRunning.Value)
                        {
                            await wiserDashboardService.UpdateServiceAsync(ConfigurationName, RunScheme.TimeId, state: "paused");
                        }
                    }

                    // Skip if already running or we don't know if it's running, except when we know the previous state sync failed.
                    if ((alreadyRunning.HasValue && !alreadyRunning.Value) || !updateSucceeded)
                    {
                        // Also skip if paused, or we don't know if it's paused, except when it's a manual run.
                        if ((paused.HasValue && !paused.Value) || SingleRun)
                        {
                            await logService.LogInformation(logger, LogScopes.RunStartAndStop, RunScheme.LogSettings, $"{ConfigurationName ?? Name} started at: {DateTime.Now}", ConfigurationName ?? Name, RunScheme.TimeId);
                            await wiserDashboardService.UpdateServiceAsync(ConfigurationName ?? Name, RunScheme.TimeId, state: "running");

                            var runStartTime = DateTime.Now;
                            stopWatch.Start();

                            await ExecuteActionAsync();

                            stopWatch.Stop();

                            await logService.LogInformation(logger, LogScopes.RunStartAndStop, RunScheme.LogSettings, $"{ConfigurationName ?? Name} finished at: {DateTime.Now}, time taken: {stopWatch.Elapsed}", ConfigurationName ?? Name, RunScheme.TimeId);

                            paused = await wiserDashboardService.IsServicePaused(ConfigurationName ?? Name, RunScheme.TimeId);
                            // Only set state to paused if we are sure it's paused.
                            if (paused.HasValue && paused.Value)
                            {
                                state = "paused";
                            }
                            else
                            {
                                var states = await wiserDashboardService.GetLogStatesFromLastRun(ConfigurationName ?? Name, RunScheme.TimeId, runStartTime);
                                if (states == null) // Exception occurred in getting states.
                                {
                                    state = "unknown";
                                }
                                else if (states.Contains("Critical", StringComparer.OrdinalIgnoreCase) || states.Contains("Error", StringComparer.OrdinalIgnoreCase))
                                {
                                    state = "failed";
                                }
                                else if (states.Contains("Warning", StringComparer.OrdinalIgnoreCase))
                                {
                                    state = "warning";
                                }
                            }

                            // If storing the state of this run fails, the state will stay on "running", which will prevent any future runs, so keep track of the success.
                            var now = DateTime.Now;
                            var nextRun = runSchemesService.GetDateTimeTillNextRun(RunScheme, now);
                            updateSucceeded = await wiserDashboardService.UpdateServiceAsync(ConfigurationName ?? Name, RunScheme.TimeId, nextRun: nextRun, lastRun: now, runTime: stopWatch.Elapsed, state: state, extraRun: false);
                            if (!updateSucceeded)
                            {
                                await errorNotificationService.NotifyOfErrorByEmailAsync(serviceFailedNotificationEmails, $"Service '{ConfigurationName ?? Name}'{(RunScheme.TimeId > 0 ? $" with time ID '{RunScheme.TimeId}'" : "")} of '{wtsSettings.Name}' status save failed.", $"Wiser Task Scheduler '{wtsSettings.Name}' failed to save the service '{ConfigurationName ?? Name}'{(RunScheme.TimeId > 0 ? $" with time ID '{RunScheme.TimeId}'" : "")} state of the last run, which is '{state}', to the database. The service will continue running and the next run will attempt a new state save. Until then, the service state in the database will be incorrect.", RunScheme.LogSettings, LogScopes.RunBody, ConfigurationName ?? Name);
                            }
                        }
                        else
                        {
                            await wiserDashboardService.UpdateServiceAsync(ConfigurationName ?? Name, RunScheme.TimeId, nextRun: runSchemesService.GetDateTimeTillNextRun(RunScheme, DateTime.Now));
                        }
                    }

                    // If the configuration only needs to be run once break out of the while loop. State will not be set on stopped because the normal configuration is still active.
                    if (SingleRun)
                    {
                        // If storing the result of this run failed the first time, try again now, otherwise the state will stay on running and it will never run again.
                        if (!updateSucceeded)
                        {
                            var now = DateTime.Now;
                            var nextRun = runSchemesService.GetDateTimeTillNextRun(RunScheme, now);
                            updateSucceeded = await wiserDashboardService.UpdateServiceAsync(ConfigurationName, RunScheme.TimeId, nextRun: nextRun, lastRun: now, runTime: stopWatch.Elapsed, state: state, extraRun: false);
                            if (!updateSucceeded)
                            {
                                await errorNotificationService.NotifyOfErrorByEmailAsync(serviceFailedNotificationEmails, $"Service '{ConfigurationName ?? Name}'{(RunScheme.TimeId > 0 ? $" with time ID '{RunScheme.TimeId}'" : "")} of '{wtsSettings.Name}' status save failed.", $"Wiser Task Scheduler '{wtsSettings.Name}' failed to save the service '{ConfigurationName ?? Name}'{(RunScheme.TimeId > 0 ? $" with time ID '{RunScheme.TimeId}'" : "")} state of the last run, which is '{state}', to the database a second time. Since this was a single run configuration, no more attempts will be made to correct this. The service state will now permanently be incorrect until it is manually updated.", RunScheme.LogSettings, LogScopes.RunStartAndStop, ConfigurationName ?? Name);
                            }
                        }

                        break;
                    }

                    await WaitTillNextRun(stoppingToken);
                }
            }
            catch (TaskCanceledException)
            {
                await logService.LogInformation(logger, LogScopes.StartAndStop, RunScheme.LogSettings, $"{ConfigurationName ?? Name} has been stopped after cancel was called.", ConfigurationName ?? Name, RunScheme.TimeId);
                await wiserDashboardService.UpdateServiceAsync(ConfigurationName ?? Name, RunScheme.TimeId, state: "stopped");
            }
            catch (Exception e)
            {
                await logService.LogCritical(logger, LogScopes.StartAndStop, RunScheme.LogSettings, $"{ConfigurationName ?? Name} stopped with exception {e}", ConfigurationName ?? Name, RunScheme.TimeId);
                await wiserDashboardService.UpdateServiceAsync(ConfigurationName ?? Name, RunScheme.TimeId, state: "crashed");
                await errorNotificationService.NotifyOfErrorByEmailAsync(serviceFailedNotificationEmails, $"Service '{ConfigurationName ?? Name}'{(RunScheme.TimeId > 0 ? $" with time ID '{RunScheme.TimeId}'" : "")} of '{wtsSettings.Name}' crashed.", $"Wiser Task Scheduler '{wtsSettings.Name}' crashed while executing the service '{ConfigurationName ?? Name}'{(RunScheme.TimeId > 0 ? $" with time ID '{RunScheme.TimeId}'" : "")} and is therefore shutdown. Please check the logs for more details. A restart is required to start the service again.", RunScheme.LogSettings, LogScopes.StartAndStop, ConfigurationName ?? Name);
            }
        }

        /// <summary>
        /// Wait till the next run, if the time to wait is longer than the allowed delay the time is split and will wait in sections until the full time has completed.
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        private async Task WaitTillNextRun(CancellationToken stoppingToken)
        {
            bool timeSplit;

            do
            {
                timeSplit = false;
                var timeTillNextRun = runSchemesService.GetTimeTillNextRun(RunScheme);

                if (timeTillNextRun.TotalMilliseconds > Int32.MaxValue)
                {
                    timeTillNextRun = new TimeSpan(0, 0, 0, 0, Int32.MaxValue);
                    timeSplit = true;
                }

                await Task.Delay(timeTillNextRun, stoppingToken);

            } while (timeSplit);
        }

        /// <summary>
        /// Execute the action of the derived worker.
        /// </summary>
        /// <returns></returns>
        protected abstract Task ExecuteActionAsync();
    }
}
