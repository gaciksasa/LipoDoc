using DeviceDataCollector.Models;
using DeviceDataCollector.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTcpServerIp(string ipAddress)
        {
            try
            {
                string appSettingsPath = Path.Combine(_environment.ContentRootPath, "appsettings.json");

                // Read the current content
                string json = await System.IO.File.ReadAllTextAsync(appSettingsPath);

                // Parse JSON
                using JsonDocument doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Create a new JSON document with the updated IP
                using var ms = new MemoryStream();
                using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });

                writer.WriteStartObject();

                // Copy all properties from the root
                foreach (var property in root.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);

                    if (property.Name == "TCPServer")
                    {
                        writer.WriteStartObject();

                        // Write all properties of TCPServer
                        foreach (var serverProp in property.Value.EnumerateObject())
                        {
                            writer.WritePropertyName(serverProp.Name);

                            if (serverProp.Name == "IPAddress")
                            {
                                writer.WriteStringValue(ipAddress);
                            }
                            else
                            {
                                serverProp.Value.WriteTo(writer);
                            }
                        }

                        writer.WriteEndObject();
                    }
                    else
                    {
                        property.Value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
                writer.Flush();

                string newJson = Encoding.UTF8.GetString(ms.ToArray());

                // Write the changes back to the file
                await System.IO.File.WriteAllTextAsync(appSettingsPath, newJson);

                // Force configuration reload to reflect changes immediately
                ((IConfigurationRoot)_configuration).Reload();

                TempData["SuccessMessage"] = $"TCP Server IP address updated to {ipAddress}. Application restart may be required for changes to take effect.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating TCP Server IP address");
                TempData["ErrorMessage"] = $"Failed to update TCP Server IP: {ex.Message}";
            }

            // This will cause the Network action to run again and refresh all data
            return RedirectToAction(nameof(Network));
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

        public async Task<IActionResult> Backup()
        {
            var backupService = HttpContext.RequestServices.GetRequiredService<DatabaseBackupService>();
            var backups = await backupService.GetBackupListAsync();

            // Get scheduled backup settings
            bool scheduledEnabled = _configuration.GetValue<bool>("DatabaseBackup:Scheduled:Enabled", false);
            string scheduledTime = _configuration.GetValue<string>("DatabaseBackup:Scheduled:Time", "03:00");
            int retentionCount = _configuration.GetValue<int>("DatabaseBackup:Scheduled:RetentionCount", 7);

            // Log the configuration for debugging
            _logger.LogInformation($"Scheduled backup enabled from config: {scheduledEnabled}");

            // Calculate statistics
            long totalSize = backups.Sum(b => b.FileSize);

            var model = new BackupViewModel
            {
                Backups = backups,
                IsScheduledBackupEnabled = scheduledEnabled,
                ScheduledBackupTime = scheduledTime,
                ScheduledBackupRetention = retentionCount,
                BackupDirectory = Path.Combine(_environment.ContentRootPath, "backups"),
                TotalBackupSize = totalSize,
                BackupCount = backups.Count
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateBackup(BackupViewModel model)
        {
            var backupService = HttpContext.RequestServices.GetRequiredService<DatabaseBackupService>();

            var result = await backupService.CreateBackupAsync(model.Description);

            if (result.Success)
            {
                TempData["SuccessMessage"] = $"Backup created successfully: {result.FileName}";
            }
            else
            {
                TempData["ErrorMessage"] = $"Backup failed: {result.ErrorMessage}";
            }

            return RedirectToAction(nameof(Backup));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteBackup(string fileName)
        {
            var backupService = HttpContext.RequestServices.GetRequiredService<DatabaseBackupService>();

            bool success = await backupService.DeleteBackupAsync(fileName);

            if (success)
            {
                TempData["SuccessMessage"] = $"Backup {fileName} deleted successfully";
            }
            else
            {
                TempData["ErrorMessage"] = $"Failed to delete backup {fileName}";
            }

            return RedirectToAction(nameof(Backup));
        }

        public async Task<IActionResult> DownloadBackup(string fileName)
        {
            var backupService = HttpContext.RequestServices.GetRequiredService<DatabaseBackupService>();

            string filePath = backupService.GetBackupFilePath(fileName);

            if (!System.IO.File.Exists(filePath))
            {
                TempData["ErrorMessage"] = $"Backup file not found: {fileName}";
                return RedirectToAction(nameof(Backup));
            }

            var fileBytes = await System.IO.File.ReadAllBytesAsync(filePath);

            return File(fileBytes, "application/gzip", fileName);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateScheduledBackupSettings(ScheduledBackupSettingsViewModel model)
        {
            try
            {
                string appSettingsPath = Path.Combine(_environment.ContentRootPath, "appsettings.json");

                // Read the current settings
                string json = await System.IO.File.ReadAllTextAsync(appSettingsPath);

                // Parse JSON
                using JsonDocument doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Create a new JSON document with updated settings
                using var ms = new MemoryStream();
                using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });

                writer.WriteStartObject();

                // Copy all properties from the root
                bool databaseBackupSectionExists = false;

                foreach (var property in root.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);

                    if (property.Name == "DatabaseBackup")
                    {
                        databaseBackupSectionExists = true;
                        writer.WriteStartObject();

                        // Write all properties of DatabaseBackup
                        var scheduledSectionExists = false;

                        foreach (var backupProp in property.Value.EnumerateObject())
                        {
                            writer.WritePropertyName(backupProp.Name);

                            if (backupProp.Name == "Scheduled")
                            {
                                scheduledSectionExists = true;
                                writer.WriteStartObject();

                                // Update the scheduled backup settings
                                writer.WriteBoolean("Enabled", model.Enabled);
                                writer.WriteString("Time", model.Time);
                                writer.WriteNumber("RetentionCount", model.RetentionCount);
                                writer.WriteNumber("IntervalHours", 24); // Keep default interval

                                writer.WriteEndObject();
                            }
                            else
                            {
                                backupProp.Value.WriteTo(writer);
                            }
                        }

                        // Add Scheduled section if it doesn't exist
                        if (!scheduledSectionExists)
                        {
                            writer.WritePropertyName("Scheduled");
                            writer.WriteStartObject();
                            writer.WriteBoolean("Enabled", model.Enabled);
                            writer.WriteString("Time", model.Time);
                            writer.WriteNumber("RetentionCount", model.RetentionCount);
                            writer.WriteNumber("IntervalHours", 24);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndObject();
                    }
                    else
                    {
                        property.Value.WriteTo(writer);
                    }
                }

                // Add DatabaseBackup section if it doesn't exist
                if (!databaseBackupSectionExists)
                {
                    writer.WritePropertyName("DatabaseBackup");
                    writer.WriteStartObject();

                    writer.WritePropertyName("Scheduled");
                    writer.WriteStartObject();
                    writer.WriteBoolean("Enabled", model.Enabled);
                    writer.WriteString("Time", model.Time);
                    writer.WriteNumber("RetentionCount", model.RetentionCount);
                    writer.WriteNumber("IntervalHours", 24);
                    writer.WriteEndObject();

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.Flush();

                string newJson = Encoding.UTF8.GetString(ms.ToArray());

                // Write the changes back to the file
                await System.IO.File.WriteAllTextAsync(appSettingsPath, newJson);

                // Force configuration reload
                ((IConfigurationRoot)_configuration).Reload();

                // If we have a service instance in DI, reload its configuration too
                var scheduledService = HttpContext.RequestServices.GetService<ScheduledBackupService>();
                if (scheduledService != null)
                {
                    scheduledService.ReloadConfiguration();
                    scheduledService.NotifyConfigurationChanged();
                    _logger.LogInformation("Notified scheduled backup service of configuration changes");
                }

                TempData["SuccessMessage"] = "Scheduled backup settings updated successfully.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating scheduled backup settings");
                TempData["ErrorMessage"] = $"Failed to update settings: {ex.Message}";
            }

            return RedirectToAction(nameof(Backup));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RestoreBackup(string fileName)
        {
            // Add a confirmation step for safety
            string confirmKey = $"restore_{fileName}";

            // Check if this is a confirmed restore
            if (TempData[confirmKey] == null)
            {
                // First request - store confirmation key in TempData and show confirmation page
                TempData[confirmKey] = true;
                TempData["RestoreFileName"] = fileName;
                TempData["WarningMessage"] = "Please confirm that you want to restore this backup. This will OVERWRITE your current database!";
                return RedirectToAction(nameof(Backup));
            }

            // This is a confirmed restore - clear the confirmation flag
            TempData.Remove(confirmKey);

            var backupService = HttpContext.RequestServices.GetRequiredService<DatabaseBackupService>();

            var result = await backupService.RestoreBackupAsync(fileName);

            if (result.Success)
            {
                TempData["SuccessMessage"] = "Database restored successfully from backup. You may need to restart the application.";
            }
            else
            {
                TempData["ErrorMessage"] = $"Error restoring database: {result.ErrorMessage}";
            }

            return RedirectToAction(nameof(Backup));
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