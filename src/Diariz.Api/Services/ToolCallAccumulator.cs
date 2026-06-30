using System.Text;

namespace Diariz.Api.Services;

/// <summary>Stitches the streamed <see cref="ToolCallFragment"/>s of one assistant turn into complete
/// <see cref="ToolCall"/>s. OpenAI streams a tool call's arguments across many fragments keyed by index;
/// the first fragment for an index carries the id and name, later ones append argument text.</summary>
public sealed class ToolCallAccumulator
{
    private sealed class Builder
    {
        public string? Id;
        public string? Name;
        public readonly StringBuilder Args = new();
    }

    private readonly SortedDictionary<int, Builder> _byIndex = new();

    public void Add(IReadOnlyList<ToolCallFragment>? fragments)
    {
        if (fragments is null) return;
        foreach (var f in fragments)
        {
            if (!_byIndex.TryGetValue(f.Index, out var b))
                _byIndex[f.Index] = b = new Builder();
            if (f.Id is not null) b.Id = f.Id;
            if (f.Name is not null) b.Name = f.Name;
            if (f.Arguments is not null) b.Args.Append(f.Arguments);
        }
    }

    /// <summary>True once any tool-call fragment has been seen.</summary>
    public bool HasCalls => _byIndex.Count > 0;

    /// <summary>The assembled tool calls, in index order. Calls missing a name are dropped (defensive).</summary>
    public IReadOnlyList<ToolCall> Build() =>
        _byIndex.Values
            .Where(b => !string.IsNullOrEmpty(b.Name))
            .Select(b => new ToolCall(b.Id ?? "", b.Name!, b.Args.ToString()))
            .ToList();
}
