using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Teaching hints that retire once the user has plainly seen them.
/// <para>
/// Some gestures cannot be discovered by looking - a modifier click is the obvious case - so the
/// UI has to say them out loud. Saying them forever is its own kind of noise: the hint is for the
/// first encounter, not the hundredth. This counts how many times a user has been shown one and
/// stops at <see cref="ShowLimit"/>.
/// </para>
/// <para>
/// Per user, not per site or per install: what someone has learned travels with them, and one
/// operator learning a gesture says nothing about their colleagues. A user we cannot identify
/// (no Identity session) always sees the hint and nothing is recorded - the hint is the safe
/// outcome, and there is nowhere honest to keep the count.
/// </para>
/// </summary>
public class UiHintService
{
    /// <summary>How many times a hint is shown before it is treated as learned.</summary>
    public const int ShowLimit = 2;

    private readonly IDbContextFactory<AuthDbContext> _authDb;
    private readonly AuthenticationStateProvider _authState;
    private readonly ILogger<UiHintService> _logger;

    public UiHintService(
        IDbContextFactory<AuthDbContext> authDb,
        AuthenticationStateProvider authState,
        ILogger<UiHintService> logger)
    {
        _authDb = authDb;
        _authState = authState;
        _logger = logger;
    }

    /// <summary>
    /// Whether this user should still be shown the hint. Errs toward showing it: a hint one time
    /// too many is a smaller cost than a gesture nobody ever discovers.
    /// </summary>
    public async Task<bool> ShouldShowAsync(string hintKey, CancellationToken ct = default)
    {
        var userId = await CurrentUserIdAsync();
        if (userId == null) return true;
        try
        {
            await using var db = await _authDb.CreateDbContextAsync(ct);
            var shown = await db.UserUiHints.AsNoTracking()
                .Where(h => h.UserId == userId && h.HintKey == hintKey)
                .Select(h => (int?)h.TimesShown)
                .FirstOrDefaultAsync(ct);
            return (shown ?? 0) < ShowLimit;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read hint state for {Hint}; showing it", hintKey);
            return true;
        }
    }

    /// <summary>
    /// Counts one showing. Call once per occasion the user could actually have read it - a page
    /// visit - not once per render, or a component that re-renders on a timer would burn the
    /// allowance in seconds.
    /// </summary>
    public async Task RecordShownAsync(string hintKey, CancellationToken ct = default)
    {
        var userId = await CurrentUserIdAsync();
        if (userId == null) return;
        try
        {
            await using var db = await _authDb.CreateDbContextAsync(ct);
            var row = await db.UserUiHints
                .FirstOrDefaultAsync(h => h.UserId == userId && h.HintKey == hintKey, ct);
            if (row == null)
            {
                row = new UserUiHint { UserId = userId, HintKey = hintKey };
                db.UserUiHints.Add(row);
            }
            // Stops climbing at the limit: the number past that point means nothing, and leaving it
            // to grow forever would make a future "reset hints" read as absurd.
            if (row.TimesShown < ShowLimit) row.TimesShown++;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Losing a count costs one extra tooltip, so it is never worth failing a render over.
            _logger.LogDebug(ex, "Could not record hint state for {Hint}", hintKey);
        }
    }

    private async Task<string?> CurrentUserIdAsync()
    {
        try
        {
            var user = (await _authState.GetAuthenticationStateAsync()).User;
            return user.Identity?.IsAuthenticated == true
                ? user.FindFirstValue(ClaimTypes.NameIdentifier)
                : null;
        }
        catch { return null; }
    }
}

/// <summary>Keys for hints that retire. Kept together so the set is visible at a glance.</summary>
public static class UiHintKeys
{
    /// <summary>Ctrl/Cmd-click on the WAN filter builds a comparison - invisible without saying so.</summary>
    public const string WanFilterCompare = "wan-filter-compare";
}
