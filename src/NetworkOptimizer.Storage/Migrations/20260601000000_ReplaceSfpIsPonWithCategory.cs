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
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE MonitoredSfps SET IsPon = CASE WHEN Category = 1 THEN 1 ELSE 0 END;");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "MonitoredSfps");
        }
    }
}
