using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Show Network Optimizer's own changes now defaults to on. The 2.11 previews shipped it off as a
    /// default nobody chose, so existing sites are turned on once.
    /// </summary>
    public partial class ShowOwnChangesDefaultOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE ChangeControlSettings SET ShowOwnChanges = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
