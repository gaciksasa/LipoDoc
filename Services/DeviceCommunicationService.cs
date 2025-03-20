using System.Net.Sockets;
using System.Text;
using DeviceDataCollector.Data;
using Microsoft.EntityFrameworkCore;

namespace DeviceDataCollector.Services
{
    public class DeviceCommunicationService
    {
        private readonly ILogger<DeviceCommunicationService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly int _timeout;

        public DeviceCommunicationService(
            ILogger<DeviceCommunicationService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _timeout = configuration.GetValue<int>("DeviceDataRetrieval:TimeoutMs", 5000);
        }

        /// <summary>
        /// Sends a serial number update command to the TCP server, which forwards it to the actual device.
        /// </summary>
        /// <param name="currentSerialNumber">The current serial number of the device</param>
        /// <param name="newSerialNumber">The new serial number to set on the device</param>
        /// <returns>Tuple with: success flag, response message, and confirmation flag</returns>
        public async Task<(bool Success, string Response, bool Confirmed)> UpdateSerialNumberAsync(string currentSerialNumber, string newSerialNumber)
        {
            _logger.LogInformation($"Initiating serial number update. Current: {currentSerialNumber}, New: {newSerialNumber}");

            try
            {
                // Get TCP server configuration from appsettings.json
                using var scope = _scopeFactory.CreateScope();
                var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

                // Use the TCP server IP and port configured in settings
                string serverIpAddress = configuration.GetValue<string>("TCPServer:IPAddress", "127.0.0.1");
                int serverPort = configuration.GetValue<int>("TCPServer:Port", 5000);

                // Connect to the TCP server
                using (var client = new TcpClient())
                {
                    _logger.LogInformation($"Connecting to TCP server at {serverIpAddress}:{serverPort}");

                    var connectTask = client.ConnectAsync(serverIpAddress, serverPort);
                    var timeoutTask = Task.Delay(_timeout);

                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning($"Connection to TCP server at {serverIpAddress}:{serverPort} timed out");
                        return (false, "Connection timed out", false);
                    }

                    if (!client.Connected)
                    {
                        _logger.LogWarning($"Failed to connect to TCP server at {serverIpAddress}:{serverPort}");
                        return (false, "Failed to connect to server", false);
                    }

                    _logger.LogInformation($"Successfully connected to TCP server at {serverIpAddress}:{serverPort}");

                    // Prepare and send the command
                    using (var stream = client.GetStream())
                    {
                        // Use the question mark separator since that seems to be what's working in your system
                        string command = $"#i?{currentSerialNumber}?{newSerialNumber}?77\u00FD";

                        byte[] commandBytes = Encoding.ASCII.GetBytes(command);
                        _logger.LogInformation($"Sending serial update command: {command}");

                        await stream.WriteAsync(commandBytes, 0, commandBytes.Length);

                        // Wait for response with timeout
                        var buffer = new byte[4096];

                        // Set up a task to read from the stream
                        var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                        var readTimeoutTask = Task.Delay(_timeout);

                        var readCompleted = await Task.WhenAny(readTask, readTimeoutTask);
                        if (readCompleted == readTimeoutTask)
                        {
                            _logger.LogWarning("No response received within timeout period");
                            return (false, "No response received (timeout)", false);
                        }

                        int bytesRead = await readTask;
                        if (bytesRead == 0)
                        {
                            _logger.LogWarning("Connection closed by server");
                            return (false, "Connection closed by server", false);
                        }

                        string response = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                        _logger.LogInformation($"Received response: {response.Trim()}");

                        // Detailed logging of the response bytes
                        _logger.LogDebug($"Response bytes: {BitConverter.ToString(buffer, 0, bytesRead)}");

                        // Check if the response confirms the operation
                        bool isConfirmed = IsSuccessResponse(response, currentSerialNumber, newSerialNumber);

                        return (true, response.Trim(), isConfirmed);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending serial number update command");
                return (false, $"Error: {ex.Message}", false);
            }
        }

        private bool IsSuccessResponse(string response, string oldSerial, string newSerial)
        {
            // Normalize the response by removing any whitespace or control characters
            response = response.Trim();

            _logger.LogInformation($"Checking response format: {response}");

            try
            {
                // Handle different separator characters
                string[] parts;
                if (response.Contains("\u00AA"))
                    parts = response.Split('\u00AA');
                else if (response.Contains("?"))
                    parts = response.Split('?');
                else if (response.Contains("|"))
                    parts = response.Split('|');
                else if (response.Contains("*"))
                    parts = response.Split('*');
                else
                    parts = new string[0];

                // Log all parts for debugging
                for (int i = 0; i < parts.Length; i++)
                {
                    _logger.LogDebug($"Response part {i}: {parts[i]}");
                }

                // Check for minimum required parts
                if (parts.Length < 4)
                {
                    _logger.LogWarning($"Response has too few parts: {parts.Length}");
                    return false;
                }

                // Check message starts with #I
                if (parts[0] != "#I")
                {
                    _logger.LogWarning($"Response does not start with #I: {parts[0]}");
                    return false;
                }

                // Check old serial number
                if (parts[1] != oldSerial)
                {
                    _logger.LogWarning($"Response old serial mismatch. Expected: {oldSerial}, Got: {parts[1]}");
                    return false;
                }

                // Check new serial number
                if (parts[2] != newSerial)
                {
                    _logger.LogWarning($"Response new serial mismatch. Expected: {newSerial}, Got: {parts[2]}");
                    return false;
                }

                // Check for OK status
                if (parts[3] != "OK")
                {
                    _logger.LogWarning($"Response status is not OK: {parts[3]}");
                    return false;
                }

                // All checks passed
                _logger.LogInformation("Response format is valid and indicates success");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing response");
                return false;
            }
        }
    }
}