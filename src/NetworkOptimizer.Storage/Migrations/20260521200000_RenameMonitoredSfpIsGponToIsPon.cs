using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Rename MonitoredSfps.IsGpon → IsPon. The column now indicates "any Passive Optical
    /// Network module" so XGS-PON, XG-PON, EPON, etc. all share the dashboard treatment.
    /// The specific PON variant is still recoverable from SfpPart / SfpCompliance.
    /// </summary>
    public partial class RenameMonitoredSfpIsGponToIsPon : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsGpon",
                table: "MonitoredSfps",
                newName: "IsPon");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsPon",
                table: "MonitoredSfps",
                newName: "IsGpon");
        }
    }
}
