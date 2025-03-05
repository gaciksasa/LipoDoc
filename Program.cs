using DeviceDataCollector.Data;
using DeviceDataCollector.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Add authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
    });

// Add authorization policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAdminRole", policy => policy.RequireRole("Admin"));
    options.AddPolicy("RequireUserRole", policy => policy.RequireRole("User", "Admin"));
});

// Add database context
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        ServerVersion.AutoDetect(builder.Configuration.GetConnectionString("DefaultConnection"))
    )
);

// Register services
builder.Services.AddSingleton<TCPServerService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TCPServerService>());
builder.Services.AddSingleton<DeviceStatusMonitorService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DeviceStatusMonitorService>());
builder.Services.AddScoped<DatabaseStatusService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DeviceMessageParser>();
builder.Services.AddScoped<DeviceDataRetrievalService>();
// Removed DevicePingService registration

// Add BCrypt
builder.Services.AddScoped<BCrypt.Net.BCrypt>();

var app = builder.Build();

// Initialize database - improved to avoid recreating existing tables
try
{
    using (var scope = app.Services.CreateScope())
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        logger.LogInformation("Checking database connection and schema...");

        // Check if database exists and can connect
        bool canConnect = await dbContext.Database.CanConnectAsync();
        if (!canConnect)
        {
            logger.LogWarning("Database connection failed. Will attempt to create database.");
            // This will create the database if it doesn't exist but won't recreate tables
            await dbContext.Database.EnsureCreatedAsync();
        }
        else
        {
            // Check if any pending migrations need to be applied
            if ((await dbContext.Database.GetPendingMigrationsAsync()).Any())
            {
                logger.LogInformation("Applying pending migrations...");
                await dbContext.Database.MigrateAsync();
                logger.LogInformation("Migrations applied successfully.");
            }
            else
            {
                logger.LogInformation("Database is up to date. No migrations needed.");
            }
        }

        // Verify table existence by performing a non-destructive query
        try
        {
            // Just check if any users exist - won't cause schema changes
            var userCount = await dbContext.Users.CountAsync();
            logger.LogInformation($"Database contains {userCount} user records.");
        }
        catch (Exception tableEx)
        {
            logger.LogError(tableEx, "Error accessing database tables. Schema may be outdated or incomplete.");
        }

        logger.LogInformation("Database initialization completed");
    }
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "An error occurred while initializing the database. Application will continue, but database features may not work.");
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();