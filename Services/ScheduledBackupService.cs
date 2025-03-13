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
        private readonly TimeSpan _interval;
        private readonly int _retention;
        private readonly bool _enabled;
        private readonly string _scheduledTime;

        public ScheduledBackupService(
            ILogger<ScheduledBackupService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;

            // Get config values with defaults
            _enabled = configuration.GetValue<bool>("DatabaseBackup:Scheduled:Enabled", false);
            _interval = TimeSpan.FromHours(configuration.GetValue<int>("DatabaseBackup:Scheduled:IntervalHours", 24));
            _retention = configuration.GetValue<int>("DatabaseBackup:Scheduled:RetentionCount", 7);
            _scheduledTime = configuration.GetValue<string>("DatabaseBackup:Scheduled:Time", "03:00");

            _logger.LogInformation($"Scheduled backup service configured with: Enabled={_enabled}, Interval={_interval.TotalHours}h, Retention={_retention}, Time={_scheduledTime}");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_enabled)
            {
                _logger.LogInformation("Scheduled backup service is disabled");
                return;
            }

            _logger.LogInformation("Scheduled backup service is starting...");

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    // Calculate time until next backup
                    var delay = CalculateTimeUntilNextBackup();
                    _logger.LogInformation($"Next scheduled backup in {delay.TotalHours:F1} hours");

                    // Wait until next backup time
                    await Task.Delay(delay, stoppingToken);

                    // Perform the backup
                    await RunBackupAsync();

                    // Cleanup old backups
                    await CleanupOldBackupsAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // This is expected when stopping
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduled backup service");
            }
            finally
            {
                _logger.LogInformation("Scheduled backup service is stopping...");
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
    }
}