using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

        // The lock file stays by design (see OpenIddictKeys.LoadOrCreate); no temporary key file may.
        Assert.Equal(["oidc-signing.pfx", "oidc-signing.pfx.lock"],
            Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray());
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
    public async Task WhileAnotherProcessIsCreatingTheKey_CreationWaits_ThenLoadsTheirKey()
    {
        // The concurrency test above rarely catches a race on its own: key generation spreads the threads out. This
        // pins the mechanism instead. On Linux File.Move(overwrite: false) checks then renames, and rename replaces
        // an existing file, so two creators can both "win" - creation has to be serialised by the lock file, and
        // whoever waited on it must take the key the holder published rather than write its own.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "oidc-signing.pfx");
        Task<string> creating;
        string theirs;
        using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            creating = Task.Run(() =>
            {
                using var cert = OpenIddictKeys.LoadOrCreateSigning(_dir);
                return cert.Thumbprint;
            });
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.False(creating.IsCompleted, "creation went ahead while another process held the key lock");

            theirs = PublishKey(path);
        }

        Assert.Equal(theirs, await creating);
    }

    private static string PublishKey(string path)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Other process", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        return cert.Thumbprint;
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
