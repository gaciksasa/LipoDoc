using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DeviceDataCollector.Data;
using Microsoft.AspNetCore.Authorization;
using DeviceDataCollector.Models;

namespace DeviceDataCollector.Controllers
{
    [Authorize] // Require authentication for all actions
    public class DeviceDataController : Controller
    {
        private readonly ApplicationDbContext _context;

        public DeviceDataController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: DeviceData
        public async Task<IActionResult> Index()
        {
            return View(await _context.DeviceData.ToListAsync());
        }

        // GET: DeviceData/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var deviceData = await _context.DeviceData
                .FirstOrDefaultAsync(m => m.Id == id);
            if (deviceData == null)
            {
                return NotFound();
            }

            return View(deviceData);
        }

        // GET: DeviceData/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: DeviceData/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Id,DeviceId,Timestamp,DataPayload,IPAddress,Port")] DeviceData deviceData)
        {
            if (ModelState.IsValid)
            {
                _context.Add(deviceData);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(deviceData);
        }

        // GET: DeviceData/Edit/5
        [Authorize(Policy = "RequireAdminRole")] // Only admins can edit
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var deviceData = await _context.DeviceData.FindAsync(id);
            if (deviceData == null)
            {
                return NotFound();
            }
            return View(deviceData);
        }

        // POST: DeviceData/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "RequireAdminRole")] // Only admins can edit
        public async Task<IActionResult> Edit(int id, [Bind("Id,DeviceId,Timestamp,DataPayload,IPAddress,Port")] DeviceData deviceData)
        {
            if (id != deviceData.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(deviceData);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!DeviceDataExists(deviceData.Id))
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
            return View(deviceData);
        }

        // GET: DeviceData/Delete/5
        [Authorize(Policy = "RequireAdminRole")] // Only admins can delete
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var deviceData = await _context.DeviceData
                .FirstOrDefaultAsync(m => m.Id == id);
            if (deviceData == null)
            {
                return NotFound();
            }

            return View(deviceData);
        }

        // POST: DeviceData/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        [Authorize(Policy = "RequireAdminRole")] // Only admins can delete
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var deviceData = await _context.DeviceData.FindAsync(id);
            if (deviceData != null)
            {
                _context.DeviceData.Remove(deviceData);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> GetCount()
        {
            int count = await _context.DeviceData.CountAsync();
            return Json(count);
        }

        private bool DeviceDataExists(int id)
        {
            return _context.DeviceData.Any(e => e.Id == id);
        }
    }
}