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

        public DbSet<DonationsData> DonationsData { get; set; }
        public DbSet<DeviceStatus> DeviceStatuses { get; set; }
        public DbSet<CurrentDeviceStatus> CurrentDeviceStatuses { get; set; }
        public DbSet<Device> Devices { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<SystemNotification> SystemNotifications { get; set; }
        public DbSet<DeviceSetup> DeviceSetups { get; set; }
        public DbSet<ExportSettingsConfig> ExportSettingsConfigs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure indexes for better performance
            modelBuilder.Entity<DonationsData>()
                .HasIndex(d => d.DeviceId);

            modelBuilder.Entity<DonationsData>()
                .HasIndex(d => d.Timestamp);

            modelBuilder.Entity<DonationsData>()
                .HasIndex(d => d.DonationIdBarcode);

            modelBuilder.Entity<DeviceStatus>()
                .HasIndex(d => d.DeviceId);

            modelBuilder.Entity<DeviceStatus>()
                .HasIndex(d => d.Timestamp);

            // For the CurrentDeviceStatus table, DeviceId is already the PK

            // Seed admin and user accounts
            modelBuilder.Entity<User>().HasData(
                new User
                {
                    Id = 1,
                    Username = "admin",
                    // Pre-hashed password for "admin123" 
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
                    Role = "Admin",
                    FullName = "Administrator",
                    Email = "admin@blooddonation.org",
                    CreatedAt = DateTime.Now // Using local time instead of UTC
                },
                new User
                {
                    Id = 2,
                    Username = "user",
                    // Pre-hashed password for "user123"
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("user123"),
                    Role = "User",
                    FullName = "Regular User",
                    Email = "user@blooddonation.org",
                    CreatedAt = DateTime.Now // Using local time instead of UTC
                }
            );
        }
    }
}