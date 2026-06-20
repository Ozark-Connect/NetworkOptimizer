using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    public partial class AddCustomOidConfigurations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomOidConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceMac = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Oid = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FieldName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ValueType = table.Column<int>(type: "INTEGER", nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomOidConfigurations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomOidConfigurations_DeviceMac",
                table: "CustomOidConfigurations",
                column: "DeviceMac");

            migrationBuilder.CreateIndex(
                name: "IX_CustomOidConfigurations_DeviceMac_Oid",
                table: "CustomOidConfigurations",
                columns: new[] { "DeviceMac", "Oid" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CustomOidConfigurations");
        }
    }
}
