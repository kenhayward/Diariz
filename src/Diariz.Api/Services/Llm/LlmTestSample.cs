using Diariz.Api.Services;

namespace Diariz.Api.Services.Llm;

/// <summary>The built-in sample call, used by the call groups that have no single real prompt to run:
/// ModelBase (a parameter scope nothing is dispatched to), MinutesAndFormulas (a multi-call pipeline),
/// Translation (needs a target language) and Chat (needs a question).
///
/// <para>The transcript is fixed and tiny on purpose - short enough that the reply time is dominated by the
/// model rather than the prompt, and self-contained so this test never touches a user's data.</para></summary>
public static class LlmTestSample
{
    /// <summary>Caps what a misbehaving endpoint can make the API hold in memory. The sample's reply is one
    /// sentence; anything past this is a server ignoring the prompt, not an answer worth showing.</summary>
    public const int MaxResponseChars = 8000;

    public static readonly IReadOnlyList<ChatMessage> Messages =
    [
        new("system", "You are a meeting assistant. Answer in one short sentence, with no preamble."),
        new("user",
            "Summarise this meeting excerpt in one sentence.\n\n" +
            "Priya: The Q3 forecast needs revising before Friday.\n" +
            "Sam: Agreed. I will take the vendor review.\n" +
            "Priya: Thanks - let us confirm the numbers on Thursday."),
    ];
}
