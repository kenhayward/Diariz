using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class ConfigControllerTests
{
    private static ConfigController Build(TelemetryOptions telemetry) =>
        new(Options.Create(telemetry));

    [Fact]
    public void Get_ReturnsTheBrowserDsn_WhenConfigured()
    {
        var result = Build(new TelemetryOptions
        {
            BrowserDsn = "https://key@errors.example/2",
            Environment = "production",
            TracesSampleRate = 0.5,
        }).Get();

        var value = Assert.IsType<OkObjectResult>(result).Value!;
        var dto = Assert.IsType<ClientConfig>(value);
        Assert.Equal("https://key@errors.example/2", dto.SentryDsn);
        Assert.Equal("production", dto.SentryEnvironment);
        Assert.Equal(0.5, dto.SentryTracesSampleRate);
    }

    [Fact]
    public void Get_ReturnsAnEmptyDsn_WhenNotConfigured()
    {
        var result = Build(new TelemetryOptions()).Get();

        var dto = Assert.IsType<ClientConfig>(Assert.IsType<OkObjectResult>(result).Value!);
        Assert.Equal("", dto.SentryDsn);
    }

    [Fact]
    public void Get_NeverReturnsTheServerSideDsn()
    {
        // The server DSN belongs to a different GlitchTip project and must not reach the browser.
        var result = Build(new TelemetryOptions
        {
            Dsn = "https://server-secret@errors.example/1",
            BrowserDsn = "https://browser@errors.example/2",
        }).Get();

        var dto = Assert.IsType<ClientConfig>(Assert.IsType<OkObjectResult>(result).Value!);
        Assert.Equal("https://browser@errors.example/2", dto.SentryDsn);
        Assert.DoesNotContain("server-secret", System.Text.Json.JsonSerializer.Serialize(dto));
    }
}
