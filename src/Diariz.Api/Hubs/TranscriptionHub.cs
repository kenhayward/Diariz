using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Diariz.Api.Hubs;

/// <summary>
/// Pushes recording status changes to the owning user. Clients join a per-user group
/// automatically on connect (group name = user id) so events are scoped per user.
/// </summary>
[Authorize]
public class TranscriptionHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (!string.IsNullOrEmpty(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, userId);
        await base.OnConnectedAsync();
    }
}

/// <summary>Strongly-typed-ish helper for sending status events from controllers/services.</summary>
public static class TranscriptionHubExtensions
{
    public static Task NotifyStatusAsync(this IHubContext<TranscriptionHub> hub,
        Guid userId, Guid recordingId, string status) =>
        hub.Clients.Group(userId.ToString())
            .SendAsync("RecordingStatusChanged", new { recordingId, status });
}
