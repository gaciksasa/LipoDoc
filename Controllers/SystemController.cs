using DeviceDataCollector.Models;
using DeviceDataCollector.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace DeviceDataCollector.Controllers
{
    [Authorize(Policy = "RequireAdminRole")]
    public class SystemController : Controller
    {
        private readonly NetworkUtilityService _networkUtilityService;
        private readonly IConfiguration _configuration;
        private readonly DatabaseConfigService _databaseConfigService;
        private readonly ILogger<SystemController> _logger;
        private readonly IWebHostEnvironment _environment;

        public SystemController(
            NetworkUtilityService networkUtilityService,
            IConfiguration configuration,
            DatabaseConfigService databaseConfigService,
            ILogger<SystemController> logger,
            IWebHostEnvironment environment)  
        {
            _networkUtilityService = networkUtilityService;
            _configuration = configuration;
            _databaseConfigService = databaseConfigService;
            _logger = logger;
            _environment = environment;  
        }

        public IActionResult Network()
        {
            var model = new NetworkViewModel
            {
                IPAddresses = _networkUtilityService.GetAllIPv4Addresses(),
                CurrentTcpServerIP = _configuration.GetValue<string>("TCPServer:IPAddress"),
                CurrentTcpServerPort = _configuration.GetValue<int>("TCPServer:Port", 5000)
            };

            return View(model);
        }

        public IActionResult Database()
        {
            // Get the current connection string from configuration
            var connectionString = _configuration.GetConnectionString("DefaultConnection");

            // Parse the connection string to extract individual components
            var connectionInfo = ParseConnectionString(connectionString);

            // Check write permissions
            string appSettingsPath = Path.Combine(_environment.ContentRootPath, "appsettings.json");
            try
            {
                using (FileStream fs = new FileStream(appSettingsPath,
                       FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // If we get here, we have permissions
                    ViewBag.HasWritePermission = true;
                }
            }
            catch
            {
                ViewBag.HasWritePermission = false;
                ViewBag.PermissionWarning = "Warning: The application does not have write permission to appsettings.json. Changes may not be saved.";
            }

            return View(connectionInfo);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Database(DatabaseConnectionViewModel model)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    // Build the connection string from the model
                    string connectionString = model.BuildConnectionString();

                    // Test the connection before saving
                    var testResult = await _databaseConfigService.TestConnectionAsync(connectionString);

                    // Update the configuration file regardless of connection test result
                    bool updated = await _databaseConfigService.UpdateConnectionStringAsync(connectionString);

                    if (updated)
                    {
                        if (testResult.Success)
                        {
                            TempData["SuccessMessage"] = "Database settings saved successfully and connection test passed. Application restart may be required for changes to take effect.";
                        }
                        else
                        {
                            // Still save the settings, but warn user about connection failure
                            TempData["WarningMessage"] = "Database settings saved, but connection test failed: " + testResult.Message + ". Please check your settings and ensure the database server is running.";
                        }
                    }
                    else
                    {
                        TempData["ErrorMessage"] = "Failed to save settings. Make sure the application has write access to appsettings.json.";
                    }

                    return RedirectToAction(nameof(Database));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating database configuration");
                    ModelState.AddModelError("", $"Error: {ex.Message}");
                }
            }

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> TestConnection([FromBody] DatabaseConnectionViewModel model)
        {
            try
            {
                string connectionString = model.BuildConnectionString();
                var result = await _databaseConfigService.TestConnectionAsync(connectionString);

                return Json(new
                {
                    success = result.Success,
                    message = result.Message,
                    version = result.Version,
                    size = result.SizeInBytes.HasValue ? FormatFileSize(result.SizeInBytes.Value) : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing database connection");
                return Json(new { success = false, message = ex.Message });
            }
        }

        // Method for the Backup settings page (placeholder)
        public IActionResult Backup()
        {
            // This is a placeholder method
            return View();
        }

        // Helper method to parse a MySQL connection string into components
        private DatabaseConnectionViewModel ParseConnectionString(string connectionString)
        {
            var model = new DatabaseConnectionViewModel
            {
                Server = "localhost", // Default values
                Port = 3306,
                Database = "devicedata",
                Username = "root",
                Password = "root"
            };

            _logger.LogDebug($"Connection string to parse: {connectionString}");

            // Parse the connection string
            if (!string.IsNullOrEmpty(connectionString))
            {
                var parts = connectionString.Split(';');
                foreach (var part in parts)
                {
                    if (string.IsNullOrEmpty(part)) continue;

                    var keyValue = part.Split('=');
                    if (keyValue.Length != 2) continue;

                    var key = keyValue[0].Trim().ToLower();
                    var value = keyValue[1].Trim();

                    switch (key)
                    {
                        case "server":
                            model.Server = value;
                            break;
                        case "port":
                            if (int.TryParse(value, out int port))
                                model.Port = port;
                            break;
                        case "database":
                            model.Database = value;
                            break;
                        case "user":
                        case "userid":
                        case "username":
                            model.Username = value;
                            break;
                        case "password":
                        case "pwd":
                            model.Password = value;
                            break;
                    }
                }

                _logger.LogDebug($"Parsed values - Server: {model.Server}, Database: {model.Database}, Username: {model.Username}, Password: {model.Password}");
            }

            return model;
        }

        // Helper method to format file size
        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    // NetworkViewModel remains in this file as it's only used here
    public class NetworkViewModel
    {
        public List<IPAddressInfo> IPAddresses { get; set; }
        public string CurrentTcpServerIP { get; set; }
        public int CurrentTcpServerPort { get; set; }
    }
}