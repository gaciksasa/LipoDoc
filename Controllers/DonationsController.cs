using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DeviceDataCollector.Data;
using Microsoft.AspNetCore.Authorization;
using DeviceDataCollector.Models;

namespace DeviceDataCollector.Controllers
{
    [Authorize] // Require authentication for all actions
    public class DonationsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public DonationsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: Donations
        public async Task<IActionResult> Index()
        {
            return View(await _context.DonationsData.ToListAsync());
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
        public async Task<IActionResult> Edit(int id, [Bind("Id,DeviceId,Timestamp,DataPayload,IPAddress,Port")] DonationsData donationData)
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
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!DonationsDataExists(donationData.Id))
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
            }

            await _context.SaveChangesAsync();
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