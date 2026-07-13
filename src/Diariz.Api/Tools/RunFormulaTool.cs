using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>Tool: run a saved Formula (a saved prompt + a chosen context) over a recording and save the
/// Markdown result to it. Not read-only (persists a <see cref="FormulaResult"/>, like <c>send_email</c>).
/// Reuses <see cref="RecordingArg"/> so it works over MCP, where <c>SelectedRecordingIds</c> is empty and the
/// client must pass an explicit <c>recording_id</c>.</summary>
public sealed class RunFormulaTool : IChatTool
{
    private readonly DiarizDbContext _db;
    private readonly IFormulaRunner _runner;

    public RunFormulaTool(DiarizDbContext db, IFormulaRunner runner)
    {
        _db = db;
        _runner = runner;
    }

    public string Name => "run_formula";
    public string Title => "Run formula";
    public string Description =>
        "Run a saved formula (a saved prompt + a chosen context) over a recording and save the Markdown " +
        "result to it. Give the formula 'name' and the recording (by 'recording_id', 'recording' name, or " +
        "the current selection).";

    public bool ReadOnly => false;

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            formula = new { type = "string", description = "The formula name to run (substring match)." },
            recording = RecordingArg.RecordingProperty(),
            recording_id = RecordingArg.RecordingIdProperty(),
        },
        required = new[] { "formula" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var formulaName = ToolFormat.ReadString(args, "formula");
        if (formulaName is null) return "Specify a formula by 'name'.";

        var lower = formulaName.ToLower();
        var visible = _db.Formulas.Where(f =>
            (f.Scope == FormulaScope.Personal && f.OwnerUserId == ctx.UserId) ||
            (f.Scope != FormulaScope.Personal && f.Enabled));
        var matches = await visible.Where(f => f.Name.ToLower().Contains(lower)).ToListAsync(ct);

        Formula formula;
        if (matches.Count == 0)
        {
            return $"No formula matching \"{formulaName}\" was found. Personal formulas you own and enabled " +
                "shared formulas are available.";
        }
        else if (matches.Count == 1)
        {
            formula = matches[0];
        }
        else
        {
            // Prefer an exact (case-insensitive) name match when several substrings match.
            var exact = matches.FirstOrDefault(f => string.Equals(f.Name, formulaName, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                formula = exact;
            }
            else
            {
                var names = string.Join(", ", matches.Select(f => $"\"{f.Name}\""));
                return $"Multiple formulas match \"{formulaName}\": {names}. Be more specific.";
            }
        }

        var (q, err) = RecordingArg.Resolve(_db, ctx, args);
        if (err != null) return err;

        // If multiple recordings match a name, take the first — acceptable for chat-tool resolution.
        var rec = await q!.Select(r => new { r.Id, Title = r.Name ?? r.Title }).FirstOrDefaultAsync(ct);
        if (rec is null) return "No matching recording was found.";

        try
        {
            var result = await _runner.RunAsync(ctx.UserId, rec.Id, formula.Id, ct);
            return $"Ran the \"{formula.Name}\" formula on {ToolFormat.RecordingLink(rec.Id, rec.Title)} and " +
                $"saved the result.\n\n{result.Text.Trim()}";
        }
        catch (FormulaNotConfiguredException)
        {
            return "Formulas need an AI endpoint. Set one in Settings before running a formula.";
        }
        catch (FormulaAccessException)
        {
            return "You can't run that formula on this recording.";
        }
        catch (FormulaNotFoundException)
        {
            return "That formula or recording could not be found.";
        }
    }
}
