namespace Diariz.Domain.Entities;

/// <summary>Which parts of a recording a formula's run may see. [Flags], stored as an int - APPEND-ONLY.</summary>
[Flags]
public enum FormulaContext { None = 0, Transcript = 1, Notes = 2, Attachments = 4, Summary = 8, Minutes = 16, Actions = 32 }
