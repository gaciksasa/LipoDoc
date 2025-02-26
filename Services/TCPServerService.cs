using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
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

        // In TCPServerService.cs
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

                    // Store all data in database EXCEPT data from the web application's send form
                    bool isFromWebApp = data.Contains("sent message to device") &&
                                        (clientIP == "127.0.0.1" || clientIP == "::1" || clientIP == "localhost");

                    if (!isFromWebApp)
                    {
                        await StoreDataAsync(clientIP, clientPort, data);
                        _logger.LogInformation($"Data from {clientIP}:{clientPort} stored in database");
                    }
                    else
                    {
                        _logger.LogInformation($"Message from web app {clientIP}:{clientPort} - NOT storing in database");
                    }

                    // Send acknowledgment
                    byte[] response = Encoding.ASCII.GetBytes("Data received successfully\r\n");
                    await stream.WriteAsync(response, 0, response.Length, stoppingToken);
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

        private async Task StoreDataAsync(string ipAddress, int port, string payload)
        {
            try
            {
                // Create a new scope to resolve the database context
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var deviceData = new DeviceData
                {
                    DeviceId = $"{ipAddress}:{port}", // Using IP:Port as device ID for now
                    Timestamp = DateTime.UtcNow,
                    DataPayload = payload,
                    IPAddress = ipAddress,
                    Port = port
                };

                dbContext.DeviceData.Add(deviceData);
                await dbContext.SaveChangesAsync();

                _logger.LogInformation($"Data from {ipAddress}:{port} stored in database");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error storing data in database");
            }
        }
    }
}