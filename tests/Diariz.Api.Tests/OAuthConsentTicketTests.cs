using Diariz.Api.Auth;
using Microsoft.AspNetCore.DataProtection;

namespace Diariz.Api.Tests;

public class OAuthConsentTicketTests
{
    private static OAuthConsentTicketProtector NewProtector() =>
        new(new EphemeralDataProtectionProvider());

    private static readonly DateTimeOffset Now = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Issue_ThenVerify_RoundTripsTheDecision()
    {
        var p = NewProtector();
        var userId = Guid.NewGuid();
        var cookie = p.Issue(userId, "client123", allow: true, Now.AddMinutes(5));

        var decision = p.Verify(cookie, "client123", Now);

        Assert.NotNull(decision);
        Assert.Equal(userId, decision!.UserId);
        Assert.True(decision.Allow);
    }

    [Fact]
    public void Verify_CarriesADenyDecision()
    {
        var p = NewProtector();
        var cookie = p.Issue(Guid.NewGuid(), "c", allow: false, Now.AddMinutes(5));
        Assert.False(p.Verify(cookie, "c", Now)!.Allow);
    }

    [Fact]
    public void Verify_RejectsAMismatchedClientId()
    {
        var p = NewProtector();
        var cookie = p.Issue(Guid.NewGuid(), "client-A", allow: true, Now.AddMinutes(5));
        // A ticket issued for one client must not authorize a different client's request.
        Assert.Null(p.Verify(cookie, "client-B", Now));
    }

    [Fact]
    public void Verify_RejectsAnExpiredTicket()
    {
        var p = NewProtector();
        var cookie = p.Issue(Guid.NewGuid(), "c", allow: true, Now.AddMinutes(5));
        Assert.Null(p.Verify(cookie, "c", Now.AddMinutes(6)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-valid-protected-blob")]
    public void Verify_RejectsMissingOrTamperedCookies(string? cookie) =>
        Assert.Null(NewProtector().Verify(cookie, "c", Now));

    [Fact]
    public void Verify_RejectsACookieFromADifferentProtector()
    {
        // A cookie minted by another keyring (or a forged one) must not validate.
        var cookie = NewProtector().Issue(Guid.NewGuid(), "c", true, Now.AddMinutes(5));
        Assert.Null(NewProtector().Verify(cookie, "c", Now));
    }
}
