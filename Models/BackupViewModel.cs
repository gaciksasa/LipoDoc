using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using DeviceDataCollector.Services;

namespace DeviceDataCollector.Models
{
    public class BackupViewModel
    {
        public List<BackupInfo> Backups { get; set; } = new List<BackupInfo>();

        [Display(Name = "Backup Description")]
        public string Description { get; set; }

        public bool IsScheduledBackupEnabled { get; set; }

        public string ScheduledBackupTime { get; set; }

        public int ScheduledBackupRetention { get; set; }

        public string BackupDirectory { get; set; }

        public long TotalBackupSize { get; set; }

        public int BackupCount { get; set; }
    }

    public class ScheduledBackupSettingsViewModel
    {
        [Display(Name = "Enable Scheduled Backups")]
        public bool Enabled { get; set; }

        [Display(Name = "Backup Time (24h format)")]
        [RegularExpression(@"^([01]?[0-9]|2[0-3]):[0-5][0-9]$", ErrorMessage = "Time must be in 24-hour format (HH:MM)")]
        public string Time { get; set; }

        [Display(Name = "Number of Backups to Retain")]
        [Range(1, 30, ErrorMessage = "Retention count must be between 1 and 30")]
        public int RetentionCount { get; set; }
    }
}