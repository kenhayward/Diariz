using Diariz.Api.Services;
using Xunit;

namespace Diariz.Api.Tests;

public class SafeRedirectTests
{
    private static readonly string[] Allowed = ["app.example.com"];

    [Fact]
    public void Within_AllowsAUrlWhoseHostIsInTheAllowlist()
    {
        const string url = "https://app.example.com/login?googleError=failed";
        Assert.Equal(url, SafeRedirect.Within(url, Allowed));
    }

    [Fact]
    public void Within_IsCaseInsensitiveOnTheHost()
    {
        const string url = "https://APP.Example.COM/auth/google/callback";
        Assert.Equal(url, SafeRedirect.Within(url, Allowed));
    }

    [Fact]
    public void Within_RejectsAForeignHost_ReturningTheFallback()
    {
        Assert.Equal("/", SafeRedirect.Within("https://evil.example.net/login", Allowed));
        Assert.Equal("/login", SafeRedirect.Within("https://evil.example.net/x", Allowed, "/login"));
    }

    [Fact]
    public void Within_RejectsANonAbsoluteOrGarbageTarget()
    {
        Assert.Equal("/", SafeRedirect.Within("/login?x=1", Allowed));
        Assert.Equal("/", SafeRedirect.Within("not a url", Allowed));
    }

    [Fact]
    public void Within_RejectsEverythingWhenTheAllowlistIsEmpty()
    {
        Assert.Equal("/", SafeRedirect.Within("https://app.example.com/x", []));
    }
}
