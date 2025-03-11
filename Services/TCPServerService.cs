using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
        private const char QUESTION_SEPARATOR = '\u003F'; // Unicode 63 - Question mark separator (?)
        private const char PIPE_SEPARATOR = '\u007C'; // Unicode 124 - Pipe separator (|)
        private const char STAR_SEPARATOR = '\u002A'; // Unicode 42 - Star separator (*)
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
                "192.168.1.124",
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

                    // Detect message type
                    string messageType = "Unknown";
                    if (data.StartsWith("#S")) messageType = "Status";
                    else if (data.StartsWith("#D")) messageType = "Data";
                    else if (data.StartsWith("#u")) messageType = "Request";
                    else if (data.StartsWith("#A")) messageType = "Acknowledge";
                    else if (data.StartsWith("#U")) messageType = "NoMoreData";

                    _logger.LogInformation($"Message type from {clientIP}:{clientPort}: {messageType}");

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

                        // Only send acknowledgment for Data messages
                        if (messageType == "Data")
                        {
                            await SendSimpleAcknowledgmentAsync(client, data, clientIP, stoppingToken);
                            _logger.LogInformation($"Sent acknowledgment for Data message from {clientIP}:{clientPort}");
                        }
                        else if (messageType == "NoMoreData")
                        {
                            // For #U messages, we don't send any response
                            _logger.LogInformation($"No response needed for NoMoreData message from {clientIP}:{clientPort}");
                        }
                        else if (messageType == "Status")
                        {
                            // For #S messages, we process but don't send a response
                            _logger.LogInformation($"Received Status message from {clientIP}:{clientPort}. No response needed.");
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"Skipping already processed message from {clientIP}:{clientPort}");

                        // Only send acknowledgment for Data messages, even for duplicates
                        if (messageType == "Data")
                        {
                            await SendSimpleAcknowledgmentAsync(client, data, clientIP, stoppingToken);
                            _logger.LogInformation($"Sent acknowledgment for duplicate Data message from {clientIP}:{clientPort}");
                        }
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
            using var sha = SHA256.Create();
            string uniqueString = $"{message}|{ipAddress}|{port}|{DateTime.Now:dd.MM.yyyy}";
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
                        // First, register or update device status based on the status message
                        // This is now the ONLY way devices are registered - through status messages
                        await RegisterOrUpdateDeviceFromStatusAsync(dbContext, status, ipAddress);

                        // Option 1: Only update the current status instead of adding to history
                        await UpdateCurrentDeviceStatusAsync(dbContext, status);

                        // Option 2: Save to history selectively or with a different frequency
                        // You can implement different strategies here, such as:
                        // 1. Only save a history record if the status changed significantly
                        // 2. Save a history record every N minutes regardless of changes
                        // 3. Save a history record based on other business rules

                        // Example of selective history recording (uncomment if needed):
                        // await MaybeRecordStatusHistoryAsync(dbContext, status);
                    }
                    else if (parsedMessage is DonationsData donationData)
                    {
                        dbContext.DonationsData.Add(donationData);
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Donation data from {ipAddress}:{port} stored in database");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing and storing device message");
            }
        }

        /// Method to update the current device status with robust error handling
        private async Task UpdateCurrentDeviceStatusAsync(ApplicationDbContext dbContext, DeviceStatus status)
        {
            try
            {
                // First check if the table exists to avoid unexpected errors
                bool tableExists = true;
                try
                {
                    // This will throw an exception if the table doesn't exist
                    await dbContext.Database.ExecuteSqlRawAsync("SELECT 1 FROM CurrentDeviceStatuses LIMIT 1");
                }
                catch
                {
                    tableExists = false;
                    _logger.LogWarning("CurrentDeviceStatuses table does not exist. Falling back to legacy behavior.");

                    // Fall back to the old behavior - add to history
                    dbContext.DeviceStatuses.Add(status);
                    await dbContext.SaveChangesAsync();
                    return;
                }

                // If we get here, the table exists
                if (tableExists)
                {
                    try
                    {
                        // Try to find existing current status for this device
                        var currentStatus = await dbContext.CurrentDeviceStatuses
                            .FirstOrDefaultAsync(s => s.DeviceId == status.DeviceId);

                        if (currentStatus == null)
                        {
                            // First status for this device, create a new record
                            currentStatus = new CurrentDeviceStatus
                            {
                                DeviceId = status.DeviceId,
                                Timestamp = status.Timestamp,
                                Status = status.Status,
                                AvailableData = status.AvailableData,
                                IPAddress = status.IPAddress,
                                Port = status.Port,
                                CheckSum = status.CheckSum,
                                StatusUpdateCount = 1
                            };
                            dbContext.CurrentDeviceStatuses.Add(currentStatus);
                            _logger.LogInformation($"Created initial current status for device {status.DeviceId}");
                        }
                        else
                        {
                            // Update existing status
                            currentStatus.Timestamp = status.Timestamp;
                            currentStatus.Status = status.Status;
                            currentStatus.AvailableData = status.AvailableData;
                            currentStatus.IPAddress = status.IPAddress;
                            currentStatus.Port = status.Port;
                            currentStatus.CheckSum = status.CheckSum;
                            currentStatus.StatusUpdateCount++; // Increment the update counter

                            _logger.LogDebug($"Updated current status for device {status.DeviceId}");
                        }

                        await dbContext.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error updating current status for device {status.DeviceId}");

                        // Fall back to the old behavior
                        dbContext.DeviceStatuses.Add(status);
                        await dbContext.SaveChangesAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Unexpected error in UpdateCurrentDeviceStatusAsync for device {status.DeviceId}");

                // As a last resort, try to use the legacy approach
                try
                {
                    dbContext.DeviceStatuses.Add(status);
                    await dbContext.SaveChangesAsync();
                    _logger.LogInformation("Used legacy approach to save device status due to error with CurrentDeviceStatuses");
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "Failed to save device status using legacy approach as well");
                }
            }
        }

        private async Task RegisterOrUpdateDeviceFromStatusAsync(ApplicationDbContext dbContext, DeviceStatus status, string currentIpAddress)
        {
            string deviceId = status.DeviceId;
            _logger.LogInformation($"Processing device registration for device ID: {deviceId} from IP: {currentIpAddress}");

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                _logger.LogWarning("Cannot register device: Empty or null device ID received");
                return;
            }

            try
            {
                // Check if this device already exists
                var existingDevice = await dbContext.Devices.FirstOrDefaultAsync(d => d.SerialNumber == deviceId);

                if (existingDevice == null)
                {
                    // Register a new device
                    _logger.LogInformation($"Registering new device: {deviceId}");
                    var device = new Device
                    {
                        SerialNumber = deviceId,
                        Name = $"Device {deviceId}",
                        RegisteredDate = DateTime.Now,
                        LastConnectionTime = DateTime.Now,
                        IsActive = true,
                        Notes = $"Device auto-registered from status message\nIP: {currentIpAddress}\nRegistered: {DateTime.Now}\nStatus: ACTIVE"
                    };

                    dbContext.Devices.Add(device);
                    await dbContext.SaveChangesAsync();
                    _logger.LogInformation($"SUCCESS: New device registered: {deviceId}");
                }
                else
                {
                    // Update existing device status
                    bool wasInactive = !existingDevice.IsActive;
                    existingDevice.LastConnectionTime = DateTime.Now;
                    existingDevice.IsActive = true;
                    await dbContext.SaveChangesAsync();

                    if (wasInactive)
                    {
                        _logger.LogInformation($"SUCCESS: Device {deviceId} marked as ACTIVE after receiving status message");
                    }
                    else
                    {
                        _logger.LogInformation($"SUCCESS: Updated device {deviceId} last connection time");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ERROR: Unexpected error registering/updating device {deviceId}: {ex.Message}");
            }
        }

        private async Task SendSimpleAcknowledgmentAsync(TcpClient client, string receivedMessage, string clientIP, CancellationToken stoppingToken)
        {
            try
            {
                byte[] responseData = null;
                string deviceId = string.Empty;

                // Extract device ID from the message, checking various separators
                char detectedSeparator = '\0';
                if (receivedMessage.Contains(SEPARATOR)) detectedSeparator = SEPARATOR;
                else if (receivedMessage.Contains(QUESTION_SEPARATOR)) detectedSeparator = QUESTION_SEPARATOR;
                else if (receivedMessage.Contains(PIPE_SEPARATOR)) detectedSeparator = PIPE_SEPARATOR;
                else if (receivedMessage.Contains(STAR_SEPARATOR)) detectedSeparator = STAR_SEPARATOR;

                if (detectedSeparator != '\0')
                {
                    var parts = receivedMessage.Split(detectedSeparator);
                    if (parts.Length > 1)
                    {
                        deviceId = parts[1];
                    }
                }

                // Create a generic acknowledgment message in hex format if we have a device ID
                if (!string.IsNullOrEmpty(deviceId))
                {
                    _logger.LogInformation($"Sending acknowledgment to device ID: {deviceId}");

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

                    // Send the response
                    if (responseData != null && responseData.Length > 0)
                    {
                        await client.GetStream().WriteAsync(responseData, 0, responseData.Length, stoppingToken);

                        // Log in the requested format with special characters clearly identified
                        _logger.LogInformation($"Sent acknowledgment to {deviceId}: #AªLD{deviceId}ª<LF>");

                        // Also log the byte representation for debugging if needed
                        string hexResponse = BitConverter.ToString(responseData);
                        _logger.LogDebug($"Raw bytes: {hexResponse}");
                    }
                }
                else
                {
                    _logger.LogWarning($"Could not extract device ID from message: {receivedMessage}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending acknowledgment to client {clientIP}");
            }
        }
    }
}