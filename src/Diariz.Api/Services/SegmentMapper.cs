using Diariz.Api.Contracts;
using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>The one mapping from stored segments to the <see cref="SegmentDto"/> every prompt builder takes.
///
/// The speaker lookup is the part worth centralising: a segment stores a diarization LABEL
/// (<c>SPEAKER_00</c>) and the display name lives on a separate <see cref="Speaker"/> row, so a caller that
/// forgets the join silently feeds the model raw labels instead of people's names.
///
/// <para>Extracted for <c>LlmTestPromptFactory</c>. The nine other call sites still inline this; converting
/// them is deliberately not part of that change.</para></summary>
public static class SegmentMapper
{
    public static List<SegmentDto> ToDtos(
        IEnumerable<Segment> segments, IReadOnlyDictionary<string, string> speakerNames) =>
        segments
            .OrderBy(s => s.Ordinal)
            .Select(s => new SegmentDto(
                s.Id,
                s.SpeakerLabel,
                speakerNames.TryGetValue(s.SpeakerLabel, out var name) ? name : s.SpeakerLabel,
                s.StartMs, s.EndMs, s.Original, s.Revised))
            .ToList();
}
