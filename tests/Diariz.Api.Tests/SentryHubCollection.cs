namespace Diariz.Api.Tests;

/// <summary>
/// Test classes that touch the PROCESS-WIDE <c>SentrySdk</c> hub, kept out of each other's way.
///
/// <para>xUnit runs classes in different collections in parallel, and <c>SentrySdk</c> is static: there is
/// one hub per process, not one per test. So while <see cref="SentryScrubberTests"/> has the SDK
/// initialised with a capturing transport and <c>TracesSampleRate = 1.0</c>, any transaction started
/// anywhere else in the assembly is sampled and delivered to THAT transport - and, in the other
/// direction, <c>JobTelemetry</c>'s transactions stop being the no-ops they are when telemetry is
/// unconfigured. The observed symptom was a roughly one-in-four failure of
/// <c>SetBeforeSendTransaction_ScrubsTheEnvelopeThatWouldReachTheServer</c>, which passed 12 times out of
/// 12 in isolation: <c>JobTelemetryTests</c> emits a transaction named <c>summarize</c>, and the capturing
/// transport handed back whichever transaction arrived first.</para>
///
/// <para>Sharing a collection makes these classes run sequentially. Add any new test class that starts a
/// transaction, initialises the SDK, or exercises <c>SentryLlmTrace</c> to this collection - not the ones
/// that use a fake such as <c>FakeLlmTrace</c>, which never reach the hub.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class SentryHubCollection
{
    public const string Name = "sentry-global-hub";
}
