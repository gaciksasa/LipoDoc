using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeviceDataCollector.Models
{
    public class DeviceSetup
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string DeviceId { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; }

        public string? SoftwareVersion { get; set; }
        public string? HardwareVersion { get; set; }
        public string? ServerAddress { get; set; }
        public string? DeviceIpAddress { get; set; }
        public string? SubnetMask { get; set; }
        public int RemotePort { get; set; }
        public int LocalPort { get; set; }

        // Lipemic threshold values
        public int LipemicIndex1 { get; set; }
        public int LipemicIndex2 { get; set; }
        public int LipemicIndex3 { get; set; }

        // Settings
        public bool TransferMode { get; set; }
        public bool BarcodesMode { get; set; }
        public bool OperatorIdEnabled { get; set; }
        public bool LotNumberEnabled { get; set; }

        // Network settings
        public string? NetworkName { get; set; }
        public string? WifiMode { get; set; }
        public string? SecurityType { get; set; }
        public string? WifiPassword { get; set; }

        // Raw response for debugging
        public string? RawResponse { get; set; } = "Manually configured setup";

        // Profile data stored as JSON
        public string? ProfilesJson { get; set; }

        // Barcode config stored as JSON
        public string? BarcodesJson { get; set; }

        [NotMapped]
        public List<DeviceProfile> Profiles { get; set; } = new List<DeviceProfile>();

        [NotMapped]
        public List<BarcodeConfig> BarcodeConfigs { get; set; } = new List<BarcodeConfig>();
    }

    public class DeviceProfile
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? RefCode { get; set; }
        public int OffsetValue { get; set; }
    }

    public class BarcodeConfig
    {
        public int Index { get; set; }
        public int MinLength { get; set; }
        public int MaxLength { get; set; }
        public string StartCode { get; set; } = string.Empty;
        public string StopCode { get; set; } = string.Empty;
    }
}