using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DeviceDataCollector.Data;
using Microsoft.AspNetCore.Authorization;
using DeviceDataCollector.Models;
using DeviceDataCollector.Controllers;

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
        public async Task<IActionResult> Index(string sortOrder, string currentFilter, string searchString, int? pageNumber)
        {
            ViewBag.CurrentSort = sortOrder;
            ViewBag.TimestampSortParm = String.IsNullOrEmpty(sortOrder) ? "timestamp_desc" : "";
            ViewBag.DeviceSortParm = sortOrder == "device" ? "device_desc" : "device";
            ViewBag.DonationIdSortParm = sortOrder == "donation_id" ? "donation_id_desc" : "donation_id";
            ViewBag.LipemicValueSortParm = sortOrder == "lipemic_value" ? "lipemic_value_desc" : "lipemic_value";
            ViewBag.LipemicGroupSortParm = sortOrder == "lipemic_group" ? "lipemic_group_desc" : "lipemic_group";

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
                // For AJAX requests, just return the first page of data
                var firstPageData = await query
                    .Take(_pageSize)
                    .ToListAsync();

                // Return the partial view for AJAX refresh
                return PartialView("_DonationList", firstPageData);
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
    }
}