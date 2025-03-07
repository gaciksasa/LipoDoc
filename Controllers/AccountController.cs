using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using DeviceDataCollector.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DeviceDataCollector.Controllers
{
    public class AccountController : Controller
    {
        private readonly AuthService _authService;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AccountController> _logger;

        public AccountController(
            AuthService authService,
            ApplicationDbContext context,
            ILogger<AccountController> logger)
        {
            _authService = authService;
            _context = context;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Login(string returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model, string returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;

            if (ModelState.IsValid)
            {
                var user = await _authService.AuthenticateAsync(model.Username, model.Password);

                if (user == null)
                {
                    ModelState.AddModelError(string.Empty, "Invalid login attempt.");
                    return View(model);
                }

                _logger.LogInformation($"User {user.Username} logged in.");

                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.Role, user.Role),
                    new Claim("FullName", user.FullName ?? string.Empty),
                    new Claim("UserId", user.Id.ToString())
                };

                var claimsIdentity = new ClaimsIdentity(
                    claims, CookieAuthenticationDefaults.AuthenticationScheme);

                var authProperties = new AuthenticationProperties
                {
                    IsPersistent = model.RememberMe,
                    RedirectUri = returnUrl
                };

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(claimsIdentity),
                    authProperties);

                if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                {
                    return Redirect(returnUrl);
                }
                else
                {
                    return RedirectToAction(nameof(HomeController.Index), "Home");
                }
            }

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            _logger.LogInformation("User logged out.");
            return RedirectToAction(nameof(HomeController.Index), "Home");
        }

        public IActionResult AccessDenied()
        {
            return View();
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Profile()
        {
            // Get the current user ID
            var userId = User.FindFirst("UserId")?.Value;
            if (string.IsNullOrEmpty(userId) || !int.TryParse(userId, out var id))
            {
                return RedirectToAction("Login");
            }

            // Get the user details
            var user = await _context.Users.FindAsync(id);
            if (user == null)
            {
                return NotFound();
            }

            // Create profile view model
            var model = new ProfileViewModel
            {
                Id = user.Id,
                Username = user.Username,
                FullName = user.FullName,
                Email = user.Email,
                Role = user.Role,
                CreatedAt = user.CreatedAt,
                LastLogin = user.LastLogin
            };

            return View(model);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Profile(ProfileViewModel model)
        {
            // Get the current user ID
            var userId = User.FindFirst("UserId")?.Value;
            if (string.IsNullOrEmpty(userId) || !int.TryParse(userId, out var id))
            {
                return RedirectToAction("Login");
            }

            // Make sure the user can only update their own profile
            if (id != model.Id)
            {
                return Forbid();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var user = await _context.Users.FindAsync(id);
                    if (user == null)
                    {
                        return NotFound();
                    }

                    // Update the allowed fields
                    user.FullName = model.FullName;
                    user.Email = model.Email;

                    // Only update password if provided
                    if (!string.IsNullOrEmpty(model.NewPassword))
                    {
                        // Verify current password first
                        bool passwordValid = BCrypt.Net.BCrypt.Verify(model.CurrentPassword, user.PasswordHash);
                        if (!passwordValid)
                        {
                            ModelState.AddModelError("CurrentPassword", "Current password is incorrect");
                            return View(model);
                        }

                        // Update password
                        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.NewPassword);
                    }

                    _context.Update(user);
                    await _context.SaveChangesAsync();
                    _logger.LogInformation($"User {user.Username} updated their profile");

                    TempData["SuccessMessage"] = "Your profile has been updated successfully.";
                    return RedirectToAction(nameof(Profile));
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    _logger.LogError(ex, $"Error updating profile for user {model.Username}");
                    ModelState.AddModelError(string.Empty, "An error occurred while updating your profile. Please try again.");
                }
            }

            return View(model);
        }
    }
}