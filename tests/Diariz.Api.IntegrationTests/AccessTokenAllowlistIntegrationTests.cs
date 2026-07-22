using System.Net;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// HTTP-level coverage of the <c>access_token</c> query-string allowlist in <c>Program.cs</c>'s JWT
/// <c>OnMessageReceived</c> handler - the only place that lets a browser-initiated request that cannot set an
/// Authorization header (an &lt;audio&gt; element, an &lt;img&gt; tag, a new-tab attachment download, the
/// SignalR WS handshake) authenticate instead via <c>?access_token=</c>. Every other integration test in this
/// project constructs a controller directly and so never runs that handler at all; this class is the
/// regression net for it, via a real <see cref="DiarizWebAppFactory"/> HTTP round trip.
///
/// The allowlist as of this writing (see Program.cs):
///   - /api/recordings/{id}/audio                                        (RecordingsController.GetAudio)
///   - /api/recordings/{recordingId}/attachments/{attachmentId}/content  (AttachmentsController.Content)
///   - /api/recordings/{recordingId}/screenshots/{screenshotId}/content  (ScreenshotsController.Content)
///   - /api/recordings/{recordingId}/screenshots/{screenshotId}/thumb    (ScreenshotsController.Thumb)
///   - /api/sections/{sectionId}/folder-attachments/{attachmentId}/content (SectionAttachmentsController.Content)
///   - /hubs/transcription (and its /negotiate)                          (TranscriptionHub)
/// It also allows /api/maintenance/{ver}/backup (the platform-backup download), which this class does not
/// cover - see the class remarks on why, at the bottom of this file's sibling report.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AccessTokenAllowlistIntegrationTests(ContainersFixture fx)
{
    private const string SamplePdf = "%PDF-1.4 pretend attachment bytes";
    private static readonly byte[] SamplePng = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];
    private static readonly byte[] SampleJpg = [0xFF, 0xD8, 0xFF, 9, 9, 9];

    private DiarizWebAppFactory NewFactory() => new(fx);

    private static async Task<Guid> SeedUserAsync(DiarizWebAppFactory factory)
    {
        var id = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        Users.Ensure(scope.ServiceProvider.GetRequiredService<DiarizDbContext>(), id);
        return id;
    }

    private static async Task<Guid> SeedRecordingWithAudioAsync(DiarizWebAppFactory factory, Guid ownerId, byte[] audio)
    {
        var recId = Guid.NewGuid();
        var blobKey = $"{ownerId}/{recId}.webm";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
        db.Recordings.Add(new Recording { Id = recId, UserId = ownerId, BlobKey = blobKey, ContentType = "audio/webm", SizeBytes = audio.Length });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IAudioStorage>()
            .UploadAsync(blobKey, new MemoryStream(audio), "audio/webm");
        return recId;
    }

    private static async Task<(Guid recordingId, Guid attachmentId)> SeedAttachmentAsync(DiarizWebAppFactory factory, Guid ownerId, byte[] bytes)
    {
        var recId = Guid.NewGuid();
        var attId = Guid.NewGuid();
        var blobKey = $"{ownerId}/attachments/{attId}.pdf";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
        db.Recordings.Add(new Recording { Id = recId, UserId = ownerId, BlobKey = "unused.webm" });
        db.Attachments.Add(new Attachment
        {
            Id = attId, RecordingId = recId, Kind = AttachmentKind.File, Name = "spec.pdf",
            BlobKey = blobKey, ContentType = "application/pdf", SizeBytes = bytes.Length, Ordinal = 0,
        });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IAudioStorage>()
            .UploadAsync(blobKey, new MemoryStream(bytes), "application/pdf");
        return (recId, attId);
    }

    private static async Task<(Guid recordingId, Guid screenshotId)> SeedScreenshotAsync(
        DiarizWebAppFactory factory, Guid ownerId, byte[] full, byte[] thumb)
    {
        var recId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var blobKey = $"{ownerId}/screenshots/{shotId}.png";
        var thumbKey = $"{ownerId}/screenshots/{shotId}.thumb.jpg";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
        db.Recordings.Add(new Recording { Id = recId, UserId = ownerId, BlobKey = "unused.webm" });
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = shotId, UserId = ownerId, RecordingId = recId, CapturedAtMs = 1000,
            BlobKey = blobKey, ThumbBlobKey = thumbKey, Width = 10, Height = 10,
            SizeBytes = full.Length + thumb.Length, Ordinal = 0,
        });
        await db.SaveChangesAsync();
        var storage = scope.ServiceProvider.GetRequiredService<IAudioStorage>();
        await storage.UploadAsync(blobKey, new MemoryStream(full), "image/png");
        await storage.UploadAsync(thumbKey, new MemoryStream(thumb), "image/jpeg");
        return (recId, shotId);
    }

    private static async Task<(Guid sectionId, Guid attachmentId)> SeedSectionAttachmentAsync(
        DiarizWebAppFactory factory, Guid ownerId, byte[] bytes)
    {
        var sectionId = Guid.NewGuid();
        var attId = Guid.NewGuid();
        var blobKey = $"{ownerId}/section-attachments/{attId}.pdf";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
        var roomId = await scope.ServiceProvider.GetRequiredService<IRoomScope>().PersonalRoomIdAsync(ownerId);
        db.Sections.Add(new Section { Id = sectionId, UserId = ownerId, RoomId = roomId, Name = "Folder" });
        db.SectionAttachments.Add(new SectionAttachment
        {
            Id = attId, SectionId = sectionId, Kind = AttachmentKind.File, Name = "doc.pdf",
            BlobKey = blobKey, ContentType = "application/pdf", SizeBytes = bytes.Length, Ordinal = 0,
        });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IAudioStorage>()
            .UploadAsync(blobKey, new MemoryStream(bytes), "application/pdf");
        return (sectionId, attId);
    }

    // ---- 1. Each allow-listed route accepts a valid access_token query parameter and returns success ----

    [Fact]
    public async Task RecordingAudioRoute_WithAccessTokenQueryParam_StreamsTheAudio()
    {
        using var factory = NewFactory();
        var audio = "pretend webm bytes"u8.ToArray();
        var userId = await SeedUserAsync(factory);
        var recId = await SeedRecordingWithAudioAsync(factory, userId, audio);
        var token = TestTokens.Issue(userId);

        using var client = factory.CreateClient();
        var resp = await client.GetAsync($"/api/recordings/{recId}/audio?access_token={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(audio, await resp.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RecordingAttachmentContentRoute_WithAccessTokenQueryParam_StreamsTheFile()
    {
        using var factory = NewFactory();
        var bytes = System.Text.Encoding.UTF8.GetBytes(SamplePdf);
        var userId = await SeedUserAsync(factory);
        var (recId, attId) = await SeedAttachmentAsync(factory, userId, bytes);
        var token = TestTokens.Issue(userId);

        using var client = factory.CreateClient();
        var resp = await client.GetAsync(
            $"/api/recordings/{recId}/attachments/{attId}/content?access_token={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(bytes, await resp.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RecordingScreenshotContentAndThumbRoutes_WithAccessTokenQueryParam_StreamTheImages()
    {
        using var factory = NewFactory();
        var userId = await SeedUserAsync(factory);
        var (recId, shotId) = await SeedScreenshotAsync(factory, userId, SamplePng, SampleJpg);
        var token = TestTokens.Issue(userId);
        using var client = factory.CreateClient();

        var content = await client.GetAsync(
            $"/api/recordings/{recId}/screenshots/{shotId}/content?access_token={Uri.EscapeDataString(token)}");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(SamplePng, await content.Content.ReadAsByteArrayAsync());

        var thumb = await client.GetAsync(
            $"/api/recordings/{recId}/screenshots/{shotId}/thumb?access_token={Uri.EscapeDataString(token)}");
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal(SampleJpg, await thumb.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task SectionFolderAttachmentContentRoute_WithAccessTokenQueryParam_StreamsTheFile()
    {
        using var factory = NewFactory();
        var bytes = System.Text.Encoding.UTF8.GetBytes(SamplePdf);
        var userId = await SeedUserAsync(factory);
        var (sectionId, attId) = await SeedSectionAttachmentAsync(factory, userId, bytes);
        var token = TestTokens.Issue(userId);

        using var client = factory.CreateClient();
        var resp = await client.GetAsync(
            $"/api/sections/{sectionId}/folder-attachments/{attId}/content?access_token={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(bytes, await resp.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task HubNegotiateRoute_WithAccessTokenQueryParam_Succeeds()
    {
        using var factory = NewFactory();
        var userId = await SeedUserAsync(factory);
        var token = TestTokens.Issue(userId);

        using var client = factory.CreateClient();
        var resp = await client.PostAsync(
            $"/hubs/transcription/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}", content: null);

        // The hub is [Authorize]-protected; a 200 here (with a connectionId body) proves the query-string
        // token authenticated the WS handshake path, not just that the endpoint exists.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("connectionId", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 2. A route NOT on the allowlist rejects the same token (it's a list, not a prefix match) ----

    [Fact]
    public async Task NonAllowlistedRecordingRoute_WithAccessTokenQueryParam_IsRejected()
    {
        // GET /api/recordings/{id} starts with /api/recordings (same prefix as the allow-listed /audio route)
        // but does not end in /audio, /content, or /thumb - it must NOT authenticate via access_token.
        using var factory = NewFactory();
        var userId = await SeedUserAsync(factory);
        var token = TestTokens.Issue(userId);

        using var client = factory.CreateClient();
        var resp = await client.GetAsync($"/api/recordings/{Guid.NewGuid()}?access_token={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- 3. No token at all is rejected on an allow-listed route ----

    [Fact]
    public async Task RecordingAudioRoute_WithNoTokenAtAll_IsRejected()
    {
        using var factory = NewFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync($"/api/recordings/{Guid.NewGuid()}/audio");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- 4. An access_token belonging to a DIFFERENT user cannot read another user's asset: the ----
    // ----    query-string path authenticates but must not bypass the controller's ownership check.  ----

    [Fact]
    public async Task RecordingAudioRoute_WithAnotherUsersAccessToken_CannotReadTheOwnersAudio()
    {
        using var factory = NewFactory();
        var owner = await SeedUserAsync(factory);
        var recId = await SeedRecordingWithAudioAsync(factory, owner, "owner's private audio"u8.ToArray());
        var intruder = await SeedUserAsync(factory);
        var intruderToken = TestTokens.Issue(intruder);

        using var client = factory.CreateClient();
        var resp = await client.GetAsync($"/api/recordings/{recId}/audio?access_token={Uri.EscapeDataString(intruderToken)}");

        // Not 401 (the token is genuine and DID authenticate the intruder) and not 200 (the recording is not
        // theirs) - the ownership filter in RecordingsController.GetAudio must still apply.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
