using Microsoft.AspNetCore.Mvc;
using System.Net.Sockets;
using System.Text;

namespace DeviceDataCollector.Controllers
{
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
            ViewBag.DefaultTargetIP = "127.0.0.3"; // Default to Hercules address
            ViewBag.DefaultPort = 5000;
            ViewBag.AppIP = _configuration.GetValue<string>("TCPServer:IPAddress", "127.0.0.2");
            ViewBag.HerculesIP = "127.0.0.3";

            return View();
        }

        [HttpPost]
        public async Task<IActionResult> SendMessageToDevice(string ipAddress, int port, string message)
        {
            _logger.LogInformation($"Attempting to send message to {ipAddress}:{port}: {message}");

            try
            {
                // Direct TCP client implementation
                using (TcpClient client = new TcpClient())
                {
                    // Set a local endpoint specific to our app
                    string serverIP = _configuration.GetValue<string>("TCPServer:IPAddress", "127.0.0.2");
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

                    _logger.LogInformation($"Connected to device at {ipAddress}:{port} from {serverIP}");

                    using (NetworkStream stream = client.GetStream())
                    {
                        client.ReceiveTimeout = 5000;
                        client.SendTimeout = 5000;

                        // Add newline to message if not present
                        string messageToSend = message.EndsWith("\r\n") ? message : message + "\r\n";
                        byte[] data = Encoding.ASCII.GetBytes(messageToSend);

                        // Send the message
                        await stream.WriteAsync(data, 0, data.Length);
                        _logger.LogInformation($"Sent message to device: {message}");

                        // Wait for response
                        string response = null;
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
    }
}