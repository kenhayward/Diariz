using Diariz.Api.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace Diariz.Api.Tests;

public class LlmUsageControllerTests
{
    [Fact]
    public void Endpoints_RequireManagePlatform_NotMerelyAnAdmin()
    {
        // ReadAdminSettings is held by Administrators too. This log carries every user's activity,
        // so it is Platform Administrator only.
        var attr = typeof(LlmUsageController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Single();
        Assert.Equal("ManagePlatform", attr.Policy);
    }
}
