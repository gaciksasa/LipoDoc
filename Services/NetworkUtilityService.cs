// Services/NetworkUtilityService.cs
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DeviceDataCollector.Services
{
    public class NetworkUtilityService
    {
        private readonly ILogger<NetworkUtilityService> _logger;

        public NetworkUtilityService(ILogger<NetworkUtilityService> logger)
        {
            _logger = logger;
        }

        public List<IPAddressInfo> GetAllIPv4Addresses()
        {
            var result = new List<IPAddressInfo>();

            try
            {
                // Get all network interfaces
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();

                foreach (var netInterface in interfaces)
                {
                    // Only consider operational interfaces
                    if (netInterface.OperationalStatus != OperationalStatus.Up)
                        continue;

                    // Skip loopback and tunnel adapters
                    if (netInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        netInterface.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                        netInterface.Description.Contains("Tunnel", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Get IP properties
                    var ipProps = netInterface.GetIPProperties();

                    // Get all unicast addresses
                    foreach (var addr in ipProps.UnicastAddresses)
                    {
                        // Only consider IPv4 addresses
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            result.Add(new IPAddressInfo
                            {
                                IPAddress = addr.Address.ToString(),
                                InterfaceName = netInterface.Name,
                                Description = netInterface.Description,
                                IsInternal = IsPrivateIP(addr.Address)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting IP addresses");
            }

            return result;
        }

        // Check if IP address is private (internal)
        private bool IsPrivateIP(IPAddress ip)
        {
            byte[] bytes = ip.GetAddressBytes();

            // Check for 10.x.x.x
            if (bytes[0] == 10)
                return true;

            // Check for 172.16.x.x - 172.31.x.x
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;

            // Check for 192.168.x.x
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;

            return false;
        }
    }

    public class IPAddressInfo
    {
        public string IPAddress { get; set; }
        public string InterfaceName { get; set; }
        public string Description { get; set; }
        public bool IsInternal { get; set; }
    }
}