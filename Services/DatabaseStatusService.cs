using DeviceDataCollector.Data;
using Microsoft.EntityFrameworkCore;

namespace DeviceDataCollector.Services
{
    public class DatabaseStatusService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DatabaseStatusService> _logger;

        public DatabaseStatusService(
            IServiceScopeFactory scopeFactory,
            ILogger<DatabaseStatusService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<(bool IsConnected, string StatusMessage)> CheckDatabaseConnectionAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Try to connect to the database
                bool canConnect = await dbContext.Database.CanConnectAsync();

                if (canConnect)
                {
                    return (true, "Connected");
                }
                else
                {
                    return (false, "Cannot connect to database");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking database connection");
                return (false, $"Database error: {ex.Message}");
            }
        }
    }
}