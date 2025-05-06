using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using Microsoft.EntityFrameworkCore;

namespace DeviceDataCollector.Services
{
    /// <summary>
    /// Background service to handle automatic export of donation records
    /// </summary>
    public class AutoExportService : BackgroundService
    {
        private readonly ILogger<AutoExportService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly HashSet<int> _processedDonationIds = new HashSet<int>();
        private DateTime _lastExportCheck = DateTime.MinValue;

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
                // Wait for 10 seconds after startup to allow the application to fully initialize
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        // Check for new donations every 10 seconds
                        await CheckForMissedDonations(stoppingToken);

                        // Wait before next check
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error checking for new donations");
                        // Wait a bit longer before next check in case of errors
                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    }
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

        private async Task CheckForMissedDonations(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var exportHelper = scope.ServiceProvider.GetRequiredService<DonationExportHelper>();

                // Get all active auto-export configurations
                var configs = await dbContext.ExportSettingsConfigs
                    .Where(c => c.AutoExportEnabled)
                    .OrderByDescending(c => c.IsDefault)
                    .ToListAsync(stoppingToken);

                if (!configs.Any())
                {
                    // No auto-export configurations enabled - nothing to do
                    return;
                }

                // Get donations created since last check
                var query = dbContext.DonationsData.AsQueryable();

                // Only get records since last check
                if (_lastExportCheck != DateTime.MinValue)
                {
                    query = query.Where(d => d.Timestamp > _lastExportCheck);
                }

                // Skip donations we've already processed
                if (_processedDonationIds.Any())
                {
                    query = query.Where(d => !_processedDonationIds.Contains(d.Id));
                }

                // Get the donations ordered by timestamp (oldest first)
                var newDonations = await query
                    .OrderBy(d => d.Timestamp)
                    .ToListAsync(stoppingToken);

                if (newDonations.Any())
                {
                    _logger.LogInformation($"Found {newDonations.Count} new donations to export");

                    // Update last check time to the most recent donation's timestamp
                    _lastExportCheck = newDonations.Max(d => d.Timestamp);

                    // Export using the first (default) configuration
                    var defaultConfig = configs.First();
                    await exportHelper.ExportDonations(newDonations, defaultConfig);

                    // Mark all as processed
                    foreach (var donation in newDonations)
                    {
                        _processedDonationIds.Add(donation.Id);
                    }

                    // Limit the size of the processed set to prevent memory growth
                    if (_processedDonationIds.Count > 1000)
                    {
                        // Keep only the most recent 500 IDs
                        _processedDonationIds.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for missed donations");
            }
        }
    }
}