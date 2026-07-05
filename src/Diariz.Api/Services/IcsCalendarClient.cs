using System.Net;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Reads the signed-in user's external iCalendar (<c>.ics</c>) feeds and projects them into the same
/// <see cref="CalendarEvent"/> shape as Google, so the Calendar views can show both. Each feed is fetched
/// behind the SSRF guard (<see cref="IcsUrlGuard"/> + a resolved-IP re-check per redirect hop), size/time
/// capped, then parsed by the pure <see cref="IcsCalendar"/>. Events are tagged <c>ics:{sourceId}</c> and
/// their ids are prefixed <c>ics:{sourceId}:</c> so they stay unique across feeds.</summary>
public interface IIcsCalendarClient
{
    /// <summary>Events from all the user's <b>enabled</b> feeds overlapping the window, merged and ordered by
    /// start. A feed that fails to fetch/parse is skipped (its <c>LastError</c> is recorded), never fatal.
    /// Empty when the user has no enabled feeds.</summary>
    Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default);

    /// <summary>Validate + test-fetch a candidate feed URL (used when adding/editing a feed): <c>(true, null)</c>
    /// when it's a safe https URL that fetches and parses, else <c>(false, reason)</c>.</summary>
    Task<(bool Ok, string? Error)> ProbeAsync(string url, CancellationToken ct = default);
}

public sealed class IcsCalendarClient : IIcsCalendarClient
{
    public const string HttpClientName = "ics-feeds";
    private const int MaxBytes = 5 * 1024 * 1024; // 5 MB - team calendars can be large
    private const int MaxRedirects = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    private readonly IHttpClientFactory _factory;
    private readonly DiarizDbContext _db;
    private readonly ILogger<IcsCalendarClient> _logger;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public IcsCalendarClient(
        IHttpClientFactory factory, DiarizDbContext db, ILogger<IcsCalendarClient> logger,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        _factory = factory;
        _db = db;
        _logger = logger;
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
    }

    public async Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
        Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default)
    {
        var feeds = await _db.IcsCalendarSources
            .Where(s => s.UserId == userId && s.Enabled)
            .ToListAsync(ct);
        if (feeds.Count == 0) return [];

        // Fetch every feed in parallel (HTTP only - no DbContext touched off-thread), then apply the health
        // bookkeeping sequentially so the shared context is used single-threaded.
        var fetches = await Task.WhenAll(feeds.Select(async feed =>
        {
            try
            {
                var text = await FetchAsync(feed.Url, ct);
                var events = IcsCalendar.ParseEvents(text, timeMin, timeMax, feed.Id.ToString(), feed.Name, feed.Color)
                    .Select(e => e with { Id = $"ics:{feed.Id}:{e.Id}" })
                    .ToList();
                return (feed, events, error: (string?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogInformation(ex, "ICS feed fetch failed for {Url}", feed.Url);
                return (feed, events: new List<CalendarEvent>(), error: Describe(ex));
            }
        }));

        var now = DateTimeOffset.UtcNow;
        foreach (var (feed, _, error) in fetches)
        {
            feed.LastError = error;
            if (error is null) feed.LastFetchedAt = now;
        }
        await _db.SaveChangesAsync(ct);

        return fetches.SelectMany(f => f.events).OrderBy(e => e.Start).ToList();
    }

    public async Task<(bool Ok, string? Error)> ProbeAsync(string url, CancellationToken ct = default)
    {
        var (ok, error) = IcsUrlGuard.ValidateSyntax(url);
        if (!ok) return (false, error);
        try
        {
            var text = await FetchAsync(url, ct);
            // A valid feed parses to at least a VCALENDAR; ParseEvents tolerates empty calendars, so require the
            // marker to catch "fetched an HTML error page" cases.
            if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                return (false, "That URL did not return a calendar feed.");
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, Describe(ex));
        }
    }

    /// <summary>Fetch a feed's raw text, enforcing https-only, a resolved-IP re-check on every redirect hop, a
    /// size cap, and a timeout. Throws on any failure (caller maps to a feed error).</summary>
    private async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        var (ok, syntaxError) = IcsUrlGuard.ValidateSyntax(url);
        if (!ok) throw new InvalidOperationException(syntaxError);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var token = cts.Token;

        var current = new Uri(url, UriKind.Absolute);
        using var client = _factory.CreateClient(HttpClientName); // handler has AllowAutoRedirect = false

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            await EnsureHostAllowedAsync(current, token);

            using var resp = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                current = new Uri(current, loc); // relative redirects resolved; re-guarded next loop
                if (current.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("The feed redirected to a non-https URL.");
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"The feed returned HTTP {(int)resp.StatusCode}.");

            return await ReadCappedAsync(resp, token);
        }
        throw new InvalidOperationException("The feed redirected too many times.");
    }

    private async Task EnsureHostAllowedAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Only https calendar URLs are allowed.");
        IPAddress[] addresses;
        try { addresses = await _resolve(uri.DnsSafeHost, ct); }
        catch { throw new InvalidOperationException("The feed host could not be resolved."); }
        if (addresses.Length == 0 || addresses.Any(IcsUrlGuard.IsBlockedAddress))
            throw new InvalidOperationException("That address is not allowed.");
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is > MaxBytes)
            throw new InvalidOperationException("The feed is too large.");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > MaxBytes) throw new InvalidOperationException("The feed is too large.");
            ms.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Describe(Exception ex) => ex switch
    {
        InvalidOperationException => ex.Message,
        HttpRequestException => "The feed could not be reached.",
        _ => "The feed could not be read.",
    };
}
