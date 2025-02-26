using DeviceDataCollector.Data;
using Microsoft.EntityFrameworkCore;

namespace DeviceDataCollector.Services
{
    public static class DatabaseInitializer
    {
        public static async Task InitializeDatabaseAsync(IServiceProvider serviceProvider, ILogger logger)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                logger.LogInformation("Checking database existence and applying migrations if needed...");

                // This will create the database if it doesn't exist and apply any pending migrations
                await dbContext.Database.MigrateAsync();

                logger.LogInformation("Database check complete - database is ready");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred while initializing the database");
                throw; // Re-throw to halt startup if database initialization fails
            }
        }
    }
}