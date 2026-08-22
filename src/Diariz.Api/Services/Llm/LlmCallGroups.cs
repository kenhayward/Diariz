using Diariz.Domain.Entities;

namespace Diariz.Api.Services.Llm;

public static class LlmCallGroups
{
    /// <summary>The parameter group whose settings apply to a call of this kind, or null for kinds that send
    /// no sampling parameters at all - embeddings post <c>{model, input}</c> and dictation posts multipart
    /// audio, so there is nothing for a temperature to mean.
    ///
    /// Every kind is listed explicitly and the default arm THROWS rather than returning a group. C# does
    /// not enforce exhaustiveness over an enum, so a member added later cannot be caught at compile time -
    /// but a silent fallthrough to Chat would quietly apply the wrong model and parameters to a new call
    /// type, which is far worse than a loud failure. `LlmCallGroupsTests.Every_kind_is_accounted_for`
    /// enumerates the enum and is what actually catches an undecided member.</summary>
    public static LlmCallGroup? GroupFor(LlmCallKind kind) => kind switch
    {
        LlmCallKind.Tags => LlmCallGroup.Tags,
        LlmCallKind.ExtractActions => LlmCallGroup.Actions,
        LlmCallKind.Summarize or LlmCallKind.SectionSummary => LlmCallGroup.Summaries,
        LlmCallKind.MeetingMinutes or LlmCallKind.SectionMinutes or LlmCallKind.MeetingTypeMinutes
            or LlmCallKind.FormulaRun => LlmCallGroup.MinutesAndFormulas,
        LlmCallKind.Translation => LlmCallGroup.Translation,
        LlmCallKind.ChatMessage or LlmCallKind.ChatTitle => LlmCallGroup.Chat,
        LlmCallKind.ScreenshotOcr => LlmCallGroup.Ocr,
        // AdminTest sends a full set of sampling parameters, but the admin chooses which group's they are
        // while editing them, so there is nothing for this to decide. It never reaches the resolver.
        LlmCallKind.Embedding or LlmCallKind.SearchQuery or LlmCallKind.Dictation
            or LlmCallKind.AdminTest or LlmCallKind.Unknown => null,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "no parameter group has been decided for this call kind"),
    };
}
