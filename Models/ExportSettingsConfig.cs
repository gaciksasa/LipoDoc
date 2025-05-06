using System.ComponentModel.DataAnnotations;

namespace DeviceDataCollector.Models
{
    public class ExportSettingsConfig
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        public bool IsDefault { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? LastUsedAt { get; set; }

        // Selected columns (stored as JSON)
        public string? SelectedColumnsJson { get; set; }

        // Column order (stored as JSON)
        public string? ColumnOrderJson { get; set; }

        public int EmptyColumnsCount { get; set; }

        // Delimiter options
        public string? Delimiter { get; set; } = ",";
        public string? CustomSeparator { get; set; }

        // Date and time formats
        public string? DateFormat { get; set; } = "dd.MM.yyyy";
        public string? TimeFormat { get; set; } = "HH:mm:ss";

        // Include headers in export
        public bool IncludeHeaders { get; set; } = true;

        // Created by user
        public string? CreatedBy { get; set; }

        // Filter options
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string? DeviceId { get; set; }

        // Export folder path
        public string? ExportFolderPath { get; set; }

        // Auto export settings
        public bool AutoExportEnabled { get; set; } = false;
        public string? AutoExportMode { get; set; } = "single_file";
        public string? CustomFileName { get; set; } = "Donations_Export";
    }
}