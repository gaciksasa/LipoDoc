using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DeviceDataCollector.Migrations
{
    public partial class RemoveDeviceStatusesTable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceStatuses");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-create the table if needed to rollback
            migrationBuilder.CreateTable(
                name: "DeviceStatuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    DeviceId = table.Column<string>(type: "longtext", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AvailableData = table.Column<int>(type: "int", nullable: false),
                    RawPayload = table.Column<string>(type: "longtext", nullable: true),
                    IPAddress = table.Column<string>(type: "longtext", nullable: true),
                    Port = table.Column<int>(type: "int", nullable: false),
                    CheckSum = table.Column<string>(type: "longtext", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceStatuses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceStatuses_DeviceId",
                table: "DeviceStatuses",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceStatuses_Timestamp",
                table: "DeviceStatuses",
                column: "Timestamp");
        }
    }
}