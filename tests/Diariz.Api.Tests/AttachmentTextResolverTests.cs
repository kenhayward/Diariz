using System.Text;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Turning ONE attachment into text. Extracted out of <c>ChatController</c> so that a single dropped
/// attachment and the bulk "include attachments" path cannot drift apart.</summary>
public class AttachmentTextResolverTests
{
    private static AttachmentTextResolver Make(FakeAudioStorage storage, FakeUrlFetcher urls) =>
        new(new AttachmentExtractor(), storage, urls);

    [Fact]
    public async Task ResolvesAFileFromStorage()
    {
        var storage = new FakeAudioStorage();
        storage.Objects["k1"] = Encoding.UTF8.GetBytes("Hello from the document.");

        var result = await Make(storage, new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.File, "notes.txt", "k1", "text/plain", null));

        Assert.NotNull(result);
        Assert.Equal("notes.txt", result!.Name);
        Assert.Contains("Hello from the document.", result.Text);
    }

    [Fact]
    public async Task ResolvesAUrlThroughTheFetcher()
    {
        var urls = new FakeUrlFetcher();
        urls.Texts["https://example.test/x"] = "Fetched page text.";

        var result = await Make(new FakeAudioStorage(), urls).ResolveAsync(
            new AttachmentRef(AttachmentKind.Url, "A link", null, null, "https://example.test/x"));

        Assert.Equal("Fetched page text.", result!.Text);
        Assert.Equal("A link", result.Name);
    }

    [Fact]
    public async Task ReturnsNull_ForAnUnsupportedFileType()
    {
        var storage = new FakeAudioStorage();
        storage.Objects["k1"] = [1, 2, 3];

        var result = await Make(storage, new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.File, "bundle.zip", "k1", "application/zip", null));

        Assert.Null(result);
        // Rejected before any blob was read - FakeAudioStorage records every key it was asked for.
        Assert.Empty(storage.Reads);
    }

    [Fact]
    public async Task ReturnsNull_WhenTheExtractionIsEmpty()
    {
        var storage = new FakeAudioStorage();
        storage.Objects["k1"] = Encoding.UTF8.GetBytes("   ");

        var result = await Make(storage, new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.File, "blank.txt", "k1", "text/plain", null));

        Assert.Null(result);
    }

    /// <summary>An unknown URL returns null from the fetcher, which is what a blocked or unreachable one
    /// does for real (the SSRF guards live in UrlFetcher, not here).</summary>
    [Fact]
    public async Task ReturnsNull_WhenTheUrlFetchFails()
    {
        var result = await Make(new FakeAudioStorage(), new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.Url, "A link", null, null, "https://example.test/x"));

        Assert.Null(result);
    }

    /// <summary>A missing blob must not take the caller down - the bulk path swallowed these, and the drop
    /// path turns null into a clean 400.</summary>
    [Fact]
    public async Task ReturnsNull_WhenStorageThrows()
    {
        var result = await Make(new FakeAudioStorage(), new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.File, "gone.txt", "no-such-key", "text/plain", null));

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenAFileHasNoBlobKey()
    {
        var result = await Make(new FakeAudioStorage(), new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.File, "orphan.txt", null, "text/plain", null));

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenAUrlAttachmentHasNoUrl()
    {
        var result = await Make(new FakeAudioStorage(), new FakeUrlFetcher()).ResolveAsync(
            new AttachmentRef(AttachmentKind.Url, "A link", null, null, null));

        Assert.Null(result);
    }
}
