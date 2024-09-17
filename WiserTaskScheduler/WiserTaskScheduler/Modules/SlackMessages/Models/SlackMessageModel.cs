using System.Xml.Serialization;
using WiserTaskScheduler.Core.Models;

namespace WiserTaskScheduler.Modules.SlackMessages.Models
{
    [XmlType("SlackMessage")]
    public class SlackMessageModel : ActionModel
    {
        /// <summary>
        /// Gets or sets the Message 
        /// </summary>
        public string Message { get; set; }
        
        /// <summary>
        /// Gets or sets the recipient the chat message is to be send to
        /// </summary>
        public string Recipient { get; set; }
    }
}
