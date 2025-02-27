using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeviceDataCollector.Services
{
    public class TCPServerService : BackgroundService
    {
        private readonly ILogger<TCPServerService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private TcpListener _server;
        private readonly int _port;
        private readonly string _ipAddress;
        private readonly HashSet<string> _appIpAddresses;

        public TCPServerService(
            ILogger<TCPServerService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;

            _port = configuration.GetValue<int>("TCPServer:Port", 5000);
            _ipAddress = configuration.GetValue<string>("TCPServer:IPAddress", "127.0.0.2");

            // List of IP addresses that are part of our application and shouldn't store data
            _appIpAddresses = new HashSet<string> {
                "127.0.0.1",
                "127.0.0.2", // Our app's primary loopback
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
                _logger.LogInformation("TCP Server Service is stopping...");
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
        {
            string clientIP = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
            int clientPort = ((IPEndPoint)client.Client.RemoteEndPoint).Port;

            // Log all connections for debugging
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

                    // Process and store the data
                    await ProcessDeviceMessageAsync(data, clientIP, clientPort);

                    // Send acknowledgment based on message type
                    await SendResponseAsync(client, data, clientIP, stoppingToken);
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
                        dbContext.DeviceStatuses.Add(status);
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Status from {ipAddress}:{port} stored in database");

                        // Also check if we have the device registered
                        await EnsureDeviceRegisteredAsync(dbContext, status.DeviceId);
                    }
                    else if (parsedMessage is DeviceData deviceData)
                    {
                        dbContext.DeviceData.Add(deviceData);
                        await dbContext.SaveChangesAsync();
                        _logger.LogInformation($"Data from {ipAddress}:{port} stored in database");

                        // Also check if we have the device registered
                        await EnsureDeviceRegisteredAsync(dbContext, deviceData.DeviceId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing and storing device message");
            }
        }

        private async Task EnsureDeviceRegisteredAsync(ApplicationDbContext dbContext, string deviceId)
        {
            // Check if the device is already registered
            var existingDevice = await dbContext.Devices.FirstOrDefaultAsync(d => d.SerialNumber == deviceId);

            if (existingDevice == null)
            {
                // Register the device with default values
                var device = new Device
                {
                    SerialNumber = deviceId,
                    Name = $"Device {deviceId}",
                    RegisteredDate = DateTime.UtcNow,
                    LastConnectionTime = DateTime.UtcNow,
                    IsActive = true
                };

                dbContext.Devices.Add(device);
                await dbContext.SaveChangesAsync();
                _logger.LogInformation($"New device registered: {deviceId}");
            }
            else
            {
                // Update last connection time
                existingDevice.LastConnectionTime = DateTime.UtcNow;
                await dbContext.SaveChangesAsync();
            }
        }

        private async Task SendResponseAsync(TcpClient client, string receivedMessage, string deviceId, CancellationToken stoppingToken)
        {
            try
            {
                string response = "";

                // Check message type to determine appropriate response
                if (receivedMessage.StartsWith("#S") || receivedMessage.StartsWith("#D"))
                {
                    // Acknowledge status or data messages
                    response = $"#Aª{deviceId}ª\n";
                }
                else if (receivedMessage.StartsWith("#u"))
                {
                    // Request for data - this would normally be followed by sending cached data
                    // For now, just respond with "no more data" message
                    response = $"#Uª{deviceId}ªB5ý\n";
                }

                if (!string.IsNullOrEmpty(response))
                {
                    byte[] responseData = Encoding.ASCII.GetBytes(response);
                    await client.GetStream().WriteAsync(responseData, 0, responseData.Length, stoppingToken);
                    _logger.LogInformation($"Sent response to {deviceId}: {response}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending response to {deviceId}");
            }
        }
    }
}