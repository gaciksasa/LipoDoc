using System.ComponentModel.DataAnnotations;

namespace DeviceDataCollector.Models
{
    public class DeviceStatus
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string DeviceId { get; set; } // Serial Number (SN) of the device

        public DateTime Timestamp { get; set; } // Time of the status update

        public DateTime DeviceTimestamp { get; set; } // Time reported by the device

        public int Status { get; set; } // 0=IDLE, 1=Process in progress, 2=Process completed

        public int AvailableData { get; set; } // Number of readings buffered in the device

        public string? RawPayload { get; set; } // Raw message content

        public string? IPAddress { get; set; }

        public int Port { get; set; }

        public string? CheckSum { get; set; }
    }
}