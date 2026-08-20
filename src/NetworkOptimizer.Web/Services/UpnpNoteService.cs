using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Operator notes against UPnP mappings, which is the boundary the UPnP Inspector's editing sits
/// behind.
///
/// Site Operator: a note annotates what the site's own gear has opened, and changes nothing on the
/// network. Site-scoped because the mappings are - a note written while looking at a branch office
/// belongs to that office.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IUpnpNoteService
{
    /// <summary>Every note on this site. A read, so any Viewer may have it.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<UpnpNote>> GetNotesAsync();

    /// <summary>Writes a note against a mapping, or clears it when the text is empty.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "upnp_note")]
    Task SaveNoteAsync(string hostIp, string port, string protocol, string? note);
}

/// <inheritdoc cref="IUpnpNoteService" />
public class UpnpNoteService : IUpnpNoteService
{
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;

    /// <param name="siteDbFactory">Per-site database factory.</param>
    /// <param name="siteContext">The site this scope operates on.</param>
    public UpnpNoteService(SiteDbContextFactory siteDbFactory, SiteContextService siteContext)
    {
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
    }

    /// <inheritdoc />
    public async Task<List<UpnpNote>> GetNotesAsync()
    {
        using var db = _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        return await db.UpnpNotes.ToListAsync();
    }

    /// <inheritdoc />
    public async Task SaveNoteAsync(string hostIp, string port, string protocol, string? note)
    {
        var normalizedProtocol = (protocol ?? string.Empty).ToLowerInvariant();
        var trimmed = note?.Trim();

        using var db = _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        var existing = await db.UpnpNotes.FirstOrDefaultAsync(n =>
            n.HostIp == hostIp && n.Port == port && n.Protocol == normalizedProtocol);

        if (existing != null)
        {
            // An emptied note is a deletion: the row exists to carry text, and keeping a blank one
            // would leave the mapping looking annotated.
            if (string.IsNullOrEmpty(trimmed))
            {
                db.UpnpNotes.Remove(existing);
            }
            else
            {
                existing.Note = trimmed;
                existing.UpdatedAt = DateTime.UtcNow;
            }
        }
        else if (!string.IsNullOrEmpty(trimmed))
        {
            db.UpnpNotes.Add(new UpnpNote
            {
                HostIp = hostIp,
                Port = port,
                Protocol = normalizedProtocol,
                Note = trimmed,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }
}
