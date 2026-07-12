namespace Diariz.Domain.Entities;

/// <summary>Who a formula belongs to. Stored as an int - APPEND-ONLY: never renumber.</summary>
public enum FormulaScope { Personal = 0, Platform = 1, Diariz = 2 }
