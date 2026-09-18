using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Diariz.Api.Auth;

/// <summary>Load-or-create persistent RSA certificates for the OpenIddict server's token signing and
/// encryption. OpenIddict's development certificates do not survive a container recreate (tokens would break on
/// every deploy), so in a self-hosted deployment we persist two self-signed certs to a mounted keys volume -
/// reusing the same volume as the Data Protection keyring. No CA is involved: the certs are self-contained and
/// only ever used in-process (the server signs/encrypts, the in-process validation reads them back).</summary>
public static class OpenIddictKeys
{
    /// <summary>The signing certificate (persisted at <c>{dir}/oidc-signing.pfx</c>), created on first use.</summary>
    public static X509Certificate2 LoadOrCreateSigning(string dir) =>
        LoadOrCreate(Path.Combine(dir, "oidc-signing.pfx"), "Diariz OIDC Signing",
            X509KeyUsageFlags.DigitalSignature);

    /// <summary>The encryption certificate (persisted at <c>{dir}/oidc-encryption.pfx</c>), created on first use.</summary>
    public static X509Certificate2 LoadOrCreateEncryption(string dir) =>
        LoadOrCreate(Path.Combine(dir, "oidc-encryption.pfx"), "Diariz OIDC Encryption",
            X509KeyUsageFlags.KeyEncipherment);

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static X509Certificate2 LoadOrCreate(string path, string subject, X509KeyUsageFlags usage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path)) return Load(path);

        // Creation is serialised across processes by an exclusive lock on a sibling file, and whoever waited on it
        // loads the key the holder published instead of writing its own - so two processes starting on an empty
        // volume together both sign with the same key. The rename below cannot provide that alone: on Linux
        // File.Move(overwrite: false) checks then renames, and rename silently replaces a file that appeared in
        // between, so both callers would "win" with different keys. The lock is released with the handle, including
        // when a process dies holding it, and the lock file is left in place: deleting it would let a waiter lock the
        // old file while a newcomer locks a new one.
        using var creating = AcquireLock($"{path}.lock");
        if (File.Exists(path)) return Load(path);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        // A long validity - these are internal keys, rotated by deleting the file, not by expiry.
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var pfx = cert.Export(X509ContentType.Pfx);

        // The file holds a private key that can sign tokens for every user, so it is created owner-only, and it
        // appears atomically: written in full under a unique temporary name, then moved into place. A crash mid-write
        // leaves no truncated key behind, and a process reading the key without the lock sees all of it or none.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(pfx);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: false);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.EphemeralKeySet);
    }

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>An exclusive lock on <paramref name="lockPath"/>, waiting while another process holds it.
    /// <see cref="FileShare.None"/> is a share-mode lock on Windows and an exclusive <c>flock</c> on Linux, so it
    /// excludes other processes and other handles in this one alike.</summary>
    private static FileStream AcquireLock(string lockPath)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static X509Certificate2 Load(string path)
    {
        // Tighten a key file written before it was created owner-only. Best effort: a read-only volume still loads.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if (File.GetUnixFileMode(path) != OwnerOnly) File.SetUnixFileMode(path, OwnerOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(path), password: null, X509KeyStorageFlags.EphemeralKeySet);
    }
}
