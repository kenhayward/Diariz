using System.Text.Json;

namespace Diariz.Api.Services;

/// <summary>The structured minutes template carried by a meeting type: an ordered list of H1/H2
/// <see cref="TemplateSection"/>s. Stored as one JSON blob on <c>MeetingType.ContentJson</c> (so the whole
/// template saves atomically). Pure - (de)serialisation + validation live here so they can be unit-tested and
/// reused by both the CRUD controller and the minutes generator.</summary>
public record MeetingTypeContent(IReadOnlyList<TemplateSection> Sections)
{
    /// <summary>The recording values a <c>field</c> block may substitute. <c>action_items</c> renders the
    /// recording's canonical Action Items table (see <see cref="MeetingMinutesPrompt.RenderActionItems"/>).</summary>
    public static readonly IReadOnlyList<string> Fields =
        ["date", "time", "title", "attendees", "duration", "action_items"];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static MeetingTypeContent Empty { get; } = new([]);

    /// <summary>Parse the stored JSON. A null/blank/garbage value yields <see cref="Empty"/> rather than throwing,
    /// so a missing or corrupt template degrades to "no sections" instead of failing the whole request.</summary>
    public static MeetingTypeContent Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            var parsed = JsonSerializer.Deserialize<MeetingTypeContent>(json, JsonOpts);
            return parsed is null ? Empty : parsed with { Sections = parsed.Sections ?? [] };
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>The prompt blocks in document order, each paired with its owning section (for context). The order
    /// matches the composer's walk, so a per-block resolver can be fed outputs positionally.</summary>
    public IEnumerable<(TemplateSection Section, TemplateBlock Block)> PromptBlocks() =>
        (Sections ?? []).SelectMany(s =>
            (s.Blocks ?? []).Where(b => b.Kind == TemplateBlock.Prompt).Select(b => (s, b)));

    /// <summary>Validate the template shape: heading levels 1-2, non-empty section titles, known block kinds, a
    /// known substitution field, and non-empty text on boilerplate/prompt blocks. Returns the first problem found.</summary>
    public (bool Ok, string? Error) Validate()
    {
        if (Sections is null) return (false, "Content has no sections.");
        foreach (var section in Sections)
        {
            if (section.Level is not (1 or 2))
                return (false, $"Section heading level must be 1 or 2 (was {section.Level}).");
            if (string.IsNullOrWhiteSpace(section.Title))
                return (false, "Every section needs a title.");
            foreach (var block in section.Blocks ?? [])
            {
                switch (block.Kind)
                {
                    case TemplateBlock.Boilerplate:
                    case TemplateBlock.Prompt:
                        if (string.IsNullOrWhiteSpace(block.Text))
                            return (false, $"A '{block.Kind}' block needs text.");
                        break;
                    case TemplateBlock.FieldKind:
                        if (block.Field is null || !Fields.Contains(block.Field))
                            return (false, $"Unknown substitution field '{block.Field}'.");
                        break;
                    default:
                        return (false, $"Unknown block kind '{block.Kind}'.");
                }
            }
        }
        return (true, null);
    }
}

/// <summary>One section of a template: a heading (<paramref name="Level"/> 1 = H1, 2 = H2, <paramref name="Title"/>)
/// followed by an ordered list of content <see cref="TemplateBlock"/>s.</summary>
public record TemplateSection(int Level, string Title, IReadOnlyList<TemplateBlock> Blocks);

/// <summary>One content block within a section. <paramref name="Kind"/> is one of <see cref="Boilerplate"/> (emit
/// <paramref name="Text"/> verbatim), <see cref="Field"/> (substitute the recording value named by
/// <see cref="TemplateBlock.Field"/>), or <see cref="Prompt"/> (run <paramref name="Text"/> as a model instruction).</summary>
public record TemplateBlock(string Kind, string? Text = null, string? Field = null)
{
    public const string Boilerplate = "boilerplate";
    public const string FieldKind = "field";
    public const string Prompt = "prompt";
}
