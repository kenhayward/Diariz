using System.Net;
using System.Net.Http.Headers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// The upload size chain, end to end over real HTTP.
///
/// Four separate limits sit between a user's file and the database, and they have to agree:
///   1. nginx `client_max_body_size`                         - 1024m (apps/web/nginx.conf)
///   2. Kestrel per-endpoint `[RequestSizeLimit]`             - 1 GiB (RecordingsController.Upload)
///   3. `FormOptions.MultipartBodyLengthLimit`                - **128 MB by ASP.NET Core default**
///   4. the app's own `Uploads:MaxBytes` check                - 500 MB, reported as a friendly 413
///
/// (3) is the one that is easy to miss: it is enforced by the multipart *form reader*, and
/// `[RequestSizeLimit]` does not raise it. Left at its default, every upload between 128 MB and 500 MB died
/// during model binding with `InvalidDataException: Multipart body length limit 134217728 exceeded` - before
/// the action ran, so the user never got the app's own "maximum upload size is 500 MB" message either.
///
/// This test buys a real >128 MB round trip rather than asserting the configured number, because the
/// number on its own proves nothing about which layer actually rejects the request. The body is streamed
/// (see <see cref="ZeroStream"/>) so neither side allocates it.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class UploadSizeLimitIntegrationTests(ContainersFixture fx)
{
    /// Just over ASP.NET Core's 128 MB (134,217,728 byte) multipart default - big enough to trip it, small
    /// enough to keep the test to a few seconds.
    private const long JustOverMultipartDefault = 134_217_728L + (4L * 1024 * 1024);

    /// <summary>A read-only stream of zeros of a given length: a large upload body that costs no memory.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = Length - Position;
            if (remaining <= 0) return 0;
            var n = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, n);
            Position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                _ => Length + offset,
            };
            return Position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Upload_LargerThanTheMultipartDefault_ReachesTheActionInsteadOfFailingToParse()
    {
        await using var factory = new DiarizWebAppFactory(fx);
        var userId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
            Users.Ensure(scope.ServiceProvider.GetRequiredService<DiarizDbContext>(), userId);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(userId));

        using var form = new MultipartFormDataContent();
        var audio = new StreamContent(new ZeroStream(JustOverMultipartDefault));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/webm");
        form.Add(audio, "audio", "long-meeting.webm");
        form.Add(new StringContent("Long meeting"), "title");
        form.Add(new StringContent("0"), "durationMs");
        form.Add(new StringContent("Upload"), "source");

        var res = await client.PostAsync("/api/recordings", form);

        // Before the fix: 400, body "Failed to read the request form. Multipart body length limit 134217728
        // exceeded." - MVC turning the form-reader's InvalidDataException into a model-state error, so the
        // request never reached the action and none of the API's own status codes applied.
        //
        // After: 415, because the action ran and rejected the *content* (zeros are not audio). The status
        // is the weaker of the two assertions on its own - a 415 could in principle come from somewhere
        // else - so the body is checked first, and it names the exact failure this test exists for.
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Multipart body length limit", body);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
    }
}
