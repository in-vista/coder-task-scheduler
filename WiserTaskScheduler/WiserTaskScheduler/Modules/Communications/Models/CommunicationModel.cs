using System.Xml.Serialization;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.Communication.Enums;
using GeeksCoreLibrary.Modules.Communication.Models;
using WiserTaskScheduler.Core.Models;

namespace WiserTaskScheduler.Modules.Communications.Models;

[XmlType("Communication")]
public class CommunicationModel : ActionModel
{
    /// <summary>
    /// Gets or sets the type of communication to process.
    /// </summary>
    public CommunicationTypes Type { get; set; } = CommunicationTypes.Email;
    
    /// <summary>
    /// Gets or sets if the direct or bulk communication must be processed
    /// </summary>
    public bool BulkSend { get; set; } = false;

    /// <summary>
    /// Gets or sets the email address that needs to be used if errors occured.
    /// </summary>
    public string EmailAddressForErrorNotifications { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of times the communication is tried.
    /// </summary>
    public int MaxNumberOfCommunicationAttempts { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum hours the message is allowed to be delayed before it is ignored.
    /// </summary>
    public int MaxDelayInHours { get; set; } = 0;

    /// <summary>
    /// Gets or sets the connection string to overwrite the configuration's connection string with.
    /// </summary>
    public string ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the settings for the SMTP if the <see cref="Type"/> is Email.
    /// </summary>
    public SmtpSettings SmtpSettings { get; set; }

    /// <summary>
    /// Gets or sets the settings for SMS if the <see cref="Type"/> is SMS.
    /// </summary>
    public SmsSettings SmsSettings { get; set; }
}