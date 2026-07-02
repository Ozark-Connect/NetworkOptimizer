using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelOutcomeMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApChannelChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ApMac = table.Column<string>(type: "TEXT", maxLength: 17, nullable: false),
                    Band = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    PreviousChannel = table.Column<int>(type: "INTEGER", nullable: true),
                    PreviousWidthMhz = table.Column<int>(type: "INTEGER", nullable: true),
                    NewChannel = table.Column<int>(type: "INTEGER", nullable: false),
                    NewWidthMhz = table.Column<int>(type: "INTEGER", nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApChannelChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApChannelOutcomes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ApMac = table.Column<string>(type: "TEXT", maxLength: 17, nullable: false),
                    Band = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    WidthMhz = table.Column<int>(type: "INTEGER", nullable: false),
                    BucketDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UtilizationSum = table.Column<double>(type: "REAL", nullable: false),
                    InterferenceSum = table.Column<double>(type: "REAL", nullable: false),
                    TxRetrySum = table.Column<double>(type: "REAL", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSampleUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApChannelOutcomes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApChannelChanges_ApMac_Band_ChangedAtUtc",
                table: "ApChannelChanges",
                columns: new[] { "ApMac", "Band", "ChangedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApChannelChanges_ChangedAtUtc",
                table: "ApChannelChanges",
                column: "ChangedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ApChannelOutcomes_ApMac_Band_Channel_WidthMhz_BucketDate",
                table: "ApChannelOutcomes",
                columns: new[] { "ApMac", "Band", "Channel", "WidthMhz", "BucketDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApChannelOutcomes_BucketDate",
                table: "ApChannelOutcomes",
                column: "BucketDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApChannelChanges");

            migrationBuilder.DropTable(
                name: "ApChannelOutcomes");
        }
    }
}
