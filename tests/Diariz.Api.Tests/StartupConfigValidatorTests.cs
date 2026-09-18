using Diariz.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace Diariz.Api.Tests;

public class StartupConfigValidatorTests
{
    // A configuration that passes every rule; each test breaks exactly one thing.
    private static Dictionary<string, string?> Valid() => new()
    {
        ["Jwt:Key"] = "k7Qe2mZ9vT4pX1rB8nL5sW3yH6cJ0dFa",
        ["Worker:CallbackSecret"] = "w9Kd3Lq7Zx2Pm5Rt",
        ["Storage:AccessKey"] = "diariz-app",
        ["Storage:SecretKey"] = "s3cr3t-Storage-Key-9x",
        ["DataProtection:KeysPath"] = "/keys",
        ["Seed:Password"] = "Adm1n!Strong-Pass",
        ["McpOAuth:Enabled"] = "true",
        ["App:PublicUrl"] = "https://diariz.example.com",
    };

    private static StartupConfigReport Check(Dictionary<string, string?> values, bool isDevelopment = false) =>
        StartupConfigValidator.Validate(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), isDevelopment);

    [Fact]
    public void AValidConfiguration_HasNoErrorsOrWarnings()
    {
        var report = Check(Valid());
        Assert.Empty(report.Errors);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Development_IsNeverChecked()
    {
        var report = Check(new Dictionary<string, string?>(), isDevelopment: true);
        Assert.Empty(report.Errors);
        Assert.Empty(report.Warnings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short-key")]
    [InlineData("0123456789012345678901234567890")]                         // 31 bytes
    [InlineData("REPLACE_WITH_A_LONG_RANDOM_SECRET_AT_LEAST_32_CHARS")]     // appsettings.json
    [InlineData("replace-with-a-long-random-secret-at-least-32-chars")]     // .env.example
    [InlineData("please-change-me-to-something-long-and-random")]
    public void JwtKey_MustBePresentLongAndNotAPlaceholder(string? key)
    {
        var values = Valid();
        values["Jwt:Key"] = key;
        Assert.Contains(Check(values).Errors, e => e.Contains("Jwt:Key"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("change-me")]                  // AppOptions default
    [InlineData("change-me-worker-secret")]    // .env.example
    public void CallbackSecret_MustBePresentLongEnoughAndNotAPlaceholder(string? secret)
    {
        var values = Valid();
        values["Worker:CallbackSecret"] = secret;
        Assert.Contains(Check(values).Errors, e => e.Contains("Worker:CallbackSecret"));
    }

    [Theory]
    [InlineData("Storage:AccessKey", "minioadmin")]
    [InlineData("Storage:SecretKey", "minioadmin")]
    [InlineData("Storage:SecretKey", "change-me-minio")]
    [InlineData("Storage:AccessKey", "")]
    [InlineData("Storage:SecretKey", "")]
    public void StorageCredentials_MustNotBeDefaults(string key, string value)
    {
        var values = Valid();
        values[key] = value;
        Assert.Contains(Check(values).Errors, e => e.Contains(key));
    }

    [Fact]
    public void StorageCredentials_UnsetFallBackToTheMinioDefault_AndAreRejected()
    {
        var values = Valid();
        values.Remove("Storage:AccessKey");
        values.Remove("Storage:SecretKey");
        Assert.Contains(Check(values).Errors, e => e.Contains("Storage:AccessKey"));
    }

    [Fact]
    public void DataProtectionKeysPath_IsRequired()
    {
        var values = Valid();
        values["DataProtection:KeysPath"] = "";
        Assert.Contains(Check(values).Errors, e => e.Contains("DataProtection:KeysPath"));
    }

    [Theory]
    [InlineData("ChangeMe123!")]
    [InlineData("changeme")]
    public void SeedPassword_MustNotBeAPlaceholder(string password)
    {
        var values = Valid();
        values["Seed:Password"] = password;
        Assert.Contains(Check(values).Errors, e => e.Contains("Seed:Password"));
    }

    [Fact]
    public void SeedPassword_MayBeUnset_SeedingIsThenSkipped()
    {
        var values = Valid();
        values.Remove("Seed:Password");
        Assert.Empty(Check(values).Errors);
    }

    [Fact]
    public void AnHttpIssuerOnAPublicHost_IsAnError_WhenMcpOAuthIsOn()
    {
        var values = Valid();
        values["App:PublicUrl"] = "http://diariz.example.com";
        Assert.Contains(Check(values).Errors, e => e.Contains("https"));
    }

    [Fact]
    public void AnExplicitMcpOAuthIssuer_TakesPrecedenceOverThePublicUrl()
    {
        var values = Valid();
        values["McpOAuth:Issuer"] = "http://auth.example.com";
        Assert.Contains(Check(values).Errors, e => e.Contains("https"));
    }

    [Theory]
    [InlineData("http://localhost:8081")]
    [InlineData("http://127.0.0.1:8081")]
    public void AnHttpLoopbackIssuer_OnlyWarns_SoALocalComposeRunStillStarts(string url)
    {
        var values = Valid();
        values["App:PublicUrl"] = url;
        var report = Check(values);
        Assert.Empty(report.Errors);
        Assert.Contains(report.Warnings, w => w.Contains("https"));
    }

    [Fact]
    public void NoIssuerAtAll_Warns_WhenMcpOAuthIsOn()
    {
        var values = Valid();
        values.Remove("App:PublicUrl");
        var report = Check(values);
        Assert.Empty(report.Errors);
        Assert.Contains(report.Warnings, w => w.Contains("App:PublicUrl"));
    }

    [Fact]
    public void AnHttpIssuer_IsIgnored_WhenMcpOAuthIsOff()
    {
        var values = Valid();
        values["McpOAuth:Enabled"] = "false";
        values["App:PublicUrl"] = "http://diariz.example.com";
        var report = Check(values);
        Assert.Empty(report.Errors);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void EveryProblemIsReported_NotJustTheFirst()
    {
        var report = Check(new Dictionary<string, string?> { ["McpOAuth:Enabled"] = "false" });
        Assert.Contains(report.Errors, e => e.Contains("Jwt:Key"));
        Assert.Contains(report.Errors, e => e.Contains("Worker:CallbackSecret"));
        Assert.Contains(report.Errors, e => e.Contains("Storage:AccessKey"));
        Assert.Contains(report.Errors, e => e.Contains("DataProtection:KeysPath"));
    }

    [Fact]
    public void ErrorMessages_NeverEchoTheSecretValue()
    {
        var values = Valid();
        values["Jwt:Key"] = "short-but-secret-value";
        Assert.DoesNotContain(Check(values).Errors, e => e.Contains("short-but-secret-value"));
    }
}
