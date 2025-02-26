using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using DeviceDataCollector.Models;
using DeviceDataCollector.Services;
using DeviceDataCollector.Data;

namespace DeviceDataCollector.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly DatabaseStatusService _databaseStatusService;

    public HomeController(
        ILogger<HomeController> logger,
        DatabaseStatusService databaseStatusService)
    {
        _logger = logger;
        _databaseStatusService = databaseStatusService;
    }

    public async Task<IActionResult> Index()
    {
        // Check database connection status
        var (isConnected, statusMessage) = await _databaseStatusService.CheckDatabaseConnectionAsync();

        // Pass database status to the view
        ViewBag.DatabaseConnected = isConnected;
        ViewBag.DatabaseStatus = statusMessage;

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