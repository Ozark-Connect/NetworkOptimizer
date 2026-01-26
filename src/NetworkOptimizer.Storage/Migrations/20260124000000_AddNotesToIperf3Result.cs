using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddNotesToIperf3Result : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Iperf3Results",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Iperf3Results");
        }
    }
}
