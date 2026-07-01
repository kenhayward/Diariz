namespace Diariz.Api.Services;

/// <summary>Supplies editable LLM prompt templates from the <c>prompts/</c> directory, so the summarise,
/// action-extraction, and meeting-minutes prompts can be changed (or volume-mounted) without a rebuild.
/// Templates are read on each call so edits apply without an API restart; a missing/unreadable/empty file
/// falls back to the supplied built-in default.</summary>
public interface IPromptTemplateProvider
{
    /// <summary>Read <c>prompts/&lt;name&gt;.md</c>, falling back to <paramref name="fallback"/> when the file
    /// is missing, unreadable, or empty.</summary>
    string Get(string name, string fallback);
}

public class FilePromptTemplateProvider : IPromptTemplateProvider
{
    private readonly string _dir;

    public FilePromptTemplateProvider(string promptsDir) => _dir = promptsDir;

    public string Get(string name, string fallback)
    {
        try
        {
            var path = Path.Combine(_dir, name + ".md");
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch (IOException)
        {
            // File busy/unreadable (e.g. mid-edit) — fall back to the built-in default.
        }
        return fallback;
    }
}
