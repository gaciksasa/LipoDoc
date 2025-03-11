using DeviceDataCollector.Data;
using Microsoft.EntityFrameworkCore;

namespace DeviceDataCollector.Services
{
    public class DeviceStatusCleanupService : BackgroundService
    {
        private readonly ILogger<DeviceStatusCleanupService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _cleanupInterval;
        private readonly TimeSpan _retentionPeriod;

        public DeviceStatusCleanupService(
            ILogger<DeviceStatusCleanupService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;

            // Run cleanup daily by default
            _cleanupInterval = TimeSpan.FromHours(
                configuration.GetValue<int>("DeviceStatusCleanup:IntervalHours", 24));

            // Keep status records for 30 days by default
            _retentionPeriod = TimeSpan.FromDays(
                configuration.GetValue<int>("DeviceStatusCleanup:RetentionDays", 30));

            _logger.LogInformation(
                $"Device Status Cleanup Service configured with interval: {_cleanupInterval.TotalHours}h, " +
                $"retention period: {_retentionPeriod.TotalDays}d");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Device Status Cleanup Service is starting...");

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await CleanupOldStatusRecordsAsync(stoppingToken);
                    await Task.Delay(_cleanupInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // This is expected when the service is stopping
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Device Status Cleanup Service");
            }
            finally
            {
                _logger.LogInformation("Device Status Cleanup Service is stopping...");
            }
        }

        private async Task CleanupOldStatusRecordsAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Starting device status cleanup job");

                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Calculate cutoff date
                var cutoffDate = DateTime.Now.Subtract(_retentionPeriod);
                _logger.LogInformation($"Removing status records older than {cutoffDate:dd.MM.yyyy HH:mm:ss}");

                // Different strategies for cleanup depending on database size:

                // Strategy 1: Direct delete for small/medium databases
                int deletedCount = await dbContext.DeviceStatuses
                    .Where(s => s.Timestamp < cutoffDate)
                    .ExecuteDeleteAsync(stoppingToken);

                // Strategy 2: Batch delete for very large tables (uncomment if needed)
                // This is safer for production databases as it won't lock the table for too long
                /*
                const int batchSize = 5000;
                int totalDeleted = 0;
                
                while (true)
                {
                    // Get a batch of IDs to delete
                    var recordsToDelete = await dbContext.DeviceStatuses
                        .Where(s => s.Timestamp < cutoffDate)
                        .Take(batchSize)
                        .ToListAsync(stoppingToken);
                    
                    if (!recordsToDelete.Any())
                        break;
                        
                    dbContext.DeviceStatuses.RemoveRange(recordsToDelete);
                    await dbContext.SaveChangesAsync(stoppingToken);
                    
                    totalDeleted += recordsToDelete.Count;
                    _logger.LogInformation($"Deleted batch of {recordsToDelete.Count} old status records, total: {totalDeleted}");
                    
                    // Brief pause to reduce database load
                    await Task.Delay(500, stoppingToken);
                }
                
                int deletedCount = totalDeleted;
                */

                _logger.LogInformation($"Cleanup completed. Removed {deletedCount} old device status records");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old device status records");
            }
        }
    }
}