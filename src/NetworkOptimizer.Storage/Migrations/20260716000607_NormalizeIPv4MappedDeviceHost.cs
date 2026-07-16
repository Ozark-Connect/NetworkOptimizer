using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeIPv4MappedDeviceHost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Speed test results captured before the client-IP normalization stored an IPv4
            // client that arrived on a dual-stack socket as the IPv4-mapped IPv6 form
            // "::ffff:a.b.c.d". Collapse those DeviceHost values to plain IPv4 so LAN Speed Test,
            // Client Speed Test, and WAN Client Speed Test show real IPs and the LAN/WAN badge is
            // correct. The 7-character "::ffff:" prefix is stripped (substr from position 8).
            //
            // IPv6-safe: the LIKE only matches "::ffff:" followed by a dotted quad, which is the
            // IPAddress.ToString() rendering of a mapped IPv4 - a genuine IPv6 DeviceHost (a VPN
            // client's Tailscale/GUA/ULA address) never starts with "::ffff:" and contains dots,
            // so it is left untouched. ClientMac is deliberately not re-derived: correlating a
            // historical row by its point-in-time IP is unsound (DHCP/reservation churn), so old
            // rows stay uncorrelated rather than risk mis-attributing one client's history to
            // another. This only cleans the stored address.
            migrationBuilder.Sql(
                "UPDATE Iperf3Results " +
                "SET DeviceHost = substr(DeviceHost, 8) " +
                "WHERE DeviceHost LIKE '::ffff:%.%.%.%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: once collapsed to plain IPv4 there is no marker recording which
            // rows were originally mapped, so the "::ffff:" prefix cannot be restored. No-op.
        }
    }
}
