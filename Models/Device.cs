using System.ComponentModel.DataAnnotations;

namespace DeviceDataCollector.Models
{
    public class Device
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string SerialNumber { get; set; } // Serial Number (SN) of the device

        public string? Name { get; set; } // Friendly name for the device

        public string? Location { get; set; } // Location of the device

        public DateTime? LastConnectionTime { get; set; } // Last time the device connected

        public DateTime RegisteredDate { get; set; } = DateTime.UtcNow;

        public bool IsActive { get; set; } = true;

        public string? Notes { get; set; }
    }
}