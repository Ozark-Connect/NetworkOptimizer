using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Splits source/destination geo enrichment on ThreatEvents (preventing
    /// destination ASNs from appearing on source-IP groupings) and adds
    /// Category/Label/IsSystem to ThreatNoiseFilters so the audit report
    /// can surface Infrastructure and TrustedUser activity in separate
    /// categorized sub-tables.
    /// </summary>
    public partial class AddDestGeoAndFilterCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "ThreatNoiseFilters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                table: "ThreatNoiseFilters",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "ThreatNoiseFilters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DestAsn",
                table: "ThreatEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestAsnOrg",
                table: "ThreatEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestCity",
                table: "ThreatEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestCountryCode",
                table: "ThreatEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DestLatitude",
                table: "ThreatEvents",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DestLongitude",
                table: "ThreatEvents",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GeoEnriched",
                table: "ThreatEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Rows already through the old enrichment path are marked enriched.
            migrationBuilder.Sql(
                "UPDATE ThreatEvents SET GeoEnriched = 1 WHERE CountryCode IS NOT NULL;");

            // Pre-fix RFC1918 source rows carry the destination's geo in their source fields.
            // Null them and re-arm the backfill. SQLite has no CIDR functions, hence the prefixes.
            migrationBuilder.Sql(@"
                UPDATE ThreatEvents
                SET CountryCode = NULL,
                    City = NULL,
                    Asn = NULL,
                    AsnOrg = NULL,
                    Latitude = NULL,
                    Longitude = NULL,
                    GeoEnriched = 0
                WHERE SourceIp LIKE '10.%'
                   OR SourceIp LIKE '192.168.%'
                   OR SourceIp LIKE '127.%'
                   OR SourceIp LIKE '169.254.%'
                   OR SourceIp LIKE '172.16.%' OR SourceIp LIKE '172.17.%'
                   OR SourceIp LIKE '172.18.%' OR SourceIp LIKE '172.19.%'
                   OR SourceIp LIKE '172.20.%' OR SourceIp LIKE '172.21.%'
                   OR SourceIp LIKE '172.22.%' OR SourceIp LIKE '172.23.%'
                   OR SourceIp LIKE '172.24.%' OR SourceIp LIKE '172.25.%'
                   OR SourceIp LIKE '172.26.%' OR SourceIp LIKE '172.27.%'
                   OR SourceIp LIKE '172.28.%' OR SourceIp LIKE '172.29.%'
                   OR SourceIp LIKE '172.30.%' OR SourceIp LIKE '172.31.%';");

            migrationBuilder.CreateIndex(
                name: "IX_ThreatNoiseFilters_Category_Enabled",
                table: "ThreatNoiseFilters",
                columns: new[] { "Category", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_ThreatNoiseFilters_Category_SourceIp",
                table: "ThreatNoiseFilters",
                columns: new[] { "Category", "SourceIp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ThreatNoiseFilters_Category_Enabled",
                table: "ThreatNoiseFilters");

            migrationBuilder.DropIndex(
                name: "IX_ThreatNoiseFilters_Category_SourceIp",
                table: "ThreatNoiseFilters");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "ThreatNoiseFilters");

            migrationBuilder.DropColumn(
                name: "IsSystem",
                table: "ThreatNoiseFilters");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "ThreatNoiseFilters");

            migrationBuilder.DropColumn(
                name: "DestAsn",
                table: "ThreatEvents");

            migrationBuilder.DropColumn(
                name: "DestAsnOrg",
                table: "ThreatEvents");

            migrationBuilder.DropColumn(
                name: "DestCity",
                table: "ThreatEvents");

            migrationBuilder.DropColumn(
                name: "DestCountryCode",
                table: "ThreatEvents");

            migrationBuilder.DropColumn(
                name: "DestLatitude",
                table: "ThreatEvents");

            migrationBuilder.DropColumn(
                name: "DestLongitude",
                table: "ThreatEvents");

            migrationBuilder.DropColumn(
                name: "GeoEnriched",
                table: "ThreatEvents");
        }
    }
}
