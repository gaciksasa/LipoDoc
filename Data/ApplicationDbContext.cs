using Microsoft.EntityFrameworkCore;
using DeviceDataCollector.Models;
using Microsoft.CodeAnalysis.Scripting;

namespace DeviceDataCollector.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<DeviceData> DeviceData { get; set; }
        public DbSet<User> Users { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Seed admin and user accounts
            modelBuilder.Entity<User>().HasData(
                new User
                {
                    Id = 1,
                    Username = "admin",
                    // In production, use a proper password hashing mechanism
                    // This is a simple hash of "admin123" for demonstration
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
                    Role = "Admin",
                    FullName = "Administrator",
                    Email = "admin@blooddonation.org",
                    CreatedAt = DateTime.UtcNow
                },
                new User
                {
                    Id = 2,
                    Username = "user",
                    // Simple hash of "user123"
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("user123"),
                    Role = "User",
                    FullName = "Regular User",
                    Email = "user@blooddonation.org",
                    CreatedAt = DateTime.UtcNow
                }
            );
        }
    }
}