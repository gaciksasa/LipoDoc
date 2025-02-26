using System.Net.Sockets;
using System.Text;

namespace DeviceDataCollector.Services
{
    public class TCPClientService
    {
        private readonly ILogger<TCPClientService> _logger;
        private readonly IConfiguration _configuration;

        public TCPClientService(ILogger<TCPClientService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        /// <summary>
        /// Sends data directly to a TCP device without storing it in the database
        /// </summary>
        public async Task<(bool Success, string? Response)> SendDataToDeviceAsync(string deviceAddress, int port, string message)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    // Set timeout for connection attempts
                    var timeoutMs = 5000; // 5 seconds timeout
                    var connectionTask = client.ConnectAsync(deviceAddress, port);

                    // Wait for the connection with timeout
                    var timeoutTask = Task.Delay(timeoutMs);
                    await Task.WhenAny(connectionTask, timeoutTask);

                    if (!client.Connected)
                    {
                        _logger.LogWarning($"Connection timeout after {timeoutMs}ms to {deviceAddress}:{port}");
                        return (false, "Connection timeout");
                    }

                    _logger.LogInformation($"Connected to device at {deviceAddress}:{port}");

                    // Get the stream
                    using (NetworkStream stream = client.GetStream())
                    {
                        // Set read/write timeout
                        client.ReceiveTimeout = 5000;
                        client.SendTimeout = 5000;

                        // Convert message to bytes and append newline if not present
                        string messageToSend = message.EndsWith("\r\n") ? message : message + "\r\n";
                        byte[] data = Encoding.ASCII.GetBytes(messageToSend);

                        // Send the message
                        await stream.WriteAsync(data, 0, data.Length);
                        _logger.LogInformation($"Sent message to device: {message}");

                        // Wait for response (optional)
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
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error receiving response from device");
                        }

                        return (true, response);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending data to device at {deviceAddress}:{port}");
                return (false, ex.Message);
            }
        }
    }
}