using System.ComponentModel.DataAnnotations;

namespace DeviceDataCollector.Models
{
    public class User
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Username { get; set; }

        [Required]
        public string PasswordHash { get; set; }

        [Required]
        public string Role { get; set; } // "Admin" or "User"

        public string? FullName { get; set; }

        public string? Email { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now; // Using local time instead of UTC

        public DateTime? LastLogin { get; set; }
    }
}