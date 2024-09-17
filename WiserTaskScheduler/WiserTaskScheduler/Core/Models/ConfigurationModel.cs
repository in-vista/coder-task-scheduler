using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using WiserTaskScheduler.Modules.Branches.Models;
using WiserTaskScheduler.Modules.CleanupItems.Models;
using WiserTaskScheduler.Modules.CleanupWiserHistory.Models;
using WiserTaskScheduler.Modules.Communications.Models;
using WiserTaskScheduler.Modules.DocumentStoreRead.Models;
using WiserTaskScheduler.Modules.GenerateFiles.Models;
using WiserTaskScheduler.Modules.HttpApis.Models;
using WiserTaskScheduler.Modules.ImportFiles.Models;
using WiserTaskScheduler.Modules.Queries.Models;
using WiserTaskScheduler.Modules.RunSchemes.Models;
using WiserTaskScheduler.Modules.ServerMonitors.Models;
using WiserTaskScheduler.Modules.WiserImports.Models;
using WiserTaskScheduler.Modules.Ftps.Models;
using WiserTaskScheduler.Modules.GenerateCommunications.Models;
using WiserTaskScheduler.Modules.SlackMessages.Models;

namespace WiserTaskScheduler.Core.Models
{
    /// <summary>
    /// A model for the configuration.
    /// </summary>
    [XmlRoot("Configuration")]
    public class ConfigurationModel
    {
        /// <summary>
        /// Gets or sets the service name.
        /// </summary>
        [Required]
        public string ServiceName { get; set; }

        /// <summary>
        /// The ID of the template that holds this configuration in Wiser.
        /// </summary>
        public int TemplateId { get; set; }

        /// <summary>
        /// Version will be automatically set, either to the template version or the ticks of the last write time for a local file.
        /// </summary>
        public long Version { get; set; }

        /// <summary>
        /// A semicolon (;) seperated list of email addresses to notify when the service failed during execution.
        /// </summary>
        public string ServiceFailedNotificationEmails { get; set; }

        /// <summary>
        /// Gets or sets the connection string that is used for queries.
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Gets or sets the log settings.
        /// </summary>
        public LogSettings LogSettings { get; set; }

        /// <summary>
        /// Gets or sets the run schemes that have been placed in the group.
        /// </summary>
        [Required]
        [XmlArray("RunSchemes")]
        [XmlArrayItem(typeof(RunSchemeModel))]
        public RunSchemeModel[] RunSchemeGroup { get; set; }

        /// <summary>
        /// Gets or sets the run schemes that have been placed outside the group..
        /// </summary>
        [XmlElement("RunScheme")]
        public RunSchemeModel[] RunSchemes { get; set; }

        /// <summary>
        /// Gets or sets the queries that have been placed in the group.
        /// </summary>
        [XmlArray("Queries")]
        [XmlArrayItem(typeof(QueryModel))]
        public QueryModel[] QueryGroup { get; set; }

        /// <summary>
        /// Gets or sets the queries that have been placed outside the group.
        /// </summary>
        [XmlElement("Query")]
        public QueryModel[] Queries { get; set; }

        /// <summary>
        /// Gets or sets the HTTP APIs that have been placed in the group.
        /// </summary>
        [XmlArray("HttpApis")]
        [XmlArrayItem(typeof(HttpApiModel))]
        public HttpApiModel[] HttpApiGroup { get; set; }

        /// <summary>
        /// Gets or sets the HTTP APIs that have been placed outside the group.
        /// </summary>
        [XmlElement("HttpApi")]
        public HttpApiModel[] HttpApis { get; set; }

        /// <summary>
        /// Gets or sets the generate files that have been placed in the group.
        /// </summary>
        [XmlArray("GenerateFiles")]
        [XmlArrayItem(typeof(GenerateFileModel))]
        public GenerateFileModel[] GenerateFileGroup { get; set; }

        /// <summary>
        /// Gets or sets the generate files that have been placed outside the group.
        /// </summary>
        [XmlElement("GenerateFile")]
        public GenerateFileModel[] GenerateFiles { get; set; }

        /// <summary>
        /// Gets or sets the import files that have been placed in the group.
        /// </summary>
        [XmlArray("ImportFiles")]
        [XmlArrayItem(typeof(ImportFileModel))]
        public ImportFileModel[] ImportFileGroup { get; set; }

        /// <summary>
        /// Gets or sets the import files that have been placed outside the group.
        /// </summary>
        [XmlElement("ImportFile")]
        public ImportFileModel[] ImportFiles { get; set; }

        /// <summary>
        /// Gets or sets the items to be cleaned from an entity that have been placed in the group.
        /// </summary>
        [XmlArray("CleanupItems")]
        [XmlArrayItem(typeof(CleanupItemModel))]
        public CleanupItemModel[] CleanupItemGroup { get; set; }

        /// <summary>
        /// Gets or sets the items to be cleaned from an entity that have been placed outside the group.
        /// </summary>
        [XmlElement("CleanupItem")]
        public CleanupItemModel[] CleanupItems { get; set; }

