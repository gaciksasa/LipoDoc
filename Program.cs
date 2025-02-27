using DeviceDataCollector.Data;
using DeviceDataCollector.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.CodeAnalysis.Scripting;
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

// Add BCrypt
builder.Services.AddScoped<BCrypt.Net.BCrypt>();

var app = builder.Build();

// Try to initialize the database, but continue even if it fails
try
{
    using (var scope = app.Services.CreateScope())
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        logger.LogInformation("Checking database existence and applying migrations if needed...");

        // This will create the database if it doesn't exist and apply any pending migrations
        await dbContext.Database.MigrateAsync();

        logger.LogInformation("Database check complete - database is ready");
    }
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "An error occurred while initializing the database. Application will continue, but database features may not work.");
    // Continue execution even if database initialization fails
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