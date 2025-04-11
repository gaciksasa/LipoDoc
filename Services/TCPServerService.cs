using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using static DeviceDataCollector.Services.DeviceMessageParser;

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

        private static readonly ConcurrentDictionary<string, string> _pendingSerialChanges = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, TcpClient> _activeClients = new ConcurrentDictionary<string, TcpClient>();
        private static readonly ConcurrentDictionary<string, string> _pendingTimeSync = new ConcurrentDictionary<string, string>();
        private static readonly ConcurrentDictionary<string, bool> _pendingSetupRequests = new ConcurrentDictionary<string, bool>();
        private static readonly ConcurrentDictionary<string, DeviceSetup> _pendingSetupUpdates = new ConcurrentDictionary<string, DeviceSetup>();

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
            _ipAddress = configuration.GetValue<string>("TCPServer:IPAddress", "127.0.0.1") ?? string.Empty;

            // List of IP addresses that are part of our application and shouldn't store data
            _appIpAddresses = new HashSet<string> {
                "127.0.0.1",
                "127.0.0.2",
                "192.168.1.130",
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
            string connectedDeviceId = null;

            _logger.LogInformation($"Client connected: {clientIP}:{clientPort}");

            using NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[4096];

            try
            {
                while (!stoppingToken.IsCancellationRequested && client.Connected)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, stoppingToken);
                    }
                    catch (IOException)
                    {
                        // Client disconnected
                        break;
                    }

                    if (bytesRead == 0)
                        break;

                    string data = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                    _logger.LogInformation($"Received from {clientIP}:{clientPort}: {data}");

                    // Extract device ID from the message
                    string extractedDeviceId = ExtractDeviceId(data);

                    if (!string.IsNullOrEmpty(extractedDeviceId))
                    {
                        connectedDeviceId = extractedDeviceId;

                        // Track this client
                        _activeClients[connectedDeviceId] = client;
                    }

                    // Detect message type
                    string messageType = "Unknown";
                    if (data.StartsWith("#S")) messageType = "Status";
                    else if (data.StartsWith("#D")) messageType = "Data";
                    else if (data.StartsWith("#u")) messageType = "Request";
                    else if (data.StartsWith("#A")) messageType = "Acknowledge";
                    else if (data.StartsWith("#U")) messageType = "NoMoreData";
                    else if (data.StartsWith("#i")) messageType = "SerialUpdateRequest";
                    else if (data.StartsWith("#I")) messageType = "SerialUpdateResponse";

                    _logger.LogInformation($"Message type from {clientIP}:{clientPort}: {messageType}");

                    // First process normal message
                    await ProcessDeviceMessageAsync(data, clientIP, clientPort);

                    // Then send acknowledgment immediately within the same method
                    if (messageType == "Status" && !string.IsNullOrEmpty(connectedDeviceId))
                    {
                        try
                        {
                            // Send acknowledgment directly using the current stream
                            await SendStatusAcknowledgmentDirect(stream, connectedDeviceId, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error sending status acknowledgment to {connectedDeviceId}");
                        }

                        // Handle pending serial changes and time syncing here
                        if (_pendingSerialChanges.TryRemove(connectedDeviceId, out string newSerialNumber))
                        {
                            _logger.LogInformation($"Found pending serial number change for device {connectedDeviceId} -> {newSerialNumber}");
                            try
                            {
                                // Send serial number change command using the current stream
                                await SendSerialUpdateCommandDirect(stream, connectedDeviceId, newSerialNumber, stoppingToken);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Error sending serial update command to {connectedDeviceId}: {ex.Message}");
                            }
                        }

                        if (_pendingTimeSync.TryRemove(connectedDeviceId, out string dateTimeFormat))
                        {
                            _logger.LogInformation($"Found pending time sync for device {connectedDeviceId} -> {dateTimeFormat}");
                            try
                            {
                                // Send time sync command using the current stream
                                await SendTimeSyncCommandDirect(stream, connectedDeviceId, dateTimeFormat, stoppingToken);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Error sending time sync command to {connectedDeviceId}: {ex.Message}");
                            }
                        }

                        if (_pendingSetupUpdates.TryRemove(connectedDeviceId, out DeviceSetup setupToUpdate))
                        {
                            _logger.LogInformation($"Found pending setup update for device {connectedDeviceId}");
                            try
                            {
                                // Send setup update command using the current stream
                                await SendSetupUpdateCommandDirect(stream, setupToUpdate, stoppingToken);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Error sending setup update command to {connectedDeviceId}: {ex.Message}");
                            }
                        }

                        if (_pendingSetupRequests.TryRemove(connectedDeviceId, out _))
                        {
                            _logger.LogInformation($"Found pending setup request for device {connectedDeviceId}");
                            try
                            {
                                // Send setup request command using the current stream
                                await SendSetupRequestDirect(stream, connectedDeviceId, stoppingToken);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Error sending setup request to {connectedDeviceId}: {ex.Message}");
                            }
                        }
                    }
                    else if (messageType == "SerialUpdateResponse")
                    {
                        // This is the response to our serial number change command
                        _logger.LogInformation($"Received serial update response: {data}");
                        // Process the response
                        await HandleSerialUpdateResponseAsync(client, data, stoppingToken);
                    }
                    else if (messageType == "Data")
                    {
                        // For data messages, send a data acknowledgment
                        try
                        {
                            await SendDataAcknowledgmentDirect(stream, connectedDeviceId, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error sending data acknowledgment to {connectedDeviceId}");
                        }
                    }
                    if (data.StartsWith("#w") || data.StartsWith("#f"))
                    {
                        // This is a response to our setup update command
                        _logger.LogInformation($"Received setup update response: {data}");

                        // Parse the response to extract the device ID
                        string deviceId = ExtractDeviceId(data);
                        _logger.LogInformation($"Device {deviceId} sent setup response: {data}");

                        // Store the response in the database for reference
                        using var scope = _scopeFactory.CreateScope();
                        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                        // Get the latest setup for this device
                        var latestSetup = await dbContext.DeviceSetups
                            .Where(s => s.DeviceId == deviceId)
                            .OrderByDescending(s => s.Timestamp)
                            .FirstOrDefaultAsync(stoppingToken);

                        if (latestSetup != null)
                        {
                            if (data.StartsWith("#f"))
                            {
                                // Update the RawResponse to include confirmation
                                latestSetup.RawResponse += $"\n\nDevice confirmed setup update at {DateTime.Now}: {data}";
                                await dbContext.SaveChangesAsync(stoppingToken);
                                _logger.LogInformation($"Updated device setup with confirmation for {deviceId}");
                            }
                            else if (data.StartsWith("#w"))
                            {
                                // Update the RawResponse to include acknowledgment
                                latestSetup.RawResponse += $"\n\nDevice acknowledged setup command at {DateTime.Now}: {data}";
                                await dbContext.SaveChangesAsync(stoppingToken);
                                _logger.LogInformation($"Updated device setup with acknowledgment for {deviceId}");
                            }
                        }

                        // Add a notification about the setup response
                        var notification = new SystemNotification
                        {
                            Type = "SetupResponse",
                            Message = data.StartsWith("#w")
                                ? $"Device {deviceId} acknowledged setup update, waiting for confirmation"
                                : $"Device {deviceId} confirmed setup update is complete",
                            Timestamp = DateTime.Now,
                            Read = false,
                            RelatedEntityId = deviceId
                        };

                        dbContext.SystemNotifications.Add(notification);
                        await dbContext.SaveChangesAsync(stoppingToken);

                        _logger.LogInformation($"Stored setup response notification for device {deviceId}");
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
                // Remove the client from tracking
                if (!string.IsNullOrEmpty(connectedDeviceId))
                {
                    RemoveActiveClient(connectedDeviceId);
                }

                try
                {
                    if (client.Connected)
                        client.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error closing client connection: {ex.Message}");
                }

                _logger.LogInformation($"Client disconnected: {clientIP}:{clientPort}");
            }
        }

        private async Task SendDataAcknowledgmentDirect(NetworkStream stream, string deviceId, CancellationToken stoppingToken)
        {
            try
            {
                // Format: #dªdeviceIdªCS<LF>
                using (var ms = new MemoryStream())
                {
                    // "#d" prefix
                    ms.WriteByte(0x23); // #
                    ms.WriteByte(0x64); // d

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Device ID bytes
                    byte[] deviceIdBytes = Encoding.ASCII.GetBytes(deviceId);
                    ms.Write(deviceIdBytes, 0, deviceIdBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Calculate the checksum
                    byte[] dataBytesSoFar = ms.ToArray();
                    byte checksum = CalculateChecksum(dataBytesSoFar);
                    ms.WriteByte(checksum);

                    // String end (0xFD)
                    ms.WriteByte(0xFD);

                    // Line feed (0x0A)
                    ms.WriteByte(0x0A);

                    byte[] responseData = ms.ToArray();

                    // Send the acknowledgment
                    await stream.WriteAsync(responseData, 0, responseData.Length, stoppingToken);

                    // Log the bytes and human-readable form
                    _logger.LogInformation($"Sent data acknowledgment to device {deviceId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending direct data acknowledgment to device {deviceId}");
                throw;
            }
        }

        private string ExtractDeviceId(string message)
        {
            try
            {
                // Normalizuj separatore na standardni
                string normalizedMessage = message
                    .Replace("?", "\u00AA")
                    .Replace("|", "\u00AA")
                    .Replace("*", "\u00AA");

                // Podeli poruku po separatorima
                string[] parts = normalizedMessage.Split('\u00AA');

                // ID uređaja je obično drugi deo (indeks 1)
                if (parts.Length > 1)
                {
                    return parts[1];
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting device ID from message");
            }

            return string.Empty;
        }

        private async Task SendSerialUpdateCommandDirect(NetworkStream stream, string currentSerialNumber, string newSerialNumber, CancellationToken stoppingToken)
        {
            try
            {
                // Build the command byte-by-byte to ensure correct encoding
                using (var ms = new MemoryStream())
                {
                    // "#i" prefix
                    ms.WriteByte(0x23); // #
                    ms.WriteByte(0x69); // i

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Current serial number bytes
                    byte[] currentSerialBytes = Encoding.ASCII.GetBytes(currentSerialNumber);
                    ms.Write(currentSerialBytes, 0, currentSerialBytes.Length);

                    // Second separator (0xAA)
                    ms.WriteByte(0xAA);

                    // New serial number bytes
                    byte[] newSerialBytes = Encoding.ASCII.GetBytes(newSerialNumber);
                    ms.Write(newSerialBytes, 0, newSerialBytes.Length);

                    // Third separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Calculate the checksum
                    byte[] commandBytesSoFar = ms.ToArray();
                    byte checksum = CalculateChecksum(commandBytesSoFar);
                    byte[] checksumBytes = new byte[] { checksum };
                    ms.Write(checksumBytes, 0, checksumBytes.Length);

                    // String end (0xFD)
                    ms.WriteByte(0xFD);

                    // Line feed (0x0A)
                    ms.WriteByte(0x0A);
                    byte[] commandBytes = ms.ToArray();

                    // Send the command
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length, stoppingToken);
                    _logger.LogInformation($"Sent serial update command to device {currentSerialNumber}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending direct serial update command");
                throw;
            }
        }

        // Metoda za obradu odgovora na komandu za promenu serijskog broja
        private async Task HandleSerialUpdateResponseAsync(TcpClient client, string response, CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation($"Processing serial update response: {response}");

                // Normalize separators
                string normalizedResponse = response
                    .Replace("?", "\u00AA")
                    .Replace("|", "\u00AA")
                    .Replace("*", "\u00AA");

                string[] parts = normalizedResponse.Split('\u00AA');

                if (parts.Length < 4)
                {
                    _logger.LogWarning("Invalid serial update response format");
                    return;
                }

                string oldSerial = parts[1];
                string newSerial = parts[2];
                string status = parts[3];

                _logger.LogInformation($"Serial update response: {oldSerial} -> {newSerial}, Status: {status}");

                // Only proceed if status is "OK"
                if (status == "OK")
                {
                    _logger.LogInformation($"Serial number change confirmed by device: {oldSerial} -> {newSerial}");

                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                        // Start a transaction to ensure all operations succeed or fail together
                        using var transaction = await dbContext.Database.BeginTransactionAsync(stoppingToken);

                        try
                        {
                            // Update Device table
                            var device = await dbContext.Devices.FirstOrDefaultAsync(d => d.SerialNumber == oldSerial);
                            if (device != null)
                            {
                                _logger.LogInformation($"Updating device record in database: {oldSerial} -> {newSerial}");

                                // Store device information before updating
                                int deviceId = device.Id;

                                // Handle CurrentDeviceStatus separately
                                var currentStatus = await dbContext.CurrentDeviceStatuses
                                    .FirstOrDefaultAsync(s => s.DeviceId == oldSerial);

                                // If we have an existing current status
                                if (currentStatus != null)
                                {
                                    _logger.LogInformation($"Found current status for device {oldSerial}");

                                    // Store current status data
                                    var statusData = new
                                    {
                                        currentStatus.Timestamp,
                                        currentStatus.Status,
                                        currentStatus.AvailableData,
                                        currentStatus.IPAddress,
                                        currentStatus.Port,
                                        currentStatus.CheckSum,
                                        currentStatus.StatusUpdateCount
                                    };

                                    // Remove the current status record (we can't update primary key)
                                    _logger.LogInformation($"Removing current status record for {oldSerial}");
                                    dbContext.CurrentDeviceStatuses.Remove(currentStatus);
                                    await dbContext.SaveChangesAsync(stoppingToken);

                                    // Check if a record with new serial number already exists
                                    var existingNewStatus = await dbContext.CurrentDeviceStatuses
                                        .FirstOrDefaultAsync(s => s.DeviceId == newSerial);

                                    if (existingNewStatus != null)
                                    {
                                        _logger.LogInformation($"Found existing status for new serial {newSerial}, updating it");
                                        existingNewStatus.Timestamp = statusData.Timestamp;
                                        existingNewStatus.Status = statusData.Status;
                                        existingNewStatus.AvailableData = statusData.AvailableData;
                                        existingNewStatus.IPAddress = statusData.IPAddress;
                                        existingNewStatus.Port = statusData.Port;
                                        existingNewStatus.CheckSum = statusData.CheckSum;
                                        existingNewStatus.StatusUpdateCount += statusData.StatusUpdateCount;
                                    }
                                    else
                                    {
                                        // Create a new record with the new DeviceId but the same data
                                        _logger.LogInformation($"Creating new current status record for {newSerial}");
                                        var newCurrentStatus = new CurrentDeviceStatus
                                        {
                                            DeviceId = newSerial,
                                            Timestamp = statusData.Timestamp,
                                            Status = statusData.Status,
                                            AvailableData = statusData.AvailableData,
                                            IPAddress = statusData.IPAddress,
                                            Port = statusData.Port,
                                            CheckSum = statusData.CheckSum,
                                            StatusUpdateCount = statusData.StatusUpdateCount
                                        };
                                        dbContext.CurrentDeviceStatuses.Add(newCurrentStatus);
                                    }

                                    await dbContext.SaveChangesAsync(stoppingToken);
                                }

                                // Now we can update the device
                                device.SerialNumber = newSerial;
                                await dbContext.SaveChangesAsync(stoppingToken);

                                // Update historical records
                                try
                                {
                                    // Use raw SQL to update DeviceStatuses and DonationsData
                                    await dbContext.Database.ExecuteSqlRawAsync(
                                        $"UPDATE DeviceStatuses SET DeviceId = '{newSerial}' WHERE DeviceId = '{oldSerial}'",
                                        stoppingToken);

                                    await dbContext.Database.ExecuteSqlRawAsync(
                                        $"UPDATE DonationsData SET DeviceId = '{newSerial}' WHERE DeviceId = '{oldSerial}'",
                                        stoppingToken);

                                    _logger.LogInformation($"Updated historical records for device {oldSerial} -> {newSerial}");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, $"Error updating historical records: {ex.Message}");
                                }

                                // Create a system notification
                                var notification = new SystemNotification
                                {
                                    Type = "SerialNumberUpdate",
                                    Message = $"Serial number successfully updated from {oldSerial} to {newSerial}",
                                    Timestamp = DateTime.Now,
                                    Read = false,
                                    RelatedEntityId = deviceId.ToString()
                                };

                                dbContext.SystemNotifications.Add(notification);
                                await dbContext.SaveChangesAsync(stoppingToken);

                                // Commit the transaction
                                await transaction.CommitAsync(stoppingToken);
                                _logger.LogInformation($"Successfully completed serial number update from {oldSerial} to {newSerial}");
                            }
                            else
                            {
                                _logger.LogWarning($"Device with serial number {oldSerial} not found in database");
                            }
                        }
                        catch (Exception ex)
                        {
                            // If anything fails, roll back the transaction
                            await transaction.RollbackAsync(stoppingToken);
                            _logger.LogError(ex, $"Error updating database for serial number change: {oldSerial} -> {newSerial}");
                        }
                    }
                }
                else
                {
                    _logger.LogWarning($"Device did not confirm serial number change. Status: {status}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling serial update response");
            }
        }

        private async Task SendStatusAcknowledgmentDirect(NetworkStream stream, string deviceId, CancellationToken stoppingToken)
        {
            try
            {
                // Format: #AªdeviceIdªS<LF>
                using (var ms = new MemoryStream())
                {
                    // "#A" prefix
                    ms.WriteByte(0x23); // #
                    ms.WriteByte(0x41); // A

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Device ID bytes
                    byte[] deviceIdBytes = Encoding.ASCII.GetBytes(deviceId);
                    ms.Write(deviceIdBytes, 0, deviceIdBytes.Length);

                    // Second separator (0xAA)
                    ms.WriteByte(0xAA);

                    // "S" (status acknowledgment)
                    ms.WriteByte(0x53); // S

                    // Line feed (0x0A)
                    ms.WriteByte(0x0A);

                    byte[] responseData = ms.ToArray();

                    // Send the acknowledgment
                    await stream.WriteAsync(responseData, 0, responseData.Length, stoppingToken);

                    // Log the data
                    _logger.LogInformation($"Sent status acknowledgment to device {deviceId}: #Aª{deviceId}ªS");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending direct status acknowledgment to device {deviceId}");
                throw;
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

                // Use the messageParser to determine message type
                var messageType = messageParser.DetermineMessageType(message);
                _logger.LogInformation($"Message type determined: {messageType}");

                // Check if this is a setup response message (#R)
                if (message.StartsWith("#R"))
                {
                    _logger.LogInformation($"Received potential setup response from {ipAddress}:{port}: {message}");

                    // Normalize for logging
                    string cleanedMessage = message.Replace("\u00AA", ".");
                    _logger.LogInformation($"Normalized setup response for logging: {cleanedMessage}");

                    // Process setup response
                    await ProcessSetupResponseAsync(dbContext, message, ipAddress, port);
                    return;
                }

                // Parse the message and store it in the database
                var parsedMessage = messageParser.ParseMessage(message, ipAddress, port);

                if (parsedMessage != null)
                {
                    if (parsedMessage is DeviceStatus status)
                    {
                        // Check if time synchronization is needed (difference > 60 seconds)
                        await CheckAndSyncDeviceTimeAsync(status);

                        // First, register or update device status based on the status message
                        // This is now the ONLY way devices are registered - through status messages
                        await RegisterOrUpdateDeviceFromStatusAsync(dbContext, status, ipAddress);

                        // Option 1: Only update the current status instead of adding to history
                        await UpdateCurrentDeviceStatusAsync(dbContext, status);
                    }
                    else if (parsedMessage is DonationsData data)
                    {
                        // Store donation data
                        dbContext.DonationsData.Add(data);
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Donation data from {data.DeviceId} stored in database");
                    }
                    else if (parsedMessage is DeviceMessageParser.SetupResponse setupResponse)
                    {
                        // Process setup response separately
                        await ProcessSetupResponseAsync(dbContext, setupResponse.RawResponse, ipAddress, port);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing and storing device message");
            }
        }

        private async Task<bool> CheckAndSyncDeviceTimeAsync(DeviceStatus status)
        {
            try
            {
                // Get current server time
                DateTime serverTime = DateTime.Now;

                // Device time was captured from the status message
                DateTime deviceTime = status.DeviceTimestamp;

                // Calculate time difference in seconds
                double timeDifference = Math.Abs((serverTime - deviceTime).TotalSeconds);

                _logger.LogDebug($"Time difference with device {status.DeviceId}: {timeDifference} seconds " +
                                 $"(Server: {serverTime}, Device: {deviceTime})");

                // Check if time difference is greater than 60 seconds
                if (timeDifference > 60)
                {
                    _logger.LogInformation($"Time difference with device {status.DeviceId} is {timeDifference} seconds. " +
                                          $"Sending time sync command automatically.");

                    // Format time as required (DDMMYYYYHHMM)
                    string dateTimeFormat = serverTime.ToString("ddMMyyyyHHmm");

                    // Rather than send immediately, queue it to be sent by the main communication loop
                    _pendingTimeSync[status.DeviceId] = dateTimeFormat;
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking device time synchronization for {status.DeviceId}");
                return false;
            }
        }

        private async Task SendTimeSyncCommandDirect(NetworkStream stream, string deviceId, string dateTimeFormat, CancellationToken stoppingToken)
        {
            try
            {
                // Delay for 500ms before sending the command (shorter delay for time sync)
                await Task.Delay(500, stoppingToken);

                // Build the command byte-by-byte to ensure correct encoding
                using (var ms = new MemoryStream())
                {
                    // "#t" prefix
                    ms.WriteByte(0x23); // #
                    ms.WriteByte(0x74); // t

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Device ID bytes
                    byte[] deviceIdBytes = Encoding.ASCII.GetBytes(deviceId);
                    ms.Write(deviceIdBytes, 0, deviceIdBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Date time bytes (format: DDMMYYYYHHMM)
                    byte[] dateTimeBytes = Encoding.ASCII.GetBytes(dateTimeFormat);
                    ms.Write(dateTimeBytes, 0, dateTimeBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // "N" character
                    ms.WriteByte(0x4E); // N

                    // Calculate the checksum
                    byte[] commandBytesSoFar = ms.ToArray();
                    byte checksum = CalculateChecksum(commandBytesSoFar);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Checksum byte
                    ms.WriteByte(checksum);

                    // String end (0xFD)
                    ms.WriteByte(0xFD);

                    // Line feed (0x0A)
                    ms.WriteByte(0x0A);

                    byte[] commandBytes = ms.ToArray();

                    // Send the command
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length, stoppingToken);
                    _logger.LogInformation($"Sent direct time sync command to device {deviceId} with time {dateTimeFormat}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending direct time sync command to device {deviceId}");
                throw;
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
                    // Check if this is a device that has been renamed
                    var notification = await dbContext.SystemNotifications
                        .Where(n => n.Type == "SerialNumberUpdate" && n.Message.Contains($"to {deviceId}"))
                        .OrderByDescending(n => n.Timestamp)
                        .FirstOrDefaultAsync();

                    if (notification != null)
                    {
                        _logger.LogInformation($"This appears to be a renamed device: {deviceId}");

                        // Just update the last connection time of the existing device
                        existingDevice = await dbContext.Devices.FindAsync(int.Parse(notification.RelatedEntityId));
                        if (existingDevice != null)
                        {
                            _logger.LogInformation($"Found existing device with ID {notification.RelatedEntityId}, updating last connection time");
                            existingDevice.LastConnectionTime = DateTime.Now;
                            existingDevice.IsActive = true;
                            await dbContext.SaveChangesAsync();
                            return;
                        }
                    }

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

        public static bool QueueSerialNumberChange(string currentSerialNumber, string newSerialNumber)
        {
            if (string.IsNullOrEmpty(currentSerialNumber) || string.IsNullOrEmpty(newSerialNumber))
                return false;

            _pendingSerialChanges[currentSerialNumber] = newSerialNumber;
            return true;
        }

        private void AddActiveClient(string deviceId, TcpClient client)
        {
            _activeClients.AddOrUpdate(deviceId, client, (_, __) => client);
        }

        private void RemoveActiveClient(string deviceId)
        {
            _activeClients.TryRemove(deviceId, out _);
        }

        public static bool QueueTimeSync(string deviceSerialNumber, string dateTimeFormat)
        {
            if (string.IsNullOrEmpty(deviceSerialNumber) || string.IsNullOrEmpty(dateTimeFormat))
                return false;

            _pendingTimeSync[deviceSerialNumber] = dateTimeFormat;
            return true;
        }
        public static bool QueueSetupRequest(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
                return false;

            var logger = LoggerFactory.Create(builder =>
                builder.AddConsole()).CreateLogger("DeviceSetupLogger");

            logger.LogInformation($"Queuing setup request for device {deviceId} to be sent on next connection");
            _pendingSetupRequests[deviceId] = true;
            logger.LogInformation($"Setup request for device {deviceId} successfully queued. Current queue count: {_pendingSetupRequests.Count}");
            return true;
        }
        private async Task SendSetupRequestDirect(NetworkStream stream, string deviceId, CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation($"Preparing setup request command for device {deviceId} on active connection");

                // Delay for 500ms before sending the command (shorter delay for time sync)
                await Task.Delay(500, stoppingToken);

                // Format: #rªdeviceIdª<LF>
                using (var ms = new MemoryStream())
                {
                    // "#r" prefix
                    ms.WriteByte(0x23); // #
                    ms.WriteByte(0x72); // r

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Device ID bytes
                    byte[] deviceIdBytes = Encoding.ASCII.GetBytes(deviceId);
                    ms.Write(deviceIdBytes, 0, deviceIdBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Line feed (0x0A)
                    ms.WriteByte(0x0A);

                    byte[] commandBytes = ms.ToArray();
                    string hexCommand = BitConverter.ToString(commandBytes);

                    _logger.LogInformation($"Setup request command assembled: {hexCommand}");

                    // Send the command on the existing connection
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length, stoppingToken);
                    _logger.LogInformation($"Setup request command sent successfully to device {deviceId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending setup request command");
                throw;
            }
        }

        private async Task ProcessSetupResponseAsync(ApplicationDbContext dbContext, string message, string ipAddress, int port)
        {
            try
            {
                _logger.LogInformation($"Setup response received from :{port}");
                _logger.LogInformation($"Raw message length: {message.Length} characters");

                // Normalize separators (?, |, *, ª)
                message = message.Replace("?", "\u00AA").Replace("|", "\u00AA").Replace("*", "\u00AA");

                // Remove ENDE, checksum and LF at the end if present
                int endeIndex = message.IndexOf("ENDE");
                if (endeIndex >= 0)
                {
                    message = message.Substring(0, endeIndex + 4); // Keep "ENDE"
                }

                // Split the message by separators
                var parts = message.Split('\u00AA', StringSplitOptions.None);

                if (parts.Length < 13) // Minimum we need for basic setup info
                {
                    _logger.LogWarning($"Invalid setup response format: {message}");
                    return;
                }

                string deviceId = parts[1];

                var setup = new DeviceSetup
                {
                    DeviceId = deviceId,
                    Timestamp = DateTime.Now,
                    RawResponse = message,
                    SoftwareVersion = parts.Length > 2 ? parts[2] : null,
                    HardwareVersion = parts.Length > 3 ? parts[3] : null,
                    ServerAddress = parts.Length > 4 ? parts[4] : null,
                    DeviceIpAddress = parts.Length > 5 ? parts[5] : null,
                    SubnetMask = parts.Length > 6 ? parts[6] : null,
                    RemotePort = parts.Length > 7 && int.TryParse(parts[7], out int remotePort) ? remotePort : 0,
                    LocalPort = parts.Length > 8 && int.TryParse(parts[8], out int localPort) ? localPort : 0,
                    LipemicIndex1 = parts.Length > 9 && int.TryParse(parts[9], out int li1) ? li1 : 0,
                    LipemicIndex2 = parts.Length > 10 && int.TryParse(parts[10], out int li2) ? li2 : 0,
                    LipemicIndex3 = parts.Length > 11 && int.TryParse(parts[11], out int li3) ? li3 : 0
                };

                // Find the part that starts with P (profiles) and B (barcodes)
                int pIndex = Array.IndexOf(parts, "P");
                int bIndex = Array.IndexOf(parts, "B");

                if (pIndex >= 0 && bIndex > pIndex)
                {
                    // Process profiles
                    try
                    {
                        var profiles = new List<DeviceProfile>();
                        int profileCount = 20;

                        for (int i = 0; i < profileCount; i++)
                        {
                            int startIndex = pIndex + 1 + (i * 3);
                            if (startIndex + 2 < parts.Length)
                            {
                                var profile = new DeviceProfile
                                {
                                    Index = i,
                                    Name = parts[startIndex] ?? string.Empty,
                                    RefCode = parts[startIndex + 1],
                                    OffsetValue = int.TryParse(parts[startIndex + 2], out int offset) ? offset : 0
                                };

                                // Only add if it has a name (not empty)
                                if (!string.IsNullOrEmpty(profile.Name))
                                {
                                    profiles.Add(profile);
                                }
                            }
                        }

                        // Store as JSON
                        setup.ProfilesJson = System.Text.Json.JsonSerializer.Serialize(profiles);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing profile data");
                    }

                    // Settings come after the profiles
                    try
                    {
                        int settingsIndex = pIndex + 1 + (20 * 3); // 20 profiles, 3 fields each
                        if (settingsIndex + 7 < parts.Length)
                        {
                            setup.TransferMode = parts[settingsIndex] == "1";
                            setup.BarcodesMode = parts[settingsIndex + 1] == "1";
                            setup.OperatorIdEnabled = parts[settingsIndex + 2] == "1";
                            setup.LotNumberEnabled = parts[settingsIndex + 3] == "1";
                            setup.NetworkName = parts[settingsIndex + 4];
                            setup.WifiMode = parts[settingsIndex + 5];
                            setup.SecurityType = parts[settingsIndex + 6];
                            setup.WifiPassword = parts[settingsIndex + 7];
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing device settings");
                    }

                    // Process barcode configs
                    try
                    {
                        if (bIndex > 0 && bIndex + 1 < parts.Length)
                        {
                            var barcodeConfigs = new List<BarcodeConfig>();
                            int barcodeCount = Math.Min(6, parts.Length - bIndex - 1);

                            for (int i = 0; i < barcodeCount; i++)
                            {
                                if (bIndex + 1 + i < parts.Length)
                                {
                                    // Each barcode config is in a space-separated string with 4 values
                                    string configStr = parts[bIndex + 1 + i];
                                    var configParts = configStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                                    if (configParts.Length >= 4)
                                    {
                                        var config = new BarcodeConfig
                                        {
                                            Index = i,
                                            MinLength = int.TryParse(configParts[0], out int min) ? min : 0,
                                            MaxLength = int.TryParse(configParts[1], out int max) ? max : 0,
                                            StartCode = configParts[2] ?? string.Empty,
                                            StopCode = configParts[3] ?? string.Empty
                                        };

                                        barcodeConfigs.Add(config);
                                    }
                                }
                            }

                            // Store as JSON
                            setup.BarcodesJson = System.Text.Json.JsonSerializer.Serialize(barcodeConfigs);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing barcode configurations");
                    }
                }

                // Check if the table exists before trying to use it
                bool tableExists = true;
                try
                {
                    // This will throw an exception if the table doesn't exist
                    await dbContext.Database.ExecuteSqlRawAsync("SELECT 1 FROM DeviceSetups LIMIT 1");
                }
                catch (Exception)
                {
                    tableExists = false;
                    _logger.LogWarning("DeviceSetups table doesn't exist yet. Apply migrations to fix this issue.");
                }

                if (tableExists)
                {
                    try
                    {
                        // Try to get existing setup
                        var existingSetup = await dbContext.DeviceSetups
                            .Where(s => s.DeviceId == deviceId)
                            .OrderByDescending(s => s.Timestamp)
                            .FirstOrDefaultAsync();

                        if (existingSetup != null)
                        {
                            _logger.LogInformation($"Updating existing setup for device {deviceId}");

                            // Copy all properties
                            existingSetup.Timestamp = setup.Timestamp;
                            existingSetup.RawResponse = setup.RawResponse;
                            existingSetup.SoftwareVersion = setup.SoftwareVersion;
                            existingSetup.HardwareVersion = setup.HardwareVersion;
                            existingSetup.ServerAddress = setup.ServerAddress;
                            existingSetup.DeviceIpAddress = setup.DeviceIpAddress;
                            existingSetup.SubnetMask = setup.SubnetMask;
                            existingSetup.RemotePort = setup.RemotePort;
                            existingSetup.LocalPort = setup.LocalPort;
                            existingSetup.LipemicIndex1 = setup.LipemicIndex1;
                            existingSetup.LipemicIndex2 = setup.LipemicIndex2;
                            existingSetup.LipemicIndex3 = setup.LipemicIndex3;
                            existingSetup.TransferMode = setup.TransferMode;
                            existingSetup.BarcodesMode = setup.BarcodesMode;
                            existingSetup.OperatorIdEnabled = setup.OperatorIdEnabled;
                            existingSetup.LotNumberEnabled = setup.LotNumberEnabled;
                            existingSetup.NetworkName = setup.NetworkName;
                            existingSetup.WifiMode = setup.WifiMode;
                            existingSetup.SecurityType = setup.SecurityType;
                            existingSetup.WifiPassword = setup.WifiPassword;
                            existingSetup.ProfilesJson = setup.ProfilesJson;
                            existingSetup.BarcodesJson = setup.BarcodesJson;
                        }
                        else
                        {
                            _logger.LogInformation($"Creating new setup for device {deviceId}");
                            dbContext.DeviceSetups.Add(setup);
                        }

                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Successfully saved setup for device {deviceId}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error saving device setup to database");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing setup response");
            }
        }

        public static async Task<bool> QueueSetupUpdateAsync(DeviceSetup setup)
        {
            if (setup == null || string.IsNullOrEmpty(setup.DeviceId))
                return false;

            // Ensure RawResponse is not null before queuing
            if (setup.RawResponse == null)
            {
                setup.RawResponse = $"Setup update queued at {DateTime.Now}";
            }

            _pendingSetupUpdates[setup.DeviceId] = setup;
            return true;
        }

        private async Task SendSetupUpdateCommandDirect(NetworkStream stream, DeviceSetup setup, CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation($"Preparing setup update command for device {setup.DeviceId}");

                // Delay for 500ms before sending the command
                await Task.Delay(500, stoppingToken);

                // Build the setup update message
                // Format: #WªSNªSWªHWªserverAddressªdeviceIPAdressªsubnetMaskªremotePortªlocalPortªLipIndex_1ªLipIndex_2ªLipIndex_3ª...
                using (var ms = new MemoryStream())
                {
                    // "#W" prefix
                    ms.WriteByte(0x23); // #
                    ms.WriteByte(0x57); // W

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Device ID
                    byte[] deviceIdBytes = Encoding.ASCII.GetBytes(setup.DeviceId);
                    ms.Write(deviceIdBytes, 0, deviceIdBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Software Version
                    byte[] swBytes = Encoding.ASCII.GetBytes(setup.SoftwareVersion ?? "");
                    ms.Write(swBytes, 0, swBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Hardware Version
                    byte[] hwBytes = Encoding.ASCII.GetBytes(setup.HardwareVersion ?? "");
                    ms.Write(hwBytes, 0, hwBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Server Address
                    byte[] serverBytes = Encoding.ASCII.GetBytes(setup.ServerAddress ?? "");
                    ms.Write(serverBytes, 0, serverBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Device IP Address
                    byte[] ipBytes = Encoding.ASCII.GetBytes(setup.DeviceIpAddress ?? "");
                    ms.Write(ipBytes, 0, ipBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Subnet Mask
                    byte[] maskBytes = Encoding.ASCII.GetBytes(setup.SubnetMask ?? "");
                    ms.Write(maskBytes, 0, maskBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Remote Port
                    byte[] remotePortBytes = Encoding.ASCII.GetBytes(setup.RemotePort.ToString());
                    ms.Write(remotePortBytes, 0, remotePortBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Local Port
                    byte[] localPortBytes = Encoding.ASCII.GetBytes(setup.LocalPort.ToString());
                    ms.Write(localPortBytes, 0, localPortBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Lipemic Index 1
                    byte[] li1Bytes = Encoding.ASCII.GetBytes(setup.LipemicIndex1.ToString());
                    ms.Write(li1Bytes, 0, li1Bytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Lipemic Index 2
                    byte[] li2Bytes = Encoding.ASCII.GetBytes(setup.LipemicIndex2.ToString());
                    ms.Write(li2Bytes, 0, li2Bytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Lipemic Index 3
                    byte[] li3Bytes = Encoding.ASCII.GetBytes(setup.LipemicIndex3.ToString());
                    ms.Write(li3Bytes, 0, li3Bytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // P marker for profiles
                    ms.WriteByte(0x50); // P

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Add profiles (20 of them)
                    List<DeviceProfile> profiles = setup.Profiles ?? new List<DeviceProfile>();
                    // Make sure we have 20 profiles
                    while (profiles.Count < 20)
                    {
                        profiles.Add(new DeviceProfile
                        {
                            Index = profiles.Count,
                            Name = "",
                            RefCode = "",
                            OffsetValue = 0
                        });
                    }

                    // Sort profiles by index
                    profiles = profiles.OrderBy(p => p.Index).ToList();

                    // Write all 20 profiles
                    for (int i = 0; i < 20; i++)
                    {
                        var profile = profiles.FirstOrDefault(p => p.Index == i) ??
                            new DeviceProfile { Index = i, Name = "", RefCode = "", OffsetValue = 0 };

                        // Name
                        byte[] nameBytes = Encoding.ASCII.GetBytes(profile.Name ?? "");
                        ms.Write(nameBytes, 0, nameBytes.Length);

                        // Separator (0xAA)
                        ms.WriteByte(0xAA);

                        // REF code
                        byte[] refBytes = Encoding.ASCII.GetBytes(profile.RefCode ?? "");
                        ms.Write(refBytes, 0, refBytes.Length);

                        // Separator (0xAA)
                        ms.WriteByte(0xAA);

                        // Offset value
                        byte[] offsetBytes = Encoding.ASCII.GetBytes(profile.OffsetValue.ToString());
                        ms.Write(offsetBytes, 0, offsetBytes.Length);

                        // Separator (0xAA) after each profile
                        ms.WriteByte(0xAA);
                    }

                    // Device settings
                    // Transfer Mode
                    byte[] transferModeBytes = Encoding.ASCII.GetBytes(setup.TransferMode ? "1" : "0");
                    ms.Write(transferModeBytes, 0, transferModeBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Barcodes Mode
                    byte[] barcodesModeBytes = Encoding.ASCII.GetBytes(setup.BarcodesMode ? "1" : "0");
                    ms.Write(barcodesModeBytes, 0, barcodesModeBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Operator ID
                    byte[] operatorIdBytes = Encoding.ASCII.GetBytes(setup.OperatorIdEnabled ? "1" : "0");
                    ms.Write(operatorIdBytes, 0, operatorIdBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // LOT Number
                    byte[] lotNumberBytes = Encoding.ASCII.GetBytes(setup.LotNumberEnabled ? "1" : "0");
                    ms.Write(lotNumberBytes, 0, lotNumberBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Network Name
                    byte[] networkNameBytes = Encoding.ASCII.GetBytes(setup.NetworkName ?? "");
                    ms.Write(networkNameBytes, 0, networkNameBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // WiFi Mode
                    byte[] wifiModeBytes = Encoding.ASCII.GetBytes(setup.WifiMode ?? "");
                    ms.Write(wifiModeBytes, 0, wifiModeBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Security Type
                    byte[] securityTypeBytes = Encoding.ASCII.GetBytes(setup.SecurityType ?? "");
                    ms.Write(securityTypeBytes, 0, securityTypeBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // WiFi Password
                    byte[] wifiPassBytes = Encoding.ASCII.GetBytes(setup.WifiPassword ?? "");
                    ms.Write(wifiPassBytes, 0, wifiPassBytes.Length);

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // B marker for barcode configurations
                    ms.WriteByte(0x42); // B

                    // Separator (0xAA)
                    ms.WriteByte(0xAA);

                    // Add barcode configurations (6 of them)
                    List<BarcodeConfig> barcodeConfigs = setup.BarcodeConfigs ?? new List<BarcodeConfig>();

                    // Make sure we have 6 barcode configs
                    while (barcodeConfigs.Count < 6)
                    {
                        barcodeConfigs.Add(new BarcodeConfig
                        {
                            Index = barcodeConfigs.Count,
                            MinLength = 0,
                            MaxLength = 0,
                            StartCode = "",
                            StopCode = ""
                        });
                    }

                    // Sort barcode configs by index
                    barcodeConfigs = barcodeConfigs.OrderBy(b => b.Index).ToList();

                    // Write all 6 barcode configurations
                    for (int i = 0; i < 6; i++)
                    {
                        var config = barcodeConfigs.FirstOrDefault(b => b.Index == i) ??
                            new BarcodeConfig { Index = i, MinLength = 0, MaxLength = 0, StartCode = "", StopCode = "" };

                        // Format: minLength maxLength StartCode StopCode
                        string configText = $"{config.MinLength} {config.MaxLength} {config.StartCode} {config.StopCode}";
                        byte[] configBytes = Encoding.ASCII.GetBytes(configText);
                        ms.Write(configBytes, 0, configBytes.Length);

                        // Separator (0xAA) after each barcode config (except the last one)
                        if (i < 5)
                        {
                            ms.WriteByte(0xAA);
                        }
                    }

                    // Separator
                    ms.WriteByte(0xAA);

                    // Add ENDE marker
                    byte[] endeBytes = Encoding.ASCII.GetBytes("ENDE");
                    ms.Write(endeBytes, 0, endeBytes.Length);

                    // Separator
                    ms.WriteByte(0xAA);

                    // Calculate checksum
                    byte[] messageBytes = ms.ToArray();
                    ushort checksumValue = CalculateChecksumUInt16(messageBytes);

                    // Convert to a 2-digit hex string (will still be modulo 253, so max FF)
                    string hexString = (checksumValue % 253).ToString("X2");

                    // Write each hex digit as a separate ASCII byte
                    ms.WriteByte((byte)hexString[0]); // First hex digit
                    ms.WriteByte((byte)hexString[1]); // Second hex digit

                    // End of message marker (0xFD)
                    ms.WriteByte(0xFD);

                    // Line feed (0x0A)
                    ms.WriteByte(0x0A);

                    byte[] completeMessage = ms.ToArray();

                    // Log the command as hex for debugging
                    string hexCommand = BitConverter.ToString(completeMessage);
                    _logger.LogInformation($"Setup command assembled (first 100 bytes): {hexCommand.Substring(0, Math.Min(hexCommand.Length, 300))}...");

                    // Send the setup update command
                    await stream.WriteAsync(completeMessage, 0, completeMessage.Length, stoppingToken);
                    _logger.LogInformation($"Sent setup update command to device {setup.DeviceId} - {completeMessage.Length} bytes");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending setup update command to device {setup.DeviceId}");
                throw;
            }
        }

        private byte CalculateChecksum(byte[] data)
        {
            byte checksum = 0;
            foreach (byte b in data)
            {
                checksum += b;
            }
            return (byte)(checksum % 253);
        }

        private ushort CalculateChecksumUInt16(byte[] data)
        {
            ushort checksum = 0;
            foreach (byte b in data)
            {
                checksum += b;
            }
            return checksum;
        }
    }
}