using Diariz.Api.Auth;

namespace Diariz.Api.Tests;

public class OpenIddictKeysTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "diariz-oidc-keys", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void TheSigningKey_IsCreatedOnce_AndReloadedAfterwards()
    {
        using var first = OpenIddictKeys.LoadOrCreateSigning(_dir);
        using var second = OpenIddictKeys.LoadOrCreateSigning(_dir);

        Assert.Equal(first.Thumbprint, second.Thumbprint);
        Assert.True(second.HasPrivateKey);
    }

    [Fact]
    public void Creating_LeavesOnlyTheFinalFile_NoPartialOrTemporaryOnes()
    {
        using var _ = OpenIddictKeys.LoadOrCreateSigning(_dir);

        Assert.Equal(["oidc-signing.pfx"], Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)).ToArray());
    }

    [Fact]
    public async Task ConcurrentFirstStarts_AllEndUpWithTheSameKey()
    {
        // Two API processes starting on an empty keys volume must not each keep a different key - tokens signed by
        // one would then fail validation on the other.
        var thumbprints = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var cert = OpenIddictKeys.LoadOrCreateSigning(_dir);
            return cert.Thumbprint;
        })));

        Assert.Single(thumbprints.Distinct());
        using var onDisk = OpenIddictKeys.LoadOrCreateSigning(_dir);
        Assert.Equal(thumbprints[0], onDisk.Thumbprint);
    }

    [Fact]
    public void TheKeyFile_IsReadableByItsOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return; // Unix file modes; the API runs on Linux

        using var _ = OpenIddictKeys.LoadOrCreateSigning(_dir);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(_dir, "oidc-signing.pfx")));
    }
}
