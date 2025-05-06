using DeviceDataCollector.Data;
using DeviceDataCollector.Models;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace DeviceDataCollector.Services
{
    /// <summary>
    /// Helper service for exporting donations - used by both the AutoExportService 
    /// and the direct export functionality in the DonationsController
    /// </summary>
    public class DonationExportHelper
    {
        private readonly ILogger<DonationExportHelper> _logger;
        private readonly ApplicationDbContext _dbContext;

        public DonationExportHelper(
            ILogger<DonationExportHelper> logger,
            ApplicationDbContext dbContext)
        {
            _logger = logger;
            _dbContext = dbContext;
        }

        /// <summary>
        /// Export a single donation using active auto-export configurations
        /// </summary>
        public async Task ExportDonationAsync(DonationsData donation)
        {
            try
            {
                if (donation == null)
                {
                    _logger.LogWarning("Cannot export null donation");
                    return;
                }

                // Get all active auto-export configurations
                var configs = await _dbContext.ExportSettingsConfigs
                    .Where(c => c.AutoExportEnabled)
                    .OrderByDescending(c => c.IsDefault)
                    .ToListAsync();

                if (!configs.Any())
                {
                    _logger.LogInformation("No auto-export configurations found, skipping export");
                    return;
                }

                _logger.LogInformation($"Found {configs.Count} auto-export configurations");

                // Create a list with just this donation
                var donationsList = new List<DonationsData> { donation };

                foreach (var config in configs)
                {
                    await ExportDonations(donationsList, config);
                }

                _logger.LogInformation($"Donation {donation.Id} exported successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error exporting donation {donation?.Id}");
            }
        }

        /// <summary>
        /// Export multiple donations using a specific configuration
        /// </summary>
        public async Task ExportDonations(List<DonationsData> donations, ExportSettingsConfig config)
        {
            try
            {
                if (donations == null || !donations.Any())
                {
                    return;
                }

                _logger.LogInformation($"Starting export of {donations.Count} donations using configuration '{config.Name}'");

                // Get the columns to export
                var selectedColumns = new List<string>();
                if (!string.IsNullOrEmpty(config.SelectedColumnsJson))
                {
                    try
                    {
                        selectedColumns = System.Text.Json.JsonSerializer.Deserialize<List<string>>(config.SelectedColumnsJson) ?? new List<string>();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error deserializing selected columns for config '{config.Name}'");
                        return;
                    }
                }

                if (!selectedColumns.Any())
                {
                    _logger.LogWarning($"No columns selected for export in configuration '{config.Name}'");
                    return;
                }

                // Get the column order
                var columnOrder = new List<string>();
                if (!string.IsNullOrEmpty(config.ColumnOrderJson))
                {
                    try
                    {
                        columnOrder = System.Text.Json.JsonSerializer.Deserialize<List<string>>(config.ColumnOrderJson) ?? new List<string>();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error deserializing column order for config '{config.Name}'");
                        columnOrder = selectedColumns.ToList(); // Fallback to selected columns
                    }
                }
                else
                {
                    // If no order specified, use the selected columns order
                    columnOrder = selectedColumns.ToList();
                }

                // Determine which export mode to use
                string exportMode = config.AutoExportMode ?? "single_file";
                string fileName = config.CustomFileName ?? "Donations_Export";

                switch (exportMode)
                {
                    case "single_file":
                        await ExportToSingleFile(donations, config, columnOrder);
                        break;
                    case "daily_file":
                        await ExportToDailyFile(donations, config, columnOrder);
                        break;
                    case "individual_files":
                        await ExportToIndividualFiles(donations, config, columnOrder);
                        break;
                    default:
                        _logger.LogWarning($"Unknown export mode '{exportMode}' for config '{config.Name}', defaulting to single file");
                        await ExportToSingleFile(donations, config, columnOrder);
                        break;
                }

                // Update LastUsedAt timestamp
                config.LastUsedAt = DateTime.Now;
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation($"Export completed for configuration '{config.Name}'");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error exporting donations with configuration '{config.Name}'");
            }
        }

        // Implement the specific export mode methods
        private async Task ExportToSingleFile(List<DonationsData> donations, ExportSettingsConfig config, List<string> columnOrder)
        {
            try
            {
                // Build the export path
                string exportFolder = GetExportFolder(config);
                string fileName = $"{config.CustomFileName ?? "Donations_Export"}.csv";
                string fullPath = Path.Combine(exportFolder, fileName);

                // Check if we need to create headers (if file doesn't exist)
                bool includeHeaders = config.IncludeHeaders && !File.Exists(fullPath);

                // Create StringBuilder for appending mode
                StringBuilder csv = new StringBuilder();

                // Include headers if needed
                if (includeHeaders)
                {
                    csv.AppendLine(BuildHeaderRow(columnOrder, config));
                }

                // Process each donation
                foreach (var donation in donations)
                {
                    // Add donation data row
                    csv.AppendLine(BuildDataRow(donation, columnOrder, config));
                }

                // Ensure folder exists
                Directory.CreateDirectory(exportFolder);

                // Write to file (append mode)
                await File.AppendAllTextAsync(fullPath, csv.ToString());

                _logger.LogInformation($"Exported {donations.Count} donations to single file: {fullPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting to single file");
            }
        }

        private async Task ExportToDailyFile(List<DonationsData> donations, ExportSettingsConfig config, List<string> columnOrder)
        {
            try
            {
                // Build the export path with date
                string exportFolder = GetExportFolder(config);
                string dateStr = DateTime.Now.ToString("yyyy_MM_dd");
                string fileName = $"{config.CustomFileName ?? "Donations_Export"}_{dateStr}.csv";
                string fullPath = Path.Combine(exportFolder, fileName);

                // Check if we need to create headers (if file doesn't exist)
                bool includeHeaders = config.IncludeHeaders && !File.Exists(fullPath);

                // Create StringBuilder for appending mode
                StringBuilder csv = new StringBuilder();

                // Include headers if needed
                if (includeHeaders)
                {
                    csv.AppendLine(BuildHeaderRow(columnOrder, config));
                }

                // Process each donation
                foreach (var donation in donations)
                {
                    // Add donation data row
                    csv.AppendLine(BuildDataRow(donation, columnOrder, config));
                }

                // Ensure folder exists
                Directory.CreateDirectory(exportFolder);

                // Write to file (append mode)
                await File.AppendAllTextAsync(fullPath, csv.ToString());

                _logger.LogInformation($"Exported {donations.Count} donations to daily file: {fullPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting to daily file");
            }
        }

        private async Task ExportToIndividualFiles(List<DonationsData> donations, ExportSettingsConfig config, List<string> columnOrder)
        {
            try
            {
                // Build the export path
                string exportFolder = GetExportFolder(config);

                // Ensure folder exists
                Directory.CreateDirectory(exportFolder);

                // Process each donation individually
                foreach (var donation in donations)
                {
                    // Create a unique filename based on donation ID
                    string identifier = !string.IsNullOrEmpty(donation.DonationIdBarcode)
                        ? donation.DonationIdBarcode.Replace("/", "_").Replace("\\", "_")
                        : donation.Id.ToString();

                    string fileName = $"{config.CustomFileName ?? "Donations_Export"}_{identifier}.csv";
                    string fullPath = Path.Combine(exportFolder, fileName);

                    // Create a new file for this donation
                    StringBuilder csv = new StringBuilder();

                    // Include headers if needed
                    if (config.IncludeHeaders)
                    {
                        csv.AppendLine(BuildHeaderRow(columnOrder, config));
                    }

                    // Add donation data
                    csv.AppendLine(BuildDataRow(donation, columnOrder, config));

                    // Write to file (create new)
                    await File.WriteAllTextAsync(fullPath, csv.ToString());

                    _logger.LogInformation($"Exported donation {donation.Id} to individual file: {fullPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting to individual files");
            }
        }

        // Helper methods
        private string GetExportFolder(ExportSettingsConfig config)
        {
            // Get base folder from configuration, or use a default
            string baseFolder = config.ExportFolderPath;

            if (string.IsNullOrEmpty(baseFolder))
            {
                // If no folder specified, use Documents/DonationsExport
                baseFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "DonationsExport"
                );
            }
            else
            {
                // Check if path is relative or absolute
                if (!Path.IsPathRooted(baseFolder))
                {
                    // Relative path - combine with MyDocuments
                    baseFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        baseFolder
                    );
                }
            }

            return baseFolder;
        }

        private string BuildHeaderRow(List<string> columnOrder, ExportSettingsConfig config)
        {
            var headerParts = new List<string>();
            string delimiter = GetDelimiter(config);

            foreach (var column in columnOrder)
            {
                if (column.StartsWith("empty_"))
                {
                    // Empty column - use a default name
                    int emptyIndex = int.Parse(column.Substring(6));
                    headerParts.Add($"\"Empty {emptyIndex + 1}\"");
                }
                else
                {
                    // Regular data column
                    headerParts.Add($"\"{GetColumnDisplayName(column)}\"");
                }
            }

            return string.Join(delimiter, headerParts);
        }

        private string BuildDataRow(DonationsData donation, List<string> columnOrder, ExportSettingsConfig config)
        {
            var row = new List<string>();
            string delimiter = GetDelimiter(config);

            foreach (var column in columnOrder)
            {
                if (column.StartsWith("empty_"))
                {
                    // Empty column - add an empty string
                    row.Add("\"\"");
                }
                else
                {
                    // Regular data column - get the actual value
                    string value = GetPropertyValue(donation, column, config.DateFormat, config.TimeFormat);
                    // Quote the value and escape any quotes inside it
                    row.Add($"\"{value.Replace("\"", "\"\"")}\"");
                }
            }

            return string.Join(delimiter, row);
        }

        private string GetDelimiter(ExportSettingsConfig config)
        {
            return config.Delimiter switch
            {
                "," => ",",
                ";" => ";",
                "\t" => "\t",
                "|" => "|",
                "custom" => !string.IsNullOrEmpty(config.CustomSeparator) ? config.CustomSeparator : ",",
                _ => ","
            };
        }

        private string GetColumnDisplayName(string columnId)
        {
            return columnId switch
            {
                "DonationIdBarcode" => "Donation ID",
                "DeviceId" => "Device ID",
                "Timestamp" => "Timestamp",
                "LipemicValue" => "Lipemic Value",
                "LipemicGroup" => "Lipemic Group",
                "LipemicStatus" => "Lipemic Status",
                "RefCode" => "Reference Code",
                "OperatorIdBarcode" => "Operator ID",
                "LotNumber" => "Lot Number",
                "MessageType" => "Message Type",
                "IPAddress" => "IP Address",
                "Port" => "Port",
                _ => columnId
            };
        }

        private string GetPropertyValue(DonationsData donation, string propertyName, string dateFormat, string timeFormat)
        {
            try
            {
                return propertyName switch
                {
                    "DonationIdBarcode" => donation.DonationIdBarcode ?? string.Empty,
                    "DeviceId" => donation.DeviceId ?? string.Empty,
                    "Timestamp" => donation.Timestamp.ToString($"{dateFormat} {timeFormat}"),
                    "LipemicValue" => donation.LipemicValue?.ToString() ?? string.Empty,
                    "LipemicGroup" => donation.LipemicGroup ?? string.Empty,
                    "LipemicStatus" => donation.LipemicStatus ?? string.Empty,
                    "RefCode" => donation.RefCode ?? string.Empty,
                    "OperatorIdBarcode" => donation.OperatorIdBarcode ?? string.Empty,
                    "LotNumber" => donation.LotNumber ?? string.Empty,
                    "MessageType" => donation.MessageType ?? string.Empty,
                    "IPAddress" => donation.IPAddress ?? string.Empty,
                    "Port" => donation.Port.ToString(),
                    _ => string.Empty
                };
            }
            catch
            {
                // If there's any error in conversion, return empty string
                return string.Empty;
            }
        }
    }
}