using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using DeviceDataCollector.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using System.Text;

namespace DeviceDataCollector.Controllers
{
    [Authorize(Policy = "RequireAdminRole")]
    public class DeviceConfigurationController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly DeviceConfigurationService _configService;
        private readonly ILogger<DeviceConfigurationController> _logger;
        private readonly IConfiguration _configuration;

        public DeviceConfigurationController(
    ApplicationDbContext context,
    DeviceConfigurationService configService,
    ILogger<DeviceConfigurationController> logger,
    IConfiguration configuration)
        {
            _context = context;
            _configService = configService;
            _logger = logger;
            _configuration = configuration;
        }

        // GET: DeviceConfiguration/Configure/5
        public async Task<IActionResult> Configure(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var device = await _context.Devices.FindAsync(id);
            if (device == null)
            {
                return NotFound();
            }

            // Get the latest status for this device to get its IP and port
            var currentStatus = await _context.CurrentDeviceStatuses
                .FirstOrDefaultAsync(s => s.DeviceId == device.SerialNumber);

            if (currentStatus == null)
            {
                // Fall back to DeviceStatuses
                var latestStatus = await _context.DeviceStatuses
                    .Where(s => s.DeviceId == device.SerialNumber)
                    .OrderByDescending(s => s.Timestamp)
                    .FirstOrDefaultAsync();

                if (latestStatus == null)
                {
                    TempData["ErrorMessage"] = "Unable to retrieve device status information. Make sure the device is connected.";
                    return RedirectToAction("Details", "Devices", new { id = id });
                }

                ViewBag.DeviceInfo = device;
                ViewBag.IPAddress = latestStatus.IPAddress;
                ViewBag.Port = latestStatus.Port;
            }
            else
            {
                ViewBag.DeviceInfo = device;
                ViewBag.IPAddress = currentStatus.IPAddress;
                ViewBag.Port = currentStatus.Port;
                ViewBag.StatusUpdateCount = currentStatus.StatusUpdateCount;
            }

            return View();
        }

        // POST: DeviceConfiguration/RequestConfiguration
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestConfiguration(int deviceId, string ipAddress, int port)
        {
            var device = await _context.Devices.FindAsync(deviceId);
            if (device == null)
            {
                return NotFound();
            }

            _logger.LogInformation($"===== Starting configuration request for device {device.Name} ({device.SerialNumber}) =====");

            // Log detailed connection information
            await LogDeviceConnectionInfo(deviceId, device.SerialNumber, ipAddress, port);

            // Check if device is currently active
            if (!device.IsActive)
            {
                _logger.LogWarning($"Attempting to fetch configuration for inactive device: {device.SerialNumber}");
            }

            _logger.LogInformation($"Passing request to DeviceConfigurationService");
            var config = await _configService.GetDeviceConfigurationAsync(device.SerialNumber, ipAddress, port);

            if (config == null)
            {
                _logger.LogWarning($"Configuration request failed for device {device.SerialNumber}");
                TempData["ErrorMessage"] = "Failed to retrieve device configuration. Please make sure the device is online and connected.";
                return RedirectToAction("Configure", new { id = deviceId });
            }

            _logger.LogInformation($"Successfully retrieved configuration for device {device.SerialNumber}");
            TempData["SuccessMessage"] = "Device configuration successfully retrieved.";

            // Store these for use in the Edit view
            TempData["DeviceId"] = deviceId;
            TempData["IPAddress"] = ipAddress;
            TempData["Port"] = port;

            return View("Edit", config);
        }

        // POST: DeviceConfiguration/Edit
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(DeviceConfigurationViewModel model, string ipAddress, int port)
        {
            if (ModelState.IsValid)
            {
                var success = await _configService.SendDeviceConfigurationAsync(model, ipAddress, port);

                if (success)
                {
                    TempData["SuccessMessage"] = "Device configuration updated successfully.";

                    // Update device name in the database if it's different
                    var device = await _context.Devices.FirstOrDefaultAsync(d => d.SerialNumber == model.DeviceId);
                    if (device != null && device.Name != model.Profiles[0].Name && !string.IsNullOrEmpty(model.Profiles[0].Name))
                    {
                        device.Name = model.Profiles[0].Name;
                        await _context.SaveChangesAsync();
                    }

                    return RedirectToAction("Details", "Devices", new { id = device?.Id });
                }
                else
                {
                    TempData["ErrorMessage"] = "Failed to update device configuration. Please try again.";
                }
            }

            return View(model);
        }

        // POST: DeviceConfiguration/ChangeSerialNumber
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangeSerialNumber(int deviceId, string oldSerialNumber, string newSerialNumber, string ipAddress, int port)
        {
            var device = await _context.Devices.FindAsync(deviceId);
            if (device == null)
            {
                return NotFound();
            }

            if (string.IsNullOrEmpty(newSerialNumber))
            {
                TempData["ErrorMessage"] = "New serial number cannot be empty.";
                return RedirectToAction("Configure", new { id = deviceId });
            }

            var success = await _configService.ChangeDeviceSerialNumberAsync(oldSerialNumber, newSerialNumber, ipAddress, port);

            if (success)
            {
                // Update device record in database with new serial number
                device.SerialNumber = newSerialNumber;
                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = $"Device serial number changed successfully from {oldSerialNumber} to {newSerialNumber}.";
                return RedirectToAction("Details", "Devices", new { id = deviceId });
            }
            else
            {
                TempData["ErrorMessage"] = "Failed to change device serial number. Please try again.";
                return RedirectToAction("Configure", new { id = deviceId });
            }
        }

