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

        public TCPServerService(
            ILogger<TCPServerService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _port = configuration.GetValue<int>("TCPServer:Port", 8080);
            _ipAddress = configuration.GetValue<string>("TCPServer:IPAddress", "0.0.0.0");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("TCP Server Service is starting...");

            _server = new TcpListener(IPAddress.Parse(_ipAddress), _port);
            _server.Start();

            _logger.LogInformation($"TCP Server listening on {_ipAddress}:{_port}");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await _server.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error accepting TCP client");
                }
            }

            _server.Stop();
            _logger.LogInformation("TCP Server Service is stopping...");
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
        {
            string clientIP = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
            int clientPort = ((IPEndPoint)client.Client.RemoteEndPoint).Port;

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

                    // Store data in database
                    await StoreDataAsync(clientIP, clientPort, data);

                    // Send acknowledgment
                    byte[] response = Encoding.ASCII.GetBytes("Data received successfully\r\n");
                    await stream.WriteAsync(response, 0, response.Length, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling client {clientIP}:{clientPort}");
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