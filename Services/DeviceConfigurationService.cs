using DeviceDataCollector.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace DeviceDataCollector.Services
{
    public class DeviceConfigurationService
    {
        private readonly ILogger<DeviceConfigurationService> _logger;
        private readonly IConfiguration _configuration;
        private const char SEPARATOR = '\u00AA'; // Unicode 170 - the special separator character
        private readonly TCPServerService _tcpServerService;

        public DeviceConfigurationService(
            ILogger<DeviceConfigurationService> logger,
            IConfiguration configuration,
            TCPServerService tcpServerService)
        {
            _logger = logger;
            _configuration = configuration;
            _tcpServerService = tcpServerService;
        }

        /// Requests device configuration from a device
        // Services/DeviceConfigurationService.cs - Update the GetDeviceConfigurationAsync method

        public async Task<DeviceConfigurationViewModel> GetDeviceConfigurationAsync(string deviceId, string ipAddress, int port)
        {
            _logger.LogInformation($"Requesting configuration from device {deviceId} at {ipAddress}:{port}");

            try
            {
                // Send the configuration request through the queue
                string requestCommand = $"#r{SEPARATOR}{deviceId}{SEPARATOR}\n";

                _logger.LogInformation($"Queuing configuration request command: {requestCommand}");

                string response = await _tcpServerService.QueueCommandAsync(
                    deviceId,
                    requestCommand,
                    "#R", // Expected response prefix
                    30000  // Longer timeout for configuration
                );

                if (!string.IsNullOrEmpty(response) && response.StartsWith("#R"))
                {
                    _logger.LogInformation($"Successfully received configuration from {deviceId}");
                    return ParseDeviceConfiguration(response);
                }
                else
                {
                    _logger.LogWarning($"Invalid configuration response from {deviceId}: {response?.Substring(0, Math.Min(50, response?.Length ?? 0))}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error requesting configuration from device {deviceId}");
                return null;
            }
        }

        /// Parses device configuration from the response string
        private DeviceConfigurationViewModel ParseDeviceConfiguration(string configResponse)
        {
            try
            {
                _logger.LogInformation("Parsing device configuration response");

                // Split the string by the separator character
                var parts = configResponse.Split(SEPARATOR);

                if (parts.Length < 10)
                {
                    _logger.LogWarning($"Configuration response has too few parts: {parts.Length}");
                    return null;
                }

                var model = new DeviceConfigurationViewModel
                {
                    DeviceId = parts[1],
                    SoftwareVersion = parts[2],
                    HardwareVersion = parts[3],
                    ServerAddress = parts[4],
                    DeviceIPAddress = parts[5],
                    SubnetMask = parts[6],
                    RemotePort = int.TryParse(parts[7], out int remotePort) ? remotePort : 5000,
                    LocalPort = int.TryParse(parts[8], out int localPort) ? localPort : 5001,
                    LipemicIndex1 = int.TryParse(parts[9], out int lipIndex1) ? lipIndex1 : 1000,
                    LipemicIndex2 = int.TryParse(parts[10], out int lipIndex2) ? lipIndex2 : 2000,
                    LipemicIndex3 = int.TryParse(parts[11], out int lipIndex3) ? lipIndex3 : 3000
                };

                // Find the P marker which indicates profiles follow
                int pIndex = -1;
                for (int i = 12; i < parts.Length; i++)
                {
                    if (parts[i] == "P")
                    {
                        pIndex = i;
                        break;
                    }
                }

                if (pIndex > 0 && pIndex + 60 < parts.Length) // 20 profiles x 3 fields
                {
                    // Parse profiles (20 profiles x 3 fields each)
                    model.Profiles.Clear();
                    for (int i = 0; i < 20; i++)
                    {
                        int baseIndex = pIndex + 1 + (i * 3);
                        if (baseIndex + 2 < parts.Length)
                        {
                            var profile = new TubeProfile
                            {
                                Name = parts[baseIndex],
                                RefCode = parts[baseIndex + 1],
                                OffsetValue = int.TryParse(parts[baseIndex + 2], out int offset) ? offset : 0
                            };
                            model.Profiles.Add(profile);
                        }
                    }

                    // After profiles, we have the settings
                    int settingsBaseIndex = pIndex + 61;
                    if (settingsBaseIndex + 7 < parts.Length)
                    {
                        model.TransferModeEnabled = parts[settingsBaseIndex] == "1";
                        model.BarcodesModeEnabled = parts[settingsBaseIndex + 1] == "1";
                        model.OperatorIdEnabled = parts[settingsBaseIndex + 2] == "1";
                        model.LotNumberEnabled = parts[settingsBaseIndex + 3] == "1";
                        model.NetworkName = parts[settingsBaseIndex + 4];
                        model.WifiMode = parts[settingsBaseIndex + 5];
                        model.SecurityType = parts[settingsBaseIndex + 6];
                        model.WifiPassword = parts[settingsBaseIndex + 7];
                    }

                    // Find the B marker for barcode configurations
                    int bIndex = -1;
                    for (int i = settingsBaseIndex + 8; i < parts.Length; i++)
                    {
                        if (parts[i] == "B")
                        {
                            bIndex = i;
                            break;
                        }
                    }

                    if (bIndex > 0 && bIndex + 6 < parts.Length)
                    {
                        // Parse barcode configs (6 configs)
                        model.BarcodeConfigurations.Clear();
                        for (int i = 0; i < 6; i++)
                        {
                            if (bIndex + 1 + i < parts.Length)
                            {
                                string[] barcodeConfig = parts[bIndex + 1 + i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                if (barcodeConfig.Length >= 4)
                                {
                                    var config = new BarcodeConfiguration
                                    {
                                        MinLength = int.TryParse(barcodeConfig[0], out int min) ? min : 0,
                                        MaxLength = int.TryParse(barcodeConfig[1], out int max) ? max : 0,
                                        StartCode = barcodeConfig[2],
                                        StopCode = barcodeConfig[3]
                                    };
                                    model.BarcodeConfigurations.Add(config);
                                }
                            }
                        }
                    }
                }

                return model;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing device configuration");
                return null;
            }
        }

        /// Sends updated configuration to a device
        public async Task<bool> SendDeviceConfigurationAsync(DeviceConfigurationViewModel config, string ipAddress, int port)
        {
            _logger.LogInformation($"Sending configuration to device {config.DeviceId} at {ipAddress}:{port}");

            try
            {
                using (var client = new TcpClient())
                {
                    // Connect to the device
                    var connectTask = client.ConnectAsync(ipAddress, port);
                    var timeoutTask = Task.Delay(5000);

                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning($"Connection to {config.DeviceId} at {ipAddress}:{port} timed out");
                        return false;
                    }

                    if (!client.Connected)
                    {
                        _logger.LogWarning($"Failed to connect to {config.DeviceId} at {ipAddress}:{port}");
                        return false;
                    }

                    using (var stream = client.GetStream())
                    {
                        // Build configuration command (#W + config data)
                        var commandBuilder = new StringBuilder();
                        commandBuilder.Append($"#W{SEPARATOR}{config.DeviceId}{SEPARATOR}");
                        commandBuilder.Append($"{config.SoftwareVersion}{SEPARATOR}");
                        commandBuilder.Append($"{config.HardwareVersion}{SEPARATOR}");
                        commandBuilder.Append($"{config.ServerAddress}{SEPARATOR}");
                        commandBuilder.Append($"{config.DeviceIPAddress}{SEPARATOR}");
                        commandBuilder.Append($"{config.SubnetMask}{SEPARATOR}");
                        commandBuilder.Append($"{config.RemotePort}{SEPARATOR}");
                        commandBuilder.Append($"{config.LocalPort}{SEPARATOR}");
                        commandBuilder.Append($"{config.LipemicIndex1}{SEPARATOR}");
                        commandBuilder.Append($"{config.LipemicIndex2}{SEPARATOR}");
                        commandBuilder.Append($"{config.LipemicIndex3}{SEPARATOR}");

                        // Add P marker for profiles
                        commandBuilder.Append($"P{SEPARATOR}");

                        // Add profiles
                        foreach (var profile in config.Profiles)
                        {
                            commandBuilder.Append($"{profile.Name}{SEPARATOR}");
                            commandBuilder.Append($"{profile.RefCode}{SEPARATOR}");
                            commandBuilder.Append($"{profile.OffsetValue}{SEPARATOR}");
                        }

                        // Add settings
                        commandBuilder.Append($"{(config.TransferModeEnabled ? "1" : "0")}{SEPARATOR}");
                        commandBuilder.Append($"{(config.BarcodesModeEnabled ? "1" : "0")}{SEPARATOR}");
                        commandBuilder.Append($"{(config.OperatorIdEnabled ? "1" : "0")}{SEPARATOR}");
                        commandBuilder.Append($"{(config.LotNumberEnabled ? "1" : "0")}{SEPARATOR}");
                        commandBuilder.Append($"{config.NetworkName}{SEPARATOR}");
                        commandBuilder.Append($"{config.WifiMode}{SEPARATOR}");
                        commandBuilder.Append($"{config.SecurityType}{SEPARATOR}");
                        commandBuilder.Append($"{config.WifiPassword}{SEPARATOR}");

                        // Add B marker for barcode configurations
                        commandBuilder.Append($"B{SEPARATOR}");

                        // Add barcode configurations
                        foreach (var barcodeConfig in config.BarcodeConfigurations)
                        {
                            commandBuilder.Append($"{barcodeConfig.MinLength} {barcodeConfig.MaxLength} {barcodeConfig.StartCode} {barcodeConfig.StopCode}{SEPARATOR}");
                        }

                        // Add checksum placeholder (actual calculation would be implemented in production)
                        // In a real implementation, you would calculate the proper checksum
                        string checksum = "00";

                        // Add end marker, checksum and LF
                        commandBuilder.Append($"{checksum}ý\n");

                        // Send the config command
                        string command = commandBuilder.ToString();
                        byte[] commandData = Encoding.ASCII.GetBytes(command);
                        await stream.WriteAsync(commandData, 0, commandData.Length);

                        _logger.LogInformation($"Configuration sent to {config.DeviceId}");

                        // Wait for initial acknowledgment (#w)
                        byte[] buffer = new byte[1024];
                        var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                        var readTimeoutTask = Task.Delay(5000);

                        var readCompletedTask = await Task.WhenAny(readTask, readTimeoutTask);
                        if (readCompletedTask == readTimeoutTask)
                        {
                            _logger.LogWarning($"No acknowledgment from {config.DeviceId}");
                            return false;
                        }

                        int bytesRead = await readTask;
                        string response = Encoding.ASCII.GetString(buffer, 0, bytesRead);

                        if (!response.StartsWith("#w"))
                        {
                            _logger.LogWarning($"Unexpected acknowledgment from {config.DeviceId}: {response}");
                            return false;
                        }

                        _logger.LogInformation($"Received initial acknowledgment from {config.DeviceId}: {response}");

                        // Wait for final confirmation (#f)
                        readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                        readTimeoutTask = Task.Delay(10000); // Longer timeout for final confirmation

                        readCompletedTask = await Task.WhenAny(readTask, readTimeoutTask);
                        if (readCompletedTask == readTimeoutTask)
                        {
                            _logger.LogWarning($"No final confirmation from {config.DeviceId}");
                            return false;
                        }

                        bytesRead = await readTask;
                        response = Encoding.ASCII.GetString(buffer, 0, bytesRead);

                        if (!response.StartsWith("#f"))
                        {
                            _logger.LogWarning($"Unexpected final confirmation from {config.DeviceId}: {response}");
                            return false;
                        }

                        _logger.LogInformation($"Received final confirmation from {config.DeviceId}: {response}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending configuration to device {config.DeviceId}");
                return false;
            }
        }

        /// Sends a command to change the device's serial number
        public async Task<bool> ChangeDeviceSerialNumberAsync(string oldSerialNumber, string newSerialNumber, string ipAddress, int port)
        {
            _logger.LogInformation($"Changing device serial number from {oldSerialNumber} to {newSerialNumber} at {ipAddress}:{port}");

            try
            {
                using (var client = new TcpClient())
                {
                    // Connect to the device
                    var connectTask = client.ConnectAsync(ipAddress, port);
                    var timeoutTask = Task.Delay(5000);

                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning($"Connection to device at {ipAddress}:{port} timed out");
                        return false;
                    }

                    if (!client.Connected)
                    {
                        _logger.LogWarning($"Failed to connect to device at {ipAddress}:{port}");
                        return false;
                    }

                    using (var stream = client.GetStream())
                    {
                        // Build serial number change command (#i + oldSN + newSN + checksum)
                        // In a real implementation, you would calculate the proper checksum
                        string checksum = "77";
                        string command = $"#i{SEPARATOR}{oldSerialNumber}{SEPARATOR}{newSerialNumber}{SEPARATOR}{checksum}ý\n";

                        byte[] commandData = Encoding.ASCII.GetBytes(command);
                        await stream.WriteAsync(commandData, 0, commandData.Length);

                        _logger.LogInformation($"Serial number change request sent to {oldSerialNumber}");

                        // Wait for confirmation
                        byte[] buffer = new byte[1024];
                        var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                        var readTimeoutTask = Task.Delay(10000);

                        var readCompletedTask = await Task.WhenAny(readTask, readTimeoutTask);
                        if (readCompletedTask == readTimeoutTask)
                        {
                            _logger.LogWarning($"No response from device at {ipAddress}:{port}");
                            return false;
                        }

                        int bytesRead = await readTask;
                        string response = Encoding.ASCII.GetString(buffer, 0, bytesRead);

                        if (!response.StartsWith("#I") || !response.Contains("OK"))
                        {
                            _logger.LogWarning($"Unexpected response from device: {response}");
                            return false;
                        }

                        _logger.LogInformation($"Serial number changed successfully: {response}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error changing device serial number");
                return false;
            }
        }

        /// Sends a command to synchronize the device's date and time with the server
        public async Task<bool> SynchronizeDeviceTimeAsync(string deviceId, string ipAddress, int port)
        {
            _logger.LogInformation($"Synchronizing time for device {deviceId} at {ipAddress}:{port}");

            try
            {
                using (var client = new TcpClient())
                {
                    // Connect to the device
                    var connectTask = client.ConnectAsync(ipAddress, port);
                    var timeoutTask = Task.Delay(5000);

                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning($"Connection to {deviceId} at {ipAddress}:{port} timed out");
                        return false;
                    }

                    if (!client.Connected)
                    {
                        _logger.LogWarning($"Failed to connect to {deviceId} at {ipAddress}:{port}");
                        return false;
                    }

                    using (var stream = client.GetStream())
                    {
                        // Get current date and time in the format DDMMYYYYHHMM
                        string dateTimeString = DateTime.Now.ToString("ddMMyyyyHHmm");

                        // Build time synchronization command (#t + SN + datetime + N + checksum)
                        // In a real implementation, you would calculate the proper checksum
                        string checksum = "DF";
                        string command = $"#t{SEPARATOR}{deviceId}{SEPARATOR}{dateTimeString}{SEPARATOR}N{SEPARATOR}{checksum}ý\n";

                        byte[] commandData = Encoding.ASCII.GetBytes(command);
                        await stream.WriteAsync(commandData, 0, commandData.Length);

                        _logger.LogInformation($"Time synchronization request sent to {deviceId}");

                        // No response expected for time synchronization
                        // Wait a moment to ensure command is processed
                        await Task.Delay(500);

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error synchronizing time for device {deviceId}");
                return false;
            }
        }

        public async Task<bool> CheckDeviceConnectivityAsync(string ipAddress, int port, int timeoutMs = 3000)
        {
            _logger.LogInformation($"Performing connectivity check to {ipAddress}:{port}");

            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(ipAddress, port);
                    var timeoutTask = Task.Delay(timeoutMs);

                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        _logger.LogInformation($"Connectivity check to {ipAddress}:{port} timed out after {timeoutMs}ms");
                        return false;
                    }

                    bool canConnect = client.Connected;
                    _logger.LogInformation($"Connectivity check result: {(canConnect ? "Success" : "Failed")}");

                    return canConnect;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error during connectivity check: {ex.Message}");
                return false;
            }
        }
    }
}