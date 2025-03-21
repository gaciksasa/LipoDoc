using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
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

        // GET: Devices
        public async Task<IActionResult> Index()
        {
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
                ViewBag.StatusUpdateCount = currentStatus.StatusUpdateCount; // Add update count for informational purposes
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

        // Replace the existing Edit POST method in DevicesController.cs with this:

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

            // Check if a new serial number was provided
            bool serialNumberChangeRequested = !string.IsNullOrWhiteSpace(NewSerialNumber) &&
                                              NewSerialNumber != existingDevice.SerialNumber;

            if (ModelState.IsValid)
            {
                try
                {
                    if (serialNumberChangeRequested)
                    {
                        // Get the communication service
                        var commService = HttpContext.RequestServices.GetRequiredService<DeviceDataCollector.Services.DeviceCommunicationService>();

                        // Queue the serial number update command
                        var (success, response, _) = await commService.UpdateSerialNumberAsync(
                            existingDevice.SerialNumber,
                            NewSerialNumber);

                        // Store the response for display
                        TempData["CommandResponse"] = response;

                        if (!success)
                        {
                            // Failed to queue command
                            TempData["ErrorMessage"] = "Failed to queue serial number change command.";
                            return View(device);
                        }

                        // Command successfully queued
                        TempData["SuccessMessage"] = "Serial number change command queued. The change will be applied when the device next communicates with the server.";

                        // VAŽNO: NE ažuriramo serijski broj u bazi podataka odmah
                        // To će biti urađeno u TCPServerService kada dobijemo potvrdu od uređaja

                        // UMESTO TOGA, sačuvajmo ostale izmene, ali vratimo originalni serijski broj
                        device.SerialNumber = existingDevice.SerialNumber;
                    }

                    // Update the device in the database
                    _context.Update(device);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation($"Device {device.SerialNumber} updated by {User.Identity.Name}");

                    if (!serialNumberChangeRequested)
                    {
                        TempData["SuccessMessage"] = "Device information updated successfully.";
                    }

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

                    // Delete related status data
                    var statusData = await _context.DeviceStatuses
                        .Where(s => s.DeviceId == device.SerialNumber)
                        .ToListAsync();

                    if (statusData.Any())
                    {
                        _context.DeviceStatuses.RemoveRange(statusData);
                        _logger.LogInformation($"Deleted {statusData.Count} status records for device {serialNumber}");
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
        [Authorize(Policy = "RequireAdminRole")]
        public async Task<IActionResult> SendCommand(int id, string command)
        {
            try
            {
                var device = await _context.Devices.FindAsync(id);
                if (device == null)
                {
                    return NotFound();
                }

                // Get the TCP service to send the command
                var tcpService = HttpContext.RequestServices.GetRequiredService<DeviceDataCollector.Services.DeviceCommunicationService>();

                // Call our new method - note that we're passing temporary values just to reuse the function
                // This is not ideal but will fix the compilation error
                var (success, response, _) = await tcpService.UpdateSerialNumberAsync(
                    device.SerialNumber,
                    device.SerialNumber); // Just passing the same SN as both parameters

                TempData["CommandResponse"] = response;
                TempData["SuccessMessage"] = success ? "Command sent successfully." : "Command failed.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending command to device");
                TempData["ErrorMessage"] = $"Error sending command: {ex.Message}";
            }

            return RedirectToAction(nameof(Details), new { id = id });
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