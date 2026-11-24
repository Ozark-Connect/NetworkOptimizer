using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddLanHealthMeshEnds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(name: "MeshChildTxRetries", table: "LanHealthLinkHours", type: "REAL", nullable: true);
            migrationBuilder.AddColumn<double>(name: "MeshChildTxAttempts", table: "LanHealthLinkHours", type: "REAL", nullable: true);
            migrationBuilder.AddColumn<double>(name: "MeshChildTxDropped", table: "LanHealthLinkHours", type: "REAL", nullable: true);
            migrationBuilder.AddColumn<int>(name: "MeshWorstSignalDbm", table: "LanHealthLinkHours", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<string>(name: "MeshWorstSignalRadio", table: "LanHealthLinkHours", type: "TEXT", maxLength: 4, nullable: true);
            migrationBuilder.AddColumn<bool>(name: "MeshWorstSignalAtParent", table: "LanHealthLinkHours", type: "INTEGER", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "MeshChildTxRetries", table: "LanHealthLinkHours");
            migrationBuilder.DropColumn(name: "MeshChildTxAttempts", table: "LanHealthLinkHours");
            migrationBuilder.DropColumn(name: "MeshChildTxDropped", table: "LanHealthLinkHours");
            migrationBuilder.DropColumn(name: "MeshWorstSignalDbm", table: "LanHealthLinkHours");
            migrationBuilder.DropColumn(name: "MeshWorstSignalRadio", table: "LanHealthLinkHours");
            migrationBuilder.DropColumn(name: "MeshWorstSignalAtParent", table: "LanHealthLinkHours");
        }
    }
}