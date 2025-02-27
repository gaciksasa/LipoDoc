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

            // Get the latest status for this device
            var latestStatus = await _context.DeviceStatuses
                .Where(s => s.DeviceId == device.SerialNumber)
                .OrderByDescending(s => s.Timestamp)
                .FirstOrDefaultAsync();

            ViewBag.LatestStatus = latestStatus;

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
        public async Task<IActionResult> Edit(int id, [Bind("Id,SerialNumber,Name,Location,IsActive,Notes")] Device device)
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

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(device);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation($"Device {device.SerialNumber} updated by {User.Identity.Name}");
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!DeviceExists(device.Id))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
                return RedirectToAction(nameof(Index));
            }
            return View(device);
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