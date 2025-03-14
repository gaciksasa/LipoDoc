using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using DeviceDataCollector.Models;
using DeviceDataCollector.Services;
using DeviceDataCollector.Data;
using Microsoft.EntityFrameworkCore;

namespace DeviceDataCollector.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly DatabaseStatusService _databaseStatusService;
    private readonly ApplicationLifetimeService _applicationLifetimeService;

    public HomeController(
        ILogger<HomeController> logger,
        DatabaseStatusService databaseStatusService,
        ApplicationLifetimeService applicationLifetimeService)
    {
        _logger = logger;
        _databaseStatusService = databaseStatusService;
        _applicationLifetimeService = applicationLifetimeService;
    }

    public async Task<IActionResult> Index()
    {
        // Check database connection status
        var (isConnected, statusMessage) = await _databaseStatusService.CheckDatabaseConnectionAsync();
        TimeSpan uptime = _applicationLifetimeService.GetUptime();
        ViewBag.SystemUptime = $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";

        // Pass database status to the view
        ViewBag.DatabaseConnected = isConnected;
        ViewBag.DatabaseStatus = statusMessage;

        // Only fetch metrics if database is connected
        if (isConnected)
        {
            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Get counts for dashboard
                ViewBag.DonationsCount = await dbContext.DonationsData.CountAsync();
                ViewBag.DevicesCount = await dbContext.Devices.CountAsync();
                ViewBag.UsersCount = await dbContext.Users.CountAsync();
                ViewBag.ActiveDevicesCount = await dbContext.Devices.CountAsync(d => d.IsActive);

                // Get recent activities
                ViewBag.RecentDonations = await dbContext.DonationsData
                    .OrderByDescending(d => d.Timestamp)
                    .Take(5)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching dashboard metrics");
            }
        }

        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}