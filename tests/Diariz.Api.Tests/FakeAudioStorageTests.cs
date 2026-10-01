using System.Text;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests;

/// <summary>The fake must behave like the real S3 client where it matters to callers, or the unit layer
/// cannot catch the bugs the difference hides.</summary>
public class FakeAudioStorageTests
{
    [Fact]
    public async Task Upload_DisposesTheStreamItWasGiven_LikeTheRealSdk()
    {
        // AWSSDK.S3 disposes the stream it uploads (pinned by AudioStorageIntegrationTests). A fake that kept it
        // open let a restore read FileStream.Length after the upload and pass every unit test while failing on
        // every real store (issue #769).
        var storage = new FakeAudioStorage();
        var input = new MemoryStream(Encoding.UTF8.GetBytes("bytes"));

        await storage.UploadAsync("u1/a.webm", input, "audio/webm");

        Assert.False(input.CanRead);
        Assert.Equal("bytes", Encoding.UTF8.GetString(storage.Objects["u1/a.webm"]));
    }
}
