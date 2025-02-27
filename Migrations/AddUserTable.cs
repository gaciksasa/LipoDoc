using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceDataCollector.Migrations
{
    public partial class AddUserTable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Username = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PasswordHash = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Role = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FullName = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Email = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastLogin = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            // Add seed data for admin and regular user
            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "Username", "PasswordHash", "Role", "FullName", "Email", "CreatedAt" },
                values: new object[] {
                    1,
                    "admin",
                    BCrypt.Net.BCrypt.HashPassword("admin123"),
                    "Admin",
                    "Administrator",
                    "admin@blooddonation.org",
                    DateTime.UtcNow
                });

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "Username", "PasswordHash", "Role", "FullName", "Email", "CreatedAt" },
                values: new object[] {
                    2,
                    "user",
                    BCrypt.Net.BCrypt.HashPassword("user123"),
                    "User",
                    "Regular User",
                    "user@blooddonation.org",
                    DateTime.UtcNow
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}