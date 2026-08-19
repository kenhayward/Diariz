using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Records the SQL text of every reader query the context runs, so a test can assert the SHAPE of what
/// EF emitted rather than only its results. The shape matters on its own for the recording-detail chain: several
/// sibling collections in one non-split query return their cartesian product, which is invisible in the results
/// (EF de-duplicates them back into the right object graph) and only shows up as row count and sort spill.</summary>
public sealed class RecordsSql : DbCommandInterceptor
{
    public List<string> Statements { get; } = [];

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Statements.Add(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken ct = default)
    {
        Statements.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }
}
