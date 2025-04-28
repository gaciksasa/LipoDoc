using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeviceDataCollector.Models
{
    public class DonationsData
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string DeviceId { get; set; } // Serial Number (SN) of the device

        public DateTime Timestamp { get; set; } // Time of reading

        public string MessageType { get; set; } // "#S" for status, "#D" for data, etc.

        public string? RawPayload { get; set; } // Raw message content

        public string? IPAddress { get; set; }

        public int Port { get; set; }

        // Status message specific fields
        public int? DeviceStatus { get; set; } // 0=IDLE, 1=Process in progress, 2=Process completed
        public int? AvailableData { get; set; } // Number of readings buffered in the device

        // Reading result fields
        public bool IsBarcodeMode { get; set; } // Whether barcode mode is enabled

        // Barcode fields
        public string? RefCode { get; set; }
        public string? DonationIdBarcode { get; set; }
        public string? OperatorIdBarcode { get; set; }
        public string? LotNumber { get; set; }

        // Lipemic test results
        public int? LipemicValue { get; set; } // iFinallDisp - value of lipemic reading
        public string? LipemicGroup { get; set; } // LipGroup - I, II, III, or IV
        public string? LipemicStatus { get; set; } // IsLipemic - LIPEMIC or PASSED

        // Checksum
        public string? CheckSum { get; set; }

        // Export tracking
        public bool Exported { get; set; } = false;

        [NotMapped]
        public bool IsLipemic => LipemicStatus == "LIPEMIC";       
    }
}