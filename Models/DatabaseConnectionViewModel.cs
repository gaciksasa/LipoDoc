using System.ComponentModel.DataAnnotations;

namespace DeviceDataCollector.Models
{
    public class DatabaseConnectionViewModel
    {
        [Required(ErrorMessage = "Server is required")]
        [Display(Name = "Server")]
        public string Server { get; set; }

        [Required(ErrorMessage = "Port is required")]
        [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535")]
        [Display(Name = "Port")]
        public int Port { get; set; }

        [Required(ErrorMessage = "Database name is required")]
        [Display(Name = "Database")]
        public string Database { get; set; }

        [Required(ErrorMessage = "Username is required")]
        [Display(Name = "Username")]
        public string Username { get; set; }

        [Required(ErrorMessage = "Password is required")]
        [Display(Name = "Password")]
        public string Password { get; set; }

        // Method to build connection string from components
        public string BuildConnectionString()
        {
            return $"server={Server};port={Port};database={Database};user={Username};password={Password}";
        }
    }
}