using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using DeviceDataCollector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeviceDataCollector.Services
{
    public class DatabaseBackupService
    {
        private readonly ILogger<DatabaseBackupService> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _backupDirectory;

        public DatabaseBackupService(
            ILogger<DatabaseBackupService> logger,
            IConfiguration configuration,
            IWebHostEnvironment environment)
        {
            _logger = logger;
            _configuration = configuration;

            // Create a 'backups' directory within the application's content root
            _backupDirectory = Path.Combine(environment.ContentRootPath, "backups");

            // Ensure the backup directory exists
            if (!Directory.Exists(_backupDirectory))
            {
                Directory.CreateDirectory(_backupDirectory);
            }
        }

        public async Task<(bool Success, string FileName, string ErrorMessage)> CreateBackupAsync(string description = "")
        {
            try
            {
                // Parse the connection string to get database details
                var connectionString = _configuration.GetConnectionString("DefaultConnection");
                var connectionParams = ParseConnectionString(connectionString);

                if (connectionParams == null)
                {
                    return (false, null, "Invalid connection string");
                }

                // Generate a filename using the current timestamp
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string backupFileName = $"{connectionParams.Database}_{timestamp}.sql";
                string backupFilePath = Path.Combine(_backupDirectory, backupFileName);

                // Create backup using mysqldump command
                bool success = await ExecuteMySqlDumpAsync(
                    connectionParams.Server,
                    connectionParams.Port,
                    connectionParams.Database,
                    connectionParams.Username,
                    connectionParams.Password,
                    backupFilePath);

                if (!success)
                {
                    return (false, null, "Backup command failed");
                }

                // Create a compressed version of the backup
                string compressedFilePath = $"{backupFilePath}.gz";
                await CompressFileAsync(backupFilePath, compressedFilePath);

                // Delete the uncompressed version to save space
                File.Delete(backupFilePath);

                // Store backup metadata
                await SaveBackupMetadataAsync(
                    Path.GetFileName(compressedFilePath),
                    description,
                    connectionParams.Database,
                    new FileInfo(compressedFilePath).Length);

                _logger.LogInformation($"Database backup created successfully: {compressedFilePath}");

                return (true, Path.GetFileName(compressedFilePath), null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating database backup");
                return (false, null, ex.Message);
            }
        }

        public async Task<List<BackupInfo>> GetBackupListAsync()
        {
            var backups = new List<BackupInfo>();

            try
            {
                // Read metadata file if it exists
                string metadataPath = Path.Combine(_backupDirectory, "backup_metadata.json");

                if (File.Exists(metadataPath))
                {
                    string json = await File.ReadAllTextAsync(metadataPath);
                    backups = System.Text.Json.JsonSerializer.Deserialize<List<BackupInfo>>(json) ?? new List<BackupInfo>();
                }

                // Verify each backup file still exists
                backups = backups.Where(b => File.Exists(Path.Combine(_backupDirectory, b.FileName))).ToList();

                // Sort by creation date (newest first)
                backups = backups.OrderByDescending(b => b.CreatedAt).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting backup list");
            }

            return backups;
        }

        public async Task<bool> DeleteBackupAsync(string fileName)
        {
            try
            {
                string filePath = Path.Combine(_backupDirectory, fileName);

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogInformation($"Deleted backup file: {fileName}");

                    // Update metadata
                    var backups = await GetBackupListAsync();
                    var backupToRemove = backups.FirstOrDefault(b => b.FileName == fileName);

                    if (backupToRemove != null)
                    {
                        backups.Remove(backupToRemove);
                        await SaveBackupMetadataAsync(backups);
                    }

                    return true;
                }
                else
                {
                    _logger.LogWarning($"Backup file not found: {fileName}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting backup file: {fileName}");
                return false;
            }
        }

        public string GetBackupFilePath(string fileName)
        {
            return Path.Combine(_backupDirectory, fileName);
        }

        private async Task<bool> ExecuteMySqlDumpAsync(string server, int port, string database, string username, string password, string outputFile)
        {
            try
            {
                // First, attempt to check if mysqldump is available
                bool mysqldumpAvailable = false;
                try
                {
                    using var checkProcess = new Process();
                    checkProcess.StartInfo = new ProcessStartInfo
                    {
                        FileName = "mysqldump",
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    mysqldumpAvailable = checkProcess.Start();
                }
                catch
                {
                    _logger.LogWarning("mysqldump command not found, falling back to EF Core direct export");
                    mysqldumpAvailable = false;
                }

                if (mysqldumpAvailable)
                {
                    // If mysqldump is available, use it (original implementation)
                    using var process = new Process();
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "mysqldump",
                        Arguments = $"--host={server} --port={port} --user={username} --password={password} --databases {database} --add-drop-database --routines --events --triggers --single-transaction",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    _logger.LogInformation($"Starting mysqldump for database {database}");
                    process.Start();

                    // Read output and write to file
                    using (var fileStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                    using (var writer = new StreamWriter(fileStream))
                    {
                        await writer.WriteAsync(await process.StandardOutput.ReadToEndAsync());
                    }

                    // Check for errors
                    string error = await process.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrEmpty(error))
                    {
                        _logger.LogError($"mysqldump error: {error}");
                        return false;
                    }

                    await process.WaitForExitAsync();
                    return process.ExitCode == 0;
                }
                else
                {
                    // If mysqldump is not available, use EF Core to create a SQL script
                    _logger.LogInformation($"Creating EF Core SQL script for database {database}");

                    // Create a new DbContext with the connection string
                    var connectionString = $"server={server};port={port};database={database};user={username};password={password}";
                    var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
                    optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));

                    using var dbContext = new ApplicationDbContext(optionsBuilder.Options);

                    // Generate script header
                    using var fileStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
                    using var writer = new StreamWriter(fileStream);

                    await writer.WriteLineAsync($"-- Database backup for '{database}' generated on {DateTime.Now}");
                    await writer.WriteLineAsync("-- Generated by DeviceDataCollector Backup Service");
                    await writer.WriteLineAsync();
                    await writer.WriteLineAsync($"DROP DATABASE IF EXISTS `{database}`;");
                    await writer.WriteLineAsync($"CREATE DATABASE `{database}`;");
                    await writer.WriteLineAsync($"USE `{database}`;");
                    await writer.WriteLineAsync();

                    // Get all tables
                    var tableNames = new List<string>();
                    using (var command = dbContext.Database.GetDbConnection().CreateCommand())
                    {
                        command.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @schema";
                        var param = command.CreateParameter();
                        param.ParameterName = "@schema";
                        param.Value = database;
                        command.Parameters.Add(param);

                        dbContext.Database.OpenConnection();
                        using var result = await command.ExecuteReaderAsync();
                        while (await result.ReadAsync())
                        {
                            tableNames.Add(result.GetString(0));
                        }
                    }

                    foreach (var tableName in tableNames)
                    {
                        await writer.WriteLineAsync($"-- Table structure for table `{tableName}`");

                        // Get create table statement
                        string createTableSql = "";
                        using (var command = dbContext.Database.GetDbConnection().CreateCommand())
                        {
                            command.CommandText = $"SHOW CREATE TABLE `{tableName}`";

                            dbContext.Database.OpenConnection();
                            using var result = await command.ExecuteReaderAsync();
                            if (await result.ReadAsync())
                            {
                                createTableSql = result.GetString(1); // Second column has the CREATE TABLE statement
                            }
                        }

                        await writer.WriteLineAsync(createTableSql + ";");
                        await writer.WriteLineAsync();

                        // Get table data
                        await writer.WriteLineAsync($"-- Dumping data for table `{tableName}`");
                        await writer.WriteLineAsync($"LOCK TABLES `{tableName}` WRITE;");
                        await writer.WriteLineAsync($"/*!40000 ALTER TABLE `{tableName}` DISABLE KEYS */;");

                        // Get all rows
                        using (var command = dbContext.Database.GetDbConnection().CreateCommand())
                        {
                            command.CommandText = $"SELECT * FROM `{tableName}`";

                            dbContext.Database.OpenConnection();
                            using var result = await command.ExecuteReaderAsync();

                            if (result.HasRows)
                            {
                                // Get column info using a different method since GetColumnSchema is not available
                                int columnCount = result.FieldCount;
                                var columnNames = new List<string>();
                                var columnTypes = new List<Type>();

                                for (int i = 0; i < columnCount; i++)
                                {
                                    columnNames.Add(result.GetName(i));
                                    columnTypes.Add(result.GetFieldType(i));
                                }

                                // Batch inserts for efficiency
                                const int batchSize = 100;
                                int rowCount = 0;
                                bool hasMoreRows = await result.ReadAsync();

                                while (hasMoreRows)
                                {
                                    StringBuilder insertStatement = new StringBuilder();
                                    insertStatement.Append($"INSERT INTO `{tableName}` VALUES ");

                                    int batchCount = 0;
                                    while (hasMoreRows && batchCount < batchSize)
                                    {
                                        if (batchCount > 0)
                                        {
                                            insertStatement.Append(",");
                                        }

                                        insertStatement.Append("(");
                                        for (int i = 0; i < columnCount; i++)
                                        {
                                            if (i > 0) insertStatement.Append(",");

                                            if (result.IsDBNull(i))
                                            {
                                                insertStatement.Append("NULL");
                                            }
                                            else
                                            {
                                                var columnType = columnTypes[i];
                                                var value = result.GetValue(i);

                                                if (value is string || value is DateTime || value is Guid)
                                                {
                                                    string escapedValue = value.ToString().Replace("'", "''");
                                                    insertStatement.Append($"'{escapedValue}'");
                                                }
                                                else if (value is bool)
                                                {
                                                    insertStatement.Append((bool)value ? "1" : "0");
                                                }
                                                else
                                                {
                                                    insertStatement.Append(value);
                                                }
                                            }
                                        }
                                        insertStatement.Append(")");

                                        rowCount++;
                                        batchCount++;
                                        hasMoreRows = await result.ReadAsync();
                                    }

                                    if (batchCount > 0)
                                    {
                                        insertStatement.Append(";");
                                        await writer.WriteLineAsync(insertStatement.ToString());
                                    }
                                }

                                _logger.LogInformation($"Exported {rowCount} rows from table '{tableName}'");
                            }
                        }

                        await writer.WriteLineAsync($"/*!40000 ALTER TABLE `{tableName}` ENABLE KEYS */;");
                        await writer.WriteLineAsync("UNLOCK TABLES;");
                        await writer.WriteLineAsync();
                    }

                    await writer.WriteLineAsync("-- End of backup");

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating database backup");
                return false;
            }
        }

        private async Task CompressFileAsync(string sourceFilePath, string destinationFilePath)
        {
            using var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read);
            using var destinationStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write);
            using var gzipStream = new GZipStream(destinationStream, CompressionLevel.Optimal);

            await sourceStream.CopyToAsync(gzipStream);
        }

        private async Task SaveBackupMetadataAsync(string fileName, string description, string database, long fileSize)
        {
            var backups = await GetBackupListAsync();

            backups.Add(new BackupInfo
            {
                FileName = fileName,
                Description = description,
                Database = database,
                FileSize = fileSize,
                CreatedAt = DateTime.Now
            });

            await SaveBackupMetadataAsync(backups);
        }

        private async Task SaveBackupMetadataAsync(List<BackupInfo> backups)
        {
            string metadataPath = Path.Combine(_backupDirectory, "backup_metadata.json");
            string json = System.Text.Json.JsonSerializer.Serialize(backups, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(metadataPath, json);
        }

        private ConnectionParameters ParseConnectionString(string connectionString)
        {
            try
            {
                var parameters = new ConnectionParameters();
                var parts = connectionString.Split(';');

                foreach (var part in parts)
                {
                    if (string.IsNullOrEmpty(part)) continue;

                    var keyValue = part.Split('=');
                    if (keyValue.Length != 2) continue;

                    var key = keyValue[0].Trim().ToLowerInvariant();
                    var value = keyValue[1].Trim();

                    switch (key)
                    {
                        case "server":
                            parameters.Server = value;
                            break;
                        case "port":
                            if (int.TryParse(value, out int port))
                                parameters.Port = port;
                            break;
                        case "database":
                            parameters.Database = value;
                            break;
                        case "user":
                        case "uid":
                        case "username":
                            parameters.Username = value;
                            break;
                        case "password":
                        case "pwd":
                            parameters.Password = value;
                            break;
                    }
                }

                return parameters;
            }
            catch
            {
                return null;
            }
        }

        public async Task<(bool Success, string ErrorMessage)> RestoreBackupAsync(string fileName)
        {
            try
            {
                string filePath = Path.Combine(_backupDirectory, fileName);

                if (!File.Exists(filePath))
                {
                    return (false, $"Backup file not found: {fileName}");
                }

                _logger.LogInformation($"Starting restore of backup: {fileName}");

                // Create temporary decompressed file
                string tempSqlFilePath = Path.Combine(_backupDirectory, Path.GetFileNameWithoutExtension(fileName));

                // Decompress the .gz file
                using (var compressedFileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (var gzipStream = new GZipStream(compressedFileStream, CompressionMode.Decompress))
                using (var outputFileStream = new FileStream(tempSqlFilePath, FileMode.Create, FileAccess.Write))
                {
                    await gzipStream.CopyToAsync(outputFileStream);
                }

                _logger.LogInformation($"Successfully decompressed backup file to: {tempSqlFilePath}");

                // Parse connection string
                var connectionString = _configuration.GetConnectionString("DefaultConnection");
                var connectionParams = ParseConnectionString(connectionString);

                if (connectionParams == null)
                {
                    // Clean up temp file
                    File.Delete(tempSqlFilePath);
                    return (false, "Invalid connection string");
                }

                // Execute the SQL file using mysql command line
                bool success = await ExecuteMySqlRestoreAsync(
                    connectionParams.Server,
                    connectionParams.Port,
                    connectionParams.Username,
                    connectionParams.Password,
                    tempSqlFilePath);

                // Clean up temp file regardless of outcome
                File.Delete(tempSqlFilePath);

                if (!success)
                {
                    return (false, "Restore command failed. See logs for details.");
                }

                _logger.LogInformation($"Successfully restored backup: {fileName}");
                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error restoring backup: {fileName}");
                return (false, ex.Message);
            }
        }

        private async Task<bool> ExecuteMySqlRestoreAsync(string server, int port, string username, string password, string sqlFilePath)
        {
            try
            {
                // First try using mysql client
                bool mysqlAvailable = false;
                try
                {
                    using var checkProcess = new Process();
                    checkProcess.StartInfo = new ProcessStartInfo
                    {
                        FileName = "mysql",
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    mysqlAvailable = checkProcess.Start();
                }
                catch
                {
                    _logger.LogWarning("mysql command not found, falling back to EF Core execution");
                    mysqlAvailable = false;
                }

                if (mysqlAvailable)
                {
                    // Use mysql client to restore
                    using var process = new Process();
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "mysql",
                        Arguments = $"--host={server} --port={port} --user={username} --password={password}",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    _logger.LogInformation($"Starting mysql restore using command line client");
                    process.Start();

                    // Read the SQL file and send it to mysql via stdin
                    string sqlScript = await File.ReadAllTextAsync(sqlFilePath);
                    await process.StandardInput.WriteAsync(sqlScript);
                    process.StandardInput.Close();

                    // Check for errors
                    string error = await process.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrEmpty(error) && !error.Contains("Warning"))
                    {
                        _logger.LogError($"mysql restore error: {error}");
                        return false;
                    }

                    await process.WaitForExitAsync();
                    return process.ExitCode == 0;
                }
                else
                {
                    // Fallback to Entity Framework Core for execution
                    _logger.LogInformation($"Restoring backup using Entity Framework Core");

                    // Parse the SQL script
                    string sqlScript = await File.ReadAllTextAsync(sqlFilePath);

                    // Create a connection string without specifying the database
                    var connectionString = $"server={server};port={port};user={username};password={password}";
                    var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
                    optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));

                    using var dbContext = new ApplicationDbContext(optionsBuilder.Options);

                    // Split the script by semicolons to get individual statements
                    // This is a simplified approach and may not work for all cases
                    var statements = sqlScript.Split(';')
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s.Trim() + ";");

                    dbContext.Database.OpenConnection();

                    // Execute each statement
                    foreach (var stmt in statements)
                    {
                        // Skip comments and certain statements that EF Core can't handle directly
                        if (stmt.TrimStart().StartsWith("--") ||
                            stmt.Contains("DEFINER") ||
                            stmt.TrimStart().StartsWith("/*!"))
                        {
                            continue;
                        }

                        try
                        {
                            using var command = dbContext.Database.GetDbConnection().CreateCommand();
                            command.CommandText = stmt;
                            command.CommandTimeout = 300; // 5 minutes

                            await command.ExecuteNonQueryAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error executing SQL statement: {stmt.Substring(0, Math.Min(100, stmt.Length))}");
                            // Continue with next statement - some errors are expected
                        }
                    }

                    dbContext.Database.CloseConnection();

                    _logger.LogInformation("Completed EF Core restore");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing MySQL restore");
                return false;
            }
        }
    }

    public class ConnectionParameters
    {
        public string Server { get; set; } = "localhost";
        public int Port { get; set; } = 3306;
        public string Database { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public class BackupInfo
    {
        public string FileName { get; set; }
        public string Description { get; set; }
        public string Database { get; set; }
        public long FileSize { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}