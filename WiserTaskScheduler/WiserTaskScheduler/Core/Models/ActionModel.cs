using System;
using System.ComponentModel.DataAnnotations;
using GeeksCoreLibrary.Core.Enums;
using GeeksCoreLibrary.Core.Models;

namespace WiserTaskScheduler.Core.Models
{
    /// <summary>
    /// An base for any action that the WTS can perform.
    /// </summary>
    public abstract class ActionModel
    {
        /// <summary>
        /// Gets or sets the time id of the run scheme this action needs to be executed in.
        /// </summary>
        [Required]
        public int TimeId { get; set; }

        /// <summary>
        /// Gets or sets the in what order this action needs to be executed.
        /// </summary>
        [Required]
        public int Order { get; set; }

        /// <summary>
        /// Gets or sets the name of the result set.
        /// </summary>
        public string ResultSetName { get; set; } = String.Empty;

        /// <summary>
        /// Gets or sets the result set to use for replacements.
        /// </summary>
        public string UseResultSet { get; set; } = String.Empty;

        /// <summary>
        /// Gets or sets the settings to use to hash the marked values.
        /// </summary>
        public HashSettingsModel HashSettings { get; set; } = new()
        {
            Algorithm = HashAlgorithms.SHA256,
            Representation = HashRepresentations.Base64
        };

        /// <summary>
        /// Gets or sets which result set needs to be a specific value.
        /// Result set name and required status code, separated by a comma (,).
        /// </summary>
        public string OnlyWithStatusCode { get; set; }

        /// <summary>
        /// Gets or sets which result set needs to have a success state.
        /// Result set name and required status (True/False), separated by a comma (,).
        /// </summary>
        public string OnlyWithSuccessState { get; set; }
        
        /// <summary>
        /// Gets or sets the value that will be checked to see if the action is performed
        /// Result set path and check value, separated by a comma (,).
        /// </summary>
        public string OnlyWithValue { get; set; }

        /// <summary>
        /// Gets or sets the log settings that apply to the action.
        /// </summary>
        public LogSettings LogSettings { get; set; }
    }
}
