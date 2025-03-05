using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Sockets;
using System.Text;

namespace DeviceDataCollector.Services
{
    public class BufferDataRetrievalService
    {
        private readonly ILogger<BufferDataRetrievalService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly int _port;
        private readonly int _timeout;

        public BufferDataRetrievalService(
            ILogger<BufferDataRetrievalService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;

            _port = configuration.GetValue<int>("TCPServer:Port", 5000);
            _timeout = configuration.GetValue<int>("BufferRetrieval:TimeoutMs", 5000);
        }

        /// <summary>
        /// Retrieves buffered data from a specific device
        /// </summary>
        public async Task RetrieveDeviceBufferedDataAsync(Device device)
        {
            _logger.LogInformation($"Retrieving buffered data from device {device.SerialNumber}");

            try
            {
                // Get the device's IP address
                string ipAddress = await GetDeviceIPAddressAsync(device.SerialNumber);
                if (string.IsNullOrEmpty(ipAddress))
                {
                    _logger.LogWarning($"No known IP address for device {device.SerialNumber}");
                    return;
                }

                using (var client = new TcpClient())
                {
                    // Connect to the device
                    try
                    {
                        await client.ConnectAsync(ipAddress, _port);
                        if (!client.Connected)
                        {
                            _logger.LogWarning($"Failed to connect to device at {ipAddress}:{_port}");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Connection error: {ex.Message}");
                        return;
                    }

                    using (var stream = client.GetStream())
                    {
                        // Send the buffer request message in hex format: #u (0x23 0x75) + separator (0xAA) + deviceId + separator (0xAA) + LF (0x0A)
                        byte[] dataRequestCommand = { 0x23, 0x75 }; // #u in ASCII
                        byte[] deviceId = Encoding.ASCII.GetBytes(device.SerialNumber);
                        byte[] separator = { 0xAA }; // Hex for the separator
                        byte[] lineFeed = { 0x0A }; // LF character

                        // Combine all parts
                        using (var ms = new MemoryStream())
                        {
                            ms.Write(dataRequestCommand, 0, dataRequestCommand.Length);
                            ms.Write(separator, 0, separator.Length);
                            ms.Write(deviceId, 0, deviceId.Length);
                            ms.Write(separator, 0, separator.Length);
                            ms.Write(lineFeed, 0, lineFeed.Length);

                            byte[] requestMessage = ms.ToArray();
                            await stream.WriteAsync(requestMessage, 0, requestMessage.Length);

                            // Log the message in hex format for debugging
                            _logger.LogInformation($"Sent binary request message to {device.SerialNumber}: {BitConverter.ToString(requestMessage)}");
                        }

                        _logger.LogInformation($"Sent buffer request to {device.SerialNumber}");

                        // Read responses in a loop until no more data
                        bool moreData = true;
                        int dataCount = 0;

                        while (moreData)
                        {
                            try
                            {
                                // Set timeout and read buffer
                                client.ReceiveTimeout = _timeout;
                                byte[] responseBuffer = new byte[4096];

                                int bytesRead = await stream.ReadAsync(responseBuffer, 0, responseBuffer.Length);
                                if (bytesRead == 0) break;

                                string response = Encoding.ASCII.GetString(responseBuffer, 0, bytesRead);
                                _logger.LogInformation($"Received from {device.SerialNumber}: {response}");

                                // Check if this is the "no more data" message
                                if (response.StartsWith("#U"))
                                {
                                    _logger.LogInformation($"Device {device.SerialNumber} has no more data");
                                    moreData = false;
                                }
                                else if (response.StartsWith("#D"))
                                {
                                    // Process the data message
                                    dataCount++;
                                    await ProcessMessageAsync(response, ipAddress, _port);

                                    // Send acknowledgment in hex format: #A (0x23 0x41) + separator (0xAA) + deviceId + separator (0xAA) + LF (0x0A)
                                    using (var ms = new MemoryStream())
                                    {
                                        // #A prefix (0x23 0x41)
                                        ms.WriteByte(0x23); // #
                                        ms.WriteByte(0x41); // A

                                        // Separator (0xAA)
                                        ms.WriteByte(0xAA);

                                        // Device ID bytes
                                        ms.Write(deviceId, 0, deviceId.Length);

                                        // Second separator (0xAA)
                                        ms.WriteByte(0xAA);

                                        // Line feed (0x0A)
                                        ms.WriteByte(0x0A);

                                        byte[] ackMessage = ms.ToArray();
                                        await stream.WriteAsync(ackMessage, 0, ackMessage.Length);

                                        // Log the acknowledgment in hex format
                                        _logger.LogInformation($"Sent binary acknowledgment to {device.SerialNumber}: {BitConverter.ToString(ackMessage)}");
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning($"Unexpected response format: {response}");
                                }
                            }
                            catch (IOException ioEx)
                            {
                                _logger.LogError(ioEx, "IO Exception during read");
                                break;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error processing device response");
                                break;
                            }
                        }

                        _logger.LogInformation($"Retrieved {dataCount} buffered records from {device.SerialNumber}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving buffered data: {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieves buffered data from all active devices
        /// </summary>
        public async Task RetrieveAllDevicesBufferedDataAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var activeDevices = await dbContext.Devices
                    .Where(d => d.IsActive)
                    .ToListAsync();

                _logger.LogInformation($"Found {activeDevices.Count} active devices");

                foreach (var device in activeDevices)
                {
                    await RetrieveDeviceBufferedDataAsync(device);

                    // Add a small delay between devices
                    await Task.Delay(1000);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving data from devices");
            }
        }

        private async Task<string> GetDeviceIPAddressAsync(string serialNumber)
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // First try from status records
            var latestStatus = await dbContext.DeviceStatuses
                .Where(s => s.DeviceId == serialNumber)
                .OrderByDescending(s => s.Timestamp)
                .FirstOrDefaultAsync();

            if (latestStatus != null && !string.IsNullOrEmpty(latestStatus.IPAddress))
            {
                return latestStatus.IPAddress;
            }

            // Then try from data records
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

        private async Task ProcessMessageAsync(string message, string ipAddress, int port)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var messageParser = scope.ServiceProvider.GetRequiredService<DeviceMessageParser>();

                var parsedMessage = messageParser.ParseMessage(message, ipAddress, port);

                if (parsedMessage is DonationsData donationData)
                {
                    dbContext.DonationsData.Add(donationData);
                    await dbContext.SaveChangesAsync();
                    _logger.LogInformation($"Stored donation data with ID: {donationData.Id}");
                }
                else if (parsedMessage is DeviceStatus status)
                {
                    dbContext.DeviceStatuses.Add(status);
                    await dbContext.SaveChangesAsync();
                    _logger.LogInformation($"Stored device status");
                }
                else if (parsedMessage == null)
                {
                    _logger.LogWarning($"Could not parse message: {message}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing message: {message}");
            }
        }
    }
}