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
builder.Services.AddScoped<DatabaseStatusService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DeviceMessageParser>();

// Add BCrypt
builder.Services.AddScoped<BCrypt.Net.BCrypt>();

var app = builder.Build();

// Initialize database
try
{
    using (var scope = app.Services.CreateScope())
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        logger.LogInformation("Checking database existence and applying migrations if needed...");

        // Ensure database exists
        dbContext.Database.EnsureCreated();

        // Apply any pending migrations
        dbContext.Database.Migrate();

        // Verify that the Devices table exists
        bool devicesTableExists = false;
        try
        {
            // Try to access the Devices table to verify it exists
            devicesTableExists = dbContext.Devices.Any();
            logger.LogInformation("Devices table exists and is accessible");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error accessing Devices table. It may not exist.");
        }

        // If the table doesn't exist, try creating it manually
        if (!devicesTableExists)
        {
            logger.LogWarning("Devices table not found. Attempting to create it...");
            try
            {
                // Force the creation of all tables again
                dbContext.Database.EnsureDeleted();
                dbContext.Database.EnsureCreated();
                logger.LogInformation("Database recreated with all tables");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to recreate database");
            }
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