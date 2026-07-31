using Diariz.Api.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

/// <summary>Non-secret configuration the SPA needs before it can boot. Anonymous by necessity: the
/// browser reads this before anyone has signed in.
///
/// This exists because the SPA is built once into one image that serves every environment, so a
/// build-time constant would force dev and production to share one error-tracking project. A browser
/// DSN is public by design - it ships inside the JavaScript bundle either way - so serving it here
/// discloses nothing that shipping it in the bundle would not.
///
/// Only add fields here that are safe for an unauthenticated caller to read.
///
/// Deliberately excluded from the published OpenAPI document (which is what generates the n8n community
/// node - see integrations/n8n-nodes-diariz). This is an internal bootstrap call the SPA makes before it
/// can start, not an integration operation a workflow author would ever want to call; including it would
/// add a meaningless operation to the generated node, and the node is published to npm where a bad
/// version cannot be corrected after the fact.</summary>
[ApiController]
[Route("api/config")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public class ConfigController : ControllerBase
{
    private readonly TelemetryOptions _telemetry;

    public ConfigController(IOptions<TelemetryOptions> telemetry) => _telemetry = telemetry.Value;

    [HttpGet]
    public IActionResult Get() => Ok(new ClientConfig(
        SentryDsn: _telemetry.BrowserDsn,
        SentryEnvironment: _telemetry.Environment,
        SentryTracesSampleRate: _telemetry.TracesSampleRate));
}

/// <summary>Browser-safe configuration. Every field here is readable by an anonymous caller - never add
/// the server-side <see cref="TelemetryOptions.Dsn"/>, an API key, or anything user-scoped.</summary>
public record ClientConfig(string SentryDsn, string SentryEnvironment, double SentryTracesSampleRate);
