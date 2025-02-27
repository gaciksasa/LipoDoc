﻿// <auto-generated />
using System;
using DeviceDataCollector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace DeviceDataCollector.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20250227071335_AddUserTable")]
    partial class AddUserTable
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "8.0.13")
                .HasAnnotation("Relational:MaxIdentifierLength", 64);

            MySqlModelBuilderExtensions.AutoIncrementColumns(modelBuilder);

            modelBuilder.Entity("DeviceData", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    MySqlPropertyBuilderExtensions.UseMySqlIdentityColumn(b.Property<int>("Id"));

                    b.Property<string>("DataPayload")
                        .IsRequired()
                        .HasColumnType("longtext");

                    b.Property<string>("DeviceId")
                        .IsRequired()
                        .HasColumnType("longtext");

                    b.Property<string>("IPAddress")
                        .IsRequired()
                        .HasColumnType("longtext");

                    b.Property<int>("Port")
                        .HasColumnType("int");

                    b.Property<DateTime>("Timestamp")
                        .HasColumnType("datetime(6)");

                    b.HasKey("Id");

                    b.ToTable("DeviceData");
                });

            modelBuilder.Entity("DeviceDataCollector.Models.User", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    MySqlPropertyBuilderExtensions.UseMySqlIdentityColumn(b.Property<int>("Id"));

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("datetime(6)");

                    b.Property<string>("Email")
                        .HasColumnType("longtext");

                    b.Property<string>("FullName")
                        .HasColumnType("longtext");

                    b.Property<DateTime?>("LastLogin")
                        .HasColumnType("datetime(6)");

                    b.Property<string>("PasswordHash")
                        .IsRequired()
                        .HasColumnType("longtext");

                    b.Property<string>("Role")
                        .IsRequired()
                        .HasColumnType("longtext");

                    b.Property<string>("Username")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("varchar(50)");

                    b.HasKey("Id");

                    b.ToTable("Users");

                    b.HasData(
                        new
                        {
                            Id = 1,
                            CreatedAt = new DateTime(2025, 2, 27, 7, 13, 34, 489, DateTimeKind.Utc).AddTicks(9550),
                            Email = "admin@blooddonation.org",
                            FullName = "Administrator",
                            PasswordHash = "$2a$11$ZthqO1.FD5gyPlQoiY3o0eBt324dlC8wwngc71L1SgAN.nAyPbNAm",
                            Role = "Admin",
                            Username = "admin"
                        },
                        new
                        {
                            Id = 2,
                            CreatedAt = new DateTime(2025, 2, 27, 7, 13, 34, 657, DateTimeKind.Utc).AddTicks(4559),
                            Email = "user@blooddonation.org",
                            FullName = "Regular User",
                            PasswordHash = "$2a$11$kaOtDuBw/3CYjnBB3YLF/uSLGNp04s8NYzau3hwhn8dCZPy098YKy",
                            Role = "User",
                            Username = "user"
                        });
                });
#pragma warning restore 612, 618
        }
    }
}