        // POST: DeviceConfiguration/SyncTime
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncTime(int deviceId, string serialNumber, string ipAddress, int port)
        {
            var device = await _context.Devices.FindAsync(deviceId);
            if (device == null)
            {
                return NotFound();
            }

            var success = await _configService.SynchronizeDeviceTimeAsync(serialNumber, ipAddress, port);

            if (success)
            {
                TempData["SuccessMessage"] = "Device time synchronized successfully.";
            }
            else
            {
                TempData["ErrorMessage"] = "Failed to synchronize device time. Please try again.";
            }

            return RedirectToAction("Configure", new { id = deviceId });
        }

        private async Task LogDeviceConnectionInfo(int deviceId, string deviceSerialNumber, string ipAddress, int port)
        {
            try
            {
                // Get the latest statuses
                var currentStatus = await _context.CurrentDeviceStatuses
                    .FirstOrDefaultAsync(s => s.DeviceId == deviceSerialNumber);

                var latestStatusHistory = await _context.DeviceStatuses
                    .Where(s => s.DeviceId == deviceSerialNumber)
                    .OrderByDescending(s => s.Timestamp)
                    .Take(1)
                    .ToListAsync();

                _logger.LogInformation($"Device connection details - ID: {deviceId}, SN: {deviceSerialNumber}, Target: {ipAddress}:{port}");

                if (currentStatus != null)
                {
                    _logger.LogInformation($"Current status record - Last updated: {currentStatus.Timestamp}, Available data: {currentStatus.AvailableData}, Update count: {currentStatus.StatusUpdateCount}");
                }
                else
                {
                    _logger.LogInformation("No current status record found");
                }

                if (latestStatusHistory.Any())
                {
                    var status = latestStatusHistory.First();
                    _logger.LogInformation($"Latest status history - Timestamp: {status.Timestamp}, Status: {status.Status}, IP: {status.IPAddress}:{status.Port}");
                }
                else
                {
                    _logger.LogInformation("No status history records found");
                }

                // Check if there are active TCP connections
                var connectionInfo = await _context.Database.ExecuteSqlRawAsync(
                    "SELECT connection_id FROM information_schema.processlist WHERE host LIKE CONCAT(@ipAddress, '%')",
                    new MySqlParameter("@ipAddress", ipAddress));

                _logger.LogInformation($"Database connections from device IP: {connectionInfo} active connections");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while logging device connection information");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DiagnosticCheck(int deviceId, string ipAddress, int port)
        {
            var device = await _context.Devices.FindAsync(deviceId);
            if (device == null)
            {
                return NotFound();
            }

            _logger.LogInformation($"===== Running diagnostic check for device {device.SerialNumber} =====");

            // Log current device state
            await LogDeviceConnectionInfo(deviceId, device.SerialNumber, ipAddress, port);

            // Check network connectivity
            bool canConnect = await _configService.CheckDeviceConnectivityAsync(ipAddress, port);

            // Get TCP server status
            var tcpServerService = HttpContext.RequestServices.GetService<TCPServerService>();
            string serverIpAddress = _configuration.GetValue<string>("TCPServer:IPAddress") ?? "Unknown";
            int serverPort = _configuration.GetValue<int>("TCPServer:Port");

            // Get device connection history
            var recentStatusUpdates = await _context.DeviceStatuses
                .Where(s => s.DeviceId == device.SerialNumber)
                .OrderByDescending(s => s.Timestamp)
                .Take(5)
                .ToListAsync();

            var diagnosticResult = new StringBuilder();
            diagnosticResult.AppendLine($"Diagnostic results for {device.SerialNumber} ({ipAddress}:{port}):");
            diagnosticResult.AppendLine($"- TCP connection test: {(canConnect ? "SUCCESS" : "FAILED")}");
            diagnosticResult.AppendLine($"- TCP server listening at {serverIpAddress}:{serverPort}");
            diagnosticResult.AppendLine($"- Device active status: {(device.IsActive ? "Active" : "Inactive")}");
            diagnosticResult.AppendLine($"- Last connection time: {(device.LastConnectionTime?.ToString() ?? "Never")}");

            if (recentStatusUpdates.Any())
            {
                diagnosticResult.AppendLine("- Recent status updates:");
                foreach (var status in recentStatusUpdates)
                {
                    diagnosticResult.AppendLine($"  * {status.Timestamp}: Status={status.Status}, AvailableData={status.AvailableData}");
                }
            }
            else
            {
                diagnosticResult.AppendLine("- No recent status updates found");
            }

            if (canConnect)
            {
                TempData["SuccessMessage"] = "Device is reachable. See diagnostic information below.";
            }
            else
            {
                TempData["ErrorMessage"] = "Cannot establish a direct connection to the device. The device may not be accepting incoming connections.";
            }

            TempData["DiagnosticResults"] = diagnosticResult.ToString();

            return RedirectToAction("Configure", new { id = deviceId });
        }
    }
}