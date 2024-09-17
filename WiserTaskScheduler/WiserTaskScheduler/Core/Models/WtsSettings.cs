using System;
using System.Collections.Generic;
using WiserTaskScheduler.Core.Models.Cleanup;
using WiserTaskScheduler.Core.Models.ParentsUpdate;
using WiserTaskScheduler.Modules.Wiser.Models;

namespace WiserTaskScheduler.Core.Models
{
    /// <summary>
    /// The settings for the WTS.
    /// </summary>
    public class WtsSettings
    {
        private string name;

        /// <summary>
        /// Gets or sets the name of the WTS to use for communication.
        /// </summary>
        public string Name
        {
            get => String.IsNullOrWhiteSpace(name) ? $"Wiser Task Scheduler ({Environment.MachineName})" : name;
            set => name = value;
        }

        /// <summary>
        /// Gets or sets the settings of the <see cref="MainService"/>.
        /// </summary>
        public MainServiceSettings MainService { get; set; }

        /// <summary>
        /// Gets or sets the settings of the <see cref="CleanupService"/>.
        /// </summary>
        public CleanupServiceSettings CleanupService { get; set; } = new CleanupServiceSettings();
        
        /// <summary>
        /// Gets or sets the settings of the <see cref="ParentsUpdateService"/>.
        /// </summary>
        public ParentsUpdateServiceSettings ParentsUpdateService { get; set; } = new ParentsUpdateServiceSettings();
        
        /// <summary>
        /// Gets or sets the settings for the connection to Wiser 3.
        /// </summary>
        public WiserSettings Wiser { get; set; }
        
        /// <summary>
        /// Gets or sets the <see cref="SlackSettings"/> used by the <see cref="SlackChatService"/>.
        /// </summary>
        public SlackSettings SlackSettings { get; set; }
        
        /// <summary>
        /// A semicolon (;) seperated list of email addresses to notify when a core service failed during execution.
        /// </summary>
        public string ServiceFailedNotificationEmails { get; set; }
        
        /// <summary>
        /// The number of minutes to wait before sending the same error notification again.
        /// </summary>
        public int ErrorNotificationsIntervalInMinutes { get; set; } = 60;
        
        /// <summary>
        /// Gets or sets the credentials from the secrets app settings.
        /// </summary>
        public IReadOnlyDictionary<string, string> Credentials { get; set; }
    }
}
