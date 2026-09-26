using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <inheritdoc />
public partial class AddHealthCheckLastKnownDevice : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LastKnownHost",
            table: "HealthCheckDefinitions",
            type: "TEXT",
            maxLength: 255,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LastKnownDeviceName",
            table: "HealthCheckDefinitions",
            type: "TEXT",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "LastKnownDeviceType",
            table: "HealthCheckDefinitions",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "LastKnownHardwareType",
            table: "HealthCheckDefinitions",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "LastKnownAt",
            table: "HealthCheckDefinitions",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "LastKnownHost", table: "HealthCheckDefinitions");
        migrationBuilder.DropColumn(name: "LastKnownDeviceName", table: "HealthCheckDefinitions");
        migrationBuilder.DropColumn(name: "LastKnownDeviceType", table: "HealthCheckDefinitions");
        migrationBuilder.DropColumn(name: "LastKnownHardwareType", table: "HealthCheckDefinitions");
        migrationBuilder.DropColumn(name: "LastKnownAt", table: "HealthCheckDefinitions");
    }
}
