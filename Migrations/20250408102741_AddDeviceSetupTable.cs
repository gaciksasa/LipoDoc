using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceDataCollector.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceSetupTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeviceTimestamp",
                table: "DeviceStatuses",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateTable(
                name: "DeviceSetups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    DeviceId = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Timestamp = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    SoftwareVersion = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    HardwareVersion = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ServerAddress = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DeviceIpAddress = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SubnetMask = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RemotePort = table.Column<int>(type: "int", nullable: false),
                    LocalPort = table.Column<int>(type: "int", nullable: false),
                    LipemicIndex1 = table.Column<int>(type: "int", nullable: false),
                    LipemicIndex2 = table.Column<int>(type: "int", nullable: false),
                    LipemicIndex3 = table.Column<int>(type: "int", nullable: false),
                    TransferMode = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    BarcodesMode = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    OperatorIdEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LotNumberEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    NetworkName = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    WifiMode = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SecurityType = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    WifiPassword = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RawResponse = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProfilesJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BarcodesJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceSetups", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CreatedAt", "PasswordHash" },
                values: new object[] { new DateTime(2025, 4, 8, 12, 27, 40, 245, DateTimeKind.Local).AddTicks(4452), "$2a$11$gux0zdUuF6J7Q7hF8FIthONzoEbvbTwoXkC1vLXuGI4.AlXbzdZgS" });

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "CreatedAt", "PasswordHash" },
                values: new object[] { new DateTime(2025, 4, 8, 12, 27, 40, 442, DateTimeKind.Local).AddTicks(3473), "$2a$11$k7QtPpV8aRj/6VUTl/QeJumrInW47IFbTen17HKGv5l65Cn6H.0gW" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceSetups");

            migrationBuilder.DropColumn(
                name: "DeviceTimestamp",
                table: "DeviceStatuses");

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CreatedAt", "PasswordHash" },
                values: new object[] { new DateTime(2025, 3, 24, 9, 51, 20, 387, DateTimeKind.Local).AddTicks(1694), "$2a$11$zZDnOvjueZ3zo6UEqnrokutSVnf7drlLc8EZ5fxY.bfRYtbTuRGVm" });

            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "CreatedAt", "PasswordHash" },
                values: new object[] { new DateTime(2025, 3, 24, 9, 51, 20, 554, DateTimeKind.Local).AddTicks(8617), "$2a$11$pWiwWz3JRorFhXBENv294OUzM6SX4JA1sCvLzBPzzskta43dvg.ly" });
        }
    }
}
