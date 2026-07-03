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

    private static X509Certificate2 LoadOrCreate(string path, string subject, X509KeyUsageFlags usage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
            return X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(path), password: null,
                X509KeyStorageFlags.EphemeralKeySet);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        // A long validity - these are internal keys, rotated by deleting the file, not by expiry.
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        var pfx = cert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.EphemeralKeySet);
    }
}
