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

        private byte CalculateChecksum(byte[] data)
        {
            byte checksum = 0;
            foreach (byte b in data)
            {
                checksum += b;
            }
            return (byte)(checksum % 256);
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

        private async Task SendSerialUpdateCommandAsync(TcpClient client, string currentSerialNumber, string newSerialNumber, CancellationToken stoppingToken)
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

                    // Log the bytes in hex format for debugging
                    _logger.LogInformation($"Sending serial update command bytes: {BitConverter.ToString(commandBytes)}");

                    // Send the command
                    await client.GetStream().WriteAsync(commandBytes, 0, commandBytes.Length, stoppingToken);
                    _logger.LogInformation($"Sent serial update command to device {currentSerialNumber}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending serial update command");
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

                        // First check if there's already a device with the new serial number
                        var existingDeviceWithNewSerial = await dbContext.Devices
                            .FirstOrDefaultAsync(d => d.SerialNumber == newSerial);

                        if (existingDeviceWithNewSerial != null)
                        {
                            _logger.LogWarning($"Cannot update device: A device with serial number {newSerial} already exists.");
                            return;
                        }

                        // Start a transaction to ensure all operations succeed or fail together
                        using var transaction = await dbContext.Database.BeginTransactionAsync(stoppingToken);

                        try
                        {
                            // Update Device table
                            var device = await dbContext.Devices.FirstOrDefaultAsync(d => d.SerialNumber == oldSerial);
                            if (device != null)
                            {
                                _logger.LogInformation($"Updating device record in database: {oldSerial} -> {newSerial}");
                                device.SerialNumber = newSerial;

                                // First check if a CurrentDeviceStatus with the new serial already exists and delete it if found
                                var existingNewStatus = await dbContext.CurrentDeviceStatuses
                                    .FirstOrDefaultAsync(s => s.DeviceId == newSerial);

                                if (existingNewStatus != null)
                                {
                                    _logger.LogInformation($"Removing existing current status for {newSerial} to avoid conflicts");
                                    dbContext.CurrentDeviceStatuses.Remove(existingNewStatus);
                                    // Save changes to remove the existing status first
                                    await dbContext.SaveChangesAsync(stoppingToken);
                                }

                                // Now check and update the current status for the old serial
                                var currentStatus = await dbContext.CurrentDeviceStatuses
                                    .FirstOrDefaultAsync(s => s.DeviceId == oldSerial);

                                if (currentStatus != null)
                                {
                                    _logger.LogInformation($"Updating current status record: {oldSerial} -> {newSerial}");

                                    // Update the existing record instead of creating a new one
                                    currentStatus.DeviceId = newSerial;

                                    // Save the changes
                                    await dbContext.SaveChangesAsync(stoppingToken);

                                    // Create a system notification
                                    var notification = new SystemNotification
                                    {
                                        Type = "SerialNumberUpdate",
                                        Message = $"Serial number successfully updated from {oldSerial} to {newSerial}",
                                        Timestamp = DateTime.Now,
                                        Read = false,
                                        RelatedEntityId = device.Id.ToString()
                                    };

                                    dbContext.SystemNotifications.Add(notification);
                                }
                                else
                                {
                                    _logger.LogWarning($"No current status found for device {oldSerial}");
                                }

                                // Save all remaining changes (device update and notification)
                                await dbContext.SaveChangesAsync(stoppingToken);

                                // Commit the transaction
                                await transaction.CommitAsync(stoppingToken);

                                _logger.LogInformation("Database updated successfully");
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

        private async Task SendStatusAcknowledgmentAsync(TcpClient client, string deviceId, CancellationToken stoppingToken)
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
                    await client.GetStream().WriteAsync(responseData, 0, responseData.Length, stoppingToken);

                    // Log the bytes and human-readable form
                    _logger.LogInformation($"Sent status acknowledgment bytes: {responseData}");
                    _logger.LogInformation($"Sent status acknowledgment to device {deviceId}: #Aª{deviceId}ªS");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending status acknowledgment to device {deviceId}");
            }
        }

        private async Task HandleSerialUpdateRequestAsync(TcpClient client, string message, string clientIP, int clientPort, CancellationToken stoppingToken)
        {
            try
            {
                // Parse the message to extract current and new serial numbers
                string[] parts = message.Split('\u00AA');
                if (parts.Length < 3)
                {
                    _logger.LogWarning($"Invalid serial update request format: {message}");
                    return;
                }

                string currentSerialNumber = parts[1];
                string newSerialNumber = parts[2];

                _logger.LogInformation($"Processing serial number update request: {currentSerialNumber} -> {newSerialNumber}");

                // Find the device's IP address from our database
                string deviceIP;
                int devicePort = 5000;

                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // Try to get IP from current status first
                    var currentStatus = await dbContext.CurrentDeviceStatuses
                        .FirstOrDefaultAsync(s => s.DeviceId == currentSerialNumber);

                    if (currentStatus != null && !string.IsNullOrEmpty(currentStatus.IPAddress))
                    {
                        deviceIP = currentStatus.IPAddress;
                        devicePort = currentStatus.Port;
                        _logger.LogInformation($"Found device IP from current status: {deviceIP}:{devicePort}");
                    }
                    else
                    {
                        // Fall back to latest donation data
                        var latestData = await dbContext.DonationsData
                            .Where(d => d.DeviceId == currentSerialNumber)
                            .OrderByDescending(d => d.Timestamp)
                            .FirstOrDefaultAsync();

                        if (latestData != null && !string.IsNullOrEmpty(latestData.IPAddress))
                        {
                            deviceIP = latestData.IPAddress;
                            devicePort = latestData.Port;
                            _logger.LogInformation($"Found device IP from latest data: {deviceIP}:{devicePort}");
                        }
                        else
                        {
                            _logger.LogWarning($"No IP address found for device {currentSerialNumber}");

                            // Return an error message to the client
                            string errorResponse = $"#I\u00AA{currentSerialNumber}\u00AA{newSerialNumber}\u00AAFailed\u00AA77\u00FD";
                            byte[] errorBytes = Encoding.ASCII.GetBytes(errorResponse);
                            await client.GetStream().WriteAsync(errorBytes, 0, errorBytes.Length, stoppingToken);
                            return;
                        }
                    }
                }

                // Forward the serial update request to the device
                using (var deviceClient = new TcpClient())
                {
                    try
                    {
                        // Connect to the device with timeout
                        var connectTask = deviceClient.ConnectAsync(deviceIP, devicePort);
                        var timeoutTask = Task.Delay(5000); // 5 second timeout

                        var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                        if (completedTask == timeoutTask)
                        {
                            _logger.LogWarning($"Connection to device at {deviceIP}:{devicePort} timed out");

                            // Return timeout error to client
                            string timeoutResponse = $"#I\u00AA{currentSerialNumber}\u00AA{newSerialNumber}\u00AATimeout\u00AA77\u00FD";
                            byte[] timeoutBytes = Encoding.ASCII.GetBytes(timeoutResponse);
                            await client.GetStream().WriteAsync(timeoutBytes, 0, timeoutBytes.Length, stoppingToken);
                            return;
                        }

                        if (!deviceClient.Connected)
                        {
                            _logger.LogWarning($"Failed to connect to device at {deviceIP}:{devicePort}");

                            // Return connection error to client
                            string connErrorResponse = $"#I\u00AA{currentSerialNumber}\u00AA{newSerialNumber}\u00AAConnFailed\u00AA77\u00FD";
                            byte[] connErrorBytes = Encoding.ASCII.GetBytes(connErrorResponse);
                            await client.GetStream().WriteAsync(connErrorBytes, 0, connErrorBytes.Length, stoppingToken);
                            return;
                        }

                        // Successfully connected to the device
                        _logger.LogInformation($"Connected to device at {deviceIP}:{devicePort}");

                        using (var deviceStream = deviceClient.GetStream())
                        {
                            // Forward the original message to the device
                            byte[] requestBytes = Encoding.ASCII.GetBytes(message);
                            await deviceStream.WriteAsync(requestBytes, 0, requestBytes.Length, stoppingToken);
                            _logger.LogInformation($"Forwarded serial update request to device: {message}");

                            // Read the device's response with timeout
                            var buffer = new byte[4096];

                            var readTask = deviceStream.ReadAsync(buffer, 0, buffer.Length, stoppingToken);
                            var readTimeoutTask = Task.Delay(10000); // 10 second read timeout

                            var readCompletedTask = await Task.WhenAny(readTask, readTimeoutTask);
                            if (readCompletedTask == readTimeoutTask)
                            {
                                _logger.LogWarning($"Reading response from device timed out");

                                // Return read timeout error to client
                                string readTimeoutResponse = $"#I\u00AA{currentSerialNumber}\u00AA{newSerialNumber}\u00AAReadTimeout\u00AA77\u00FD";
                                byte[] readTimeoutBytes = Encoding.ASCII.GetBytes(readTimeoutResponse);
                                await client.GetStream().WriteAsync(readTimeoutBytes, 0, readTimeoutBytes.Length, stoppingToken);
                                return;
                            }

                            int bytesRead = await readTask;
                            if (bytesRead == 0)
                            {
                                _logger.LogWarning($"Device closed connection without sending response");

                                // Return empty response error to client
                                string emptyResponse = $"#I\u00AA{currentSerialNumber}\u00AA{newSerialNumber}\u00AANoResponse\u00AA77\u00FD";
                                byte[] emptyBytes = Encoding.ASCII.GetBytes(emptyResponse);
                                await client.GetStream().WriteAsync(emptyBytes, 0, emptyBytes.Length, stoppingToken);
                                return;
                            }

                            // Get the device's response
                            string deviceResponse = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                            _logger.LogInformation($"Received response from device: {deviceResponse}");

                            // Forward the response back to the client
                            await client.GetStream().WriteAsync(buffer, 0, bytesRead, stoppingToken);
                            _logger.LogInformation($"Forwarded device response to client");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error communicating with device at {deviceIP}:{devicePort}");

                        // Return error to client
                        string exceptionResponse = $"#I\u00AA{currentSerialNumber}\u00AA{newSerialNumber}\u00AAError\u00AA77\u00FD";
                        byte[] exceptionBytes = Encoding.ASCII.GetBytes(exceptionResponse);
                        await client.GetStream().WriteAsync(exceptionBytes, 0, exceptionBytes.Length, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling serial update request");
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
                        // Check if time synchronization is needed (difference > 60 seconds)
                        await CheckAndSyncDeviceTimeAsync(status);

                        // First, register or update device status based on the status message
                        // This is now the ONLY way devices are registered - through status messages
                        await RegisterOrUpdateDeviceFromStatusAsync(dbContext, status, ipAddress);

                        // Option 1: Only update the current status instead of adding to history
                        await UpdateCurrentDeviceStatusAsync(dbContext, status);
                    }
                    // Rest of the existing code for handling other message types...
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
        private async Task SendTimeSyncCommandAsync(TcpClient client, string deviceId, string dateTimeFormat, CancellationToken stoppingToken)
        {
            try
            {
                if (client == null || !client.Connected)
                {
                    _logger.LogWarning($"Cannot send time sync to device {deviceId}: Client is null or not connected");
                    return;
                }

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

                    // Log the bytes in hex format for debugging
                    _logger.LogInformation($"Sending time sync command bytes: {BitConverter.ToString(commandBytes)}");

                    // Send the command
                    await client.GetStream().WriteAsync(commandBytes, 0, commandBytes.Length, stoppingToken);
                    _logger.LogInformation($"Sent automatic time sync command to device {deviceId} with time {dateTimeFormat}");

                    // Add a system notification about the time sync
                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    var notification = new SystemNotification
                    {
                        Type = "TimeSync",
                        Message = $"Automatic time synchronization performed for device {deviceId}. " +
                                  $"Device time was off by more than 60 seconds.",
                        Timestamp = DateTime.Now,
                        RelatedEntityId = deviceId,
                        Read = false
                    };

                    dbContext.SystemNotifications.Add(notification);
                    await dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending time sync command to device {deviceId}");
            }
        }


        private async Task SendSerialUpdateNotification(int deviceId, string oldSerial, string newSerial)
        {
            // This could use SignalR to update clients in real-time
            // Or store the notification in the database

            // For a simpler approach, you could create a table for notifications
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var notification = new SystemNotification
            {
                Type = "SerialNumberUpdate",
                Message = $"Serial number successfully updated from {oldSerial} to {newSerial}",
                Timestamp = DateTime.Now,
                Read = false,
                RelatedEntityId = deviceId.ToString()
            };

            dbContext.SystemNotifications.Add(notification);
            await dbContext.SaveChangesAsync();
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
    }

    public static class ConcurrentDictionaryExtensions
    {
        public static TValue GetOrDefault<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dictionary, TKey key)
            where TValue : class
        {
            dictionary.TryGetValue(key, out var value);
            return value;
        }
    }
}