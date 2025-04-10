using System.Net.Sockets;
using System.Text;
using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
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
            _logger.LogInformation($"Queueing serial number update. Current: {currentSerialNumber}, New: {newSerialNumber}");

            try
            {
                // Dodaj serijski broj u red čekanja za promenu
                bool queued = TCPServerService.QueueSerialNumberChange(currentSerialNumber, newSerialNumber);

                if (!queued)
                {
                    return (false, "Failed to queue serial number change", false);
                }

                // Ovde možemo vratiti uspeh za ubacivanje u red čekanja,
                // ali potvrdu ćemo dobiti tek kada uređaj odgovori na komandu
                return (true, "Serial number change queued. The change will be applied when the device next communicates with the server.", false);

                // Napomena: Stvarna potvrda promene serijskog broja će biti obrađena u TCPServerService
                // kada dobijemo odgovor od uređaja. Baza podataka će biti ažurirana tamo.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error queueing serial number update command");
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

        /// <summary>
        /// Requests the device setup configuration
        /// </summary>
        /// <param name="serialNumber">The serial number of the device</param>
        /// <returns>True if request was queued successfully</returns>
        public bool RequestDeviceSetup(string serialNumber)
        {
            if (string.IsNullOrEmpty(serialNumber))
            {
                _logger.LogWarning("Cannot request setup for null or empty serial number");
                return false;
            }

            _logger.LogInformation($"Queuing setup request for device: {serialNumber}");

            try
            {
                // Queue the request in the TCPServerService
                return TCPServerService.QueueSetupRequest(serialNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error queuing setup request for device {serialNumber}");
                return false;
            }
        }

        public async Task<(bool Success, string Response)> UpdateDeviceSetupAsync(DeviceSetup setup)
        {
            if (setup == null || string.IsNullOrEmpty(setup.DeviceId))
            {
                _logger.LogWarning("Cannot update setup for null or empty device");
                return (false, "Invalid setup data");
            }

            _logger.LogInformation($"Preparing to update setup for device: {setup.DeviceId}");

            try
            {
                // Verify setup data is valid
                if (setup.Profiles == null || setup.BarcodeConfigs == null)
                {
                    _logger.LogWarning($"Setup data is incomplete for device {setup.DeviceId}");
                    return (false, "Setup data is incomplete");
                }

                // Log the key setup parameters for verification
                _logger.LogInformation($"Setup details: ServerAddress={setup.ServerAddress}, RemotePort={setup.RemotePort}, " +
                                      $"Profiles={setup.Profiles.Count}, BarcodeConfigs={setup.BarcodeConfigs.Count}");

                // Queue the setup update in the TCPServerService
                bool success = await TCPServerService.QueueSetupUpdateAsync(setup);

                if (!success)
                {
                    _logger.LogWarning($"Failed to queue setup update command for device {setup.DeviceId}");
                    return (false, "Failed to queue setup update command");
                }

                _logger.LogInformation($"Setup update for device {setup.DeviceId} successfully queued");
                return (true, "Setup update queued. The change will be applied when the device next communicates with the server.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error queueing setup update for device {setup.DeviceId}");
                return (false, $"Error: {ex.Message}");
            }
        }
    }
}