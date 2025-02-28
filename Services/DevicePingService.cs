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
            // Using very short intervals for rapid status changes
            _pingInterval = TimeSpan.FromSeconds(
                configuration.GetValue<int>("DevicePing:IntervalSeconds", 10)); // Reduced to 10 seconds
            _pingPort = configuration.GetValue<int>("DevicePing:Port", 5000);
            _pingTimeout = configuration.GetValue<int>("DevicePing:TimeoutMs", 1000); // Reduced to 1 second
            _maxFailuresBeforeInactive = configuration.GetValue<int>("DevicePing:MaxFailuresBeforeInactive", 1); // Set to 1 for immediate change

            _logger.LogInformation($"Rapid Device Ping Service configured with interval: {_pingInterval.TotalSeconds}s, " +
                                  $"port: {_pingPort}, timeout: {_pingTimeout}ms, " +
                                  $"max failures: {_maxFailuresBeforeInactive} (immediate status change)");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Rapid Device Ping Service is starting...");

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

                // Get all registered devices
                var devices = await dbContext.Devices.ToListAsync(stoppingToken);

                _logger.LogInformation($"Pinging {devices.Count} devices...");

                foreach (var device in devices)
                {
                    // Skip devices with very recent connections (they're definitely active)
                    TimeSpan skipTimeThreshold = TimeSpan.FromSeconds(30); // Reduced to just 30 seconds
                    if (device.LastConnectionTime.HasValue &&
                        DateTime.Now - device.LastConnectionTime.Value < skipTimeThreshold)
                    {
                        _logger.LogDebug($"Skipping recent device: {device.SerialNumber} (connected {(DateTime.Now - device.LastConnectionTime.Value).TotalSeconds:F1} seconds ago)");

                        // Make sure device is marked as active if it has a recent connection
                        if (!device.IsActive)
                        {
                            device.IsActive = true;
                            await dbContext.SaveChangesAsync();
                            _logger.LogInformation($"Device {device.SerialNumber} marked as ACTIVE due to recent connection");
                        }

                        // Reset failure count for recently connected devices
                        _deviceFailureCount[device.SerialNumber] = 0;
                        continue;
                    }

                    // Try to get the device IP from the latest status or data records
                    var latestIP = await GetDeviceIPAddressAsync(dbContext, device.SerialNumber, stoppingToken);
                    if (string.IsNullOrEmpty(latestIP))
                    {
                        _logger.LogWarning($"No known IP address for device: {device.SerialNumber}, cannot ping");

                        // If no known IP and device is active, mark as inactive immediately
                        if (device.IsActive)
                        {
                            device.IsActive = false;
                            await dbContext.SaveChangesAsync();
                            _logger.LogWarning($"Device {device.SerialNumber} marked as INACTIVE (no known IP address)");
                        }
                        continue;
                    }

                    _logger.LogDebug($"Pinging device {device.SerialNumber} at {latestIP}:{_pingPort}...");

                    // Ping the device with quick timeout
                    var isAlive = await PingDeviceAsync(latestIP, _pingPort, device.SerialNumber, stoppingToken);

                    // Update device status immediately
                    if (isAlive && !device.IsActive)
                    {
                        device.IsActive = true;
                        device.LastConnectionTime = DateTime.Now;
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"🟢 Device {device.SerialNumber} marked as ACTIVE (ping successful)");
                    }
                    else if (!isAlive && device.IsActive)
                    {
                        device.IsActive = false;
                        await dbContext.SaveChangesAsync();
                        _logger.LogWarning($"🔴 Device {device.SerialNumber} marked as INACTIVE (ping failed)");
                    }

                    _logger.LogInformation($"Device {device.SerialNumber} at {latestIP}:{_pingPort} is {(isAlive ? "RESPONDING" : "NOT RESPONDING")}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while pinging devices");
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
                    // Use quick timeout for immediate status detection
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
                        _logger.LogDebug($"Failed to connect to {ipAddress}:{port}");
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

                        // Use quick timeout for response
                        client.ReceiveTimeout = _pingTimeout;

                        // Try to read response with quick timeout
                        var buffer = new byte[1024];
                        var readTask = stream.ReadAsync(buffer, 0, buffer.Length, stoppingToken);
                        var readTimeoutTask = Task.Delay(_pingTimeout, stoppingToken);

                        var readCompletedTask = await Task.WhenAny(readTask, readTimeoutTask);
                        if (readCompletedTask == readTimeoutTask)
                        {
                            _logger.LogDebug($"No response from {ipAddress}:{port} after sending ping message");
                            return false;
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