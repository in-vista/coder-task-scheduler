using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using WiserTaskScheduler.Core.Interfaces;
using WiserTaskScheduler.Core.Models;
using WiserTaskScheduler.Core.Enums;
using WiserTaskScheduler.Modules.ServerMonitors.Interfaces;
using WiserTaskScheduler.Modules.ServerMonitors.Models;
using WiserTaskScheduler.Modules.ServerMonitors.Enums;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using GeeksCoreLibrary.Modules.Communication.Interfaces;

namespace WiserTaskScheduler.Modules.ServerMonitors.Services
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal class ServerMonitorsService : IServerMonitorsService, IActionsService, IScopedService
    {
        private readonly IServiceProvider serviceProvider;
        private readonly ILogService logService;
        private readonly ILogger<ServerMonitorsService> logger;

        private string connectionString;

        private readonly Dictionary<string, bool> emailDrivesSent = new();
        private bool emailRamSent;
        private bool emailCpuSent;
        private bool emailNetworkSent;

        private string receiver;
        private string subject;
        private string body;

        //create the right performanceCounter variable types.
        private readonly PerformanceCounter cpuCounter = new("Processor", "% Processor Time", "_Total");
        private readonly PerformanceCounter ramCounter = new("Memory", "Available MBytes");
        private readonly DriveInfo[] allDrives = DriveInfo.GetDrives();

        private bool firstValueUsed;
        private int cpuIndex;
        private int aboveThresholdTimer;
        float[] cpuValues;

        public ServerMonitorsService(IServiceProvider serviceProvider, ILogService logService, ILogger<ServerMonitorsService> logger)
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
            var monitorItem = (ServerMonitorModel)action;
            using var scope = serviceProvider.CreateScope();
            await using var databaseConnection = scope.ServiceProvider.GetRequiredService<IDatabaseConnection>();

            await databaseConnection.ChangeConnectionStringsAsync(connectionString, connectionString);

            var gclCommunicationsService = scope.ServiceProvider.GetRequiredService<ICommunicationsService>();

            var threshold = monitorItem.Threshold;

            //Check which type of server monitor is used.
            //Cpu is default Value.
            switch (monitorItem.ServerMonitorType)
            {
                case ServerMonitorTypes.Drive:
                    if (monitorItem.DriveName == "All")
                    {
                       return await GetAllHardDrivesSpaceAsync(monitorItem, threshold, configurationServiceName, gclCommunicationsService);
                    }
                    else
                    {
                       return await GetHardDriveSpaceAsync(monitorItem, threshold, configurationServiceName, gclCommunicationsService, monitorItem.DriveName);
                    }
                case ServerMonitorTypes.Ram:
                    return await GetRamSpaceAsync(monitorItem, threshold, configurationServiceName, gclCommunicationsService);
                case ServerMonitorTypes.Cpu:
                    return await GetCpuUsageAsync(monitorItem, threshold, configurationServiceName, gclCommunicationsService);
                case ServerMonitorTypes.Network:
                    return await GetNetworkUtilization(monitorItem, threshold, configurationServiceName, gclCommunicationsService);
                default:
                    throw new ArgumentOutOfRangeException(nameof(monitorItem.ServerMonitorType));
            }
        }

        /// <summary>
        /// Gets all the available hard drives and checks the free space available for all of them
        /// </summary>
        /// <param name="monitorItem">The information for the monitor action. </param>
        /// <param name="threshold">The threshold to check if an email needs to be send.</param>
        /// <param name="gclCommunicationsService">The name of the service in the configuration, used for logging.</param>
        /// <param name="configurationServiceName">The communications service from the GCL to actually send out the email.</param>
        /// <returns></returns>
        private async Task<JObject> GetAllHardDrivesSpaceAsync(ServerMonitorModel monitorItem, int threshold, string configurationServiceName, ICommunicationsService gclCommunicationsService)
        {
            var jArray = new JArray();

            foreach (var drive in allDrives)
            {
                var row = new JObject();

                //Checks for each drive if it already exists in the dictionary.
                emailDrivesSent.TryAdd(drive.Name, false);

                //Calculate the percentage of free space availible and see if it matches with the given threshold.
                var freeSpace = drive.TotalFreeSpace;
                var fullSpace = drive.TotalSize;
                var percentage = Decimal.Round(freeSpace) / Decimal.Round(fullSpace) * 100;

                //Set the email settings correctly
                receiver = monitorItem.EmailAddressForWarning;
                subject = $"Low space on Drive {drive.Name}";
                body = $"Drive {drive.Name} only has {Decimal.Round(percentage)}% space left, this is below the threshold of {threshold}%.";

                await logService.LogInformation(logger, LogScopes.RunStartAndStop, monitorItem.LogSettings, $"Drive {drive} has {drive.TotalFreeSpace}Bytes of free space", configurationServiceName, monitorItem.TimeId, monitorItem.Order);

                //Check if the threshold is higher then the free space available.
                if (percentage > threshold)
                {
                    emailDrivesSent[drive.Name] = false;

                    row.Add("HardwareType", "HardDrive");
                    row.Add("HardDriveName", drive.Name);
                    row.Add("Threshold", threshold);
                    row.Add("EmailSend", emailDrivesSent[drive.Name]);
                    row.Add("SpacePercentage", percentage);
                    jArray.Add(row);

                    continue;
                }

                //Check if an email already has been sent
                if (emailDrivesSent[drive.Name])
                {
                    row.Add("HardwareType", "HardDrive");
                    row.Add("HardDriveName", drive.Name);
                    row.Add("Threshold", threshold);
                    row.Add("EmailSend", emailDrivesSent[drive.Name]);
                    row.Add("SpacePercentage", percentage);
                    jArray.Add(row);

                    continue;
                }

                row.Add("HardwareType", "HardDrive");
                row.Add("HardDriveName", drive.Name);
                row.Add("Threshold", threshold);
                row.Add("EmailSend", emailDrivesSent[drive.Name]);
                row.Add("SpacePercentage", percentage);
                jArray.Add(row);

                emailDrivesSent[drive.Name] = true;
                await gclCommunicationsService.SendEmailAsync(receiver, subject, body);
            }

            return new JObject
            {
                {"Results", jArray}
            };
        }

        /// <summary>
        /// This gets the hard drive free space available for one specific drive.
        /// </summary>
        /// <param name="monitorItem">The information for the monitor action.</param>
        /// <param name="threshold">The threshold to check if an email needs to be send.</param>
        /// <param name="gclCommunicationsService">The name of the service in the configuration, used for logging.</param>
        /// <param name="configurationServiceName">The communications service from the GCL to actually send out the email.</param>
        /// <param name="driveName">The name of the drive that is used by the monitor.</param>
        /// <returns></returns>
        private async Task<JObject> GetHardDriveSpaceAsync(ServerMonitorModel monitorItem, int threshold, string configurationServiceName, ICommunicationsService gclCommunicationsService, string driveName)
        {
            var drive = new DriveInfo(driveName);

            //Check to see if the drive is already within the dictionary.
            emailDrivesSent.TryAdd(drive.Name, false);

            //Calculate the percentage of free space availible and see if it matches with the given threshold.
            var freeSpace = drive.TotalFreeSpace;
            var fullSpace = drive.TotalSize;
            var percentage = Decimal.Round(freeSpace) / Decimal.Round(fullSpace) * 100;

            //Set the email settings correctly.
            receiver = monitorItem.EmailAddressForWarning;
            subject = $"Low space on Drive {drive.Name}";
            body = $"Drive {drive.Name} only has {Decimal.Round(percentage)}% space left, this is below the threshold of {threshold}%.";

            await logService.LogInformation(logger, LogScopes.RunStartAndStop, monitorItem.LogSettings, $"Drive {drive} has {drive.TotalFreeSpace}Bytes of free space", configurationServiceName, monitorItem.TimeId, monitorItem.Order);

            //Check if the threshold is higher then the free space available.
            if (percentage > threshold)
            {
                emailDrivesSent[drive.Name] = false;
                return new JObject
                {
                    {"HardwareType", "HardDrive"},
                    {"HardDriveName", drive.Name},
                    {"Threshold", threshold},
                    {"EmailSend", emailDrivesSent[drive.Name]},
                    {"SpacePercentage", percentage}
                };
            }

            //Check if an email already has been sent.
            if (emailDrivesSent[drive.Name])
            {
                return new JObject
                {
                    {"HardwareType", "HardDrive"},
                    {"HardDriveName", drive.Name},
                    {"Threshold", threshold},
                    {"EmailSend", emailDrivesSent[drive.Name]},
                    {"SpacePercentage", percentage}
                };
            }

            emailDrivesSent[drive.Name] = true;
            await gclCommunicationsService.SendEmailAsync(receiver, subject, body);

            return new JObject
            {
                {"HardwareType", "HardDrive"},
                {"HardDriveName", drive.Name},
                {"Threshold", threshold},
                {"EmailSend", emailDrivesSent[drive.Name]},
                {"SpacePercentage", percentage}
            };
        }

        /// <summary>
        /// Gets the available RAM space and caclulates if it needs to send an email.
        /// </summary>
        /// <param name="monitorItem">The information for the monitor action.</param>
        /// <param name="threshold">The threshold to check if an email needs to be send.</param>
        /// <param name="gclCommunicationsService">The name of the service in the configuration, used for logging.</param>
        /// <param name="configurationServiceName">The communications service from the GCL to actually send out the email.</param>
        /// <returns></returns>
        private async Task<JObject> GetRamSpaceAsync(ServerMonitorModel monitorItem, int threshold, string configurationServiceName, ICommunicationsService gclCommunicationsService)
        {
            var ramValue = ramCounter.NextValue();
            await logService.LogInformation(logger, LogScopes.RunStartAndStop, monitorItem.LogSettings, $"RAM is: {ramValue}MB available", configurationServiceName, monitorItem.TimeId, monitorItem.Order);

            //Set the email settings correctly.
            receiver = monitorItem.EmailAddressForWarning;
            subject = "Low on RAM space.";
            body = $"Your ram has {ramValue}MB available which is below your set threshold of {threshold}MB.";

            //Check if the ram is above the threshold.
            if (ramValue > threshold)
            {
                emailRamSent = false;
                return new JObject
                {
                    {"HardwareType", "RAM"},
                    {"Threshold", threshold},
                    {"EmailSend", emailRamSent},
                    {"RamValue", ramValue}
                };
            }

            //Check if an email already has been sent.
            if (emailRamSent)
            {
                return new JObject
                {
                    {"HardwareType", "RAM"},
                    {"Threshold", threshold},
                    {"EmailSend", emailRamSent},
                    {"RamValue", ramValue}
                };
            }

            emailRamSent = true;
            await gclCommunicationsService.SendEmailAsync(receiver, subject, body);

            return new JObject
            {
                {"HardwareType", "RAM"},
                {"Threshold", threshold},
                {"EmailSend", emailRamSent},
                {"RamValue", ramValue}
            };
        }

        /// <summary>
        /// Checks the Cpu usage detection type to use and calls the right function.
        /// </summary>
        /// <param name="monitorItem">The information for the monitor action.</param>
        /// <param name="threshold">The threshold to check if an email needs to be send.</param>
        /// <param name="gclCommunicationsService">The name of the service in the configuration, used for logging.</param>
        /// <param name="configurationServiceName">The communications service from the GCL to actually send out the email.</param>
        /// <returns></returns>
        private async Task<JObject> GetCpuUsageAsync(ServerMonitorModel monitorItem, int threshold, string configurationServiceName, ICommunicationsService gclCommunicationsService)
        {
            //gets the detection type of cpu usage to use.
            switch(monitorItem.CpuUsageDetectionType)
            {
                case CpuUsageDetectionTypes.ArrayCount:
                    return await GetCpuUsageArrayCountAsync(monitorItem, threshold, configurationServiceName, gclCommunicationsService);
                case CpuUsageDetectionTypes.Counter:
                    return await GetCpuUsageCounterAsync(monitorItem, threshold, configurationServiceName, gclCommunicationsService);
                default:
                    throw new ArgumentOutOfRangeException(nameof(monitorItem.CpuUsageDetectionType));
            }
        }

        /// <summary>
        /// Gets the CPU usage with the use of a counter which checks if for a certain amount of times the value above the threshold is.
        /// </summary>
        /// <param name="monitorItem">The information for the monitor action.</param>
        /// <param name="threshold">The threshold to check if an email needs to be send.</param>
        /// <param name="gclCommunicationsService">The name of the service in the configuration, used for logging.</param>
        /// <param name="configurationServiceName">The communications service from the GCL to actually send out the email.</param>
        /// <returns></returns>
        private async Task<JObject> GetCpuUsageArrayCountAsync(ServerMonitorModel monitorItem, int threshold, string configurationServiceName, ICommunicationsService gclCommunicationsService)
        {
            cpuValues ??= new float[monitorItem.CpuArrayCountSize];

            // The first value of performance counter will always be 0.
            if (!firstValueUsed)
            {
                firstValueUsed = true;
                cpuCounter.NextValue();
            }
            var count = 0;
            var realvalue = cpuCounter.NextValue();

            //gets 60 percent of the size of the array.
            var arrayCountThreshold = (int)(monitorItem.CpuArrayCountSize * 0.6);
            //Puts the value into the array.
            cpuValues[cpuIndex] = realvalue;
            //if the index for the array is at the end make it start at the beginning again.
            //so then it loops through the array the whole time.
            cpuIndex = (cpuIndex + 1) % 10;

            //Set the email settings correctly.
            receiver = monitorItem.EmailAddressForWarning;
            subject = "CPU usage too high.";
            body = $"The CPU usage has exceeded the threshold of {threshold}% for 60% or more consecutive times";

            //Counts how many values inside the array are above the threshold and adds them to the count.
            count = cpuValues.Count(val => val > threshold);
            await logService.LogInformation(logger, LogScopes.RunStartAndStop, monitorItem.LogSettings, $"Array count is: {count}", configurationServiceName, monitorItem.TimeId, monitorItem.Order);

            //Checks if the count is above the threshold.
            if (count <= arrayCountThreshold)
            {
                emailCpuSent = false;
                return new JObject
                {
                    {"HardwareType", "CPU"},
                    {"DetectionType", "ArrayCount"},
                    {"Threshold", threshold},
                    {"EmailSend", emailCpuSent},
                    {"ArrayCount", count}
                };
            }

            //Check if an email already has been sent
            if (emailCpuSent)
            {
                return new JObject
                {
                    {"HardwareType", "CPU"},
                    {"DetectionType", "ArrayCount"},
                    {"Threshold", threshold},
                    {"EmailSend", emailCpuSent},
                    {"ArrayCount", count}
                };
            }

            emailCpuSent = true;
            await gclCommunicationsService.SendEmailAsync(receiver, subject, body);

            return new JObject
            {
                {"HardwareType", "CPU"},
                {"DetectionType", "ArrayCount"},
                {"Threshold", threshold},
                {"EmailSend", emailCpuSent},
                {"ArrayCount", count}
            };
        }

        /// <summary>
        /// Gets the CPU usage with the use of a counter which checks if for a certain amount of times the value is above the threshold.
        /// </summary>
        /// <param name="monitorItem">The information for the monitor action.</param>
        /// <param name="threshold">The threshold to check if an email needs to be send.</param>
        /// <param name="gclCommunicationsService">The name of the service in the configuration, used for logging.</param>
        /// <param name="configurationServiceName">The communications service from the GCL to actually send out the email.</param>
        /// <returns></returns>
        private async Task<JObject> GetCpuUsageCounterAsync(ServerMonitorModel monitorItem, int threshold, string configurationServiceName, ICommunicationsService gclCommunicationsService)
        {
            //the first value of performance counter will always be 0.
            if (!firstValueUsed)
            {
                firstValueUsed = true;
                cpuCounter.NextValue();
            }
            var realvalue = cpuCounter.NextValue();

            //Set the email settings correctly.
            receiver = monitorItem.EmailAddressForWarning;
            subject = "CPU usage too high.";
            body = $"The CPU usage has been above the threshold of {threshold}% for {aboveThresholdTimer} runs.";
            await logService.LogInformation(logger, LogScopes.RunStartAndStop, monitorItem.LogSettings, $"CPU is: {realvalue}%", configurationServiceName, monitorItem.TimeId, monitorItem.Order);

            //Checks if the value is above the threshold and if so then adds 1 to the counter.
            //If the value is below the threshold then reset the counter to 0.
            if(realvalue < threshold)
            {
                emailCpuSent = false;
                aboveThresholdTimer = 0;
                return new JObject
                {
                    {"HardwareType", "CPU"},
                    {"DetectionType", "Counter"},
                    {"Threshold", threshold},
                    {"EmailSend", emailCpuSent},
                    {"CountSize", monitorItem.CpuCounterSize},
                    {"Count", aboveThresholdTimer}
                };
            }

            //Check if an email already has been sent
            if (emailCpuSent)
            {
                return new JObject
                {
                    {"HardwareType", "CPU"},
                    {"DetectionType", "Counter"},
                    {"Threshold", threshold},
                    {"EmailSend", emailCpuSent},
                    {"CountSize", monitorItem.CpuCounterSize},
                    {"Count", aboveThresholdTimer}
                };
            }

            aboveThresholdTimer++;
            if (aboveThresholdTimer >= monitorItem.CpuCounterSize)
            {
                emailCpuSent = true;
                await gclCommunicationsService.SendEmailAsync(receiver, subject, body);
            }

            await logService.LogInformation(logger, LogScopes.RunStartAndStop, monitorItem.LogSettings, $"CPU timer count  is: {aboveThresholdTimer}", configurationServiceName, monitorItem.TimeId, monitorItem.Order);
            return new JObject
            {
                {"HardwareType", "CPU"},
                {"DetectionType", "Counter"},
                {"Threshold", threshold},
                {"EmailSend", emailCpuSent},
                {"CountSize", monitorItem.CpuCounterSize},
                {"Count", aboveThresholdTimer}
            };
        }

        /// <summary>
        /// Gets the network utilization of the specified network card and checks if an e-mail needs to be send.
        /// </summary>
        /// <param name="monitorItem">The information for the monitor action.</param>
        /// <param name="threshold">The threshold to check if an email needs to be send.</param>
        /// <param name="gclCommunicationsService">The name of the service in the configuration, used for logging.</param>
        /// <param name="configurationServiceName">The communications service from the GCL to actually send out the email.</param>
        /// <returns></returns>
        private async Task<JObject> GetNetworkUtilization(ServerMonitorModel monitorItem, int threshold, string configurationServiceName, ICommunicationsService gclCommunicationsService)
        {
            var networkInterfaceName = monitorItem.NetworkInterfaceName;

            var numberOfIterations = 10;

            //get the correct types of the performancecounter class
            var bandwidthCounter = new PerformanceCounter("Network Interface", "Current Bandwidth", networkInterfaceName);
            var bandwidth = bandwidthCounter.NextValue();
            var dataSentCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", networkInterfaceName);
            var dataReceivedCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", networkInterfaceName);

            var sendSum = 0f;
            var receiveSum = 0f;

            for (var index = 0; index < numberOfIterations; index++)
            {
                sendSum += dataSentCounter.NextValue();
                receiveSum += dataReceivedCounter.NextValue();
            }
            var dataSent = sendSum;
            var dataReceived = receiveSum;

            var utilization = (8 * (dataSent + dataReceived)) / (bandwidth * numberOfIterations) * 100;
            await logService.LogInformation(logger, LogScopes.RunStartAndStop, monitorItem.LogSettings, $"Network utilization: {utilization}", configurationServiceName, monitorItem.TimeId, monitorItem.Order);

            //Set the email settings correctly.
            receiver = monitorItem.EmailAddressForWarning;
            subject = "High network utilization.";
            body = $"Your network utilization is {utilization} which is above the threshold of {threshold}.";

            //Checks if the value is above the threshold.
            if (utilization < threshold)
            {
                emailNetworkSent = false;
                return new JObject
                {
                    {"HardwareType", "Network"},
                    {"Threshold", threshold},
                    {"EmailSend", emailCpuSent},
                    {"Utilization", utilization}
                };
            }

            //Check if an email already has been sent
            if (emailNetworkSent)
            {
                return new JObject
                {
                    {"HardwareType", "Network"},
                    {"Threshold", threshold},
                    {"EmailSend", emailCpuSent},
                    {"Utilization", utilization}
                };
            }

            emailNetworkSent = true;
            await gclCommunicationsService.SendEmailAsync(receiver, subject, body);
            return new JObject
            {
                {"HardwareType", "Network"},
                {"Threshold", threshold},
                {"EmailSend", emailCpuSent},
                {"Utilization", utilization}
            };
        }
    }
}