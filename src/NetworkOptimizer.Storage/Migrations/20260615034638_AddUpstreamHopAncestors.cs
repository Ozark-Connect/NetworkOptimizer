using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Adds AncestorHopIps to UpstreamDiscoveries: the monitored hops proven upstream of
    /// each hop on the discovery traces, so ISP Health can confirm one hop routes through
    /// another across divergent paths.
    ///
    /// The scaffolder also emitted UniFiSshSettings changes because the model snapshot was
    /// stale (it described the old shape with a Host column). Those columns already exist
    /// identically on every site - the original AddUniFiSshSettings migration created them -
    /// so applying that DDL would crash with duplicate/no-such-column. The snapshot is
    /// regenerated to the correct shape (design-time only, never runs on sites); this
    /// migration deliberately carries ONLY the AncestorHopIps column.
    /// </summary>
    public partial class AddUpstreamHopAncestors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AncestorHopIps",
                table: "UpstreamDiscoveries",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AncestorHopIps",
                table: "UpstreamDiscoveries");
        }
    }
}
