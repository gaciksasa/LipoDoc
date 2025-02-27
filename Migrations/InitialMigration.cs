using Microsoft.EntityFrameworkCore.Migrations;

namespace DeviceDataCollector.Migrations
{
    public partial class RenameDeviceDataToDonationsData : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename the table
            migrationBuilder.RenameTable(
                name: "DeviceData",
                newName: "DonationsData");

            // Reestablish the indexes on the renamed table
            migrationBuilder.CreateIndex(
                name: "IX_DonationsData_DeviceId",
                table: "DonationsData",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DonationsData_DonationIdBarcode",
                table: "DonationsData",
                column: "DonationIdBarcode");

            migrationBuilder.CreateIndex(
                name: "IX_DonationsData_Timestamp",
                table: "DonationsData",
                column: "Timestamp");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert the table rename
            migrationBuilder.RenameTable(
                name: "DonationsData",
                newName: "DeviceData");

            // Reestablish the indexes on the original table
            migrationBuilder.CreateIndex(
                name: "IX_DeviceData_DeviceId",
                table: "DeviceData",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceData_DonationIdBarcode",
                table: "DeviceData",
                column: "DonationIdBarcode");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceData_Timestamp",
                table: "DeviceData",
                column: "Timestamp");
        }
    }
}