using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace DeviceDataCollector.Models
{
    public class DeviceConfigurationViewModel
    {
        public string DeviceId { get; set; }

        [Display(Name = "Software Version")]
        public string SoftwareVersion { get; set; }

        [Display(Name = "Hardware Version")]
        public string HardwareVersion { get; set; }

        [Display(Name = "Server Address")]
        [Required]
        public string ServerAddress { get; set; }

        [Display(Name = "Device IP Address")]
        [Required]
        public string DeviceIPAddress { get; set; }

        [Display(Name = "Subnet Mask")]
        [Required]
        public string SubnetMask { get; set; }

        [Display(Name = "Remote Port")]
        [Required]
        [Range(1, 65535)]
        public int RemotePort { get; set; }

        [Display(Name = "Local Port")]
        [Required]
        [Range(1, 65535)]
        public int LocalPort { get; set; }

        [Display(Name = "Lipemic Index 1")]
        [Required]
        [Range(0, 10000)]
        public int LipemicIndex1 { get; set; }

        [Display(Name = "Lipemic Index 2")]
        [Required]
        [Range(0, 10000)]
        public int LipemicIndex2 { get; set; }

        [Display(Name = "Lipemic Index 3")]
        [Required]
        [Range(0, 10000)]
        public int LipemicIndex3 { get; set; }

        [Display(Name = "Enable Data Transfer")]
        public bool TransferModeEnabled { get; set; }

        [Display(Name = "Enable Barcode Mode")]
        public bool BarcodesModeEnabled { get; set; }

        [Display(Name = "Enable Operator ID")]
        public bool OperatorIdEnabled { get; set; }

        [Display(Name = "Enable LOT Number")]
        public bool LotNumberEnabled { get; set; }

        [Display(Name = "WiFi Network Name (SSID)")]
        public string NetworkName { get; set; }

        [Display(Name = "WiFi Mode")]
        public string WifiMode { get; set; }

        [Display(Name = "Security Type")]
        public string SecurityType { get; set; }

        [Display(Name = "WiFi Password")]
        public string WifiPassword { get; set; }

        public List<TubeProfile> Profiles { get; set; } = new List<TubeProfile>();

        public List<BarcodeConfiguration> BarcodeConfigurations { get; set; } = new List<BarcodeConfiguration>();

        public DeviceConfigurationViewModel()
        {
            // Initialize with 20 profiles
            for (int i = 0; i < 20; i++)
            {
                Profiles.Add(new TubeProfile { Name = $"Tube{i + 1}", RefCode = "", OffsetValue = 0 });
            }

            // Initialize with 6 barcode configurations
            for (int i = 0; i < 6; i++)
            {
                BarcodeConfigurations.Add(new BarcodeConfiguration { MinLength = 0, MaxLength = 16, StartCode = "", StopCode = "" });
            }
        }
    }

    public class TubeProfile
    {
        [Display(Name = "Profile Name")]
        public string Name { get; set; }

        [Display(Name = "REF Code")]
        public string RefCode { get; set; }

        [Display(Name = "Offset Value")]
        public int OffsetValue { get; set; }
    }

    public class BarcodeConfiguration
    {
        [Display(Name = "Minimum Length")]
        [Range(0, 100)]
        public int MinLength { get; set; }

        [Display(Name = "Maximum Length")]
        [Range(0, 100)]
        public int MaxLength { get; set; }

        [Display(Name = "Start Code")]
        public string StartCode { get; set; }

        [Display(Name = "Stop Code")]
        public string StopCode { get; set; }
    }
}