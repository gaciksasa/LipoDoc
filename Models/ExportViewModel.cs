using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace DeviceDataCollector.Models
{
    public class ExportViewModel
    {
        // Available columns that can be selected for export
        public List<ColumnSelectionItem> AvailableColumns { get; set; } = new List<ColumnSelectionItem>();

        // Columns selected by the user
        public List<string> SelectedColumns { get; set; } = new List<string>();

        // Column ordering
        public List<string> ColumnOrder { get; set; } = new List<string>();

        // Empty columns to add
        [Display(Name = "Empty Columns")]
        [Range(0, 20, ErrorMessage = "Empty columns count must be between 0 and 20")]
        public int EmptyColumnsCount { get; set; } = 0;

        // Filter options
        [Display(Name = "Start Date")]
        [DataType(DataType.Date)]
        public DateTime? StartDate { get; set; }

        [Display(Name = "End Date")]
        [DataType(DataType.Date)]
        public DateTime? EndDate { get; set; }

        [Display(Name = "Device ID")]
        public string DeviceId { get; set; }

        public List<SelectListItem> AvailableDevices { get; set; } = new List<SelectListItem>();

        // Delimiter options
        [Display(Name = "Column Delimiter")]
        public string Delimiter { get; set; } = ",";

        [Display(Name = "Date Format")]
        public string DateFormat { get; set; } = "yyyy-MM-dd";

        [Display(Name = "Time Format")]
        public string TimeFormat { get; set; } = "HH:mm:ss";

        [Display(Name = "Include Headers")]
        public bool IncludeHeaders { get; set; } = true;

        [Display(Name = "Custom Separator")]
        public string CustomSeparator { get; set; } = "";

        [Display(Name = "Export Folder")]
        public string ExportFolderPath { get; set; } = string.Empty;

        [Display(Name = "Auto Export Enabled")]
        public bool AutoExportEnabled { get; set; } = false;

        [Display(Name = "Auto Export Mode")]
        public string AutoExportMode { get; set; } = "single_file"; // Options: single_file, daily_file, individual_files

        [Display(Name = "Custom File Name")]
        public string CustomFileName { get; set; } = "Donations_Export";

        // Delimiters to choose from
        public List<SelectListItem> DelimiterOptions => new List<SelectListItem>
        {
            new SelectListItem { Value = ",", Text = "Comma (,)" },
            new SelectListItem { Value = ";", Text = "Semicolon (;)" },
            new SelectListItem { Value = "\t", Text = "Tab" },
            new SelectListItem { Value = "|", Text = "Pipe (|)" },
            new SelectListItem { Value = "custom", Text = "Custom..." }
        };

        // Date formats to choose from
        public List<SelectListItem> DateFormatOptions => new List<SelectListItem>
        {
            new SelectListItem { Value = "yyyy-MM-dd", Text = "yyyy-MM-dd" },
            new SelectListItem { Value = "dd.MM.yyyy", Text = "dd.MM.yyyy" },
            new SelectListItem { Value = "MM/dd/yyyy", Text = "MM/dd/yyyy" }
        };

        // Time formats to choose from
        public List<SelectListItem> TimeFormatOptions => new List<SelectListItem>
        {
            new SelectListItem { Value = "HH:mm:ss", Text = "HH:mm:ss (24-hour)" },
            new SelectListItem { Value = "hh:mm:ss tt", Text = "hh:mm:ss AM/PM (12-hour)" },
            new SelectListItem { Value = "HH:mm", Text = "HH:mm (24-hour)" }
        };

        public List<SelectListItem> SavedConfigurations { get; set; } = new List<SelectListItem>();
        public int? SelectedConfigId { get; set; }
        public string ConfigName { get; set; }
        public string ConfigDescription { get; set; }
        public bool SaveAsNew { get; set; } = true;
        public bool SetAsDefault { get; set; } = true;
    }

    public class ColumnSelectionItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Selected { get; set; }
    }
}