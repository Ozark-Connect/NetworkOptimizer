using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    public partial class ReplaceSfpIsPonWithCategory : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "MonitoredSfps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE MonitoredSfps SET Category = 1 WHERE IsPon = 1;");

            migrationBuilder.DropColumn(
                name: "IsPon",
                table: "MonitoredSfps");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPon",
                table: "MonitoredSfps",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE MonitoredSfps SET IsPon = 1 WHERE Category = 1;");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "MonitoredSfps");
        }
    }
}
