namespace Diariz.Domain.Entities;

/// <summary>Lifecycle of an async formula run's result row. Append only - values persist as ints.</summary>
public enum FormulaRunStatus
{
    Generating = 0, // job enqueued, LLM not finished
    Ready = 1,      // Text populated
    Failed = 2      // run errored (see Error)
}
