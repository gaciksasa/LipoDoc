// Controllers/SystemController.cs
using DeviceDataCollector.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeviceDataCollector.Controllers
{
    [Authorize(Policy = "RequireAdminRole")]
    public class SystemController : Controller
    {
        private readonly NetworkUtilityService _networkUtilityService;
        private readonly IConfiguration _configuration;

        public SystemController(
            NetworkUtilityService networkUtilityService,
            IConfiguration configuration)
        {
            _networkUtilityService = networkUtilityService;
            _configuration = configuration;
        }

        public IActionResult Network()
        {
            var model = new NetworkViewModel
            {
                IPAddresses = _networkUtilityService.GetAllIPv4Addresses(),
                CurrentTcpServerIP = _configuration.GetValue<string>("TCPServer:IPAddress"),
                CurrentTcpServerPort = _configuration.GetValue<int>("TCPServer:Port", 5000)
            };

            return View(model);
        }
    }

    public class NetworkViewModel
    {
        public List<IPAddressInfo> IPAddresses { get; set; }
        public string CurrentTcpServerIP { get; set; }
        public int CurrentTcpServerPort { get; set; }
    }
}