using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DeviceDataCollector.Data;
using Microsoft.AspNetCore.Authorization;
using DeviceDataCollector.Models;
using DeviceDataCollector.Controllers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DeviceDataCollector.Controllers
{
    [Authorize] // Require authentication for all actions
    public class DonationsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<DonationsController> _logger;
        private readonly int _pageSize = 20; // Default page size

        public DonationsController(
            ApplicationDbContext context,
            ILogger<DonationsController> logger)
        {
            _context = context;
            _logger = logger;
        }

        // GET: Donations
        public async Task<IActionResult> Index(string sortOrder, string currentFilter, string searchString, int? pageNumber, bool? todayOnly = null)
        {
            ViewBag.CurrentSort = sortOrder;
            ViewBag.TimestampSortParm = sortOrder == "timestamp_asc" ? "timestamp_desc" : "timestamp_asc";
            ViewBag.DeviceSortParm = sortOrder == "device" ? "device_desc" : "device";
            ViewBag.DonationIdSortParm = sortOrder == "donation_id" ? "donation_id_desc" : "donation_id";
            ViewBag.LipemicValueSortParm = sortOrder == "lipemic_value" ? "lipemic_value_desc" : "lipemic_value";
            ViewBag.LipemicGroupSortParm = sortOrder == "lipemic_group" ? "lipemic_group_desc" : "lipemic_group";

            // Fix: Make sure todayOnly is properly handled with a default value of true
            bool todayOnlyValue = todayOnly ?? true;
            ViewBag.TodayOnly = todayOnlyValue;

            if (searchString != null)
            {
                pageNumber = 1;
            }
            else
            {
                searchString = currentFilter;
            }

            ViewBag.CurrentFilter = searchString;

            var query = _context.DonationsData.AsQueryable();

            // Apply today only filter if enabled
            if (todayOnlyValue)
            {
                DateTime yesterday = DateTime.Now.AddDays(-1);
                query = query.Where(d => d.Timestamp >= yesterday);
            }

            // Apply search filter if provided
            if (!String.IsNullOrEmpty(searchString))
            {
                query = query.Where(d =>
                    (d.DonationIdBarcode != null && d.DonationIdBarcode.Contains(searchString)) ||
                    (d.DeviceId != null && d.DeviceId.Contains(searchString)) ||
                    (d.OperatorIdBarcode != null && d.OperatorIdBarcode.Contains(searchString)) ||
                    (d.RefCode != null && d.RefCode.Contains(searchString))
                );
            }

            // Apply sorting
            switch (sortOrder)
            {
                case "timestamp_desc":
                    query = query.OrderByDescending(d => d.Timestamp);
                    break;
                case "timestamp_asc":
                    query = query.OrderBy(d => d.Timestamp);
                    break;
                case "device":
                    query = query.OrderBy(d => d.DeviceId);
                    break;
                case "device_desc":
                    query = query.OrderByDescending(d => d.DeviceId);
                    break;
                case "donation_id":
                    query = query.OrderBy(d => d.DonationIdBarcode);
                    break;
                case "donation_id_desc":
                    query = query.OrderByDescending(d => d.DonationIdBarcode);
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
                    query = query.OrderByDescending(d => d.Timestamp); // Default sort is newest first
                    break;
            }

            // Check if it's an AJAX request for auto-refresh
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                // For AJAX requests, we'll use the current page number from the request
                // This allows us to refresh data for the current page the user is viewing
                var ajaxPageIndex = pageNumber ?? 1;
                var ajaxPageData = await query
                    .Skip((ajaxPageIndex - 1) * _pageSize)
                    .Take(_pageSize)
                    .ToListAsync();

                // Return the partial view for AJAX refresh
                return PartialView("_DonationList", ajaxPageData);
            }

            // For normal page loads, return paginated data
            var pageIndex = pageNumber ?? 1;
            var paginatedList = await PaginatedList<DonationsData>.CreateAsync(query, pageIndex, _pageSize);

            return View(paginatedList);
        }

        // GET: Donations/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var donationData = await _context.DonationsData
                .FirstOrDefaultAsync(m => m.Id == id);
            if (donationData == null)
            {
                return NotFound();
            }

            return View(donationData);
        }

        // GET: Donations/Edit/5
        [Authorize(Policy = "RequireAdminRole")] // Only admins can edit
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var donationData = await _context.DonationsData.FindAsync(id);
            if (donationData == null)
            {
                return NotFound();
            }
            return View(donationData);
        }

        // POST: Donations/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "RequireAdminRole")] // Only admins can edit
        public async Task<IActionResult> Edit(int id, [Bind("Id,DeviceId,Timestamp,MessageType,RawPayload,IPAddress,Port,DeviceStatus,AvailableData,IsBarcodeMode,RefCode,DonationIdBarcode,OperatorIdBarcode,LotNumber,LipemicValue,LipemicGroup,LipemicStatus,CheckSum")] DonationsData donationData)
        {
            if (id != donationData.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(donationData);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation($"Donation record {id} updated by {User.Identity.Name}");
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    if (!DonationsDataExists(donationData.Id))
                    {
                        return NotFound();
                    }
                    else
                    {
                        _logger.LogError(ex, $"Concurrency error updating donation {id}");
                        throw;
                    }
                }
                return RedirectToAction(nameof(Index));
            }
            return View(donationData);
        }

        // GET: Donations/Delete/5
        [Authorize(Policy = "RequireAdminRole")] // Only admins can delete
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var donationData = await _context.DonationsData
                .FirstOrDefaultAsync(m => m.Id == id);
            if (donationData == null)
            {
                return NotFound();
            }

            return View(donationData);
        }

        // POST: Donations/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "RequireAdminRole")] // Only admins can delete
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var donationData = await _context.DonationsData.FindAsync(id);
            if (donationData != null)
            {
                _context.DonationsData.Remove(donationData);
                await _context.SaveChangesAsync();
                _logger.LogInformation($"Donation record {id} deleted by {User.Identity.Name}");
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> GetCount()
        {
            int count = await _context.DonationsData.CountAsync();
            return Json(count);
        }

        private bool DonationsDataExists(int id)
        {
            return _context.DonationsData.Any(e => e.Id == id);
        }

        [HttpGet]
        public async Task<IActionResult> Export()
        {
            // Create a view model with all available columns
            var model = new ExportViewModel
            {
                AvailableColumns = new List<ColumnSelectionItem>
        {
            new ColumnSelectionItem { Id = "DonationIdBarcode", Name = "Donation ID", Selected = true },
            new ColumnSelectionItem { Id = "DeviceId", Name = "Device ID", Selected = true },
            new ColumnSelectionItem { Id = "Timestamp", Name = "Timestamp", Selected = true },
            new ColumnSelectionItem { Id = "LipemicValue", Name = "Lipemic Value", Selected = true },
            new ColumnSelectionItem { Id = "LipemicGroup", Name = "Lipemic Group", Selected = true },
            new ColumnSelectionItem { Id = "LipemicStatus", Name = "Lipemic Status", Selected = true },
            new ColumnSelectionItem { Id = "RefCode", Name = "Reference Code", Selected = false },
            new ColumnSelectionItem { Id = "OperatorIdBarcode", Name = "Operator ID", Selected = true },
            new ColumnSelectionItem { Id = "LotNumber", Name = "Lot Number", Selected = true },
            new ColumnSelectionItem { Id = "MessageType", Name = "Message Type", Selected = false },
            new ColumnSelectionItem { Id = "IPAddress", Name = "IP Address", Selected = false },
            new ColumnSelectionItem { Id = "Port", Name = "Port", Selected = false }
        },
                Delimiter = ",",
                DateFormat = "yyyy-MM-dd",
                TimeFormat = "HH:mm:ss",
                EmptyColumnsCount = 0
            };

            // Initialize ColumnOrder with default selected columns
            model.ColumnOrder = model.AvailableColumns
                .Where(c => c.Selected)
                .Select(c => c.Id)
                .ToList();

            try
            {
                // Get list of unique device IDs from donations table
                var deviceIds = await _context.DonationsData
                    .Select(d => d.DeviceId)
                    .Distinct()
                    .Where(d => !string.IsNullOrEmpty(d))
                    .OrderBy(d => d)
                    .ToListAsync();

                // Get device names from devices table
                var deviceList = await _context.Devices
                    .OrderBy(d => d.SerialNumber)
                    .ToListAsync();

                // Create select list items with device details
                var deviceItems = new List<SelectListItem>();
                foreach (var deviceId in deviceIds)
                {
                    var device = deviceList.FirstOrDefault(d => d.SerialNumber == deviceId);
                    string displayName = device != null && !string.IsNullOrEmpty(device.Name)
                        ? $"{device.Name} ({deviceId})"
                        : deviceId;

                    deviceItems.Add(new SelectListItem
                    {
                        Value = deviceId,
                        Text = displayName
                    });
                }

                // Add "All Devices" option at the top
                deviceItems.Insert(0, new SelectListItem { Value = "", Text = "-- All Devices --" });

                // Assign to model
                model.AvailableDevices = deviceItems;

                _logger.LogInformation($"Populated device dropdown with {deviceItems.Count - 1} devices");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading device list for export dropdown");
                // Create a default empty list with just the "All Devices" option
                model.AvailableDevices = new List<SelectListItem>
        {
            new SelectListItem { Value = "", Text = "-- All Devices --" }
        };
            }

            // Set default date range to last 30 days
            model.StartDate = DateTime.Now.AddDays(-30).Date;
            model.EndDate = DateTime.Now.Date;

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> ExportData(ExportViewModel model)
        {
            // Get the data from the database based on filters
            var query = _context.DonationsData.AsQueryable();

            // Apply date range filter if provided
            if (model.StartDate.HasValue)
            {
                query = query.Where(d => d.Timestamp >= model.StartDate.Value);
            }

            if (model.EndDate.HasValue)
            {
                // Add one day to include the end date fully
                var endDatePlusOne = model.EndDate.Value.AddDays(1);
                query = query.Where(d => d.Timestamp < endDatePlusOne);
            }

            // Apply device filter if provided
            if (!string.IsNullOrEmpty(model.DeviceId))
            {
                query = query.Where(d => d.DeviceId == model.DeviceId);
            }

            // Order by timestamp descending (newest first)
            var donations = await query
                .OrderByDescending(d => d.Timestamp)
                .ToListAsync();

            // Generate CSV content
            var csv = new StringBuilder();

            // Process column order
            List<string> columnsInOrder = model.ColumnOrder;

            // Extract data columns and empty columns
            var dataColumns = new List<string>();
            var emptyColumns = new List<string>();

            foreach (var column in columnsInOrder)
            {
                if (column.StartsWith("empty_"))
                {
                    emptyColumns.Add(column);
                }
                else
                {
                    dataColumns.Add(column);
                }
            }

            // Add header
            var headerParts = new List<string>();

            // Process each column according to its type
            foreach (var column in columnsInOrder)
            {
                if (column.StartsWith("empty_"))
                {
                    // Empty column - use a default name or the name provided
                    int emptyIndex = int.Parse(column.Substring(6));
                    headerParts.Add($"\"Empty {emptyIndex + 1}\"");
                }
                else
                {
                    // Regular data column
                    headerParts.Add($"\"{GetColumnDisplayName(column)}\"");
                }
            }

            var headerLine = string.Join(model.Delimiter, headerParts);
            csv.AppendLine(headerLine);

            // Add data rows
            foreach (var donation in donations)
            {
                var row = new List<string>();

                // Add data for columns in the specified order
                foreach (var column in columnsInOrder)
                {
                    if (column.StartsWith("empty_"))
                    {
                        // Empty column - add an empty string
                        row.Add("\"\"");
                    }
                    else
                    {
                        // Regular data column - get the actual value
                        string value = GetPropertyValue(donation, column, model.DateFormat, model.TimeFormat);
                        // Quote the value and escape any quotes inside it
                        row.Add($"\"{value.Replace("\"", "\"\"")}\"");
                    }
                }

                csv.AppendLine(string.Join(model.Delimiter, row));
            }

            // Generate file name
            string fileName = $"Donations_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

            // Return CSV file
            byte[] bytes = Encoding.UTF8.GetBytes(csv.ToString());
            return File(bytes, "text/csv", fileName);
        }

        private string GetColumnDisplayName(string columnId)
        {
            return columnId switch
            {
                "DonationIdBarcode" => "Donation ID",
                "DeviceId" => "Device ID",
                "Timestamp" => "Timestamp",
                "LipemicValue" => "Lipemic Value",
                "LipemicGroup" => "Lipemic Group",
                "LipemicStatus" => "Lipemic Status",
                "RefCode" => "Reference Code",
                "OperatorIdBarcode" => "Operator ID",
                "LotNumber" => "Lot Number",
                "MessageType" => "Message Type",
                "IPAddress" => "IP Address",
                "Port" => "Port",
                _ => columnId
            };
        }

        private string GetPropertyValue(DonationsData donation, string propertyName, string dateFormat, string timeFormat)
        {
            return propertyName switch
            {
                "DonationIdBarcode" => donation.DonationIdBarcode ?? string.Empty,
                "DeviceId" => donation.DeviceId ?? string.Empty,
                "Timestamp" => donation.Timestamp.ToString($"{dateFormat} {timeFormat}"),
                "LipemicValue" => donation.LipemicValue?.ToString() ?? string.Empty,
                "LipemicGroup" => donation.LipemicGroup ?? string.Empty,
                "LipemicStatus" => donation.LipemicStatus ?? string.Empty,
                "RefCode" => donation.RefCode ?? string.Empty,
                "OperatorIdBarcode" => donation.OperatorIdBarcode ?? string.Empty,
                "LotNumber" => donation.LotNumber ?? string.Empty,
                "MessageType" => donation.MessageType ?? string.Empty,
                "IPAddress" => donation.IPAddress ?? string.Empty,
                "Port" => donation.Port.ToString(),
                _ => string.Empty
            };
        }
    }
}