using System.Xml.Serialization;
using MySqlConnector;
using WiserTaskScheduler.Core.Models;

namespace WiserTaskScheduler.Modules.Branches.Models
{
    /// <summary>
    /// A model for processing the branch queue.
    /// </summary>
    [XmlType("BranchQueue")]
    public class BranchQueueModel : ActionModel
    {
        /// <summary>
        /// Gets or sets the username of the user that should be used for creating and deleting branches.
        /// Normal users should not have permissions to create or drop a database, only this user should.
        /// </summary>
        public string UsernameForManagingBranches { get; set; }

        /// <summary>
        /// Gets or sets the password of the user that should be used for creating and deleting branches.
        /// Normal users should not have permissions to create or drop a database, only this user should.
        /// </summary>
        public string PasswordForManagingBranches { get; set; }

        /// <summary>
        /// Gets or sets the ID of the mail template to get information from.
        /// This is for sending notifications about the status of the creation of a new branch.
        /// </summary>
        public ulong CreatedBranchTemplateId { get; set; }

        /// <summary>
        /// Gets or sets the ID of the mail template to get information from.
        /// This is for sending notifications about the status of the merging of a branch to the main branch.
        /// </summary>
        public ulong MergedBranchTemplateId { get; set; }

        /// <summary>
        /// Gets or sets the ID of the mail template to get information from.
        /// This is for sending notifications about the status of the deletion of a branch.
        /// </summary>
        public ulong DeletedBranchTemplateId { get; set; }

        /// <summary>
        /// Gets or sets whether to use the <see cref="MySqlBulkCopy"/> class when creating branches.
        /// This is only applicable if the server of the branch database is different from the main database server.
        /// This is NOT used by default, because it requires the setting "local_infile" to be enabled in the MySQL server.
        /// Enabling local_infile can pose security risks if untrusted users have access to your MySQL server, as it allows them to load data from local files on the client machine. Ensure that you understand these risks and mitigate them appropriately.
        /// However, creating branches with this setting enabled will be much faster than the default method.
        /// </summary>
        public bool UseMySqlBulkCopyWhenCreatingBranches { get; set; }

        /// <summary>
        /// Gets or sets the specific rules for copying tables.
        /// </summary>
        [XmlArray("CopyTableRules")]
        [XmlArrayItem(typeof(CopyTableRuleModel))]
        public CopyTableRuleModel[] CopyTableRules { get; set; }
    }
}