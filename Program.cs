using DeviceDataCollector.Data;
using DeviceDataCollector.Middleware;
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
builder.Services.AddSingleton<DeviceStatusCleanupService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DeviceStatusCleanupService>());
builder.Services.AddScoped<DatabaseStatusService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DeviceMessageParser>();
builder.Services.AddScoped<BCrypt.Net.BCrypt>();
builder.Services.AddSingleton<IViewContextAccessor, ViewContextAccessor>();
builder.Services.AddScoped<NetworkUtilityService>();
builder.Services.AddScoped<DatabaseConfigService>();
builder.Services.AddScoped<DatabaseBackupService>(provider =>
    new DatabaseBackupService(
        provider.GetRequiredService<ILogger<DatabaseBackupService>>(),
        provider.GetRequiredService<IConfiguration>(),
        provider.GetRequiredService<IWebHostEnvironment>()));
builder.Services.AddSingleton<ScheduledBackupService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ScheduledBackupService>());
builder.Services.AddSingleton<ApplicationLifetimeService>();

var app = builder.Build();

// Initialize database 
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
            // This will create the database if it doesn't exist
            await dbContext.Database.EnsureCreatedAsync();
            logger.LogInformation("Database created successfully.");
        }

        // Explicitly check if CurrentDeviceStatuses table exists by attempting a query
        bool currentDeviceStatusTableExists = false;
        try
        {
            // Try to query the table (this will throw an exception if it doesn't exist)
            await dbContext.CurrentDeviceStatuses.FirstOrDefaultAsync();
            currentDeviceStatusTableExists = true;
        }
        catch (Exception)
        {
            logger.LogWarning("CurrentDeviceStatuses table does not exist. It will be created.");
        }

        if (!currentDeviceStatusTableExists)
        {
            try
            {
                // Create the table manually using SQL
                var createTableSql = @"
                CREATE TABLE IF NOT EXISTS `CurrentDeviceStatuses` (
                    `DeviceId` varchar(255) NOT NULL,
                    `Timestamp` datetime(6) NOT NULL,
                    `Status` int NOT NULL,
                    `AvailableData` int NOT NULL,
                    `IPAddress` longtext NULL,
                    `Port` int NOT NULL,
                    `CheckSum` longtext NULL,
                    `StatusUpdateCount` int NOT NULL DEFAULT 0,
                    PRIMARY KEY (`DeviceId`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

                await dbContext.Database.ExecuteSqlRawAsync(createTableSql);
                logger.LogInformation("CurrentDeviceStatuses table created successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error creating CurrentDeviceStatuses table.");
            }
        }

        // Apply any pending migrations for other tables
        try
        {
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Error applying migrations.");
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
app.UseAuthenticationRedirect();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();