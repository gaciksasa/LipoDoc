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
                    else if (data.StartsWith("#i")) messageType = "SerialUpdateRequest";
                    else if (data.StartsWith("#I")) messageType = "SerialUpdateResponse";

                    _logger.LogInformation($"Message type from {clientIP}:{clientPort}: {messageType}");

                    // Prvo procesiramo normalnu poruku
                    await ProcessDeviceMessageAsync(data, clientIP, clientPort);

                    // Zatim proveravamo da li treba da pošaljemo komandu za promenu serijskog broja
                    if (messageType == "Status")
                    {
                        // Pokušajmo da izvučemo serijski broj iz poruke
                        string deviceId = ExtractDeviceId(data);
                        if (!string.IsNullOrEmpty(deviceId) && _pendingSerialChanges.TryRemove(deviceId, out string newSerialNumber))
                        {
                            _logger.LogInformation($"Found pending serial number change for device {deviceId} -> {newSerialNumber}");
                            // Pošalji komandu za promenu serijskog broja
                            await SendSerialUpdateCommandAsync(client, deviceId, newSerialNumber, stoppingToken);
                        }
                    }
                    else if (messageType == "SerialUpdateResponse")
                    {
                        // Ovo je odgovor na našu komandu za promenu serijskog broja
                        _logger.LogInformation($"Received serial update response: {data}");
                        // Ovde ćemo procesirati odgovor - već se radi u ProcessDeviceMessageAsync
                        // Samo pošaljimo potvrdu klijentu da smo primili odgovor
                        await HandleSerialUpdateResponseAsync(client, data, stoppingToken);
                    }

                    // Rest of your existing code for handling different message types...
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

        private async Task SendSerialUpdateCommandAsync(TcpClient client, string currentSerialNumber, string newSerialNumber, CancellationToken stoppingToken)
        {
            try
            {
                // Kreiraj komandu za promenu serijskog broja
                // Format: #iªcurrentSerialNumberªnewSerialNumberª77ý
                string command = $"#i\u00AA{currentSerialNumber}\u00AA{newSerialNumber}\u00AA77\u00FD";

                _logger.LogInformation($"Sending serial update command: {command}");

                // Pošalji komandu
                byte[] commandBytes = Encoding.ASCII.GetBytes(command);
                await client.GetStream().WriteAsync(commandBytes, 0, commandBytes.Length, stoppingToken);

                _logger.LogInformation($"Sent serial update command to device {currentSerialNumber}");
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

                // Normalizuj separatore
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

                // Ako je status "OK", ažuriraj serijski broj u bazi
                if (status == "OK")
                {
                    _logger.LogInformation($"Serial number change confirmed by device: {oldSerial} -> {newSerial}");

                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                        // Ažuriraj Device tabelu
                        var device = await dbContext.Devices.FirstOrDefaultAsync(d => d.SerialNumber == oldSerial);
                        if (device != null)
                        {
                            _logger.LogInformation($"Updating device record in database: {oldSerial} -> {newSerial}");
                            device.SerialNumber = newSerial;

                            // Ažuriraj i CurrentDeviceStatus tabelu
                            var currentStatus = await dbContext.CurrentDeviceStatuses
                                .FirstOrDefaultAsync(s => s.DeviceId == oldSerial);

                            if (currentStatus != null)
                            {
                                _logger.LogInformation($"Updating current status record: {oldSerial} -> {newSerial}");

                                // Kreiraj novi status sa novim serijskim brojem
                                var newStatus = new CurrentDeviceStatus
                                {
                                    DeviceId = newSerial,
                                    Timestamp = currentStatus.Timestamp,
                                    Status = currentStatus.Status,
                                    AvailableData = currentStatus.AvailableData,
                                    IPAddress = currentStatus.IPAddress,
                                    Port = currentStatus.Port,
                                    CheckSum = currentStatus.CheckSum,
                                    StatusUpdateCount = currentStatus.StatusUpdateCount
                                };

                                // Ukloni stari status i dodaj novi
                                dbContext.CurrentDeviceStatuses.Remove(currentStatus);
                                dbContext.CurrentDeviceStatuses.Add(newStatus);
                            }

                            await dbContext.SaveChangesAsync();
                            _logger.LogInformation("Database updated successfully");
                        }
                        else
                        {
                            _logger.LogWarning($"Device with serial number {oldSerial} not found in database");
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

                // Forward the serial update request to the real device
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
                        // First, register or update device status based on the status message
                        // This is now the ONLY way devices are registered - through status messages
                        await RegisterOrUpdateDeviceFromStatusAsync(dbContext, status, ipAddress);

                        // Option 1: Only update the current status instead of adding to history
                        await UpdateCurrentDeviceStatusAsync(dbContext, status);
                    }
                    else if (parsedMessage is DonationsData donationData)
                    {
                        dbContext.DonationsData.Add(donationData);
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Donation data from {ipAddress}:{port} stored in database");
                    }
                    else if (parsedMessage is SerialUpdateResponse updateResponse)
                    {
                        _logger.LogInformation($"Serial number update response received: {updateResponse.OldSerialNumber} -> {updateResponse.NewSerialNumber}, Status: {updateResponse.Status}");

                        // If this is a successful update (contains 'OK' status), update the device record
                        if (updateResponse.Status == "OK")
                        {
                            var device = await dbContext.Devices.FirstOrDefaultAsync(d => d.SerialNumber == updateResponse.OldSerialNumber);
                            if (device != null)
                            {
                                _logger.LogInformation($"Updating device serial number in database from {device.SerialNumber} to {updateResponse.NewSerialNumber}");
                                device.SerialNumber = updateResponse.NewSerialNumber;
                                await dbContext.SaveChangesAsync();
                                _logger.LogInformation($"Database updated successfully with new serial number: {updateResponse.NewSerialNumber}");
                            }
                            else
                            {
                                _logger.LogWarning($"Device with serial number {updateResponse.OldSerialNumber} not found in database");
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"Serial number update was not successful. Status: {updateResponse.Status}");
                        }
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

        public static bool QueueSerialNumberChange(string currentSerialNumber, string newSerialNumber)
        {
            if (string.IsNullOrEmpty(currentSerialNumber) || string.IsNullOrEmpty(newSerialNumber))
                return false;

            _pendingSerialChanges[currentSerialNumber] = newSerialNumber;
            return true;
        }
    }
}