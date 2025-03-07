using System.ComponentModel.DataAnnotations;

namespace DeviceDataCollector.Models
{
    public class ProfileViewModel
    {
        public int Id { get; set; }

        [Required]
        [Display(Name = "Username")]
        public string Username { get; set; }

        [Display(Name = "Full Name")]
        [StringLength(100, ErrorMessage = "Full name cannot be longer than 100 characters")]
        public string? FullName { get; set; }

        [Display(Name = "Email Address")]
        [EmailAddress(ErrorMessage = "Please enter a valid email address")]
        [StringLength(100, ErrorMessage = "Email cannot be longer than 100 characters")]
        public string? Email { get; set; }

        [Display(Name = "Role")]
        public string Role { get; set; }

        [Display(Name = "Account Created")]
        public DateTime CreatedAt { get; set; }

        [Display(Name = "Last Login")]
        public DateTime? LastLogin { get; set; }

        // Password change fields
        [Display(Name = "Current Password")]
        [StringLength(50, MinimumLength = 6, ErrorMessage = "Password must be between 6 and 50 characters")]
        public string? CurrentPassword { get; set; }

        [Display(Name = "New Password")]
        [StringLength(50, MinimumLength = 6, ErrorMessage = "Password must be between 6 and 50 characters")]
        public string? NewPassword { get; set; }

        [Display(Name = "Confirm New Password")]
        [Compare("NewPassword", ErrorMessage = "The new password and confirmation password do not match.")]
        public string? ConfirmPassword { get; set; }
    }
}