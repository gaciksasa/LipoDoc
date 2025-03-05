﻿using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeviceDataCollector.Services
{
    public class TCPServerService : BackgroundService
    {
        private readonly ILogger<TCPServerService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private TcpListener? _server;
        private readonly int _port;
        private readonly string _ipAddress = string.Empty;
        private readonly HashSet<string> _appIpAddresses;

        // Added to prevent duplicate processing
        private readonly HashSet<string> _processedMessageHashes = new HashSet<string>();
        private readonly object _lockObject = new object();
        private readonly TimeSpan _messageHashExpiration = TimeSpan.FromMinutes(5); // Messages older than this will be processed again
        private Timer? _cleanupTimer;

        // Define the separator characters consistently across the service
        private const char SEPARATOR = '\u00AA'; // Unicode 170 - this is the special separator character
        private const char QUESTION_SEPARATOR = '\u003F'; // Unicode 63 - Question mark separator
        private const char LINE_FEED = '\u000A'; // Unicode 10 - Line Feed character

        public TCPServerService(
            ILogger<TCPServerService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;

            _port = configuration.GetValue<int>("TCPServer:Port", 5000);
            _ipAddress = configuration.GetValue<string>("TCPServer:IPAddress", "127.0.0.2") ?? string.Empty;

            // List of IP addresses that are part of our application and shouldn't store data
            _appIpAddresses = new HashSet<string> {
                "127.0.0.1",
                "127.0.0.2",
                // "192.168.1.124",
                "::1",
                "localhost"
            };

            _logger.LogInformation($"TCP Server will bind to {_ipAddress}:{_port}");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("TCP Server Service is starting...");

            try
            {
                _server = new TcpListener(IPAddress.Parse(_ipAddress), _port);
                _server.Start();

                _logger.LogInformation($"TCP Server listening on {_ipAddress}:{_port}");

                // Start timer to clean up old message hashes
                _cleanupTimer = new Timer(CleanupOldMessageHashes, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

                while (!stoppingToken.IsCancellationRequested)
                {
                    TcpClient client = await _server.AcceptTcpClientAsync(stoppingToken);
                    _ = HandleClientAsync(client, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // This is expected when stopping
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in TCP server on {_ipAddress}:{_port}");
            }
            finally
            {
                _server?.Stop();
                _cleanupTimer?.Dispose();
                _logger.LogInformation("TCP Server Service is stopping...");
            }
        }

        private void CleanupOldMessageHashes(object? state)
        {
            try
            {
                // Remove message hashes older than expiration time
                lock (_lockObject)
                {
                    if (_processedMessageHashes.Count > 1000)
                    {
                        _logger.LogInformation($"Clearing message hash cache (size: {_processedMessageHashes.Count})");
                        _processedMessageHashes.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up message hashes");
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
        {
            string clientIP = ((IPEndPoint)(client.Client.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0))).Address.ToString();
            int clientPort = ((IPEndPoint)(client.Client.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0))).Port;

            // Log all connections for debugging
            _logger.LogInformation($"Client connected: {clientIP}:{clientPort}");

            // Register device even without data - this is a temporary ID until we get a real one
            string tempDeviceId = $"Device_{clientIP.Replace('.', '_')}";
            _logger.LogInformation($"Attempting immediate device registration with temporary ID: {tempDeviceId}");
            await ForceRegisterDeviceAsync(tempDeviceId, clientIP, clientPort);

            using NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[4096];

            try
            {
                while (!stoppingToken.IsCancellationRequested && client.Connected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, stoppingToken);

                    if (bytesRead == 0)
                        break;

                    string data = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                    _logger.LogInformation($"Received from {clientIP}:{clientPort}: {data}");

                    // Generate a unique hash for this message to prevent duplicates
                    string messageHash = ComputeMessageHash(data, clientIP, clientPort);
                    bool isNewMessage = false;

                    lock (_lockObject)
                    {
                        if (!_processedMessageHashes.Contains(messageHash))
                        {
                            _processedMessageHashes.Add(messageHash);
                            isNewMessage = true;
                        }
                    }

                    if (isNewMessage)
                    {
                        // Process and store the data
                        await ProcessDeviceMessageAsync(data, clientIP, clientPort);

                        // Send acknowledgment based on message type
                        await SendResponseAsync(client, data, clientIP, stoppingToken);
                    }
                    else
                    {
                        _logger.LogWarning($"Skipping duplicate message from {clientIP}:{clientPort}: {messageHash}");

                        // Still send acknowledgment so device doesn't retry
                        await SendResponseAsync(client, data, clientIP, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, $"Error handling client {clientIP}:{clientPort}");
                }
            }
            finally
            {
                client.Close();
                _logger.LogInformation($"Client disconnected: {clientIP}:{clientPort}");
            }
        }

        private string ComputeMessageHash(string message, string ipAddress, int port)
        {
            // Create a hash to identify this exact message
            using var sha = System.Security.Cryptography.SHA256.Create();
            string uniqueString = $"{message}|{ipAddress}|{port}|{DateTime.Now:yyyy-MM-dd}";
            byte[] inputBytes = Encoding.UTF8.GetBytes(uniqueString);
            byte[] hashBytes = sha.ComputeHash(inputBytes);

            // Convert to hex string
            return BitConverter.ToString(hashBytes).Replace("-", "");
        }

        private async Task ProcessDeviceMessageAsync(string message, string ipAddress, int port)
        {
            try
            {
                // Create a new scope to resolve the dependencies
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var messageParser = scope.ServiceProvider.GetRequiredService<DeviceMessageParser>();

                // Parse the message and store it in the database
                var parsedMessage = messageParser.ParseMessage(message, ipAddress, port);

                if (parsedMessage != null)
                {
                    if (parsedMessage is DeviceStatus status)
                    {
                        dbContext.DeviceStatuses.Add(status);
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Status from {ipAddress}:{port} stored in database");

                        // Also check if we have the device registered
                        await EnsureDeviceRegisteredAsync(dbContext, status.DeviceId);
                    }
                    else if (parsedMessage is DonationsData donationData)
                    {
                        dbContext.DonationsData.Add(donationData);
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Donation data from {ipAddress}:{port} stored in database");

                        // Also check if we have the device registered
                        await EnsureDeviceRegisteredAsync(dbContext, donationData.DeviceId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing and storing device message");
            }
        }

        private async Task EnsureDeviceRegisteredAsync(ApplicationDbContext dbContext, string deviceId)
        {
            // Add detailed logging at the beginning of the method
            _logger.LogInformation($"Attempting to register device with ID: {deviceId}");

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                _logger.LogWarning("Cannot register device: Empty or null device ID received");
                return;
            }

            try
            {
                // Check if the device is already registered
                var existingDevice = await dbContext.Devices.FirstOrDefaultAsync(d => d.SerialNumber == deviceId);

                if (existingDevice == null)
                {
                    // Register the device with default values
                    var device = new Device
                    {
                        SerialNumber = deviceId,
                        Name = $"Device {deviceId}",
                        RegisteredDate = DateTime.Now,
                        LastConnectionTime = DateTime.Now,
                        IsActive = true
                    };

                    _logger.LogInformation($"Registering new device: {deviceId}");
                    dbContext.Devices.Add(device);

                    try
                    {
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"✅ SUCCESS: New device registered: {deviceId}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ ERROR: Failed to save new device {deviceId} to database: {ex.Message}");
                        throw; // Re-throw so the calling method knows there was an error
                    }
                }
                else
                {
                    // Update last connection time AND set active status to true
                    _logger.LogInformation($"Updating existing device: {deviceId}, Previous connection: {existingDevice.LastConnectionTime}, Active status: {existingDevice.IsActive}");

                    existingDevice.LastConnectionTime = DateTime.Now;
                    existingDevice.IsActive = true; // Mark device as active when it connects

                    try
                    {
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"✅ SUCCESS: Updated device {deviceId}, new connection time: {existingDevice.LastConnectionTime}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"❌ ERROR: Failed to update existing device {deviceId}: {ex.Message}");
                        throw; // Re-throw so the calling method knows there was an error
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ ERROR: Unexpected error registering/updating device {deviceId}: {ex.Message}");
                throw; // Re-throw so the calling method knows there was an error
            }
        }

        private async Task ForceRegisterDeviceAsync(string deviceId, string ipAddress, int port)
        {
            _logger.LogInformation($"Force registering device: {deviceId} from {ipAddress}:{port}");

            try
            {
                // Create a new scope to resolve the dependencies
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // First check if this device already exists by serial number
                var existingDevice = await dbContext.Devices.FirstOrDefaultAsync(d => d.SerialNumber == deviceId);

                // Also check if we might have a device with matching IP in notes
                if (existingDevice == null)
                {
                    existingDevice = await dbContext.Devices.FirstOrDefaultAsync(
                        d => d.Notes != null && d.Notes.Contains($"Last IP: {ipAddress}"));
                }

                if (existingDevice == null)
                {
                    // Register the device with default values
                    var device = new Device
                    {
                        SerialNumber = deviceId,
                        Name = $"Device at {ipAddress}",
                        RegisteredDate = DateTime.Now,
                        LastConnectionTime = DateTime.Now,
                        IsActive = true,
                        Notes = $"Automatically registered\nLast IP: {ipAddress}\nLast Port: {port}\nRegistered on: {DateTime.Now}"
                    };

                    dbContext.Devices.Add(device);
                    await dbContext.SaveChangesAsync();
                    _logger.LogInformation($"*** NEW DEVICE REGISTERED: {deviceId} ***");
                }
                else
                {
                    _logger.LogInformation($"Updating existing device: {deviceId}");

                    // Update the device
                    existingDevice.LastConnectionTime = DateTime.Now;

                    // Optionally update notes with the new IP
                    if (existingDevice.SerialNumber == deviceId)
                    {
                        existingDevice.Notes = existingDevice.Notes?.Replace(
                            $"Last IP:", $"Previous IP:") + $"\nLast IP: {ipAddress}\nLast update: {DateTime.Now}";
                    }

                    await dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error registering temporary device: {ex.Message}");
            }
        }

        private async Task SendResponseAsync(TcpClient client, string receivedMessage, string clientIP, CancellationToken stoppingToken)
        {
            try
            {
                byte[] responseData = null;
                string deviceId = string.Empty;

                // Handle status messages
                if (receivedMessage.StartsWith("#S"))
                {
                    // Parse the status message to check for available data and get device ID
                    var (availableData, extractedDeviceId) = ParseStatusMessage(receivedMessage);
                    deviceId = extractedDeviceId; // Use the extracted device ID

                    if (!string.IsNullOrEmpty(deviceId) && availableData > 0)
                    {
                        // Request data from device if there's available data using hex format:
                        // #u (0x23 0x75) + separator (0xAA) + deviceId bytes + separator (0xAA) + LF (0x0A)
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

                            responseData = ms.ToArray();
                        }

                        _logger.LogInformation($"Device has {availableData} records available, requesting data");
                    }
                    else if (!string.IsNullOrEmpty(deviceId))
                    {
                        // Simply acknowledge the status message if no data available, using the same hex format
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

                            responseData = ms.ToArray();
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Could not extract device ID from status message, no response sent");
                    }
                }
                // Handle data messages
                else if (receivedMessage.StartsWith("#D"))
                {
                    // Extract device ID from data message
                    var parts = receivedMessage.Contains(SEPARATOR)
                        ? receivedMessage.Split(SEPARATOR)
                        : receivedMessage.Split(QUESTION_SEPARATOR);

                    if (parts.Length > 1)
                    {
                        deviceId = parts[1];
                    }

                    // Send acknowledgment for data messages if we have a device ID
                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        // Create acknowledgment message in hex format
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

                            responseData = ms.ToArray();
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Could not extract device ID from data message, no response sent");
                    }
                }
                // Handle "no more data" messages from device
                else if (receivedMessage.StartsWith("#U"))
                {
                    // Extract device ID from no more data message
                    var parts = receivedMessage.Contains(SEPARATOR)
                        ? receivedMessage.Split(SEPARATOR)
                        : receivedMessage.Split(QUESTION_SEPARATOR);

                    if (parts.Length > 1)
                    {
                        deviceId = parts[1];
                        _logger.LogInformation($"Device {deviceId} has no more data to send");
                    }
                    else
                    {
                        _logger.LogWarning("Could not extract device ID from no more data message");
                    }

                    // No response needed for "no more data" message
                }
                // Handle data request
                else if (receivedMessage.StartsWith("#u"))
                {
                    // Extract device ID from request message
                    var parts = receivedMessage.Contains(SEPARATOR)
                        ? receivedMessage.Split(SEPARATOR)
                        : receivedMessage.Split(QUESTION_SEPARATOR);

                    if (parts.Length > 1)
                    {
                        deviceId = parts[1];
                    }

                    // This is a request from us to the device, no direct response needed
                    // The device should respond with data or "no more data" message
                }

                // Send the response if it's not empty
                if (responseData != null && responseData.Length > 0)
                {
                    // Log the actual bytes being sent for debugging
                    _logger.LogDebug($"Sending bytes: {BitConverter.ToString(responseData)}");

                    await client.GetStream().WriteAsync(responseData, 0, responseData.Length, stoppingToken);

                    // Log with appropriate identifier
                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        _logger.LogInformation($"Sent binary response to {deviceId}: {BitConverter.ToString(responseData)}");
                    }
                    else
                    {
                        _logger.LogInformation($"Sent binary response to client {clientIP}: {BitConverter.ToString(responseData)}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending response to client {clientIP}");
            }
        }

        // Helper method to parse the number of available data records from a status message
        // and also return the device ID
        private (int availableData, string deviceId) ParseStatusMessage(string statusMessage)
        {
            try
            {
                // Expected formats: 
                // #S\u00AALD0000000\u00AA0\u00AA14:18:2826:02:2025\u00AA1\u00AAD7ý
                // #S\u003FLD0000000\u003F0\u003F02:05:5901:01:2020\u003F1\u003FC7\u003F

                // Determine which separator is being used
                char separator = statusMessage.Contains(SEPARATOR) ? SEPARATOR : QUESTION_SEPARATOR;
                var parts = statusMessage.Split(separator);

                // Log the parsing for debugging
                _logger.LogDebug($"Parsing status message with separator '{separator}', found {parts.Length} parts");

                string deviceId = string.Empty;
                int availableData = 0;

                // Device ID should be at index 1
                if (parts.Length > 1)
                {
                    deviceId = parts[1];
                    _logger.LogDebug($"Extracted device ID: {deviceId}");
                }

                // AvailableData should be at index 4
                if (parts.Length > 4 && int.TryParse(parts[4], out int parsedData))
                {
                    availableData = parsedData;
                    _logger.LogDebug($"Extracted available data count: {availableData}");
                }

                return (availableData, deviceId);
            }
            catch (Exception ex)
            {
                // Log parsing error but don't crash
                _logger.LogError(ex, "Error parsing status message");
                return (0, string.Empty); // Return empty values as fallback
            }
        }
    }
}