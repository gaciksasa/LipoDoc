using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace DeviceDataCollector.Services
{
    public class ScheduledBackupService : BackgroundService
    {
        private readonly ILogger<ScheduledBackupService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private TimeSpan _interval;
        private int _retention;
        private bool _enabled;
        private string _scheduledTime;

        public ScheduledBackupService(
            ILogger<ScheduledBackupService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _configuration = configuration;

            // Load configuration
            ReloadConfiguration();
        }

        public void ReloadConfiguration()
        {
            // Get config values with defaults
            _enabled = _configuration.GetValue<bool>("DatabaseBackup:Scheduled:Enabled", false);
            _interval = TimeSpan.FromHours(_configuration.GetValue<int>("DatabaseBackup:Scheduled:IntervalHours", 24));
            _retention = _configuration.GetValue<int>("DatabaseBackup:Scheduled:RetentionCount", 7);
            _scheduledTime = _configuration.GetValue<string>("DatabaseBackup:Scheduled:Time", "03:00");

            _logger.LogInformation($"Scheduled backup service configuration loaded: Enabled={_enabled}, Interval={_interval.TotalHours}h, Retention={_retention}, Time={_scheduledTime}");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Scheduled backup service is starting...");

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    // Reload configuration in case it changed
                    ReloadConfiguration();

                    if (!_enabled)
                    {
                        _logger.LogInformation("Scheduled backup service is disabled. Waiting for configuration changes...");

                        // Create a linked token source that combines the stopping token and our config change token
                        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                            stoppingToken, _configChangeTokenSource.Token);

                        try
                        {
                            // Wait for either the application to stop or a config change
                            await Task.Delay(TimeSpan.FromMinutes(5), linkedTokenSource.Token);
                        }
                        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                        {
                            // If we get here, it's because of a config change, not app shutdown
                            _logger.LogInformation("Waking up to check for configuration changes");
                        }
                        continue;
                    }

                    // Calculate time until next backup
                    var delay = CalculateTimeUntilNextBackup();
                    _logger.LogInformation($"Next scheduled backup in {delay.TotalHours:F1} hours at {DateTime.Now.Add(delay).ToString("HH:mm:ss dd-MM-yyyy")}");

                    // Wait until next backup time or until configuration changes
                    try
                    {
                        // Create a linked token source that combines the stopping token and our config change token
                        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                            stoppingToken, _configChangeTokenSource.Token);

                        await Task.Delay(delay, linkedTokenSource.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // If we get here, it's because of a config change, not app shutdown
                        _logger.LogInformation("Schedule interrupted due to configuration change");
                        continue; // Skip to next loop iteration to recalculate
                    }

                    // Reload configuration again before performing backup
                    ReloadConfiguration();

                    if (_enabled)
                    {
                        // Perform the backup
                        await RunBackupAsync();

                        // Cleanup old backups
                        await CleanupOldBackupsAsync();
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // This is expected when stopping
                _logger.LogInformation("Scheduled backup service is being stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduled backup service");
            }
            finally
            {
                _logger.LogInformation("Scheduled backup service is stopping...");
                _configChangeTokenSource.Dispose();
            }
        }

        private async Task RunBackupAsync()
        {
            try
            {
                _logger.LogInformation("Starting scheduled backup...");

                using var scope = _scopeFactory.CreateScope();
                var backupService = scope.ServiceProvider.GetRequiredService<DatabaseBackupService>();

                var result = await backupService.CreateBackupAsync("Scheduled automatic backup");

                if (result.Success)
                {
                    _logger.LogInformation($"Scheduled backup completed successfully: {result.FileName}");
                }
                else
                {
                    _logger.LogError($"Scheduled backup failed: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error performing scheduled backup");
            }
        }

        private async Task CleanupOldBackupsAsync()
        {
            try
            {
                _logger.LogInformation($"Cleaning up old backups, keeping {_retention} most recent...");

                using var scope = _scopeFactory.CreateScope();
                var backupService = scope.ServiceProvider.GetRequiredService<DatabaseBackupService>();

                var backups = await backupService.GetBackupListAsync();
                var scheduledBackups = backups
                    .Where(b => b.Description == "Scheduled automatic backup")
                    .OrderByDescending(b => b.CreatedAt)
                    .Skip(_retention)
                    .ToList();

                foreach (var backup in scheduledBackups)
                {
                    _logger.LogInformation($"Deleting old backup: {backup.FileName}");
                    await backupService.DeleteBackupAsync(backup.FileName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old backups");
            }
        }

        private TimeSpan CalculateTimeUntilNextBackup()
        {
            // Parse the scheduled time
            var scheduledTimeParts = _scheduledTime.Split(':');
            int scheduledHour = int.Parse(scheduledTimeParts[0]);
            int scheduledMinute = int.Parse(scheduledTimeParts[1]);

            var now = DateTime.Now;
            var scheduledToday = new DateTime(now.Year, now.Month, now.Day, scheduledHour, scheduledMinute, 0);

            // If today's scheduled time has passed, move to tomorrow
            if (now > scheduledToday)
            {
                scheduledToday = scheduledToday.AddDays(1);
            }

            return scheduledToday - now;
        }

        private CancellationTokenSource _configChangeTokenSource = new CancellationTokenSource();

        public void NotifyConfigurationChanged()
        {
            _logger.LogInformation("Configuration change notification received, resetting schedule...");

            // Cancel the current waiting operation to force a recalculation
            var oldTokenSource = _configChangeTokenSource;
            _configChangeTokenSource = new CancellationTokenSource();

            try
            {
                oldTokenSource.Cancel();
                oldTokenSource.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error canceling previous token");
            }
        }
    }
}