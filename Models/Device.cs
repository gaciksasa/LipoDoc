using System.ComponentModel.DataAnnotations;

namespace DeviceDataCollector.Models
{
    public class Device
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string? SerialNumber { get; set; } // Serial Number (SN) of the device

        public string? Name { get; set; } // Friendly name for the device

        public string? Location { get; set; } // Location of the device

        public DateTime? LastConnectionTime { get; set; } // Last time the device connected - Updated by status messages

        public DateTime RegisteredDate { get; set; } = DateTime.Now; // Using local time instead of UTC

        public bool IsActive { get; set; } = true; // Active by default when registered via status message

        public string? Notes { get; set; }
    }
}