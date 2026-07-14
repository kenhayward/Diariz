namespace Diariz.Domain.Entities;

/// <summary>A formula a meeting type runs <b>alongside</b> the minutes, in the same pipeline. Its result lands in
/// the recording's Formulas tab like any other run.
///
/// <para>Unlike the <see cref="MeetingType.PrimaryFormulaId"/> (which is RESTRICT - a template with no primary
/// formula produces no minutes), an additional formula going away is not a broken template, so this cascades: the
/// link simply disappears with the formula.</para></summary>
public class MeetingTypeFormula
{
    public Guid Id { get; set; }

    public Guid MeetingTypeId { get; set; }
    public MeetingType? MeetingType { get; set; }

    public Guid FormulaId { get; set; }
    public Formula? Formula { get; set; }

    /// <summary>Run order within the meeting type.</summary>
    public int Ordinal { get; set; }
}
