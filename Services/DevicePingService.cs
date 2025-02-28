using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Sockets;
using System.Text;

namespace DeviceDataCollector.Services
{
    public class DevicePingService : BackgroundService
    {
        private readonly ILogger<DevicePingService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly TimeSpan _pingInterval;
        private readonly int _pingPort;
        private readonly int _pingTimeout;

        // Track consecutive failures before marking a device inactive
        private readonly int _maxFailuresBeforeInactive;
        private readonly Dictionary<string, int> _deviceFailureCount = new Dictionary<string, int>();

        public DevicePingService(
            ILogger<DevicePingService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _configuration = configuration;

            // Get ping configuration from appsettings.json or use defaults
            _pingInterval = TimeSpan.FromSeconds(
                configuration.GetValue<int>("DevicePing:IntervalSeconds", 60));
            _pingPort = configuration.GetValue<int>("DevicePing:Port", 5000);
            _pingTimeout = configuration.GetValue<int>("DevicePing:TimeoutMs", 2000);
            _maxFailuresBeforeInactive = configuration.GetValue<int>("DevicePing:MaxFailuresBeforeInactive", 3);

            _logger.LogInformation($"Device Ping Service configured with interval: {_pingInterval.TotalSeconds}s, " +
                                  $"port: {_pingPort}, timeout: {_pingTimeout}ms, " +
                                  $"max failures: {_maxFailuresBeforeInactive}");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Device Ping Service is starting...");

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await PingAllDevicesAsync(stoppingToken);
                    await Task.Delay(_pingInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // This is expected when the service is stopping
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Device Ping Service");
            }
            finally
            {
                _logger.LogInformation("Device Ping Service is stopping...");
            }
        }

        private async Task PingAllDevicesAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Create a new scope to resolve the dependencies
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Get all registered devices including their active status
                var devices = await dbContext.Devices
                    .Select(d => new { d.Id, d.SerialNumber, d.LastConnectionTime, d.IsActive })
                    .ToListAsync(stoppingToken);

                _logger.LogInformation($"Pinging {devices.Count} devices...");

                foreach (var device in devices)
                {
                    // Skip very recent connections to avoid excessive pinging
                    if (device.LastConnectionTime.HasValue &&
                        DateTime.Now - device.LastConnectionTime.Value < TimeSpan.FromMinutes(5))
                    {
                        _logger.LogDebug($"Skipping recent device: {device.SerialNumber}");
                        continue;
                    }

                    // Try to get the device IP from the latest status or data records
                    var latestIP = await GetDeviceIPAddressAsync(dbContext, device.SerialNumber, stoppingToken);
                    if (string.IsNullOrEmpty(latestIP))
                    {
                        _logger.LogWarning($"No known IP address for device: {device.SerialNumber}");
                        continue;
                    }

                    // Ping the device
                    var isAlive = await PingDeviceAsync(latestIP, _pingPort, device.SerialNumber, stoppingToken);

                    // Check if we need to update device status
                    await UpdateDeviceStatusAsync(dbContext, device.SerialNumber, device.Id, device.IsActive, isAlive);

                    _logger.LogInformation($"Device {device.SerialNumber} at {latestIP}:{_pingPort} is {(isAlive ? "responding" : "not responding")}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while pinging devices");
            }
        }

        private async Task UpdateDeviceStatusAsync(ApplicationDbContext dbContext, string serialNumber, int deviceId, bool currentStatus, bool isAlive)
        {
            try
            {
                // Initialize failure count for this device if it doesn't exist
                if (!_deviceFailureCount.ContainsKey(serialNumber))
                {
                    _deviceFailureCount[serialNumber] = 0;
                }

                if (isAlive)
                {
                    // Device is responding - reset failure count
                    _deviceFailureCount[serialNumber] = 0;

                    // If device was inactive, mark it as active
                    if (!currentStatus)
                    {
                        var device = await dbContext.Devices.FindAsync(deviceId);
                        if (device != null)
                        {
                            device.IsActive = true;
                            await dbContext.SaveChangesAsync();
                            _logger.LogInformation($"Device {serialNumber} is now marked as ACTIVE");
                        }
                    }
                }
                else
                {
                    // Device is not responding - increment failure count
                    _deviceFailureCount[serialNumber]++;

                    // If we've reached max failures and device is active, mark it inactive
                    if (_deviceFailureCount[serialNumber] >= _maxFailuresBeforeInactive && currentStatus)
                    {
                        var device = await dbContext.Devices.FindAsync(deviceId);
                        if (device != null)
                        {
                            device.IsActive = false;
                            await dbContext.SaveChangesAsync();
                            _logger.LogWarning($"Device {serialNumber} is now marked as INACTIVE after {_deviceFailureCount[serialNumber]} consecutive failures");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating status for device {serialNumber}");
            }
        }

        private async Task<string> GetDeviceIPAddressAsync(ApplicationDbContext dbContext, string serialNumber, CancellationToken stoppingToken)
        {
            // First try to get IP from latest status
            var latestStatus = await dbContext.DeviceStatuses
                .Where(s => s.DeviceId == serialNumber)
                .OrderByDescending(s => s.Timestamp)
                .FirstOrDefaultAsync(stoppingToken);

            if (latestStatus != null && !string.IsNullOrEmpty(latestStatus.IPAddress))
            {
                return latestStatus.IPAddress;
            }

            // If no status, try from data records
            var latestData = await dbContext.DonationsData
                .Where(d => d.DeviceId == serialNumber)
                .OrderByDescending(d => d.Timestamp)
                .FirstOrDefaultAsync(stoppingToken);

            if (latestData != null && !string.IsNullOrEmpty(latestData.IPAddress))
            {
                return latestData.IPAddress;
            }

            return null;
        }

        private async Task<bool> PingDeviceAsync(string ipAddress, int port, string deviceId, CancellationToken stoppingToken)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    // Set timeout for connection
                    var connectTask = client.ConnectAsync(ipAddress, port);
                    var timeoutTask = Task.Delay(_pingTimeout, stoppingToken);

                    // Wait for either connection or timeout
                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        _logger.LogDebug($"Connection to {ipAddress}:{port} timed out");
                        return false;
                    }

                    // Check if connection was successful
                    if (!client.Connected)
                    {
                        return false;
                    }

                    // Send a status request message
                    using (var stream = client.GetStream())
                    {
                        // Format: #u|deviceId (status request message)
                        var pingMessage = $"#u|{deviceId}\r\n";
                        var data = Encoding.ASCII.GetBytes(pingMessage);

                        await stream.WriteAsync(data, 0, data.Length, stoppingToken);
                        _logger.LogDebug($"Sent ping message to {ipAddress}:{port}: {pingMessage}");

                        // Set read timeout
                        client.ReceiveTimeout = _pingTimeout;

                        // Try to read response
                        var buffer = new byte[1024];
                        var readTask = stream.ReadAsync(buffer, 0, buffer.Length, stoppingToken);
                        var readTimeoutTask = Task.Delay(_pingTimeout, stoppingToken);

                        var readCompletedTask = await Task.WhenAny(readTask, readTimeoutTask);
                        if (readCompletedTask == readTimeoutTask)
                        {
                            _logger.LogDebug($"No response from {ipAddress}:{port}");
                            return true; // Device is reachable even without response
                        }

                        var bytesRead = await readTask;
                        if (bytesRead > 0)
                        {
                            var response = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                            _logger.LogDebug($"Received response from {ipAddress}:{port}: {response}");
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"Error pinging device at {ipAddress}:{port}");
                return false;
            }
        }
    }
}