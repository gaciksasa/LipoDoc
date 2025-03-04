using Microsoft.AspNetCore.Mvc;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Authorization;

namespace DeviceDataCollector.Controllers
{
    [Authorize] // Require authentication for all actions
    public class DeviceController : Controller
    {
        private readonly ILogger<DeviceController> _logger;
        private readonly IConfiguration _configuration;

        public DeviceController(
            ILogger<DeviceController> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public IActionResult SendMessage()
        {
            // Get the default IP from config for the view
            ViewBag.DefaultTargetIP = "192.168.1.101"; // Default
            ViewBag.DefaultPort = 5000;
            ViewBag.AppIP = _configuration.GetValue<string>("TCPServer:IPAddress", "192.168.1.124");
            // ViewBag.HerculesIP = "127.0.0.3";

            return View();
        }

        public enum MessageEncoding
        {
            ASCII,       // Standard ASCII encoding
            UTF8,        // UTF-8 encoding
            Base64,      // Base64 encoded message
            Hex,         // Hexadecimal representation
            Binary       // Raw binary conversion
        }

        [HttpPost]
        public async Task<IActionResult> SendMessageToDevice(
            string ipAddress,
            int port,
            string message,
            MessageEncoding encoding = MessageEncoding.ASCII)
        {
            _logger.LogInformation($"Attempting to send message to {ipAddress}:{port} with {encoding} encoding");

            try
            {
                // Prepare the message based on encoding
                byte[] messageBytes = encoding switch
                {
                    MessageEncoding.ASCII => Encoding.ASCII.GetBytes(message + "\r\n"),
                    MessageEncoding.UTF8 => Encoding.UTF8.GetBytes(message + "\r\n"),
                    MessageEncoding.Base64 => Convert.FromBase64String(message),
                    MessageEncoding.Hex => ConvertHexStringToBytes(message),
                    MessageEncoding.Binary => ConvertBinaryStringToBytes(message),
                    _ => Encoding.ASCII.GetBytes(message + "\r\n")
                };

                using (TcpClient client = new TcpClient())
                {
                    // Set a local endpoint specific to our app
                    string serverIP = _configuration.GetValue<string>("TCPServer:IPAddress", "192.168.1.124") ?? string.Empty;
                    client.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(serverIP), 0));

                    // Set timeout for connection
                    var timeoutMs = 5000;
                    var connectionTask = client.ConnectAsync(ipAddress, port);

                    await Task.WhenAny(connectionTask, Task.Delay(timeoutMs));

                    if (!client.Connected)
                    {
                        _logger.LogWarning($"Connection timeout after {timeoutMs}ms to {ipAddress}:{port}");
                        return Json(new
                        {
                            success = false,
                            response = $"Connection timeout connecting to {ipAddress}:{port}",
                            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                        });
                    }

                    using (NetworkStream stream = client.GetStream())
                    {
                        client.ReceiveTimeout = 5000;
                        client.SendTimeout = 5000;

                        // Send the message
                        await stream.WriteAsync(messageBytes, 0, messageBytes.Length);
                        _logger.LogInformation($"Sent {encoding} encoded message to device: {BitConverter.ToString(messageBytes)}");

                        // Wait for response
                        string? response = null;
                        try
                        {
                            byte[] responseBuffer = new byte[1024];

                            // Set a timeout for reading the response
                            var readTask = stream.ReadAsync(responseBuffer, 0, responseBuffer.Length);
                            await Task.WhenAny(readTask, Task.Delay(3000));

                            if (readTask.IsCompleted)
                            {
                                int bytesRead = await readTask;
                                if (bytesRead > 0)
                                {
                                    response = Encoding.ASCII.GetString(responseBuffer, 0, bytesRead);
                                    _logger.LogInformation($"Response from device: {response}");
                                }
                            }
                            else
                            {
                                _logger.LogInformation("No response received from device (timeout)");
                                response = "No response (timeout)";
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error receiving response from device");
                            response = $"Error receiving response: {ex.Message}";
                        }

                        return Json(new
                        {
                            success = true,
                            response = response,
                            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending data to device at {ipAddress}:{port}");
                return Json(new
                {
                    success = false,
                    response = ex.Message,
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                });
            }
        }

        // Helper method to convert hex string to bytes
        private byte[] ConvertHexStringToBytes(string hexString)
        {
            // Remove any spaces or non-hex characters
            hexString = new string(hexString.Where(c => "0123456789ABCDEFabcdef".Contains(c)).ToArray());

            // Ensure even length
            if (hexString.Length % 2 != 0)
                throw new ArgumentException("Hex string must have an even number of characters");

            byte[] bytes = new byte[hexString.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hexString.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        // Helper method to convert binary string to bytes
        private byte[] ConvertBinaryStringToBytes(string binaryString)
        {
            // Remove any spaces
            binaryString = binaryString.Replace(" ", "");

            // Validate binary string
            if (!binaryString.All(c => c == '0' || c == '1'))
                throw new ArgumentException("Input must be a valid binary string");

            // Pad to ensure full bytes
            if (binaryString.Length % 8 != 0)
                throw new ArgumentException("Binary string length must be a multiple of 8");

            byte[] bytes = new byte[binaryString.Length / 8];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(binaryString.Substring(i * 8, 8), 2);
            }
            return bytes;
        }
    }
}