using Microsoft.EntityFrameworkCore.Migrations;

namespace DeviceDataCollector.Migrations
{
    public partial class AddExportedFieldToDonationsOnly : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Keep ONLY this line that adds the Exported column
            migrationBuilder.AddColumn<bool>(
                name: "Exported",
                table: "DonationsData",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // Remove any other operations like creating tables
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keep ONLY this line that drops the Exported column
            migrationBuilder.DropColumn(
                name: "Exported",
                table: "DonationsData");

            // Remove any other operations
        }
    }
}