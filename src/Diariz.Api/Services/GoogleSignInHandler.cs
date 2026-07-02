using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>What a Google sign-in resolved to. <see cref="User"/> is set on every outcome except
/// <see cref="GoogleSignInOutcome.Rejected"/>.</summary>
public enum GoogleSignInOutcome
{
    /// <summary>Active, enabled account — issue a token.</summary>
    SignedIn,
    /// <summary>Account exists but is pending admin approval.</summary>
    AwaitingApproval,
    /// <summary>Account exists but an admin has disabled it.</summary>
    Disabled,
    /// <summary>Sign-in refused (e.g. unverified email, or the account couldn't be created).</summary>
    Rejected,
}

public record GoogleSignInResult(GoogleSignInOutcome Outcome, ApplicationUser? User, string? Reason = null);

public interface IGoogleSignInHandler
{
    /// <summary>Link or create the account for a validated Google identity and apply the same access gate
    /// as password login. Never issues a token itself — the caller maps the outcome to a response.</summary>
    Task<GoogleSignInResult> SignInAsync(GoogleUserInfo info);
}

/// <summary>Resolves a validated Google identity to a Diariz account:
/// match by Google subject, else auto-link by (verified) email, else create a pending account.
/// New accounts land as <see cref="UserStatus.Requested"/> (admin approval required); a verified Google
/// sign-in also completes onboarding for an already-approved <see cref="UserStatus.Invited"/> account.
/// Never overrides a manually-set name; always refreshes the profile picture.</summary>
public class GoogleSignInHandler : IGoogleSignInHandler
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IPlatformSettingsService _platform;

    public GoogleSignInHandler(UserManager<ApplicationUser> users, IPlatformSettingsService platform)
    {
        _users = users;
        _platform = platform;
    }

    public async Task<GoogleSignInResult> SignInAsync(GoogleUserInfo info)
    {
        // Google proves ownership only when the email is verified; never link/create otherwise.
        if (!info.EmailVerified || string.IsNullOrWhiteSpace(info.Email))
            return new GoogleSignInResult(GoogleSignInOutcome.Rejected, null,
                "Your Google account's email address is not verified.");

        var name = string.IsNullOrWhiteSpace(info.Name) ? null : info.Name.Trim();

        // 1) Already linked: the stable subject is the primary key.
        var user = await _users.Users.FirstOrDefaultAsync(u => u.GoogleSubject == info.Subject);

        if (user is null)
        {
            // 2) Same (verified) email as an existing account → auto-link.
            user = await _users.FindByEmailAsync(info.Email);
            if (user is not null)
            {
                user.GoogleSubject = info.Subject;
                // An Invited account was already admin-approved; a verified Google sign-in finishes setup.
                if (user.Status == UserStatus.Invited)
                {
                    if (string.IsNullOrWhiteSpace(user.FullName)) user.FullName = name;
                    user.EmailConfirmed = true;
                    user.Status = UserStatus.Active;
                }
            }
            else
            {
                // 3) Brand-new Google user → pending admin approval.
                return await CreatePendingAsync(info, name);
            }
        }

        // Refresh the picture and backfill a missing name from Google (never overwrite a chosen name).
        user.PictureUrl = info.Picture;
        if (string.IsNullOrWhiteSpace(user.FullName) && name is not null) user.FullName = name;
        await _users.UpdateAsync(user);

        if (user.Status == UserStatus.Requested)
            return new GoogleSignInResult(GoogleSignInOutcome.AwaitingApproval, user);
        if (!user.IsEnabled)
            return new GoogleSignInResult(GoogleSignInOutcome.Disabled, user);
        return new GoogleSignInResult(GoogleSignInOutcome.SignedIn, user);
    }

    private async Task<GoogleSignInResult> CreatePendingAsync(GoogleUserInfo info, string? name)
    {
        var user = new ApplicationUser
        {
            UserName = info.Email,
            Email = info.Email,
            GoogleSubject = info.Subject,
            FullName = name,
            PictureUrl = info.Picture,
            Status = UserStatus.Requested,
            IsEnabled = true,
            EmailConfirmed = true, // Google verified it
            QuotaBytes = (await _platform.GetAsync()).StarterQuotaBytes,
        };
        var created = await _users.CreateAsync(user); // no password — Google is the credential
        if (!created.Succeeded)
            return new GoogleSignInResult(GoogleSignInOutcome.Rejected, null,
                "We couldn't create an account for this Google user.");
        await _users.AddToRoleAsync(user, Roles.Standard);
        return new GoogleSignInResult(GoogleSignInOutcome.AwaitingApproval, user);
    }
}
