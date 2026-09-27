using System.Data;
using System.Text.Json.Serialization;

namespace WpfStudio.Database.Models;

public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Local SQL Server";
    public string Server { get; init; } = "localhost";
    public string Database { get; init; } = "master";
    public bool WindowsAuthentication { get; init; } = true;
    public string UserName { get; init; } = "";
    [JsonIgnore] public string Password { get; init; } = "";
    public bool RememberPassword { get; init; }
    public bool TrustServerCertificate { get; init; }
    public override string ToString() => Name;
}

public enum SchemaNodeKind { Database, Tables, Views, Procedures, Table, View, Procedure, Columns, Keys, Indexes, Column, Key, Index }
public sealed record SchemaItem(string Name, SchemaNodeKind Kind, string Database, string? Schema = null, int? ObjectId = null, string? Detail = null)
{
    public bool CanExpand => Kind is SchemaNodeKind.Database or SchemaNodeKind.Tables or SchemaNodeKind.Views or SchemaNodeKind.Procedures or SchemaNodeKind.Table or SchemaNodeKind.View or SchemaNodeKind.Columns or SchemaNodeKind.Keys or SchemaNodeKind.Indexes;
    public bool HasDefinition => Kind is SchemaNodeKind.Table or SchemaNodeKind.View or SchemaNodeKind.Procedure;
    public string QualifiedName => Schema is null ? Name : $"[{Schema.Replace("]", "]]", StringComparison.Ordinal)}].[{Name.Replace("]", "]]", StringComparison.Ordinal)}]";
}

public sealed record SqlBatch(string Text, int StartLine, int StartOffset);
public sealed record QueryMessage(string Text, int? Line = null, bool IsError = false)
{
    public override string ToString() => Line is null ? Text : $"Line {Line}: {Text}";
}
public sealed record QueryResultSet(string Name, DataTable Table, bool IsTruncated, long DisplayBytes)
{
    public string Summary => $"{Table.Rows.Count:N0} rows" + (IsTruncated ? " — truncated (10,000 rows / 16 MiB; individual values 65,536 characters)" : "");
}
public sealed record QueryExecutionResult(IReadOnlyList<QueryResultSet> Results, IReadOnlyList<QueryMessage> Messages, TimeSpan Elapsed, bool WasCancelled = false, bool DisplayTruncated = false);

public interface IConnectionProfileStore
{
    Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, CancellationToken cancellationToken = default);
}
public interface IDatabaseService
{
    Task<IReadOnlyList<SchemaItem>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task<IReadOnlyList<SchemaItem>> GetChildrenAsync(ConnectionProfile profile, SchemaItem parent, CancellationToken cancellationToken);
    Task<string> GetDefinitionAsync(ConnectionProfile profile, SchemaItem item, CancellationToken cancellationToken);
    Task<QueryExecutionResult> ExecuteAsync(ConnectionProfile profile, string sql, int startLine, CancellationToken cancellationToken);
}
