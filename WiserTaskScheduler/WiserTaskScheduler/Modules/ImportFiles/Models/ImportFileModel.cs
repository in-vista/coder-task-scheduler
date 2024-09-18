using System.Collections.Generic;
using System.Xml.Serialization;
using WiserTaskScheduler.Core.Models;
using WiserTaskScheduler.Modules.ImportFiles.Enums;

namespace WiserTaskScheduler.Modules.ImportFiles.Models
{
    [XmlType("ImportFile")]
    public class ImportFileModel : ActionModel
    {
        /// <summary>
        /// Gets or sets the full path to the file.
        /// </summary>
        public string FilePath { get; set; }
        
        /// <summary>
        /// Gets or sets the file type to be imported.
        /// </summary>
        public FileTypes FileType { get; set; } = FileTypes.CSV;
        
        /// <summary>
        /// Gets or sets the search pattern to find files in a folder if FilePath is a folder.
        /// </summary>
        public string SearchPattern { get; set; } = "*.*";
        
        /// <summary>
        /// Get or sets the folder to move the file to after it has been processed. If empty, the file will not be moved.
        /// </summary>
        public string ProcessedFolder { get; set; }

        /// <summary>
        /// Gets or sets whether a suffix should be added to the file name when moving it to the processed folder.
        /// The suffix will be the current date and time in the format <c>YYYYMMDDHHMMSS</c>.
        /// This only applies if <see cref="ProcessedFolder"/> is set.
        /// </summary>
        public bool AddSuffixToFileNameAfterProcessing { get; set; }

        /// <summary>
        /// Gets or sets the separator to split a line on.
        /// The value "\t" can be used for tab-separated files.
        /// </summary>
        public string Separator { get; set; } = ",";
        
        /// <summary>
        /// Gets or sets the escape character used in CSV files
        /// </summary>
        public string EscapeCharacter { get; set; } = "";

        /// <summary>
        /// Gets or sets if the first row of the file are field names.
        /// If set to true, then columns can be requested by field name. Otherwise the index needs to be given to request a column.
        /// </summary>
        public bool HasFieldNames { get; set; } = true;

        /// <summary>
        /// Gets or sets whether a single file is imported (true) or multiple files based on a dataset (false).
        /// </summary>
        public bool SingleFile { get; set; } = true;

        /// <summary>
        /// Gets or sets the xml mappings for the file.
        /// </summary>
        public List<XmlMapModel> XmlMapping { get; set; }
    }
}
