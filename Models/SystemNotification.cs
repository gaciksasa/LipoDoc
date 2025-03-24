using System;
using System.ComponentModel.DataAnnotations;

namespace DeviceDataCollector.Models
{
    public class SystemNotification
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string Type { get; set; }

        [Required]
        public string Message { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.Now;

        public bool Read { get; set; } = false;

        public string? RelatedEntityId { get; set; }
    }
}