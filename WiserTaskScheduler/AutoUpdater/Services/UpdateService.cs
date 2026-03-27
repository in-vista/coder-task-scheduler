using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.ServiceProcess;
using AutoUpdater.Enums;
using AutoUpdater.Interfaces;
using AutoUpdater.Models;
using GeeksCoreLibrary.Modules.Communication.Interfaces;
using GeeksCoreLibrary.Modules.Communication.Models;
using Microsoft.Extensions.Options;

namespace AutoUpdater.Services;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class UpdateService : IUpdateService
{
    private const string WtsTempPath = "C:/temp/wts";
    private const string WtsExeFile = "WiserTaskScheduler.exe";

    private const int UpdateDelayAfterServiceShutdown = 60000;
    private const int DeleteFileAttempts = 5;
    private const int DeleteFileDelay = 5000;

    private readonly UpdateSettings updateSettings;
    private readonly ILogger<UpdateService> logger;
    private readonly IServiceProvider serviceProvider;

    private Version lastDownloadedVersion;
    
    public UpdateService(IOptions<UpdateSettings> updateSettings, ILogger<UpdateService> logger, IServiceProvider serviceProvider)
    {
        this.updateSettings = updateSettings.Value;
        this.logger = logger;
        this.serviceProvider = serviceProvider;
        
        Directory.CreateDirectory(Path.Combine(WtsTempPath, "update"));
        Directory.CreateDirectory(Path.Combine(WtsTempPath, "backups"));
    }

    /// <inheritdoc />
    public async Task UpdateServicesAsync()
    {
        logger.LogInformation("Starting with updating the WTS services.");

        try
        {
            var versionList = await GetVersionList();
            if (lastDownloadedVersion == null || lastDownloadedVersion != versionList[0].Version)
            {
                // If the update failed to download, do not continue with the update.
                if (!await DownloadUpdate(versionList[0].Version))
                {
                    return;
                }
            }

            foreach (var wts in updateSettings.WtsInstancesToUpdate)
            {
                new Thread(() => UpdateWts(wts, versionList)).Start();
            }
        }
        catch (Exception e)
        {
            logger.LogError($"Failed to update the WTS services due to exception:{Environment.NewLine}{Environment.NewLine}{e}");
        }
    }

    /// <summary>
    /// Get the list of all the versions of the WTS.
    /// </summary>
    /// <returns>Returns the list of all the versions of the WTS.</returns>
    private async Task<List<VersionModel>> GetVersionList()
    {
        logger.LogInformation("Retrieving version list from server.");
        
        using var request = new HttpRequestMessage(HttpMethod.Get, updateSettings.VersionListUrl);
        using var client = new HttpClient(new HttpClientHandler() {AllowAutoRedirect = true});
        using var response = await client.SendAsync(request);
        return await response.Content.ReadFromJsonAsync<List<VersionModel>>();
    }

    /// <summary>
    /// Download the update files to the disk.
    /// </summary>
    /// <param name="version">The version being downloaded.</param>
    /// <returns>Returns true if the update has been downloaded.</returns>
    private async Task<bool> DownloadUpdate(Version version)
    {
        logger.LogInformation("Download the latest update from the server.");
        
        var filePath = Path.Combine(WtsTempPath, "update", "update.zip");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        
        var downloadUrl = updateSettings.VersionDownloadUrl.EndsWith("/") ? updateSettings.VersionDownloadUrl : $"{updateSettings.VersionDownloadUrl}/";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{downloadUrl}version{version.ToString()}.zip");
        using var client = new HttpClient(new HttpClientHandler() {AllowAutoRedirect = true});
        using var response = await client.SendAsync(request);
        
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError($"Failed to download the update for version {version}.");
            return false;
        }
        
