using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DeyeSolar.Web.Tests;

// Authentication is expected to read users, memberships and installation status even when a request is refused.
// These assertions protect the actual solar/settings/rules data and public price cache instead of context allocation.
internal sealed class SolarDataCommandCounter : DbCommandInterceptor
{
    private static readonly Regex Tables = new(@"\b(?:Readings|ExportReadings|TriggerRules|AppSettings|RuleRunLogs|ExportPrices)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private int _commands;
    public int Commands => Volatile.Read(ref _commands);
    private void Count(DbCommand command) { if (Tables.IsMatch(command.CommandText)) Interlocked.Increment(ref _commands); }
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Count(command); return result; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Count(command); return ValueTask.FromResult(result); }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Count(command); return result; }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Count(command); return ValueTask.FromResult(result); }
    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    { Count(command); return result; }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { Count(command); return ValueTask.FromResult(result); }
}
