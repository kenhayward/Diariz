namespace Diariz.Domain.Entities;

/// <summary>Join: a formula carries zero or more Workflow Signals. Composite key (FormulaId, WorkflowSignalId).</summary>
public class FormulaWorkflowSignal
{
    public Guid FormulaId { get; set; }
    public Formula? Formula { get; set; }
    public Guid WorkflowSignalId { get; set; }
    public WorkflowSignal? WorkflowSignal { get; set; }
}
