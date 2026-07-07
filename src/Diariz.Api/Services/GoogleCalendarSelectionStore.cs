using System.Text.Json;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Reads/writes the user's chosen set of Google calendars (for recording attribution + the Calendar
/// overlay), stored as a JSON id array on <see cref="UserSettings.GoogleSelectedCalendarIdsJson"/>. A null
/// result means "not chosen" - callers fall back to the calendars the user has made visible in Google.</summary>
public interface IGoogleCalendarSelectionStore
{
    Task<IReadOnlySet<string>?> GetSelectedIdsAsync(Guid userId, CancellationToken ct = default);
    Task SetSelectedIdsAsync(Guid userId, IReadOnlyList<string> ids, CancellationToken ct = default);
}

public class GoogleCalendarSelectionStore(DiarizDbContext db) : IGoogleCalendarSelectionStore
{
    public async Task<IReadOnlySet<string>?> GetSelectedIdsAsync(Guid userId, CancellationToken ct = default)
    {
        var s = await db.UserSettings.FindAsync([userId], ct);
        if (string.IsNullOrEmpty(s?.GoogleSelectedCalendarIdsJson)) return null;
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(s.GoogleSelectedCalendarIdsJson);
            return ids is null ? null : new HashSet<string>(ids);
        }
        catch (JsonException)
        {
            return null; // corrupt value - treat as "not chosen" rather than throwing
        }
    }

    public async Task SetSelectedIdsAsync(Guid userId, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var s = await db.UserSettings.FindAsync([userId], ct);
        if (s is null)
        {
            s = new UserSettings { UserId = userId };
            db.UserSettings.Add(s);
        }
        s.GoogleSelectedCalendarIdsJson = JsonSerializer.Serialize(ids);
        await db.SaveChangesAsync(ct);
    }
}
