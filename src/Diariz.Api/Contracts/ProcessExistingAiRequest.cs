namespace Diariz.Api.Contracts;

/// <summary>Choose independent post-transcription AI operations; never re-transcribe audio.</summary>
public record ProcessExistingAiRequest(
    bool Summary = false,
    bool Minutes = false,
    bool Actions = false,
    bool Tags = false,
    bool Embeddings = false,
    bool OverwriteManualEdits = false);
