using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Which WAN a site treats as its primary, read from the persisted profiles rather than live.
/// <para>
/// Two things need this and need it to agree: routing an agent's probes, and deciding who owns a
/// monitoring target that carries no WAN stamp. An unstamped row is a PRIMARY-WAN measurement
/// everywhere it is read, so the answer here is what keeps a secondary WAN's discovery from
/// writing over the primary's targets.
/// </para>
/// </summary>
public static class PrimaryWanResolver
{
    /// <summary>
    /// The site's primary WAN key, normalized, or null when the gateway has never been polled.
    /// Reads the persisted profiles so a caller running while the console is unreachable still
    /// gets the site's real answer instead of a guess.
    /// </summary>
    /// <param name="db">The site's database.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<string?> ResolveKeyAsync(NetworkOptimizerDbContext db, CancellationToken ct = default)
    {
        var group = (await db.WanProfiles.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IsPrimary == true, ct))?.WanNetworkgroup;
        return string.IsNullOrEmpty(group) ? null : GatewayWanHelper.WanInterfaceKeyFromKey(group);
    }
}
