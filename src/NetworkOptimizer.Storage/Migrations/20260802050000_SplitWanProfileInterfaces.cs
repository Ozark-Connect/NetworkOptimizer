using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SplitWanProfileInterfaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One name could not serve both jobs: throughput is keyed on the physical port because
            // VLAN sub-interface counters double, while SQM and PPPoE detection need the logical
            // uplink. The existing value was the data path, so it keeps that meaning.
            migrationBuilder.RenameColumn(
                name: "Interface", table: "WanProfiles", newName: "DataPathInterface");

            migrationBuilder.AddColumn<string>(
                name: "CounterInterface", table: "WanProfiles", type: "TEXT", maxLength: 100, nullable: true);

            // Stored WAN rates are keyed on gateway MAC + interface, so caching one without the
            // other still leaves an offline site unable to read its own history.
            migrationBuilder.AddColumn<string>(
                name: "GatewayMac", table: "WanProfiles", type: "TEXT", maxLength: 50, nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CounterInterface", table: "WanProfiles");
            migrationBuilder.DropColumn(name: "GatewayMac", table: "WanProfiles");
            migrationBuilder.RenameColumn(
                name: "DataPathInterface", table: "WanProfiles", newName: "Interface");
        }
    }
}
