using DeviceDataCollector.Data;
using Microsoft.EntityFrameworkCore;

namespace DeviceDataCollector.Services
{
    public class DeviceStatusMonitorService : BackgroundService
    {
        private readonly ILogger<DeviceStatusMonitorService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _checkInterval;
        private readonly TimeSpan _inactiveThreshold;

        public DeviceStatusMonitorService(
            ILogger<DeviceStatusMonitorService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;

            // Check device status every 5 seconds (could be configured in appsettings.json)
            _checkInterval = TimeSpan.FromSeconds(
                configuration.GetValue<int>("DeviceStatusMonitor:CheckIntervalSeconds", 5));

            // Mark devices as inactive if no status in 10 seconds (could be configured in appsettings.json)
            _inactiveThreshold = TimeSpan.FromSeconds(
                configuration.GetValue<int>("DeviceStatusMonitor:InactiveThresholdSeconds", 10));

            _logger.LogInformation(
                $"Device Status Monitor Service configured with check interval: {_checkInterval.TotalSeconds}s, " +
                $"inactive threshold: {_inactiveThreshold.TotalSeconds}s");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Device Status Monitor Service is starting...");

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await UpdateDeviceStatusesAsync(stoppingToken);
                    await Task.Delay(_checkInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // This is expected when the service is stopping
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Device Status Monitor Service");
            }
            finally
            {
                _logger.LogInformation("Device Status Monitor Service is stopping...");
            }
        }

        private async Task UpdateDeviceStatusesAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Create a new scope to resolve dependencies
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Calculate the cutoff time for inactive devices
                var cutoffTime = DateTime.Now.Subtract(_inactiveThreshold);

                // Find all active devices with LastConnectionTime older than the threshold (or null)
                var devicesToUpdate = await dbContext.Devices
                    .Where(d => d.IsActive &&
                           (!d.LastConnectionTime.HasValue || d.LastConnectionTime < cutoffTime))
                    .ToListAsync(stoppingToken);

                if (devicesToUpdate.Any())
                {
                    _logger.LogInformation($"Marking {devicesToUpdate.Count} devices as inactive due to inactivity");

                    // Mark these devices as inactive
                    foreach (var device in devicesToUpdate)
                    {
                        device.IsActive = false;
                        _logger.LogInformation($"Device {device.SerialNumber} marked as inactive - " +
                            $"Last connection: {(device.LastConnectionTime.HasValue ? device.LastConnectionTime.ToString() : "Never")}");
                    }

                    // Save changes to the database
                    await dbContext.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating device statuses");
            }
        }
    }
}