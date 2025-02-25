using Microsoft.EntityFrameworkCore;
using DeviceDataCollector.Models;

namespace DeviceDataCollector.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<DeviceData> DeviceData { get; set; }
    }
}