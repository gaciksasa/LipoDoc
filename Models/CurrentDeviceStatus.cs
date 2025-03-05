using System.ComponentModel.DataAnnotations;

namespace DeviceDataCollector.Models
{
    public class CurrentDeviceStatus
    {
        [Key]
        public string DeviceId { get; set; } // Serial Number (SN) of the device - Primary Key

        public DateTime Timestamp { get; set; } // Time of the last status update

        public int Status { get; set; } // 0=IDLE, 1=Process in progress, 2=Process completed

        public int AvailableData { get; set; } // Number of readings buffered in the device

        public string? IPAddress { get; set; }

        public int Port { get; set; }

        public string? CheckSum { get; set; }

        // Tracking field for diagnostic purposes
        public int StatusUpdateCount { get; set; } = 0; // Count of times this status has been updated
    }
}