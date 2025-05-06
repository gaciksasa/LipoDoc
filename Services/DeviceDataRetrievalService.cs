using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Sockets;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace DeviceDataCollector.Services
{
    public class DeviceDataRetrievalService
    {
        private readonly ILogger<DeviceDataRetrievalService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly int _port;
        private readonly int _timeout;

        public DeviceDataRetrievalService(
            ILogger<DeviceDataRetrievalService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _configuration = configuration;

            _port = configuration.GetValue<int>("TCPServer:Port", 5000);
            _timeout = configuration.GetValue<int>("DeviceDataRetrieval:TimeoutMs", 5000);
        }

        /// <summary>
        /// Retrieves buffered data from all active devices
        /// </summary>
        public async Task RetrieveAllDevicesBufferedDataAsync()
        {
            _logger.LogInformation("Starting buffered data retrieval from all devices");

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Get all active devices
                var devices = await dbContext.Devices
                    .Where(d => d.IsActive)
                    .ToListAsync();

                _logger.LogInformation($"Found {devices.Count} active devices to retrieve data from");

                foreach (var device in devices)
                {
                    await RetrieveDeviceBufferedDataAsync(device);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving buffered data from devices");
            }
        }

        /// <summary>
        /// Retrieves buffered data from a specific device
        /// </summary>
        public async Task RetrieveDeviceBufferedDataAsync(Device device)
        {
            _logger.LogInformation($"Retrieving buffered data from device {device.SerialNumber}");

            try
            {
                // Get the last known IP address for the device
                string ipAddress = await GetDeviceIPAddressAsync(device.SerialNumber);

                if (string.IsNullOrEmpty(ipAddress))
                {
                    _logger.LogWarning($"No known IP address for device {device.SerialNumber}, cannot retrieve data");
                    return;
                }

                // Connect to the device and request buffered data
                await RequestAndProcessBufferedDataAsync(ipAddress, _port, device.SerialNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving buffered data from device {device.SerialNumber}");
            }
        }

        /// <summary>
        /// Retrieves the last known IP address for a device
        /// </summary>
        private async Task<string> GetDeviceIPAddressAsync(string serialNumber)
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // First try to get IP from latest status
            var latestStatus = await dbContext.DeviceStatuses
                .Where(s => s.DeviceId == serialNumber)
                .OrderByDescending(s => s.Timestamp)
                .FirstOrDefaultAsync();

            if (latestStatus != null && !string.IsNullOrEmpty(latestStatus.IPAddress))
            {
                return latestStatus.IPAddress;
            }

            // If no status, try from data records
            var latestData = await dbContext.DonationsData
                .Where(d => d.DeviceId == serialNumber)
                .OrderByDescending(d => d.Timestamp)
                .FirstOrDefaultAsync();

            if (latestData != null && !string.IsNullOrEmpty(latestData.IPAddress))
            {
                return latestData.IPAddress;
            }

            return null;
        }

        /// <summary>
        /// Connects to a device, requests buffered data, and processes responses
        /// </summary>
        private async Task RequestAndProcessBufferedDataAsync(string ipAddress, int port, string deviceId)
        {
            _logger.LogInformation($"Connecting to device {deviceId} at {ipAddress}:{port}");

            try
            {
                using (var client = new TcpClient())
                {
                    // Try to connect to the device
                    var connectTask = client.ConnectAsync(ipAddress, port);
                    var timeoutTask = Task.Delay(_timeout);

                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning($"Connection to {deviceId} at {ipAddress}:{port} timed out");
                        return;
                    }

                    if (!client.Connected)
                    {
                        _logger.LogWarning($"Failed to connect to {deviceId} at {ipAddress}:{port}");
                        return;
                    }

                    _logger.LogInformation($"Successfully connected to {deviceId} at {ipAddress}:{port}");

                    using (var stream = client.GetStream())
                    {
                        // Send data request message in hex format: #u (0x23 0x75) + separator (0xAA) + deviceId + separator (0xAA) + LF (0x0A)
                        using (var ms = new MemoryStream())
                        {
                            // #u prefix (0x23 0x75)
                            ms.WriteByte(0x23); // #
                            ms.WriteByte(0x75); // u

                            // Separator (0xAA)
                            ms.WriteByte(0xAA);

                            // Device ID bytes
                            byte[] deviceIdBytes = Encoding.ASCII.GetBytes(deviceId);
                            ms.Write(deviceIdBytes, 0, deviceIdBytes.Length);

                            // Second separator (0xAA)
                            ms.WriteByte(0xAA);

                            // Line feed (0x0A)
                            ms.WriteByte(0x0A);

                            byte[] requestData = ms.ToArray();
                            await stream.WriteAsync(requestData, 0, requestData.Length);
                            _logger.LogInformation($"Sent binary data request to {deviceId}: {BitConverter.ToString(requestData)}");
                        }

                        // Read responses until we get a #U message (no more data)
                        bool moreData = true;
                        int dataCount = 0;

                        while (moreData)
                        {
                            // Wait for response
                            var buffer = new byte[4096];
                            var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                            var readTimeoutTask = Task.Delay(_timeout);

                            var readCompletedTask = await Task.WhenAny(readTask, readTimeoutTask);
                            if (readCompletedTask == readTimeoutTask)
                            {
                                _logger.LogWarning($"No response from {deviceId} within timeout period");
                                break;
                            }

                            int bytesRead = await readTask;
                            if (bytesRead == 0)
                            {
                                _logger.LogWarning($"Connection closed by {deviceId}");
                                break;
                            }

                            string response = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                            _logger.LogInformation($"Received from {deviceId}: {response.Trim()}");

                            // Check if this is the "no more data" message (#U)
                            if (response.StartsWith("#U"))
                            {
                                _logger.LogInformation($"Device {deviceId} has no more buffered data");
                                moreData = false;
                            }
                            else if (response.StartsWith("#D"))
                            {
                                // This is data - we need to process it
                                await ProcessDeviceMessageAsync(response, ipAddress, port);
                                dataCount++;

                                // Send acknowledgment in hex format
                                using (var ms = new MemoryStream())
                                {
                                    // #A prefix (0x23 0x41)
                                    ms.WriteByte(0x23); // #
                                    ms.WriteByte(0x41); // A

                                    // Separator (0xAA)
                                    ms.WriteByte(0xAA);

                                    // Device ID bytes
                                    byte[] deviceIdBytes = Encoding.ASCII.GetBytes(deviceId);
                                    ms.Write(deviceIdBytes, 0, deviceIdBytes.Length);

                                    // Second separator (0xAA)
                                    ms.WriteByte(0xAA);

                                    // Line feed (0x0A)
                                    ms.WriteByte(0x0A);

                                    byte[] ackData = ms.ToArray();
                                    await stream.WriteAsync(ackData, 0, ackData.Length);
                                    _logger.LogInformation($"Sent binary acknowledgment to {deviceId}: {BitConverter.ToString(ackData)}");
                                }
                            }
                            else
                            {
                                _logger.LogWarning($"Unexpected response from {deviceId}: {response.Trim()}");
                                // Still send acknowledgment in case this helps, using hex format
                                using (var ms = new MemoryStream())
                                {
                                    // #A prefix (0x23 0x41)
                                    ms.WriteByte(0x23); // #
                                    ms.WriteByte(0x41); // A

                                    // Separator (0xAA)
                                    ms.WriteByte(0xAA);

                                    // Device ID bytes
                                    byte[] deviceIdBytes = Encoding.ASCII.GetBytes(deviceId);
                                    ms.Write(deviceIdBytes, 0, deviceIdBytes.Length);

                                    // Second separator (0xAA)
                                    ms.WriteByte(0xAA);

                                    // Line feed (0x0A)
                                    ms.WriteByte(0x0A);

                                    byte[] ackData = ms.ToArray();
                                    await stream.WriteAsync(ackData, 0, ackData.Length);
                                }
                            }
                        }

                        _logger.LogInformation($"Retrieved {dataCount} buffered data records from {deviceId}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error during data retrieval from {deviceId} at {ipAddress}:{port}");
            }
        }

        /// <summary>
        /// Processes a message received from a device
        /// </summary>
        private async Task ProcessDeviceMessageAsync(string message, string ipAddress, int port)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var messageParser = scope.ServiceProvider.GetRequiredService<DeviceMessageParser>();

                // Try to get the export helper (it's optional, so we use GetService rather than GetRequiredService)
                var exportHelper = scope.ServiceProvider.GetService<DonationExportHelper>();

                // Use the messageParser to determine message type
                var messageType = messageParser.DetermineMessageType(message);
                _logger.LogInformation($"Message type determined: {messageType}");

                // Parse the message and store it in the database
                var parsedMessage = messageParser.ParseMessage(message, ipAddress, port);

                if (parsedMessage != null)
                {
                    if (parsedMessage is DeviceStatus status)
                    {
                        dbContext.DeviceStatuses.Add(status);
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Buffered status from {ipAddress}:{port} stored in database");
                    }
                    else if (parsedMessage is DonationsData donationData)
                    {
                        // Store donation data
                        dbContext.DonationsData.Add(donationData);
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Donation data from {donationData.DeviceId} stored in database");

                        // Trigger auto-export if export helper is available
                        if (exportHelper != null)
                        {
                            try
                            {
                                await exportHelper.ExportDonationAsync(donationData);
                            }
                            catch (Exception exportEx)
                            {
                                _logger.LogError(exportEx, $"Error auto-exporting donation {donationData.Id}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing and storing buffered device message");
            }
        }
    }
}