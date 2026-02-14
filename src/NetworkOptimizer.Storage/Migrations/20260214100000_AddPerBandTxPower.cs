using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddPerBandTxPower : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TxPower24Dbm",
                table: "PlannedAps",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TxPower5Dbm",
                table: "PlannedAps",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TxPower6Dbm",
                table: "PlannedAps",
                type: "INTEGER",
                nullable: true);

            // Copy existing single TX power value into all three band columns
            migrationBuilder.Sql(
                "UPDATE PlannedAps SET TxPower24Dbm = TxPowerDbm, TxPower5Dbm = TxPowerDbm, TxPower6Dbm = TxPowerDbm WHERE TxPowerDbm IS NOT NULL");

            // SQLite doesn't support DROP COLUMN before 3.35.0 - use table rebuild
            migrationBuilder.Sql(@"
                CREATE TABLE PlannedAps_new (
                    Id INTEGER NOT NULL CONSTRAINT PK_PlannedAps PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Model TEXT NOT NULL,
                    Latitude REAL NOT NULL,
                    Longitude REAL NOT NULL,
                    Floor INTEGER NOT NULL DEFAULT 1,
                    OrientationDeg INTEGER NOT NULL,
                    MountType TEXT NOT NULL DEFAULT 'ceiling',
                    TxPower24Dbm INTEGER NULL,
                    TxPower5Dbm INTEGER NULL,
                    TxPower6Dbm INTEGER NULL,
                    AntennaMode TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                INSERT INTO PlannedAps_new SELECT Id, Name, Model, Latitude, Longitude, Floor, OrientationDeg, MountType, TxPower24Dbm, TxPower5Dbm, TxPower6Dbm, AntennaMode, CreatedAt, UpdatedAt FROM PlannedAps;
                DROP TABLE PlannedAps;
                ALTER TABLE PlannedAps_new RENAME TO PlannedAps;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TxPowerDbm",
                table: "PlannedAps",
                type: "INTEGER",
                nullable: true);

            // Copy 5 GHz value back as the single TX power
            migrationBuilder.Sql(
                "UPDATE PlannedAps SET TxPowerDbm = TxPower5Dbm");

            // Use table rebuild to drop the per-band columns
            migrationBuilder.Sql(@"
                CREATE TABLE PlannedAps_new (
                    Id INTEGER NOT NULL CONSTRAINT PK_PlannedAps PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Model TEXT NOT NULL,
                    Latitude REAL NOT NULL,
                    Longitude REAL NOT NULL,
                    Floor INTEGER NOT NULL DEFAULT 1,
                    OrientationDeg INTEGER NOT NULL,
                    MountType TEXT NOT NULL DEFAULT 'ceiling',
                    TxPowerDbm INTEGER NULL,
                    AntennaMode TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                INSERT INTO PlannedAps_new SELECT Id, Name, Model, Latitude, Longitude, Floor, OrientationDeg, MountType, TxPowerDbm, AntennaMode, CreatedAt, UpdatedAt FROM PlannedAps;
                DROP TABLE PlannedAps;
                ALTER TABLE PlannedAps_new RENAME TO PlannedAps;
            ");
        }
    }
}
