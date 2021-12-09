﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoImportServiceCore.Core.Enums;
using AutoImportServiceCore.Core.Helpers;
using AutoImportServiceCore.Core.Interfaces;
using AutoImportServiceCore.Core.Models;
using AutoImportServiceCore.Core.Workers;
using AutoImportServiceCore.Modules.RunSchemes.Models;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutoImportServiceCore.Core.Services
{
    /// <summary>
    /// A service to manage all AIS configurations that are provided by Wiser.
    /// </summary>
    public class MainService : IMainService, ISingletonService
    {
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger<MainService> logger;

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, ConfigurationsWorker>> activeConfigurations;

        /// <inheritdoc />
        public LogSettings LogSettings { get; set; }

        /// <summary>
        /// Creates a new instance of <see cref="MainService"/>.
        /// </summary>
        public MainService(IServiceProvider serviceProvider, ILogger<MainService> logger)
        {
            this.serviceProvider = serviceProvider;
            this.logger = logger;

            activeConfigurations = new ConcurrentDictionary<string, ConcurrentDictionary<int, ConfigurationsWorker>>();
        }

        /// <inheritdoc />
        public async Task ManageConfigurations()
        {
            var configurations = await GetConfigurations();

            foreach (var configuration in configurations)
            {
                if (activeConfigurations.ContainsKey(configuration.ServiceName))
                {
                    continue;
                }

                activeConfigurations.TryAdd(configuration.ServiceName, new ConcurrentDictionary<int, ConfigurationsWorker>());
                
                foreach (var runScheme in configuration.RunSchemes)
                {
                    var thread = new Thread(() => StartConfiguration(configuration.ServiceName, runScheme));
                    thread.Start();
                }
            }
        }

        /// <inheritdoc />
        public async Task StopAllConfigurations()
        {
            List<Task> configurationStopTasks = new List<Task>();
            var cancellationToken = new CancellationToken();

            foreach (var configuration in activeConfigurations)
            {
                foreach (var worker in configuration.Value)
                {
                    configurationStopTasks.Add(worker.Value.StopAsync(cancellationToken));
                }
            }
            
            for(var i = 0; i < configurationStopTasks.Count; i++)
            {
                await configurationStopTasks[i];

                LogHelper.LogInformation(logger, LogScopes.RunStartAndStop, LogSettings, $"Stopped {i + 1}/{configurationStopTasks.Count} configurations workers.");
            }
        }

        /// <summary>
        /// Retrieve all configurations.
        /// </summary>
        /// <returns>Returns the configurations</returns>
        private async Task<IEnumerable<ConfigurationModel>> GetConfigurations()
        {
            var configurations = new List<ConfigurationModel>();

            var configuration = JsonConvert.DeserializeObject<ConfigurationModel>(await File.ReadAllTextAsync(@"C:\Ontwikkeling\Intern\autoimportservice_core\AISCoreTestSettings.json"));

            // Check for duplicate run scheme time ids.
            var runSchemeTimeIds = new List<int>();

            foreach (var runScheme in configuration.RunSchemes)
            {
                runSchemeTimeIds.Add(runScheme.TimeId);
            }

            var duplicateTimeIds = runSchemeTimeIds.GroupBy(id => id).Where(id => id.Count() > 1).Select(id => id.Key).ToList();

            // Only add configuration if no run scheme time ids are double.
            if (duplicateTimeIds.Count == 0)
            {
                configurations.Add(configuration);
            }
            else
            {
                LogHelper.LogError(logger, LogScopes.RunStartAndStop, LogSettings, $"Did not start {configuration.ServiceName} due to duplicate run scheme time ids: {String.Join(", ", duplicateTimeIds)}");
            }

            return configurations;
        }

        /// <summary>
        /// Starts a new <see cref="ConfigurationsWorker"/> for the specified configuration and run scheme.
        /// </summary>
        /// <param name="name">The name of the worker.</param>
        /// <param name="runScheme">The run scheme of the worker.</param>
        private async void StartConfiguration(string name, RunSchemeModel runScheme)
        {
            using (var scope = serviceProvider.CreateScope())
            {
                runScheme.LogSettings ??= LogSettings;

                var worker = scope.ServiceProvider.GetRequiredService<ConfigurationsWorker>();
                worker.Initialize($"{name} (Time id: {runScheme.TimeId})", runScheme);
                activeConfigurations[name].TryAdd(runScheme.TimeId, worker);
                await worker.StartAsync(new CancellationToken());
                await worker.ExecuteTask; // Keep scope alive until worker stops.
            }
        }
    }
}
