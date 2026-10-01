namespace Diariz.Api.IntegrationTests.Infrastructure;

/// <summary>The <c>s3.json</c> the SeaweedFS test container starts with. One root identity, mirroring the
/// compose stack's root (the compose stack also has a scoped app identity; tests create a bucket per test,
/// so they need bucket-creation rights on arbitrary names and run as root).</summary>
internal static class SeaweedFsS3Config
{
    public const string AccessKey = "diariz-test-root";
    public const string SecretKey = "diariz-test-root-secret-0123456789abcdef";

    public static string Json => $$"""
        {"identities":[{"name":"root","credentials":[{"accessKey":"{{AccessKey}}","secretKey":"{{SecretKey}}"}],
          "actions":["Admin","Read","Write","List","Tagging"]}]}
        """;
}
