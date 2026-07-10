using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The wire format of a group's permissions. The API applies JsonStringEnumConverter globally, which
/// would render a [Flags] enum as "ManageRooms, ManageUsers" - and the web does bit arithmetic on this field
/// (`permissions &amp; bit`, `permissions ^ bit`), which silently yields 0 and NaN on a string. So the DTO must
/// carry a number. Controller tests assert on the returned object and never see the JSON, which is exactly how
/// this escaped until it was exercised against a running server.</summary>
public class GroupDtoSerializationTests
{
    private static JsonSerializerOptions Options()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonConfig.Apply(o);
        return o;
    }

    [Fact]
    public void GroupDto_SerializesPermissionsAsANumber()
    {
        var dto = new GroupDto(Guid.Empty, "Platform Administrators", null, null, null,
            (int)(PlatformPermission.ManageRooms | PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform),
            IsSystem: true, MemberIds: []);

        var json = JsonSerializer.Serialize(dto, Options());

        Assert.Contains("\"permissions\":7", json);
        Assert.DoesNotContain("ManageRooms", json);
    }

    [Fact]
    public void GroupInput_DeserializesPermissionsFromANumber()
    {
        const string json = """{"name":"Engineering","description":null,"icon":null,"color":null,"permissions":3}""";

        var input = JsonSerializer.Deserialize<GroupInput>(json, Options())!;

        Assert.Equal(PlatformPermission.ManageRooms | PlatformPermission.ManageUsers, input.Permissions);
    }
}