        await File.WriteAllBytesAsync(filePath, await response.Content.ReadAsByteArrayAsync());
        lastDownloadedVersion = version;
        return true;
    }
    
    /// <summary>
    /// Update a single WTS, method is started on its own thread.
    /// </summary>
    /// <param name="wts">The WTS information to update.</param>
    /// <param name="versionList">All the versions of the WTS to be checked against.</param>
    private void UpdateWts(WtsModel wts, List<VersionModel> versionList)
    {
        logger.LogInformation($"Checking updates for WTS '{wts.ServiceName}'.");

        if (!ValidUpdateDay(wts))
        {
            logger.LogInformation($"WTS '{wts.ServiceName}' is not allowed to be updated and will check again tomorrow.");
            return;
        }
        
        UpdateStates updateState;
        var version = new Version(0, 0, 0, 0);
        
        var path = Path.Combine(wts.PathToFolder, WtsExeFile);
        if (Path.Exists(path))
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(Path.Combine(wts.PathToFolder, WtsExeFile));
            version = new Version(versionInfo.FileVersion);
            updateState = CheckForUpdates(version, versionList);
        }
        else
        {
            // If the WTS is not found on the location, it is considered to need to be updated.
            updateState = UpdateStates.Update;
            logger.LogWarning($"WTS '{wts.ServiceName}' not found at '{wts.PathToFolder}' so the latest version will be installed.");
        }

        switch (updateState)
        {
            case UpdateStates.UpToDate:
                logger.LogInformation($"WTS '{wts.ServiceName}' is up-to-date.");
                return;
            case UpdateStates.BreakingChanges:
                logger.LogWarning($"Could not update WTS '{wts.ServiceName}' to version {versionList[0].Version} due to breaking changes since the current version of the WTS ({version}).{Environment.NewLine}Please check the release logs and resolve the breaking changes before manually updating the WTS.");
                EmailAdministrator(wts.ContactEmail, "WTS Auto Updater - Manual action required", $"Could not update WTS '{wts.ServiceName}' to version {versionList[0].Version} due to breaking changes since the current version of the WTS ({version}).<br/>Please check the release logs and resolve the breaking changes before manually updating the WTS.", wts.ServiceName);
                return;
            case UpdateStates.Update:
                // If the update time is in the future wait until the update time.
                if (wts.UpdateTime > DateTime.Now.TimeOfDay)
                {
                    logger.LogInformation($"WTS '{wts.ServiceName}' will be updated at {wts.UpdateTime}.");
                    Thread.Sleep(wts.UpdateTime - DateTime.Now.TimeOfDay);
                }
        
                logger.LogInformation($"Updating WTS '{wts.ServiceName}'.");
                
                PerformUpdate(wts, version, versionList[0].Version);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(updateState), updateState.ToString());
        }
    }
    
    /// <summary>
    /// Check if the WTS is allowed to be updated today.
    /// </summary>
    /// <param name="wts">The information of the WTS that is being processed.</param>
    /// <returns>Returns true if it is a valid day to update, false if not.</returns>
    private bool ValidUpdateDay(WtsModel wts)
    {
        var allowedDays = wts.UpdateDays ?? new[] {1, 2, 3, 4, 5};
        return allowedDays.Contains((int) DateTime.Now.DayOfWeek);
    }

    /// <summary>
    /// Check if the WTS can and needs to be updated.
    /// </summary>
    /// <param name="version">The current version of the WTS.</param>
    /// <param name="versionList">All the versions of the WTS to be checked against.</param>
    /// <returns></returns>
    private UpdateStates CheckForUpdates(Version version, List<VersionModel> versionList)
    {
        if (version == versionList[0].Version)
        {
            return UpdateStates.UpToDate;
        }
        
        for (var i = versionList.Count - 1; i >= 0; i--)
        {
            if (version >= versionList[i].Version)
            {
                continue;
            }

            if (versionList[i].ContainsBreakingChanges)
            {
                return UpdateStates.BreakingChanges;
            }
        }

        return UpdateStates.Update;
    }

    /// <summary>
    /// Update the current WTS to the newest version.
    /// Tries to restore to current version if the update failed.
    /// </summary>
    /// <param name="wts">The WTS information to update.</param>
    /// <param name="currentVersion">The current version of the WTS.</param>
    /// <param name="versionToUpdateTo">The version to update the WTS to.</param>
    private void PerformUpdate(WtsModel wts, Version currentVersion, Version versionToUpdateTo)
    {
        bool serviceAlreadyStopped;
        
        using (var serviceController = new ServiceController())
        {
            serviceController.ServiceName = wts.ServiceName;

            // Check if the service has been found. If the status throws an invalid operation exception the service does not exist.
            try
            {
                serviceAlreadyStopped = serviceController.Status == ServiceControllerStatus.Stopped;
            }
            catch (InvalidOperationException)
            {
                EmailAdministrator(wts.ContactEmail, "WTS Auto Updater - WTS not found", $"The service for WTS '{wts.ServiceName}' could not be found on the server and can therefore not be updated.", wts.ServiceName);
                logger.LogWarning($"No service found for '{wts.ServiceName}'.");
                return;
            }

            if (!serviceAlreadyStopped)
            {
                serviceController.Stop();
                serviceController.WaitForStatus(ServiceControllerStatus.Stopped);

                // Wait for 1 minute after the service has been stopped to increase the chance all resources have been released.
                Thread.Sleep(UpdateDelayAfterServiceShutdown);
            }
        }

        try
        {
            BackupWts(wts);
            PlaceWts(wts, Path.Combine(WtsTempPath, "update", "update.zip"));

            // If the service was not running when the update started it does not need to restart.
            if (!serviceAlreadyStopped)
            {
                using var serviceController = new ServiceController();
                serviceController.ServiceName = wts.ServiceName;
                try
                {
                    serviceController.Start();
                    serviceController.WaitForStatus(ServiceControllerStatus.Running);
                }
                catch (InvalidOperationException updateException)
                {
                    RevertUpdate(wts, serviceController, currentVersion, versionToUpdateTo, updateException);

                    return;
                }
            }
            
            logger.LogInformation($"WTS '{wts.ServiceName}' has been successfully updated to version {versionToUpdateTo}.");

            if (wts.SendEmailOnUpdateComplete)
            {
                EmailAdministrator(wts.ContactEmail, "WTS Auto Updater - Update installed", $"The service for WTS '{wts.ServiceName}' has been successfully updated to version {versionToUpdateTo}.", wts.ServiceName);
            }
        }
        catch (Exception e)
        {
            logger.LogError($"Exception occured while updating WTS '{wts.ServiceName}'.{Environment.NewLine}{Environment.NewLine}{e}");
            EmailAdministrator(wts.ContactEmail, "WTS Auto Updater - Updating failed!", $"Failed to update WTS '{wts.ServiceName}' to version {versionToUpdateTo}.<br/><br/>Error when updating:<br/>{e.ToString().ReplaceLineEndings("<br/>")}", wts.ServiceName);
        }
    }

    /// <summary>
    /// Make a backup of the current WTS.
    /// </summary>
    /// <param name="wts">The WTS information to update.</param>
    private void BackupWts(WtsModel wts)
    {
        var backupPath = Path.Combine(WtsTempPath, "backups", wts.ServiceName);
        Directory.CreateDirectory(backupPath);
        
        // Delete old backups.
        foreach (var file in new DirectoryInfo(backupPath).GetFiles())
        {
            file.Delete();
        }
        
        ZipFile.CreateFromDirectory(wts.PathToFolder,  Path.Combine(backupPath, $"{DateTime.Now:yyyyMMdd}.zip"));
    }

    /// <summary>
    /// Delete all files except the app settings and extract the files from the <see cref="source"/> to the folder.
    /// </summary>
    /// <param name="wts">The WTS information to update.</param>
    /// <param name="source">The source to extract the files from.</param>
    private void PlaceWts(WtsModel wts, string source)
    {
        foreach (var file in new DirectoryInfo(wts.PathToFolder).GetFiles())
        {
            if (file.Name.StartsWith("appsettings") && file.Name.EndsWith(".json"))
            {
                continue;
            }
            
            // Try to delete the file 5 times with a 1 second delay between each attempt.
            var attempts = 0;
            do
            {
                attempts++;
                try
                {
                    file.Delete();
                    break;
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempts >= DeleteFileAttempts)
                        throw;
                    
                    Thread.Sleep(DeleteFileDelay);
                }
            } while (true);
        }
        
        ZipFile.ExtractToDirectory(source, wts.PathToFolder, true);
    }

    /// <summary>
    /// Revert the WTS back to the previous version from the backup made prior to the update.
    /// </summary>
    /// <param name="wts">The WTS information to update.</param>
    /// <param name="serviceController"></param>
    /// <param name="currentVersion"></param>
    /// <param name="versionToUpdateTo"></param>
    /// <param name="updateException"></param>
    private void RevertUpdate(WtsModel wts, ServiceController serviceController, Version currentVersion, Version versionToUpdateTo, InvalidOperationException updateException)
    {
        PlaceWts(wts, Path.Combine(WtsTempPath, "backups", wts.ServiceName, $"{DateTime.Now:yyyyMMdd}.zip"));

        try
        {
            // Try to start the previous installed version again.
            serviceController.Start();
            serviceController.WaitForStatus(ServiceControllerStatus.Running);
            EmailAdministrator(wts.ContactEmail, "WTS Auto Updater - Updating failed!", $"Failed to update WTS '{wts.ServiceName}' to version {versionToUpdateTo}, successfully restored to version {currentVersion}.<br/><br/>Error when updating:<br/>{updateException.ToString().ReplaceLineEndings("<br/>")}", wts.ServiceName);
        }
        catch (InvalidOperationException revertException)
        {
            logger.LogError($"Failed to update WTS '{wts.ServiceName}' to version {versionToUpdateTo}, failed to restore version {currentVersion}.{Environment.NewLine}{Environment.NewLine}Error when reverting:{Environment.NewLine}{revertException}{Environment.NewLine}{Environment.NewLine}Error when updating:<br/>{updateException}");
            EmailAdministrator(wts.ContactEmail, "WTS Auto Updater - Updating and reverting failed!", $"Failed to update WTS '{wts.ServiceName}' to version {versionToUpdateTo}, failed to restore version {currentVersion}.<br/><br/>Error when reverting:<br/>{revertException.ToString().ReplaceLineEndings("<br/>")}<br/><br/>Error when updating:<br/>{updateException.ToString().ReplaceLineEndings("<br/>")}", wts.ServiceName);
        }
    }
    
    /// <summary>
    /// Send a email to the administrator of the WTS.
    /// </summary>
    /// <param name="receiver">The email address of the administrator.</param>
    /// <param name="subject">The subject of the email.</param>
    /// <param name="body">The body of the email.</param>
    /// <param name="serviceName">The name of the WTS service for which an email is being send.</param>
    private void EmailAdministrator(string receiver, string subject, string body, string serviceName)
    {
        if (String.IsNullOrWhiteSpace(receiver))
        {
            logger.LogWarning($"No email address provided for '{serviceName}'.");
            return;
        }
        
        var scope = serviceProvider.CreateScope();
        var communicationsService = scope.ServiceProvider.GetRequiredService<ICommunicationsService>();
        var receivers = new List<CommunicationReceiverModel>();

        foreach (var emailAddress in receiver.Split(';'))
        {
            if (!String.IsNullOrWhiteSpace(emailAddress))
            {
                receivers.Add(new CommunicationReceiverModel() {Address = emailAddress});
            }
        }

        var communication = new SingleCommunicationModel()
        {
            Receivers =  receivers,
            Subject = $"{subject} ({Environment.MachineName})",
            Content = body
        };

        try
        {
            communicationsService.SendEmailDirectlyAsync(communication);
        }
        catch (Exception e)
        {
            logger.LogError($"Failed to send email to '{receiver}' for '{serviceName}'.{Environment.NewLine}{Environment.NewLine}{e}");
            throw;
        }
    }
}