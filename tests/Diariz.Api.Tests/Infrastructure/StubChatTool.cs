using System.Text.Json;
using Diariz.Api.Tools;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>A minimal <see cref="IChatTool"/> for tests: records the args it was called with and returns a
/// canned result.</summary>
public sealed class StubChatTool : IChatTool
{
    public StubChatTool(string name, string result = "stub-result")
    {
        Name = name;
        Result = result;
    }

    public string Name { get; }
    public string Title => Name;
    public string Description => $"Stub {Name}";
    public object ParametersSchema => new { type = "object", properties = new { } };
    public string Result { get; set; }
    public int Calls { get; private set; }
    public string? LastArgs { get; private set; }

    public Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        Calls++;
        LastArgs = args.GetRawText();
        return Task.FromResult(Result);
    }
}
