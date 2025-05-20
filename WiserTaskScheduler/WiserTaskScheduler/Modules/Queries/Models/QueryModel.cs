using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using WiserTaskScheduler.Core.Models;

namespace WiserTaskScheduler.Modules.Queries.Models
{
    /// <summary>
    /// A model for a query.
    /// </summary>
    [XmlType("Query")]
    public class QueryModel : ActionModel
    {
        /// <summary>
        /// Gets or sets the query to execute.
        /// </summary>
        [Required]
        public string Query { get; set; }

        /// <summary>
        /// Gets or sets the seconds the query has before timeout.
        /// </summary>
        public int Timeout { get; set; } = 30;

        /// <summary>
        /// Only execute this query when the used resultset has a succes state of true
        /// </summary>
        public bool OnlyWithSuccessfullResultSet { get; set; } = false;

        /// <summary>
        /// Gets or sets the information for character encoding to use.
        /// </summary>
        public CharacterEncodingModel CharacterEncoding { get; set; } = new CharacterEncodingModel();
        
        /// <summary>
        /// Gets or sets if the queries in this action needs to be performed within a transaction.
        /// </summary>
        public bool UseTransaction { get; set; }
    }
}
