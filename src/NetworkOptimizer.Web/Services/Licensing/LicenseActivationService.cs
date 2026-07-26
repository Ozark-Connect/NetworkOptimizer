using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Licensing;

/// <summary>
/// Manages the license keys entered on this instance: activation against the
/// license server, refresh (shared with the phone-home loop), removal, and
/// per-site coverage assignment. Licensing rows are registry data, so all
/// access goes through the main-database factory.
/// </summary>
public class LicenseActivationService : ILicenseActivationService
{
    /// <summary>How long after activation a perpetual key gets its one confirm check.</summary>
    public static readonly TimeSpan PerpetualConfirmWindow = TimeSpan.FromDays(30);

    /// <summary>How far ahead of a term key's paid-through date daily refresh checks begin.</summary>
    public static readonly TimeSpan TermRefreshWindow = TimeSpan.FromDays(30);

    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly LicenseServerClient _client;
    private readonly LicenseStateService _stateService;
    private readonly TimeProvider _time;
    private readonly ILogger<LicenseActivationService> _logger;

    public LicenseActivationService(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        LicenseServerClient client,
        LicenseStateService stateService,
        TimeProvider time,
        ILogger<LicenseActivationService> logger)
    {
        _mainDbFactory = mainDbFactory;
        _client = client;
        _stateService = stateService;
        _time = time;
        _logger = logger;
    }

