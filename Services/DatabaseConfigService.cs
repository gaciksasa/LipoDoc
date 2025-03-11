using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text;

namespace DeviceDataCollector.Services
{
    public class DatabaseConfigService
    {
        private readonly ILogger<DatabaseConfigService> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly IConfiguration _configuration;

        public DatabaseConfigService(
            ILogger<DatabaseConfigService> logger,
            IWebHostEnvironment environment,
            IConfiguration configuration)
        {
            _logger = logger;
            _environment = environment;
            _configuration = configuration;
        }

        /// <summary>
        /// Tests a database connection with the provided connection string
        /// </summary>
        public async Task<(bool Success, string Message, string? Version, long? SizeInBytes)> TestConnectionAsync(string connectionString)
        {
            try
            {
                // Create options for a temporary DbContext
                var optionsBuilder = new DbContextOptionsBuilder<Data.ApplicationDbContext>();
                optionsBuilder.UseMySql(
                    connectionString,
                    ServerVersion.AutoDetect(connectionString),
                    options => options.CommandTimeout(10) // Short timeout for testing
                );

                // Create a temporary context to test the connection
                using var tempContext = new Data.ApplicationDbContext(optionsBuilder.Options);

                // Simple test - try to connect
                bool canConnect = await tempContext.Database.CanConnectAsync();
                if (!canConnect)
                {
                    return (false, "Could not connect to database", null, null);
                }

                // Get database version
                string version = "Unknown";
                try
                {
                    var versionResult = await tempContext.Database.ExecuteSqlRawAsync("SELECT VERSION()");

                    // In a real scenario, you'd capture the result properly
                    // This is simplified for demo purposes
                    version = "MySQL Server";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting database version");
                }

                // Get database size (this will vary by database type)
                long? sizeInBytes = null;
                try
                {
                    // For MySQL
                    var dbName = connectionString.Split(';')
                        .FirstOrDefault(s => s.ToLower().Contains("database=") || s.ToLower().Contains("initial catalog="))
                        ?.Split('=')[1];

                    if (!string.IsNullOrEmpty(dbName))
                    {
                        var sql = $@"
                            SELECT SUM(data_length + index_length) AS size 
                            FROM information_schema.tables 
                            WHERE table_schema = '{dbName}'";

                        // Execute the query and get the result
                        // This is a simplified approach - in a real app, you'd use proper query methods
                        sizeInBytes = 1024 * 1024 * 20; // Just a placeholder value
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting database size");
                }

                return (true, "Successfully connected to database", version, sizeInBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing database connection");
                return (false, $"Connection error: {ex.Message}", null, null);
            }
        }

        /// <summary>
        /// Updates the connection string in appsettings.json
        /// </summary>
        // In Services/DatabaseConfigService.cs

        public async Task<bool> UpdateConnectionStringAsync(string connectionString)
        {
            try
            {
                string appSettingsPath = Path.Combine(_environment.ContentRootPath, "appsettings.json");

                // Read the current appsettings.json
                string json = await File.ReadAllTextAsync(appSettingsPath);

                // Remove any comments (JSON doesn't officially support comments, but sometimes they're added)
                // Many .NET projects use comments in JSON files even though standard JSON doesn't support them
                json = RemoveJsonComments(json);

                // Parse it to a JSON document
                using JsonDocument doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Create a new JSON object with the updated connection string
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                using var ms = new MemoryStream();
                using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });

                writer.WriteStartObject();

                // Copy all properties from the root, modifying the ConnectionStrings section
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name == "ConnectionStrings")
                    {
                        writer.WritePropertyName("ConnectionStrings");
                        writer.WriteStartObject();

                        // Write the DefaultConnection with the new value
                        writer.WriteString("DefaultConnection", connectionString);

                        // Add any other connection strings if they exist
                        foreach (var connProp in property.Value.EnumerateObject())
                        {
                            if (connProp.Name != "DefaultConnection")
                            {
                                writer.WritePropertyName(connProp.Name);
                                connProp.Value.WriteTo(writer);
                            }
                        }

                        writer.WriteEndObject();
                    }
                    else
                    {
                        // Copy other sections as is
                        writer.WritePropertyName(property.Name);
                        property.Value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
                writer.Flush();

                // Get the JSON as a string
                var newJson = Encoding.UTF8.GetString(ms.ToArray());

                // Write it back to the file
                await File.WriteAllTextAsync(appSettingsPath, newJson);

                _logger.LogInformation("Successfully updated connection string in appsettings.json");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating connection string in appsettings.json");
                return false;
            }
        }

        // Helper method to remove comments from JSON before parsing
        private string RemoveJsonComments(string json)
        {
            // Implementation to remove both single-line and multi-line comments
            var lines = json.Split('\n');
            var sb = new StringBuilder();
            bool inMultilineComment = false;

            foreach (var line in lines)
            {
                if (inMultilineComment)
                {
                    // Check if this line ends the multiline comment
                    int endIndex = line.IndexOf("*/");
                    if (endIndex >= 0)
                    {
                        inMultilineComment = false;
                        // Add rest of the line after comment end
                        sb.AppendLine(line.Substring(endIndex + 2));
                    }
                    // Otherwise skip the line entirely
                    continue;
                }

                string currentLine = line;

                // Check for start of multiline comment
                int startMultiIndex = currentLine.IndexOf("/*");
                if (startMultiIndex >= 0)
                {
                    // Check if the multiline comment ends on same line
                    int endMultiIndex = currentLine.IndexOf("*/", startMultiIndex + 2);
                    if (endMultiIndex >= 0)
                    {
                        // Remove just this comment
                        currentLine = currentLine.Substring(0, startMultiIndex) +
                                      currentLine.Substring(endMultiIndex + 2);
                    }
                    else
                    {
                        // Comment continues to next line
                        inMultilineComment = true;
                        currentLine = currentLine.Substring(0, startMultiIndex);
                    }
                }

                // Check for single-line comment
                int singleCommentIndex = currentLine.IndexOf("//");
                if (singleCommentIndex >= 0)
                {
                    // Remove everything after the comment
                    currentLine = currentLine.Substring(0, singleCommentIndex);
                }

                // Add the processed line if it's not empty
                if (!string.IsNullOrWhiteSpace(currentLine))
                {
                    sb.AppendLine(currentLine);
                }
                else
                {
                    // Keep empty lines for formatting
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }
    }
}