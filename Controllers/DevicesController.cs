using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using DeviceDataCollector.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeviceDataCollector.Controllers
{
    [Authorize]
    public class DevicesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<DevicesController> _logger;

        public DevicesController(
            ApplicationDbContext context,
            ILogger<DevicesController> logger)
        {
            _context = context;
            _logger = logger;
        }

        private void AddSerialChangeQueuedNotification(string serialNumber, string newSerialNumber)
        {
            TempData["SerialChangeQueued"] = true;
            TempData["QueuedSerial"] = serialNumber;
            TempData["QueuedNewSerial"] = newSerialNumber;

            // The timestamp can help identify/differentiate notifications
            TempData["QueuedTimestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        // GET: Devices
        public async Task<IActionResult> Index()
        {
            try
            {
                // Check for serial update notifications
                var serialUpdateNotifications = await _context.SystemNotifications
                    .Where(n => n.Type == "SerialNumberUpdate" && !n.Read)
                    .OrderByDescending(n => n.Timestamp)
                    .ToListAsync();

                // Mark them as read
                foreach (var notification in serialUpdateNotifications)
                {
                    notification.Read = true;
                }

                if (serialUpdateNotifications.Any())
                {
                    await _context.SaveChangesAsync();
                }

                // Store as a list in ViewBag, not as IQueryable
                ViewBag.SerialUpdateNotifications = serialUpdateNotifications;
                ViewBag.HasSerialUpdateNotifications = serialUpdateNotifications.Any();
            }
            catch (Exception ex)
            {
                // Log the error but continue
                _logger.LogError(ex, "Error accessing SystemNotifications");
                ViewBag.SerialUpdateNotifications = new List<SystemNotification>();
                ViewBag.HasSerialUpdateNotifications = false;
            }

            // Get distinct devices to avoid duplicates with old/new serial numbers
            // We achieve this by getting the latest set of devices which should have updated serial numbers
            var devices = await _context.Devices
                .OrderByDescending(d => d.LastConnectionTime)
                .ToListAsync();

            // Check if it's an AJAX request
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                // Return the partial view for AJAX refresh
                return PartialView("_DeviceList", devices);
            }

            return View(devices);
        }

        // GET: Devices/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var device = await _context.Devices
                .FirstOrDefaultAsync(m => m.Id == id);

            if (device == null)
            {
                return NotFound();
            }

            // Get the latest status for this device from the CurrentDeviceStatuses table
            var currentStatus = await _context.CurrentDeviceStatuses
                .FirstOrDefaultAsync(s => s.DeviceId == device.SerialNumber);

            // If no current status exists, fall back to the DeviceStatuses table (for backward compatibility)
            if (currentStatus == null)
            {
                var latestStatus = await _context.DeviceStatuses
                    .Where(s => s.DeviceId == device.SerialNumber)
                    .OrderByDescending(s => s.Timestamp)
                    .FirstOrDefaultAsync();

                ViewBag.LatestStatus = latestStatus;
            }
            else
            {
                // Map CurrentDeviceStatus to DeviceStatus for compatibility with existing view
                var latestStatus = new DeviceStatus
                {
                    DeviceId = currentStatus.DeviceId,
                    Timestamp = currentStatus.Timestamp,
                    Status = currentStatus.Status,
                    AvailableData = currentStatus.AvailableData,
                    IPAddress = currentStatus.IPAddress,
                    Port = currentStatus.Port,
                    CheckSum = currentStatus.CheckSum
                };

                ViewBag.LatestStatus = latestStatus;
                ViewBag.StatusUpdateCount = currentStatus.StatusUpdateCount;

                bool isInSetupMode = ViewBag.LatestStatus != null && ViewBag.LatestStatus.Status == 3;
                ViewBag.IsInSetupMode = isInSetupMode;
            }

            // Get count of readings for this device
            var readingsCount = await _context.DonationsData
                .Where(d => d.DeviceId == device.SerialNumber)
                .CountAsync();

            ViewBag.ReadingsCount = readingsCount;

            return View(device);
        }

        // GET: Devices/Edit/5
        [Authorize(Policy = "RequireAdminRole")]
        public async Task<IActionResult> Edit(int? id)
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

            return View(device);
        }

        // POST: Devices/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "RequireAdminRole")]
        public async Task<IActionResult> Edit(int id, [Bind("Id,SerialNumber,Name,Location,IsActive,Notes")] Device device, string NewSerialNumber)
        {
            if (id != device.Id)
            {
                return NotFound();
            }

            // Get the existing device to preserve fields we don't want to update
            var existingDevice = await _context.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
            if (existingDevice == null)
            {
                return NotFound();
            }

            // Preserve these fields
            device.RegisteredDate = existingDevice.RegisteredDate;
            device.LastConnectionTime = existingDevice.LastConnectionTime;

            // IMPORTANT: Remove validation error for NewSerialNumber if it's empty
            if (string.IsNullOrEmpty(NewSerialNumber))
            {
                ModelState.Remove("NewSerialNumber");
            }

            // Check if a new serial number was provided
            bool serialNumberChangeRequested = !string.IsNullOrWhiteSpace(NewSerialNumber) &&
                                              NewSerialNumber != existingDevice.SerialNumber;

            // Check if the new serial already exists in another device
            if (serialNumberChangeRequested)
            {
                var duplicateDevice = await _context.Devices
                    .FirstOrDefaultAsync(d => d.SerialNumber == NewSerialNumber && d.Id != id);

                if (duplicateDevice != null)
                {
                    ModelState.AddModelError("NewSerialNumber", "This serial number is already assigned to another device.");
                    return View(device);
                }
            }

            if (ModelState.IsValid)
            {
                try
                {
                    if (serialNumberChangeRequested)
                    {
                        // Clear any existing serial change notifications for this device
                        if (TempData["SerialChangeQueued"] != null && TempData["QueuedSerial"].ToString() == existingDevice.SerialNumber)
                        {
                            TempData.Remove("SerialChangeQueued");
                            TempData.Remove("QueuedSerial");
                            TempData.Remove("QueuedNewSerial");
                            TempData.Remove("QueuedTimestamp");
                        }

                        // Queue the new serial number update command
                        var commService = HttpContext.RequestServices.GetRequiredService<DeviceDataCollector.Services.DeviceCommunicationService>();
                        var (success, response, _) = await commService.UpdateSerialNumberAsync(
                            existingDevice.SerialNumber,
                            NewSerialNumber);

                        if (!success)
                        {
                            TempData["ErrorMessage"] = "Failed to queue serial number change command.";
                            return View(device);
                        }

                        // Add the notification
                        AddSerialChangeQueuedNotification(existingDevice.SerialNumber, NewSerialNumber);

                        // Keep the existing serial number until device confirms the change
                        device.SerialNumber = existingDevice.SerialNumber;

                        TempData["SuccessMessage"] = $"Serial number change request from {existingDevice.SerialNumber} to {NewSerialNumber} has been queued. The device will be updated when it confirms the change. Once the device updates, refresh the page to see the changes.";
                    }
                    else
                    {
                        TempData["SuccessMessage"] = "Device information updated successfully.";
                    }

                    // Update the device in the database
                    _context.Update(device);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation($"Device {device.SerialNumber} updated by {User.Identity.Name}");

                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    if (!DeviceExists(device.Id))
                    {
                        return NotFound();
                    }
                    else
                    {
                        _logger.LogError(ex, "Concurrency error updating device");
                        TempData["ErrorMessage"] = $"Concurrency error: {ex.Message}";
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating device");
                    TempData["ErrorMessage"] = $"Error updating device: {ex.Message}";
                    return View(device);
                }
            }
            return View(device);
        }

        // GET: Devices/Delete/5
        [Authorize(Policy = "RequireAdminRole")]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var device = await _context.Devices
                .FirstOrDefaultAsync(m => m.Id == id);
            if (device == null)
            {
                return NotFound();
            }

            // Get count of associated data
            var dataCount = await _context.DonationsData
                .Where(d => d.DeviceId == device.SerialNumber)
                .CountAsync();

            var statusCount = await _context.DeviceStatuses
                .Where(s => s.DeviceId == device.SerialNumber)
                .CountAsync();

            ViewBag.DataCount = dataCount;
            ViewBag.StatusCount = statusCount;

            return View(device);
        }

        // POST: Devices/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "RequireAdminRole")]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var device = await _context.Devices.FindAsync(id);
            if (device == null)
            {
                return NotFound();
            }

            try
            {
                // Track what's being deleted for logging
                string serialNumber = device.SerialNumber;
                string deviceName = device.Name;

                // Option to cascade delete related data
                bool deleteRelatedData = Request.Form.ContainsKey("deleteRelatedData");

                if (deleteRelatedData)
                {
                    // Delete related donations data
                    var donationData = await _context.DonationsData
                        .Where(d => d.DeviceId == device.SerialNumber)
                        .ToListAsync();

                    if (donationData.Any())
                    {
                        _context.DonationsData.RemoveRange(donationData);
                        _logger.LogInformation($"Deleted {donationData.Count} donation records for device {serialNumber}");
                    }

                    // Delete related status data - MODIFIED: Use ExecuteDelete instead of loading entities
                    int deletedStatuses = await _context.DeviceStatuses
                        .Where(s => s.DeviceId == device.SerialNumber)
                        .ExecuteDeleteAsync();

                    _logger.LogInformation($"Deleted {deletedStatuses} status records for device {serialNumber}");

                    // Also delete from CurrentDeviceStatuses table
                    try
                    {
                        var currentStatus = await _context.CurrentDeviceStatuses
                            .FirstOrDefaultAsync(s => s.DeviceId == device.SerialNumber);

                        if (currentStatus != null)
                        {
                            _context.CurrentDeviceStatuses.Remove(currentStatus);
                            _logger.LogInformation($"Deleted current status record for device {serialNumber}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Could not delete current status for device {serialNumber}, continuing with device deletion");
                    }
                }

                // Delete the device
                _context.Devices.Remove(device);
                await _context.SaveChangesAsync();

                _logger.LogInformation($"Device {serialNumber} ({deviceName}) deleted by {User.Identity.Name}");

                TempData["SuccessMessage"] = $"Device {serialNumber} was successfully deleted.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting device {device.SerialNumber}");
                TempData["ErrorMessage"] = $"Error deleting device: {ex.Message}";
                return RedirectToAction(nameof(Delete), new { id = id });
            }
        }

        // GET: Devices/Donations/5
        public async Task<IActionResult> Donations(int? id, string sortOrder, string searchString, int? pageNumber)
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

            ViewBag.Device = device;
            ViewBag.CurrentSort = sortOrder;
            ViewBag.TimestampSortParm = String.IsNullOrEmpty(sortOrder) ? "timestamp_desc" : "";
            ViewBag.LipemicValueSortParm = sortOrder == "lipemic_value" ? "lipemic_value_desc" : "lipemic_value";
            ViewBag.LipemicGroupSortParm = sortOrder == "lipemic_group" ? "lipemic_group_desc" : "lipemic_group";

            var pageSize = 20;

            var query = _context.DonationsData
                .Where(d => d.DeviceId == device.SerialNumber && d.MessageType == "#D")
                .AsQueryable();

            if (!String.IsNullOrEmpty(searchString))
            {
                query = query.Where(d =>
                    d.DonationIdBarcode != null && d.DonationIdBarcode.Contains(searchString) ||
                    d.RefCode != null && d.RefCode.Contains(searchString) ||
                    d.OperatorIdBarcode != null && d.OperatorIdBarcode.Contains(searchString)
                );
            }

            switch (sortOrder)
            {
                case "timestamp_desc":
                    query = query.OrderByDescending(d => d.Timestamp);
                    break;
                case "lipemic_value":
                    query = query.OrderBy(d => d.LipemicValue);
                    break;
                case "lipemic_value_desc":
                    query = query.OrderByDescending(d => d.LipemicValue);
                    break;
                case "lipemic_group":
                    query = query.OrderBy(d => d.LipemicGroup);
                    break;
                case "lipemic_group_desc":
                    query = query.OrderByDescending(d => d.LipemicGroup);
                    break;
                default:
                    query = query.OrderBy(d => d.Timestamp);
                    break;
            }

            return View(await PaginatedList<DonationsData>.CreateAsync(query, pageNumber ?? 1, pageSize));
        }

        private bool DeviceExists(int id)
        {
            return _context.Devices.Any(e => e.Id == id);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestSetup(int id)
        {
            var device = await _context.Devices.FindAsync(id);
            if (device == null)
            {
                return NotFound();
            }

            // Check if device is in setup mode
            var currentStatus = await _context.CurrentDeviceStatuses
                .FirstOrDefaultAsync(s => s.DeviceId == device.SerialNumber);

            if (currentStatus == null || currentStatus.Status != 3)
            {
                TempData["ErrorMessage"] = "Device is not in setup mode. Setup request can only be sent when device status is 3.";
                return RedirectToAction(nameof(Details), new { id = id });
            }

            var commService = HttpContext.RequestServices.GetRequiredService<DeviceDataCollector.Services.DeviceCommunicationService>();
            bool success = commService.RequestDeviceSetup(device.SerialNumber);

            if (success)
            {
                // Set a flag to indicate that we just requested a setup
                TempData["SetupRequested"] = true;
                // Redirect directly to the Setup page
                return RedirectToAction(nameof(Setup), new { id = id });
            }
            else
            {
                TempData["ErrorMessage"] = "Failed to send setup request to the device.";
                return RedirectToAction(nameof(Details), new { id = id });
            }
        }

        // GET: Devices/Setup/5
        public async Task<IActionResult> Setup(int? id)
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

            DeviceSetup? setup = null;
            try
            {
                // Check if the table exists first
                bool tableExists = true;
                try
                {
                    // Try to query the table - if it doesn't exist, an exception will be thrown
                    await _context.Database.ExecuteSqlRawAsync("SELECT 1 FROM DeviceSetups LIMIT 1");
                }
                catch (Exception)
                {
                    tableExists = false;
                    _logger.LogWarning("DeviceSetups table doesn't exist yet. Migrations need to be applied.");
                }

                if (tableExists)
                {
                    // If we just sent a request to the device, wait a moment for the response
                    if (TempData["SetupRequested"] != null)
                    {
                        // Wait briefly to allow device to respond
                        await Task.Delay(1500);
                    }

                    // Check for pending setup update from TempData
                    if (TempData["PendingSetupUpdate"] != null && TempData["PendingSetupUpdateDeviceId"]?.ToString() == device.SerialNumber)
                    {
                        ViewBag.PendingSetupUpdate = true;
                    }

                    // Check for setup response notifications
                    var setupNotifications = await _context.SystemNotifications
                        .Where(n => n.Type == "SetupResponse" && n.RelatedEntityId == device.SerialNumber && !n.Read)
                        .OrderByDescending(n => n.Timestamp)
                        .ToListAsync();

                    if (setupNotifications.Any())
                    {
                        // Mark them as read
                        foreach (var notification in setupNotifications)
                        {
                            notification.Read = true;
                        }
                        await _context.SaveChangesAsync();

                        // Store the most recent notification message
                        ViewBag.SetupResponseMessage = setupNotifications.First().Message;
                    }

                    // Try to get the setup from the database
                    setup = await _context.DeviceSetups
                        .Where(s => s.DeviceId == device.SerialNumber)
                        .OrderByDescending(s => s.Timestamp)
                        .FirstOrDefaultAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving device setup. The table might not exist yet.");
                // Continue with null setup
            }

            if (setup == null)
            {
                TempData["InfoMessage"] = "No setup information available for this device yet. Click 'Request Setup' to request it from the device.";
                return RedirectToAction(nameof(Details), new { id = id });
            }

            // Deserialize JSON data for display
            try
            {
                if (!string.IsNullOrEmpty(setup.ProfilesJson))
                {
                    setup.Profiles = System.Text.Json.JsonSerializer.Deserialize<List<DeviceProfile>>(setup.ProfilesJson)
                                     ?? new List<DeviceProfile>();
                }

                if (!string.IsNullOrEmpty(setup.BarcodesJson))
                {
                    setup.BarcodeConfigs = System.Text.Json.JsonSerializer.Deserialize<List<BarcodeConfig>>(setup.BarcodesJson)
                                           ?? new List<BarcodeConfig>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deserializing JSON data for device setup");
            }

            ViewBag.Device = device;
            return View(setup);
        }

        // GET: Devices/EditSetup/5
        [Authorize(Policy = "RequireAdminRole")]
        public async Task<IActionResult> EditSetup(int? id)
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

            // Check if the device is in setup mode
            var currentStatus = await _context.CurrentDeviceStatuses
                .FirstOrDefaultAsync(s => s.DeviceId == device.SerialNumber);

            if (currentStatus == null || currentStatus.Status != 3)
            {
                TempData["ErrorMessage"] = "Device is not in setup mode. Setup can only be modified when device status is 3.";
                return RedirectToAction(nameof(Setup), new { id = id });
            }

            // Get the latest setup info
            var setup = await _context.DeviceSetups
                .Where(s => s.DeviceId == device.SerialNumber)
                .OrderByDescending(s => s.Timestamp)
                .FirstOrDefaultAsync();

            if (setup == null)
            {
                TempData["ErrorMessage"] = "No setup information available for this device. Please request setup information first.";
                return RedirectToAction(nameof(Details), new { id = id });
            }

            // Deserialize JSON data for editing
            try
            {
                if (!string.IsNullOrEmpty(setup.ProfilesJson))
                {
                    setup.Profiles = System.Text.Json.JsonSerializer.Deserialize<List<DeviceProfile>>(setup.ProfilesJson)
                                     ?? new List<DeviceProfile>();
                }
                else
                {
                    setup.Profiles = new List<DeviceProfile>();
                }

                if (!string.IsNullOrEmpty(setup.BarcodesJson))
                {
                    setup.BarcodeConfigs = System.Text.Json.JsonSerializer.Deserialize<List<BarcodeConfig>>(setup.BarcodesJson)
                                           ?? new List<BarcodeConfig>();
                }
                else
                {
                    setup.BarcodeConfigs = new List<BarcodeConfig>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deserializing JSON data for device setup");
            }

            ViewBag.Device = device;
            return View(setup);
        }

        // POST: Devices/SaveSetup

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "RequireAdminRole")]
        public async Task<IActionResult> SaveSetup(DeviceSetup model, int deviceModelId, List<DeviceProfile> Profiles, List<BarcodeConfig> BarcodeConfigs)
        {
            var device = await _context.Devices.FindAsync(deviceModelId);
            if (device == null)
            {
                return NotFound();
            }

            // Make sure we have the right device ID
            model.DeviceId = device.SerialNumber;

            try
            {
                // Check if the device is in setup mode
                var currentStatus = await _context.CurrentDeviceStatuses
                    .FirstOrDefaultAsync(s => s.DeviceId == device.SerialNumber);

                if (currentStatus == null || currentStatus.Status != 3)
                {
                    TempData["ErrorMessage"] = "Device is not in setup mode. Setup can only be modified when device is in Setup mode.";
                    return RedirectToAction(nameof(Setup), new { id = deviceModelId });
                }

                // Set current time
                model.Timestamp = DateTime.Now;

                // Process profiles and barcode configurations
                model.Profiles = Profiles ?? new List<DeviceProfile>();
                model.BarcodeConfigs = BarcodeConfigs ?? new List<BarcodeConfig>();

                // Serialize JSON data
                model.ProfilesJson = System.Text.Json.JsonSerializer.Serialize(model.Profiles);
                model.BarcodesJson = System.Text.Json.JsonSerializer.Serialize(model.BarcodeConfigs);

                // IMPORTANT: Ensure RawResponse is not null - this is what's causing the error
                if (model.RawResponse == null)
                {
                    // Create a placeholder RawResponse if it's null
                    model.RawResponse = $"Manually edited setup for device {model.DeviceId} at {DateTime.Now}";
                }

                // Save the updated setup to the database first
                var existingSetup = await _context.DeviceSetups.FindAsync(model.Id);
                if (existingSetup != null)
                {
                    // Update existing setup
                    _context.Entry(existingSetup).CurrentValues.SetValues(model);
                    existingSetup.ProfilesJson = model.ProfilesJson;
                    existingSetup.BarcodesJson = model.BarcodesJson;
                    existingSetup.RawResponse = model.RawResponse; 

                    await _context.SaveChangesAsync();
                    _logger.LogInformation($"Updated setup for device {model.DeviceId} in database");
                }
                else
                {
                    // Create new setup entry
                    _context.DeviceSetups.Add(model);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation($"Created new setup for device {model.DeviceId} in database");
                }

                if (string.IsNullOrEmpty(model.WifiPassword))
                {
                    // If we're getting a null or empty password when there should be one,
                    // check if we have an existing setup with a password
                    if (existingSetup != null && !string.IsNullOrEmpty(existingSetup.WifiPassword))
                    {
                        _logger.LogWarning($"WiFi password was empty in form submission for device {model.DeviceId}, preserving existing password");
                        model.WifiPassword = existingSetup.WifiPassword;
                    }
                    else
                    {
                        _logger.LogWarning($"WiFi password is empty for device {model.DeviceId}, setting default empty string");
                        model.WifiPassword = string.Empty; // Default to empty string if no password found
                    }
                }

                // Now send the setup to the device
                var deviceCommunicationService = HttpContext.RequestServices.GetRequiredService<DeviceCommunicationService>();
                var (success, response) = await deviceCommunicationService.UpdateDeviceSetupAsync(model);

                if (success)
                {
                    TempData["PendingSetupUpdate"] = true;
                    TempData["PendingSetupUpdateDeviceId"] = model.DeviceId;
                    TempData["PendingSetupTimestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                    TempData["SuccessMessage"] = "Setup update has been queued and will be sent to the device. Please wait for device response.";
                    return RedirectToAction(nameof(Setup), new { id = deviceModelId });
                }
                else
                {
                    TempData["ErrorMessage"] = $"Failed to send setup to device: {response}";
                    return RedirectToAction(nameof(EditSetup), new { id = deviceModelId });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error saving or sending setup to device {model.DeviceId}");
                TempData["ErrorMessage"] = $"Error: {ex.Message}";
                return RedirectToAction(nameof(EditSetup), new { id = deviceModelId });
            }
        }

    }

    // Helper class for pagination
    public class PaginatedList<T> : List<T>
    {
        public int PageIndex { get; private set; }
        public int TotalPages { get; private set; }

        public PaginatedList(List<T> items, int count, int pageIndex, int pageSize)
        {
            PageIndex = pageIndex;
            TotalPages = (int)Math.Ceiling(count / (double)pageSize);
            this.AddRange(items);
        }

        public bool HasPreviousPage => PageIndex > 1;
        public bool HasNextPage => PageIndex < TotalPages;

        public static async Task<PaginatedList<T>> CreateAsync(IQueryable<T> source, int pageIndex, int pageSize)
        {
            var count = await source.CountAsync();
            var items = await source.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync();
            return new PaginatedList<T>(items, count, pageIndex, pageSize);
        }
    }
}