    /// <summary>All entered keys, newest first (for the Licensing card).</summary>
    public async Task<List<LicenseKeyRecord>> GetKeysAsync()
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        return await db.LicenseKeyRecords.AsNoTracking()
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();
    }

    /// <summary>All site assignments (for the Licensing card).</summary>
    public async Task<List<SiteLicenseAssignment>> GetAssignmentsAsync()
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        return await db.SiteLicenseAssignments.AsNoTracking().ToListAsync();
    }

    /// <summary>
    /// Validates, stores and activates a key entered by the operator. A key
    /// that fails the local checksum is rejected without touching the server.
    /// When the server is unreachable the key is kept as pending and the
    /// phone-home loop keeps retrying. Returns a user-facing error message, or
    /// null on success.
    /// </summary>
    public async Task<string?> ActivateAsync(string enteredKey)
    {
        if (!LicenseKeyUtilities.TryNormalize(enteredKey, out var canonical))
            return "That does not look like a valid license key. Check for typos and try again.";

        var now = _time.GetUtcNow().UtcDateTime;
        var wasNewlyAdded = false;

        await using (var db = await _mainDbFactory.CreateDbContextAsync())
        {
            if (!await db.LicenseKeyRecords.AnyAsync(k => k.LicenseKey == canonical))
            {
                db.LicenseKeyRecords.Add(new LicenseKeyRecord
                {
                    LicenseKey = canonical,
                    Status = LicenseKeyStatuses.Pending,
                    NextCheckAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await db.SaveChangesAsync();
                wasNewlyAdded = true;
            }
        }

        var error = await RefreshKeyAsync(canonical);

        // A brand-new key that verifies as revoked was denied by the server: either the
        // license is already bound to another installation (node-lock seat conflict) or it
        // was revoked. Don't leave a confusing Revoked row or assign sites to it - roll the
        // just-added key back and tell the operator what to do.
        if (error == null && wasNewlyAdded)
        {
            await using var db = await _mainDbFactory.CreateDbContextAsync();
            var record = await db.LicenseKeyRecords.FirstOrDefaultAsync(k => k.LicenseKey == canonical);
            if (record is { Status: LicenseKeyStatuses.Revoked })
            {
                db.LicenseKeyRecords.Remove(record);
                await db.SaveChangesAsync();
                await _stateService.RecomputeAsync();
                return "This key can't be activated on this installation. It may already be in use on "
                    + "another installation, or it is no longer valid. If you moved installations, remove "
                    + "the key from the old one first, then try again. Otherwise contact support.";
            }
        }

        await AutoAssignAsync();
        await _stateService.RecomputeAsync();
        return error;
    }

    /// <summary>
    /// Checks one key against the license server and applies the verified
    /// entitlement to its record. Used by activation, the phone-home loop, and
    /// the manual refresh button. Returns a user-facing error message, or null
    /// when a verified entitlement was applied.
    /// </summary>
    public async Task<string?> RefreshKeyAsync(string canonicalKey)
    {
        var installationId = await GetOrCreateInstallationIdAsync();
        int siteCount;
        await using (var db = await _mainDbFactory.CreateDbContextAsync())
        {
            // A single-site (non-multi-site) instance has no Site row, but the implicit
            // main site still counts as one licensed site.
            siteCount = Math.Max(1, await db.Sites.CountAsync());
        }

        var result = await _client.CheckAsync(canonicalKey, installationId, AppVersionInfo.Informational, siteCount);
        var now = _time.GetUtcNow().UtcDateTime;

        await using (var db = await _mainDbFactory.CreateDbContextAsync())
        {
            var record = await db.LicenseKeyRecords.FirstOrDefaultAsync(k => k.LicenseKey == canonicalKey);
            if (record == null)
                return "License key not found";

            if (result.Success)
            {
                ApplyEntitlement(record, result.Payload!, result.RawEnvelopeJson!, now);
            }
            else
            {
                // Never downgrade cached state on an unverified failure; keep
                // retrying hourly via the phone-home loop.
                record.LastCheckError = result.ErrorMessage;
                if (record.NextCheckAt != null || record.Status == LicenseKeyStatuses.Pending)
                    record.NextCheckAt = now + TimeSpan.FromHours(1);
                record.UpdatedAt = now;
            }
            await db.SaveChangesAsync();
        }

        if (result.Success)
        {
            _logger.LogInformation("License {Key} refreshed: {Status}, model {Model}, allowance {Allowance}",
                LicenseKeyUtilities.MaskKey(canonicalKey), result.Payload!.Status, result.Payload.Model, result.Payload.SiteAllowance);
            return null;
        }

        _logger.LogWarning("License check failed for {Key}: {Error}",
            LicenseKeyUtilities.MaskKey(canonicalKey), result.ErrorMessage);
        return result.ErrorMessage;
    }

    /// <summary>Removes a key; its site assignments cascade away. </summary>
    public async Task RemoveAsync(int licenseKeyRecordId)
    {
        string? removedKey = null;
        await using (var db = await _mainDbFactory.CreateDbContextAsync())
        {
            var record = await db.LicenseKeyRecords.FindAsync(licenseKeyRecordId);
            if (record == null)
                return;
            removedKey = record.LicenseKey;
            db.LicenseKeyRecords.Remove(record);
            await db.SaveChangesAsync();
            _logger.LogInformation("License {Key} removed", LicenseKeyUtilities.MaskKey(record.LicenseKey));
        }

        // Best-effort: free this installation's binding on the server so the key can
        // be claimed on another installation. Never block local removal on it - if the
        // server is unreachable the binding stays put and the operator clears it there.
        var installationId = await GetOrCreateInstallationIdAsync();
        await _client.ReleaseAsync(removedKey, installationId);

        await _stateService.RecomputeAsync();
    }

    /// <summary>
    /// Assigns a site to a covering key (null unassigns). Rejects assignments
    /// beyond the key's allowance with a user-facing message; returns null on
    /// success.
    /// </summary>
    public async Task<string?> AssignAsync(int siteId, int? licenseKeyRecordId)
    {
        await using (var db = await _mainDbFactory.CreateDbContextAsync())
        {
            var existing = await db.SiteLicenseAssignments.FirstOrDefaultAsync(a => a.SiteId == siteId);

            if (licenseKeyRecordId == null)
            {
                if (existing != null)
                {
                    db.SiteLicenseAssignments.Remove(existing);
                    await db.SaveChangesAsync();
                }
            }
            else
            {
                var key = await db.LicenseKeyRecords.FindAsync(licenseKeyRecordId.Value);
                if (key == null)
                    return "License key not found";

                var used = await db.SiteLicenseAssignments
                    .CountAsync(a => a.LicenseKeyRecordId == key.Id && a.SiteId != siteId);
                if (used >= key.SiteAllowance)
                    return $"This key covers {key.SiteAllowance} site(s) and all of its slots are in use.";

                if (existing == null)
                {
                    db.SiteLicenseAssignments.Add(new SiteLicenseAssignment
                    {
                        SiteId = siteId,
                        LicenseKeyRecordId = key.Id,
                        CreatedAt = _time.GetUtcNow().UtcDateTime,
                    });
                }
                else
                {
                    existing.LicenseKeyRecordId = key.Id;
                }
                await db.SaveChangesAsync();
            }
        }

        await _stateService.RecomputeAsync();
        return null;
    }

    /// <summary>
    /// Assigns uncovered sites to active keys with spare allowance, default
    /// site first then oldest first, so activating a key never leaves working
    /// sites uncovered when slots are available.
    /// </summary>
    public async Task AutoAssignAsync()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = await _mainDbFactory.CreateDbContextAsync();

        var sites = await db.Sites.ToListAsync();
        var keys = await db.LicenseKeyRecords.ToListAsync();
        var assignments = await db.SiteLicenseAssignments.ToListAsync();

        var assignedSiteIds = assignments.Select(a => a.SiteId).ToHashSet();
        var uncovered = sites
            .Where(s => !assignedSiteIds.Contains(s.Id))
            .OrderByDescending(s => s.IsDefault)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToList();

        foreach (var key in keys.Where(k => LicenseStateService.IsActiveCurrent(k, now)).OrderBy(k => k.CreatedAt))
        {
            var spare = key.SiteAllowance - assignments.Count(a => a.LicenseKeyRecordId == key.Id);
            while (spare > 0 && uncovered.Count > 0)
            {
                var site = uncovered[0];
                uncovered.RemoveAt(0);
                var assignment = new SiteLicenseAssignment
                {
                    SiteId = site.Id,
                    LicenseKeyRecordId = key.Id,
                    CreatedAt = now,
                };
                db.SiteLicenseAssignments.Add(assignment);
                assignments.Add(assignment);
                spare--;
                _logger.LogInformation("Auto-assigned site {Slug} to license {Key}",
                    site.Slug, LicenseKeyUtilities.MaskKey(key.LicenseKey));
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The stable anonymous id this installation sends with license checks,
    /// created on first use.
    /// </summary>
    public async Task<Guid> GetOrCreateInstallationIdAsync()
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.LicensingInstallationId);
        if (setting?.Value != null && Guid.TryParse(setting.Value, out var existing))
            return existing;

        var id = Guid.NewGuid();
        if (setting == null)
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingKeys.LicensingInstallationId,
                Value = id.ToString(),
            });
        }
        else
        {
            setting.Value = id.ToString();
            setting.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        }
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// Applies a verified entitlement to a key record, including the next
    /// phone-home schedule (the pure scheduling core is
    /// <see cref="ComputeNextCheck"/>).
    /// </summary>
    private static void ApplyEntitlement(LicenseKeyRecord record, EntitlementPayload payload, string rawJson, DateTime nowUtc)
    {
        record.Org = payload.Org;
        record.Model = payload.Model;
        record.SiteAllowance = payload.SiteAllowance;
        record.Status = payload.Status == EntitlementValues.StatusRevoked
            ? LicenseKeyStatuses.Revoked
            : LicenseKeyStatuses.Active;
        record.IssuedAt = payload.IssuedAt.UtcDateTime;
        record.PaidThrough = payload.PaidThrough?.UtcDateTime;
        record.PerpetualConfirmed = payload.PerpetualConfirmed;
        record.ActivatedAt ??= nowUtc;
        record.LastCheckAt = nowUtc;
        record.LastCheckError = null;
        record.EntitlementJson = rawJson;
        record.NextCheckAt = ComputeNextCheck(record, nowUtc);
        record.UpdatedAt = nowUtc;
    }

    /// <summary>
    /// Pure scheduling core for the phone-home cadence:
    /// confirmed perpetual and revoked keys never check again; unconfirmed
    /// perpetual keys check daily once the 30-day fraud window has passed;
    /// term keys check daily inside the 30-day pre-expiry window (continuing
    /// through expiry and grace so late renewals recover automatically).
    /// </summary>
    public static DateTime? ComputeNextCheck(LicenseKeyRecord record, DateTime nowUtc)
    {
        if (record.Status == LicenseKeyStatuses.Revoked)
            return null;

        if (record.Model == LicenseKeyModels.Perpetual)
        {
            if (record.PerpetualConfirmed)
                return null;
            var confirmAt = (record.ActivatedAt ?? nowUtc) + PerpetualConfirmWindow;
            return confirmAt > nowUtc ? confirmAt : nowUtc + TimeSpan.FromDays(1);
        }

        if (record.Model == LicenseKeyModels.Term && record.PaidThrough != null)
        {
            var windowStart = record.PaidThrough.Value - TermRefreshWindow;
            return nowUtc < windowStart ? windowStart : nowUtc + TimeSpan.FromDays(1);
        }

        // Pending or incomplete records retry hourly.
        return nowUtc + TimeSpan.FromHours(1);
    }
}
