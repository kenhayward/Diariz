using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Diariz.Api.Tests;

public class GoogleSignInHandlerTests
{
    private static GoogleSignInHandler Build(IdentityTestHost host) =>
        new(host.Users, new PlatformSettingsService(host.Db));

    private static GoogleUserInfo Info(
        string sub = "google-sub-1", string email = "u@x.test", bool verified = true,
        string? name = "Ada Lovelace", string? picture = "https://pic/1.png") =>
        new(sub, email, verified, name, picture, null);

    private static async Task<ApplicationUser> Seed(
        IdentityTestHost host, string email, UserStatus status, bool enabled = true, string? googleSub = null)
    {
        var user = new ApplicationUser
        {
            UserName = email, Email = email, Status = status, IsEnabled = enabled,
            EmailConfirmed = status == UserStatus.Active, GoogleSubject = googleSub,
        };
        await host.Users.CreateAsync(user);
        return user;
    }

    // ---- New user ----

    [Fact]
    public async Task NewUser_CreatedAsRequested_AwaitingApproval_WithGoogleFieldsAndStandardRole()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();

        var result = await Build(host).SignInAsync(Info());

        Assert.Equal(GoogleSignInOutcome.AwaitingApproval, result.Outcome);
        var user = await host.Users.FindByEmailAsync("u@x.test");
        Assert.NotNull(user);
        Assert.Equal(UserStatus.Requested, user!.Status);
        Assert.Equal("google-sub-1", user.GoogleSubject);
        Assert.Equal("https://pic/1.png", user.PictureUrl);
        Assert.Equal("Ada Lovelace", user.FullName);
        Assert.True(user.EmailConfirmed);
        Assert.False(await host.Users.HasPasswordAsync(user)); // Google is the credential
        Assert.Contains(Roles.Standard, await host.Users.GetRolesAsync(user));
    }

    // ---- Link by subject ----

    [Fact]
    public async Task ExistingLinkedActiveUser_SignedIn_AndPictureRefreshed()
    {
        using var host = new IdentityTestHost();
        var user = await Seed(host, "u@x.test", UserStatus.Active, googleSub: "google-sub-1");

        var result = await Build(host).SignInAsync(Info(picture: "https://pic/new.png"));

        Assert.Equal(GoogleSignInOutcome.SignedIn, result.Outcome);
        Assert.Equal(user.Id, result.User!.Id);
        Assert.Equal("https://pic/new.png", (await host.Users.FindByIdAsync(user.Id.ToString()))!.PictureUrl);
    }

    // ---- Auto-link by email ----

    [Fact]
    public async Task ExistingActiveUserByEmail_AutoLinked_AndSignedIn()
    {
        using var host = new IdentityTestHost();
        var user = await Seed(host, "u@x.test", UserStatus.Active);

        var result = await Build(host).SignInAsync(Info(sub: "brand-new-sub"));

        Assert.Equal(GoogleSignInOutcome.SignedIn, result.Outcome);
        Assert.Equal("brand-new-sub", (await host.Users.FindByIdAsync(user.Id.ToString()))!.GoogleSubject);
    }

    [Fact]
    public async Task InvitedUser_SigningInWithGoogle_CompletesOnboarding_ToActive()
    {
        using var host = new IdentityTestHost();
        var user = await Seed(host, "inv@x.test", UserStatus.Invited);

        var result = await Build(host).SignInAsync(Info(email: "inv@x.test", name: "Grace Hopper"));

        Assert.Equal(GoogleSignInOutcome.SignedIn, result.Outcome);
        var updated = await host.Users.FindByIdAsync(user.Id.ToString());
        Assert.Equal(UserStatus.Active, updated!.Status);
        Assert.True(updated.EmailConfirmed);
        Assert.Equal("Grace Hopper", updated.FullName);
    }

    [Fact]
    public async Task ManuallySetName_NotOverwrittenByGoogleName()
    {
        using var host = new IdentityTestHost();
        var user = await Seed(host, "u@x.test", UserStatus.Active, googleSub: "google-sub-1");
        user.FullName = "Chosen Name";
        await host.Users.UpdateAsync(user);

        await Build(host).SignInAsync(Info(name: "Google Name"));

        Assert.Equal("Chosen Name", (await host.Users.FindByIdAsync(user.Id.ToString()))!.FullName);
    }

    // ---- Gating ----

    [Fact]
    public async Task RequestedUser_AwaitingApproval()
    {
        using var host = new IdentityTestHost();
        await Seed(host, "u@x.test", UserStatus.Requested, googleSub: "google-sub-1");

        var result = await Build(host).SignInAsync(Info());

        Assert.Equal(GoogleSignInOutcome.AwaitingApproval, result.Outcome);
    }

    [Fact]
    public async Task DisabledUser_Rejected_AsDisabled()
    {
        using var host = new IdentityTestHost();
        await Seed(host, "u@x.test", UserStatus.Active, enabled: false, googleSub: "google-sub-1");

        var result = await Build(host).SignInAsync(Info());

        Assert.Equal(GoogleSignInOutcome.Disabled, result.Outcome);
    }

    [Fact]
    public async Task UnverifiedEmail_Rejected_AndNoAccountCreated()
    {
        using var host = new IdentityTestHost();
        await host.SeedRolesAsync();

        var result = await Build(host).SignInAsync(Info(verified: false));

        Assert.Equal(GoogleSignInOutcome.Rejected, result.Outcome);
        Assert.Null(await host.Users.FindByEmailAsync("u@x.test"));
    }
}
