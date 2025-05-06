using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using Microsoft.EntityFrameworkCore;

namespace DeviceDataCollector.Services
{
    /// <summary>
    /// Service to handle automatic export of donation records
    /// </summary>
    public class AutoExportService : BackgroundService
    {
        private readonly ILogger<AutoExportService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;

        public AutoExportService(
            ILogger<AutoExportService> logger,
            IServiceScopeFactory scopeFactory)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Auto Export Service is starting...");

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    // We'll implement the actual export logic in Phase 2
                    // For now, just add a placeholder

                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // This is expected when stopping
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Auto Export Service");
            }
            finally
            {
                _logger.LogInformation("Auto Export Service is stopping...");
            }
        }
    }
}