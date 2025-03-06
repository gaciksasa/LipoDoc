using DeviceDataCollector.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceDataCollector.Services
{
    public class DeviceMessageParser
    {
        private readonly ILogger<DeviceMessageParser> _logger;

        public DeviceMessageParser(ILogger<DeviceMessageParser> logger)
        {
            _logger = logger;
        }

        public enum MessageType
        {
            StatusMessage,    // #S
            DataMessage,      // #D
            RequestMessage,   // #u
            AcknowledgeMessage, // #A
            NoMoreDataMessage, // #U
            Unknown
        }

        /// <summary>
        /// Parses a raw message from the device and returns the appropriate data model
        /// </summary>
        public object ParseMessage(string message, string ipAddress, int port)
        {
            try
            {
                // First, log the raw message before any processing
                _logger.LogInformation($"Processing raw message: {message}");

                // Normalize separators - add any separator character you want to support
                message = message.Replace("|", "ª").Replace("?", "ª").Replace("*", "ª");

                // Clean up the message - remove any unwanted control characters but keep the separators
                message = CleanMessage(message);

                if (string.IsNullOrWhiteSpace(message))
                {
                    _logger.LogWarning("Empty message after cleaning");
                    return null;
                }

                var messageType = DetermineMessageType(message);
                _logger.LogInformation($"Detected message type: {messageType}");

                switch (messageType)
                {
                    case MessageType.StatusMessage:
                        _logger.LogInformation($"Status message received: {message}");
                        var statusResult = ParseStatusMessage(message, ipAddress, port);
                        if (statusResult != null)
                        {
                            _logger.LogInformation($"Parsed status: DeviceId={statusResult.DeviceId}, Status={statusResult.Status}, AvailableData={statusResult.AvailableData}, Timestamp={statusResult.Timestamp}");
                        }
                        return statusResult;

                    case MessageType.DataMessage:
                        _logger.LogInformation($"Data message received: {message}");
                        var dataResult = ParseDataMessage(message, ipAddress, port);
                        if (dataResult != null)
                        {
                            _logger.LogInformation($"Parsed data: DeviceId={dataResult.DeviceId}, DonationId={dataResult.DonationIdBarcode ?? "none"}, LipemicValue={dataResult.LipemicValue?.ToString() ?? "none"}");
                        }
                        return dataResult;

                    case MessageType.RequestMessage:
                        _logger.LogInformation($"Request message received: {message}");
                        return null; // We don't store request messages

                    case MessageType.AcknowledgeMessage:
                        _logger.LogInformation($"Acknowledge message received: {message}");
                        return null; // We don't store acknowledge messages

                    case MessageType.NoMoreDataMessage:
                        _logger.LogInformation($"No more data message received: {message}");
                        return null; // We don't store these messages

                    default:
                        _logger.LogWarning($"Unknown message format received: {message}");
                        return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error parsing message: {message}");
                return null;
            }
        }

        private string CleanMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            // Replace line feeds, carriage returns, and other control characters, keeping the message ª separator
            var cleaned = Regex.Replace(message, @"[^\x20-\x7E\xFDª]", "");
            return cleaned;
        }

        private MessageType DetermineMessageType(string message)
        {
            if (message.StartsWith("#S"))
                return MessageType.StatusMessage;
            else if (message.StartsWith("#D"))
                return MessageType.DataMessage;
            else if (message.StartsWith("#u"))
                return MessageType.RequestMessage;
            else if (message.StartsWith("#A"))
                return MessageType.AcknowledgeMessage;
            else if (message.StartsWith("#U"))
                return MessageType.NoMoreDataMessage;
            else
                return MessageType.Unknown;
        }

        private DeviceStatus ParseStatusMessage(string message, string ipAddress, int port)
        {
            // Format: #SªSNªStatusª"vreme.timevreme.date"ª"AvailableData"ª"CS"ý
            var parts = message.Split('ª');

            _logger.LogDebug($"Status message parts count: {parts.Length}, parts: {string.Join(", ", parts)}");

            if (parts.Length < 5)
            {
                _logger.LogWarning($"Invalid status message format (not enough parts): {message}");
                return null;
            }

            try
            {
                var deviceStatus = new DeviceStatus
                {
                    DeviceId = parts[1],
                    Status = int.TryParse(parts[2], out int status) ? status : 0,
                    Timestamp = ParseTimestamp(parts[3]),
                    AvailableData = int.TryParse(parts[4], out int availableData) ? availableData : 0,
                    CheckSum = parts.Length > 5 ? parts[5].TrimEnd('ý') : null,
                    RawPayload = message,
                    IPAddress = ipAddress,
                    Port = port
                };

                return deviceStatus;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error parsing status message parts: {message}");
                return null;
            }
        }

        private DonationsData ParseDataMessage(string message, string ipAddress, int port)
        {
            // Format depends on the barcode mode, but all start with: #DªSNªvreme.timevreme.dateª
            var parts = message.Split('ª');

            _logger.LogDebug($"Data message parts count: {parts.Length}, parts: {string.Join(", ", parts)}");

            if (parts.Length < 5)
            {
                _logger.LogWarning($"Invalid data message format (not enough parts): {message}");
                return null;
            }

            try
            {
                var donationData = new DonationsData
                {
                    DeviceId = parts[1],
                    Timestamp = ParseTimestamp(parts[2]),
                    MessageType = "#D",
                    RawPayload = message,
                    IPAddress = ipAddress,
                    Port = port
                };

                // Check if barcode mode is enabled
                int startIndex = 3;
                if (parts[startIndex] == "B")
                {
                    donationData.IsBarcodeMode = true;
                    startIndex++; // Skip the B marker

                    // Extract barcode data based on position and availability
                    if (startIndex < parts.Length && !string.IsNullOrEmpty(parts[startIndex]))
                        donationData.RefCode = parts[startIndex];
                    startIndex++;

                    if (startIndex < parts.Length && !string.IsNullOrEmpty(parts[startIndex]))
                        donationData.DonationIdBarcode = parts[startIndex];
                    startIndex++;

                    if (startIndex < parts.Length && !string.IsNullOrEmpty(parts[startIndex]))
                        donationData.OperatorIdBarcode = parts[startIndex];
                    startIndex++;

                    if (startIndex < parts.Length && !string.IsNullOrEmpty(parts[startIndex]))
                        donationData.LotNumber = parts[startIndex];
                    startIndex++;
                }

                // Find the "M" marker which indicates start of measurement data
                int measurementIndex = Array.IndexOf(parts, "M", startIndex);
                if (measurementIndex >= 0)
                {
                    if (measurementIndex + 1 < parts.Length && int.TryParse(parts[measurementIndex + 1], out int lipemicValue))
                        donationData.LipemicValue = lipemicValue;

                    if (measurementIndex + 2 < parts.Length)
                        donationData.LipemicGroup = parts[measurementIndex + 2];

                    if (measurementIndex + 3 < parts.Length)
                        donationData.LipemicStatus = parts[measurementIndex + 3];
                }

                // Extract checksum - it's the last part before the delimiter ý
                var lastPart = parts[parts.Length - 1];
                donationData.CheckSum = lastPart.TrimEnd('ý');

                return donationData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error parsing data message parts: {message}");
                return null;
            }
        }

        private DateTime ParseTimestamp(string timestamp)
        {
            try
            {
                if (string.IsNullOrEmpty(timestamp))
                    return DateTime.Now; // Return local time instead of UTC

                _logger.LogDebug($"Parsing timestamp: {timestamp}");

                // Format: "HH:mm:ssdd:MM:yyyy" or similar
                // Need to handle possible formats

                string cleanTimestamp = timestamp.Replace(":", "").Replace(".", "");

                if (cleanTimestamp.Length >= 12)
                {
                    string timeStr = cleanTimestamp.Substring(0, 6);
                    string dateStr = cleanTimestamp.Substring(6);

                    int hour = int.Parse(timeStr.Substring(0, 2));
                    int minute = int.Parse(timeStr.Substring(2, 2));
                    int second = int.Parse(timeStr.Substring(4, 2));

                    int day = int.Parse(dateStr.Substring(0, 2));
                    int month = int.Parse(dateStr.Substring(2, 2));
                    int year = int.Parse(dateStr.Substring(4));

                    // Return local DateTime instead of UTC
                    return new DateTime(year, month, day, hour, minute, second);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error parsing timestamp: {timestamp}");
            }

            return DateTime.Now; // Default to current local time if parsing fails
        }
    }
}