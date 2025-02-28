using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using Microsoft.EntityFrameworkCore;

namespace DeviceDataCollector.Services
{
    public class AuthService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AuthService> _logger;

        public AuthService(ApplicationDbContext context, ILogger<AuthService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<User?> AuthenticateAsync(string username, string password)
        {
            try
            {
                var user = await _context.Users
                    .FirstOrDefaultAsync(u => u.Username == username);

                if (user == null)
                {
                    return null;
                }

                // Verify password
                bool passwordValid = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);

                if (!passwordValid)
                {
                    return null;
                }

                // Update last login time with local time
                user.LastLogin = DateTime.Now; // Using local time instead of UTC
                await _context.SaveChangesAsync();

                return user;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during authentication");
                return null;
            }
        }

        public bool IsAdmin(User user)
        {
            return user.Role == "Admin";
        }

        public bool IsUser(User user)
        {
            return user.Role == "User";
        }
    }
}