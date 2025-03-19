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
        /// Sends a serial number update command directly to a device and processes the response.
        /// </summary>
        /// <returns>Tuple with: success flag, response message, and confirmation flag</returns>
        public async Task<(bool Success, string Response, bool Confirmed)> UpdateSerialNumberAsync(string currentSerialNumber, string newSerialNumber)
        {
            _logger.LogInformation($"Initiating serial number update. Current: {currentSerialNumber}, New: {newSerialNumber}");

            try
            {
                // Connect to local TCP server on port 5000
                using (var client = new TcpClient())
                {
                    string ipAddress = "127.0.0.1"; // Connect to localhost
                    int port = 5000; // Standard TCP server port

                    // Connect to our TCP server
                    var connectTask = client.ConnectAsync(ipAddress, port);
                    var timeoutTask = Task.Delay(_timeout);

                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning($"Connection to server on port {port} timed out");
                        return (false, "Connection timed out", false);
                    }

                    if (!client.Connected)
                    {
                        _logger.LogWarning($"Failed to connect to server on port {port}");
                        return (false, "Failed to connect to server", false);
                    }

                    _logger.LogInformation($"Successfully connected to server on port {port}");

                    // Prepare and send the command
                    using (var stream = client.GetStream())
                    {
                        // Format is: #iªCurrentSNªNewSNª77ý
                        string command = $"#i\u00AA{currentSerialNumber}\u00AA{newSerialNumber}\u00AA77\u00FD";
                        byte[] commandBytes = Encoding.ASCII.GetBytes(command);

                        _logger.LogInformation($"Sending command: {command}");
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

                        // Check if the response is in the expected format: #IªSX0000000ªLD0000000ªOKª77ý
                        bool isConfirmed = IsSuccessResponse(response, currentSerialNumber, newSerialNumber);

                        if (isConfirmed)
                        {
                            _logger.LogInformation("Response confirms successful serial number update");

                            // Update the database directly here
                            using (var scope = _scopeFactory.CreateScope())
                            {
                                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                                var device = await dbContext.Devices.FirstOrDefaultAsync(d => d.SerialNumber == currentSerialNumber);

                                if (device != null)
                                {
                                    _logger.LogInformation($"Updating device {device.Id} serial number in database from {currentSerialNumber} to {newSerialNumber}");
                                    device.SerialNumber = newSerialNumber;
                                    await dbContext.SaveChangesAsync();
                                    _logger.LogInformation("Database updated successfully");
                                }
                                else
                                {
                                    _logger.LogWarning($"Device with serial number {currentSerialNumber} not found in database");
                                }
                            }

                            return (true, response.Trim(), true);
                        }
                        else
                        {
                            _logger.LogWarning("Response does not confirm successful serial number update");
                            return (true, response.Trim(), false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending serial number update command");
                return (false, $"Error: {ex.Message}", false);
            }
        }

        /// <summary>
        /// Checks if the response indicates a successful serial number update
        /// </summary>
        private bool IsSuccessResponse(string response, string oldSerial, string newSerial)
        {
            // Normalize the response by removing any whitespace or control characters
            response = response.Trim();

            _logger.LogInformation($"Checking response format: {response}");

            // Expected format: #IªSX0000000ªLD0000000ªOKª77ý
            // Where SX0000000 is the old serial and LD0000000 is the new serial

            try
            {
                // Split the response by the separator character 'ª'
                string[] parts = response.Split('\u00AA');

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