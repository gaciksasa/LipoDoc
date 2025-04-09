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
            NoMoreDataMessage,  // #U
            SerialUpdateResponseMessage, // #I 
            SetupResponseMessage, // #R
            Unknown
        }

        public class SerialUpdateResponse
        {
            public string OldSerialNumber { get; set; }
            public string NewSerialNumber { get; set; }
            public string Status { get; set; }
            public string CheckSum { get; set; }
            public string RawPayload { get; set; }
            public DateTime Timestamp { get; set; }
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

                    case MessageType.SerialUpdateResponseMessage:
                        _logger.LogInformation($"Serial number update response received: {message}");
                        // Parse the response and validate it
                        var responseResult = ParseSerialUpdateResponse(message);
                        if (responseResult != null)
                        {
                            _logger.LogInformation($"Parsed serial update response: OldSN={responseResult.OldSerialNumber}, NewSN={responseResult.NewSerialNumber}, Status={responseResult.Status}");
                        }
                        return responseResult;

                    case MessageType.SetupResponseMessage:
                        _logger.LogInformation($"Setup response received: {message}");
                        var setupResult = ParseSetupResponse(message, ipAddress, port);
                        if (setupResult != null)
                        {
                            _logger.LogInformation($"Parsed setup response: DeviceId={setupResult.DeviceId}, SW={setupResult.SoftwareVersion}, HW={setupResult.HardwareVersion}");
                        }
                        return setupResult;

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

        public MessageType DetermineMessageType(string message)
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
            else if (message.StartsWith("#I"))
                return MessageType.SerialUpdateResponseMessage;
            else if (message.StartsWith("#R"))
                return MessageType.SetupResponseMessage;
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
                    Timestamp = ParseTimestamp(parts[3]), // device's timestamp
                    DeviceTimestamp = ParseTimestamp(parts[3]), // device's original timestamp
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

            if (parts.Length < 4) // Minimum: #D, SN, timestamp, and something else
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

                // First, let's find the key markers
                int bIndex = -1;
                int mIndex = -1;

                for (int i = 3; i < parts.Length; i++)
                {
                    if (parts[i] == "B") bIndex = i;
                    if (parts[i] == "M") mIndex = i;
                }

                _logger.LogDebug($"B marker at position: {bIndex}, M marker at position: {mIndex}");

                // Process barcode data if B marker found
                if (bIndex >= 0)
                {
                    donationData.IsBarcodeMode = true;

                    // Calculate how many barcode fields we have between B and M (or end)
                    int endIndex = mIndex > 0 ? mIndex : parts.Length;
                    int barcodeFieldsCount = endIndex - bIndex - 1;

                    _logger.LogDebug($"Barcode fields count: {barcodeFieldsCount}");

                    // Assign barcode fields based on their position after B marker
                    for (int i = 0; i < barcodeFieldsCount && bIndex + 1 + i < parts.Length; i++)
                    {
                        string value = parts[bIndex + 1 + i];
                        if (string.IsNullOrEmpty(value)) continue;

                        switch (i)
                        {
                            case 0: donationData.RefCode = value; break;
                            case 1: donationData.DonationIdBarcode = value; break;
                            case 2: donationData.OperatorIdBarcode = value; break;
                            case 3: donationData.LotNumber = value; break;
                        }
                    }
                }

                // Process measurement data if M marker found
                if (mIndex >= 0 && mIndex + 1 < parts.Length)
                {
                    // Extract lipemic data based on position after M marker
                    if (mIndex + 1 < parts.Length && int.TryParse(parts[mIndex + 1], out int lipemicValue))
                        donationData.LipemicValue = lipemicValue;

                    if (mIndex + 2 < parts.Length)
                        donationData.LipemicGroup = parts[mIndex + 2];

                    if (mIndex + 3 < parts.Length)
                        donationData.LipemicStatus = parts[mIndex + 3];
                }

                // Handle special case when the message doesn't follow the expected format
                // In some cases, B and M markers might not be correctly delimited
                if (bIndex < 0 && mIndex < 0 && parts.Length >= 4)
                {
                    _logger.LogWarning("No B or M markers found, attempting alternate parsing");

                    // Try to identify parts by their format
                    for (int i = 3; i < parts.Length; i++)
                    {
                        string part = parts[i];

                        // Try to detect numeric values that could be lipemic values
                        if (int.TryParse(part, out int value) && value > 0)
                        {
                            donationData.LipemicValue = value;

                            // Check if next item could be lipemic group (I, II, III, IV)
                            if (i + 1 < parts.Length &&
                                (parts[i + 1] == "I" || parts[i + 1] == "II" ||
                                 parts[i + 1] == "III" || parts[i + 1] == "IV"))
                            {
                                donationData.LipemicGroup = parts[i + 1];

                                // Check if next item could be lipemic status
                                if (i + 2 < parts.Length &&
                                    (parts[i + 2] == "LIPEMIC" || parts[i + 2] == "PASSED"))
                                {
                                    donationData.LipemicStatus = parts[i + 2];
                                }
                            }

                            break;
                        }
                    }
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


        private SerialUpdateResponse ParseSerialUpdateResponse(string message)
        {
            // Format: #IªOldSNªNewSNªOKª77ý
            var parts = message.Split('ª');

            _logger.LogDebug($"Serial update response parts count: {parts.Length}, parts: {string.Join(", ", parts)}");

            if (parts.Length < 4)
            {
                _logger.LogWarning($"Invalid serial update response format (not enough parts): {message}");
                return null;
            }

            try
            {
                var response = new SerialUpdateResponse
                {
                    OldSerialNumber = parts[1],
                    NewSerialNumber = parts[2],
                    Status = parts[3],
                    CheckSum = parts.Length > 4 ? parts[4].TrimEnd('\u00FD') : null,
                    RawPayload = message,
                    Timestamp = DateTime.Now
                };

                _logger.LogInformation($"Parsed serial update response: Old={response.OldSerialNumber}, New={response.NewSerialNumber}, Status={response.Status}");
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error parsing serial update response: {message}");
                return null;
            }
        }

        public class SetupResponse
        {
            public string DeviceId { get; set; }
            public string SoftwareVersion { get; set; }
            public string HardwareVersion { get; set; }
            public string SetupData { get; set; }
            public string RawResponse { get; set; }
        }

        private SetupResponse ParseSetupResponse(string message, string ipAddress, int port)
        {
            try
            {
                // Format is documented in the protocol
                // This is a complex message with many parts, so we'll keep it simple and
                // store the raw message for detailed parsing later

                // Normalize separators
                message = message.Replace("?", "ª").Replace("|", "ª").Replace("*", "ª");
                var parts = message.Split('ª');

                if (parts.Length < 4)
                {
                    _logger.LogWarning($"Invalid setup response format: {message}");
                    return null;
                }

                return new SetupResponse
                {
                    DeviceId = parts[1],
                    SoftwareVersion = parts[2],
                    HardwareVersion = parts[3],
                    SetupData = message,
                    RawResponse = message
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error parsing setup response: {message}");
                return null;
            }
        }
    }
}