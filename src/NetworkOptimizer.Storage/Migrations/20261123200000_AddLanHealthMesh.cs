using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddLanHealthMesh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(name: "ProbeRttMs", table: "LanHealthLinkHours", type: "REAL", nullable: true);
            migrationBuilder.AddColumn<double>(name: "MeshTxRetries", table: "LanHealthLinkHours", type: "REAL", nullable: true);
            migrationBuilder.AddColumn<double>(name: "MeshTxAttempts", table: "LanHealthLinkHours", type: "REAL", nullable: true);
            migrationBuilder.AddColumn<double>(name: "MeshTxDropped", table: "LanHealthLinkHours", type: "REAL", nullable: true);
            migrationBuilder.AddColumn<int>(name: "MeshReassociations", table: "LanHealthLinkHours", type: "INTEGER", nullable: false, defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ProbeRttMs", table: "LanHealthLinkHours");
            migrationBuilder.DropColumn(name: "MeshTxRetries", table: "LanHealthLinkHours");
            migrationBuilder.DropColumn(name: "MeshTxAttempts", table: "LanHealthLinkHours");
            migrationBuilder.DropColumn(name: "MeshTxDropped", table: "LanHealthLinkHours");
            migrationBuilder.DropColumn(name: "MeshReassociations", table: "LanHealthLinkHours");
        }
    }
}