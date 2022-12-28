﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WiserTaskScheduler.Core.Enums;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models;

namespace WiserTaskScheduler.Core.Services
{
    public class LogService : ILogService, ISingletonService
    {
        private readonly IServiceProvider serviceProvider;

        private bool updatedLogTable;

        public LogService(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public async Task LogDebug<T>(ILogger<T> logger, LogScopes logScope, LogSettings logSettings, string message, string configurationName, int timeId = 0, int order = 0)
        {
            await Log(logger, LogLevel.Debug, logScope, logSettings, message, configurationName, timeId, order);
        }

        /// <inheritdoc />
        public async Task LogInformation<T>(ILogger<T> logger, LogScopes logScope, LogSettings logSettings, string message, string configurationName, int timeId = 0, int order = 0)
        {
            await Log(logger, LogLevel.Information, logScope, logSettings, message, configurationName, timeId, order);
        }

        /// <inheritdoc />
        public async Task LogWarning<T>(ILogger<T> logger, LogScopes logScope, LogSettings logSettings, string message, string configurationName, int timeId = 0, int order = 0)
        {
            await Log(logger, LogLevel.Warning, logScope, logSettings, message, configurationName, timeId, order);
        }

        /// <inheritdoc />
        public async Task LogError<T>(ILogger<T> logger, LogScopes logScope, LogSettings logSettings, string message, string configurationName, int timeId = 0, int order = 0)
        {
            await Log(logger, LogLevel.Error, logScope, logSettings, message, configurationName, timeId, order);
        }

        /// <inheritdoc />
        public async Task LogCritical<T>(ILogger<T> logger, LogScopes logScope, LogSettings logSettings, string message, string configurationName, int timeId = 0, int order = 0)
        {
            await Log(logger, LogLevel.Critical, logScope, logSettings, message, configurationName, timeId, order);
        }

        /// <inheritdoc />
        public async Task Log<T>(ILogger<T> logger, LogLevel logLevel, LogScopes logScope, LogSettings logSettings, string message, string configurationName, int timeId = 0, int order = 0)
        {
            if (logLevel < logSettings.LogMinimumLevel)
            {
                return;
            }

            switch (logScope)
            {
                // Log the message if the scope is allowed to log or if log is at least a warning.
                case LogScopes.StartAndStop when logSettings.LogStartAndStop || logLevel > LogLevel.Information:
                case LogScopes.RunStartAndStop when logSettings.LogRunStartAndStop || logLevel > LogLevel.Information:
                case LogScopes.RunBody when logSettings.LogRunBody || logLevel > LogLevel.Information:
                {
                    try
                    {
                        // Try writing the log to the database.
                        try
                        {
                            using var scope = serviceProvider.CreateScope();
                            using var databaseConnection = scope.ServiceProvider.GetRequiredService<IDatabaseConnection>();
                            
                            // Update log table if it has not already been done since launch. The table definitions can only change when the WTS restarts with a new update.
                            if (!updatedLogTable)
                            {
                                var databaseHelpersService = scope.ServiceProvider.GetRequiredService<IDatabaseHelpersService>();
                                await databaseHelpersService.CheckAndUpdateTablesAsync(new List<string> {WiserTableNames.WtsLogs});
                                updatedLogTable = true;
                            }

                            databaseConnection.ClearParameters();
                            databaseConnection.AddParameter("message", message);
                            databaseConnection.AddParameter("level", logLevel.ToString());
                            databaseConnection.AddParameter("scope", logScope.ToString());
                            databaseConnection.AddParameter("source", typeof(T).Name);
                            databaseConnection.AddParameter("configuration", configurationName);
                            databaseConnection.AddParameter("timeId", timeId);
                            databaseConnection.AddParameter("order", order);
                            databaseConnection.AddParameter("addedOn", DateTime.Now);
                            
#if DEBUG
                            databaseConnection.AddParameter("isTest", 1);
#else
                    databaseConnection.AddParameter("isTest", 0);
#endif
                            
                            await databaseConnection.ExecuteAsync(@$"INSERT INTO {WiserTableNames.WtsLogs} (message, level, scope, source, configuration, time_id, `order`, added_on, is_test)
                                                                    VALUES(?message, ?level, ?scope, ?source, ?configuration, ?timeId, ?order, ?addedOn, ?isTest)");
                        }
                        catch (Exception e)
                        {
                            // If writing to the database fails log its error.
                            logger.Log(logLevel, $"Failed to write log to database due to exception: ${e}");
                        }

                        logger.Log(logLevel, message);
                    }
                    catch
                    {
                        // If writing to the log file fails ignore it. We can't write it somewhere else and the application needs to continue.
                    }
                    
                    break;
                }

                // Stop when the scope is evaluated above but is not allowed to log, to prevent the default exception to be thrown.
                case LogScopes.StartAndStop:
                case LogScopes.RunStartAndStop:
                case LogScopes.RunBody:
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(logScope), logScope.ToString());
            }
        }
    }
}