        /// <summary>
        /// Gets or sets the communications that have been placed in the group.
        /// </summary>
        [XmlArray("Communications")]
        [XmlArrayItem(typeof(CommunicationModel))]
        public CommunicationModel[] CommunicationGroup { get; set; }

        /// <summary>
        /// Gets or sets the communications that have been placed outside the group.
        /// </summary>
        [XmlElement("Communication")]
        public CommunicationModel[] Communications { get; set; }

        /// <summary>
        /// Gets or sets the branch queue models.
        /// </summary>
        [XmlElement("BranchQueue")]
        public BranchQueueModel BranchQueueModel { get; set; }

        /// <summary>
        /// Gets or sets the Wiser the imports need to be handled for that have been placed in the group.
        /// </summary>
        [XmlArray("WiserImports")]
        [XmlArrayItem(typeof(WiserImportModel))]
        public WiserImportModel[] WiserImportGroup { get; set; }

        /// <summary>
        /// Gets or sets the Wiser the imports need to be handled for that have been placed outside the group.
        /// </summary>
        [XmlElement("WiserImport")]
        public WiserImportModel[] WiserImports { get; set; }
        
        /// <summary>
        /// Gets or sets the FTPs that have been placed in the group.
        /// </summary>
        [XmlArray("Ftps")]
        [XmlArrayItem(typeof(FtpModel))]
        public FtpModel[] FtpGroup { get; set; }
        
        /// <summary>
        /// Gets or sets the FTPs that have been placed outside the group.
        /// </summary>
        [XmlElement("Ftp")]
        public FtpModel[] Ftps { get; set; }
        
        /// <summary>
        /// Gets or sets the Wiser histories that need to be cleaned that have been placed in the group.
        /// </summary>
        [XmlArray("CleanupWiserHistories")]
        [XmlArrayItem(typeof(CleanupWiserHistoryModel))]
        public CleanupWiserHistoryModel[] CleanupWiserHistoryGroup { get; set; }

        /// <summary>
        /// Gets or sets the Wiser histories that need to be cleaned that have been placed outside the group.
        /// </summary>
        [XmlElement("CleanupWiserHistory")]
        public CleanupWiserHistoryModel[] CleanupWiserHistories { get; set; }
        
        /// <summary>
        /// Gets or sets the generate communications that have been placed in the group.
        /// </summary>
        [XmlArray("GenerateCommunications")]
        [XmlArrayItem(typeof(GenerateCommunicationModel))]
        public GenerateCommunicationModel[] GenerateCommunicationGroup { get; set; }
        
        /// <summary>
        /// Gets or sets the generate communications that have been placed outside the group.
        /// </summary>
        [XmlElement("GenerateCommunication")]
        public GenerateCommunicationModel[] GenerateCommunications { get; set; }

        /// <summary>
        /// Gets or sets the Server Monitors that have been placed inside the group
        /// </summary>
        [XmlArray("ServerMonitors")]
        [XmlArrayItem(typeof(ServerMonitorModel))]
        public ServerMonitorModel[] ServerMonitorsGroup { get; set; }

        /// <summary>
        /// Gets or sets the Server Monitors that have been placed outside the group
        /// </summary>
        [XmlElement("ServerMonitor")]
        public ServerMonitorModel[] ServerMonitor { get; set; }
        
        /// <summary>
        /// Gets or sets the Document store readers that have been placed inside the group
        /// </summary>
        [XmlArray("DocumentStoreReads")]
        [XmlArrayItem(typeof(DocumentStoreReadModel))]
        public DocumentStoreReadModel[] DocumentStoreReadersGroup { get; set; }

        /// <summary>
        /// Gets or sets the Server Monitors that have been placed outside the group
        /// </summary>
        [XmlElement("DocumentStoreRead")]
        public DocumentStoreReadModel[] DocumentStoreReader { get; set; }
        
        /// <summary>
        /// Gets or sets the SlackMessages that have been placed in the group.
        /// </summary>
        [XmlArray("SlackMessages")]
        [XmlArrayItem(typeof(SlackMessageModel))]
        public SlackMessageModel[] SlackMessageGroup { get; set; }

        /// <summary>
        /// Gets or sets the SlackMessages that have been placed outside the group.
        /// </summary>
        [XmlElement("SlackMessage")]
        public SlackMessageModel[] SlackMessages { get; set; }

        /// <summary>
        /// Get all run schemes that are defined in this configuration.
        /// Will combine the run schemes inside and outside the group.
        /// </summary>
        /// <returns>Returns all the run schemes in this configuration.</returns>
        public List<RunSchemeModel> GetAllRunSchemes()
        {
            var runSchemes = new List<RunSchemeModel>();
            if (RunSchemeGroup != null)
            {
                runSchemes.AddRange(RunSchemeGroup);
            }

            if (RunSchemes != null)
            {
                runSchemes.AddRange(RunSchemes);
            }

            return runSchemes;
        }
    }
}
