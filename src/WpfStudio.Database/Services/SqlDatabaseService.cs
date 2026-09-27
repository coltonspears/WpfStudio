using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using WpfStudio.Database.Models;
using SqlBatch = WpfStudio.Database.Models.SqlBatch;

namespace WpfStudio.Database.Services;

public sealed class SqlDatabaseService : IDatabaseService
{
    public static string BuildConnectionString(ConnectionProfile profile, string? database = null)
    {
        if (string.IsNullOrWhiteSpace(profile.Server)) throw new ArgumentException("Enter a SQL Server name.");
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = profile.Server.Trim(),
            InitialCatalog = database ?? profile.Database,
            IntegratedSecurity = profile.WindowsAuthentication,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = profile.TrustServerCertificate,
            ApplicationName = "WpfStudio",
            ConnectTimeout = 15,
            PersistSecurityInfo = false,
            MultipleActiveResultSets = false,
        };
        if (!profile.WindowsAuthentication) { builder.UserID = profile.UserName; builder.Password = profile.Password; }
        return builder.ConnectionString;
    }

    public async Task<IReadOnlyList<SchemaItem>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(BuildConnectionString(profile, "master"));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand("SELECT name FROM sys.databases WHERE HAS_DBACCESS(name) = 1 AND state = 0 ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SchemaItem>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new SchemaItem(reader.GetString(0), SchemaNodeKind.Database, reader.GetString(0)));
        return result;
    }

    public async Task<IReadOnlyList<SchemaItem>> GetChildrenAsync(ConnectionProfile profile, SchemaItem parent, CancellationToken cancellationToken)
    {
        if (parent.Kind == SchemaNodeKind.Database)
            return new[] { SchemaNodeKind.Tables, SchemaNodeKind.Views, SchemaNodeKind.Procedures }.Select(kind => new SchemaItem(kind.ToString(), kind, parent.Database)).ToArray();
        if (parent.Kind is SchemaNodeKind.Table or SchemaNodeKind.View)
            return new[] { SchemaNodeKind.Columns, SchemaNodeKind.Keys, SchemaNodeKind.Indexes }.Select(kind => new SchemaItem(kind.ToString(), kind, parent.Database, parent.Schema, parent.ObjectId)).ToArray();

        (string sql, SchemaNodeKind kind) = parent.Kind switch
        {
            SchemaNodeKind.Tables => ("SELECT name, SCHEMA_NAME(schema_id), object_id, CAST(NULL AS nvarchar(max)) FROM sys.tables ORDER BY SCHEMA_NAME(schema_id), name", SchemaNodeKind.Table),
            SchemaNodeKind.Views => ("SELECT name, SCHEMA_NAME(schema_id), object_id, CAST(NULL AS nvarchar(max)) FROM sys.views ORDER BY SCHEMA_NAME(schema_id), name", SchemaNodeKind.View),
            SchemaNodeKind.Procedures => ("SELECT name, SCHEMA_NAME(schema_id), object_id, CAST(NULL AS nvarchar(max)) FROM sys.procedures ORDER BY SCHEMA_NAME(schema_id), name", SchemaNodeKind.Procedure),
            SchemaNodeKind.Columns => ("SELECT c.name, SCHEMA_NAME(o.schema_id), c.object_id, TYPE_NAME(c.user_type_id) + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END FROM sys.columns c JOIN sys.objects o ON c.object_id=o.object_id WHERE c.object_id=@id ORDER BY c.column_id", SchemaNodeKind.Column),
            SchemaNodeKind.Keys => ("SELECT name, SCHEMA_NAME(schema_id), parent_object_id, type_desc FROM sys.objects WHERE parent_object_id=@id AND type IN ('PK','UQ','F') ORDER BY name", SchemaNodeKind.Key),
            SchemaNodeKind.Indexes => ("SELECT name, CAST(NULL AS nvarchar(128)), object_id, type_desc + CASE WHEN is_unique=1 THEN ' UNIQUE' ELSE '' END FROM sys.indexes WHERE object_id=@id AND name IS NOT NULL ORDER BY name", SchemaNodeKind.Index),
            _ => throw new ArgumentException("This schema item has no children.", nameof(parent))
        };
        await using var connection = new SqlConnection(BuildConnectionString(profile, parent.Database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", parent.ObjectId ?? 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SchemaItem>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new SchemaItem(reader.GetString(0), kind, parent.Database, reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return result;
    }

    public async Task<string> GetDefinitionAsync(ConnectionProfile profile, SchemaItem item, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(BuildConnectionString(profile, item.Database));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (item.Kind is SchemaNodeKind.View or SchemaNodeKind.Procedure)
        {
            await using var command = new SqlCommand("SELECT OBJECT_DEFINITION(@id)", connection);
            command.Parameters.AddWithValue("@id", item.ObjectId);
            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is string definition ? definition : "-- Definition unavailable: the module is encrypted or VIEW DEFINITION permission is required.";
        }
        if (item.Kind != SchemaNodeKind.Table) return $"-- {item.Name}: {item.Detail}";
        const string sql = """
            SELECT QUOTENAME(c.name), QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name),
                t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
                dc.definition, cc.definition
            FROM sys.columns c JOIN sys.types t ON c.user_type_id=t.user_type_id
            LEFT JOIN sys.default_constraints dc ON c.default_object_id=dc.object_id
            LEFT JOIN sys.computed_columns cc ON c.object_id=cc.object_id AND c.column_id=cc.column_id
            WHERE c.object_id=@id ORDER BY c.column_id
            """;
        await using var columnsCommand = new SqlCommand(sql, connection);
        columnsCommand.Parameters.AddWithValue("@id", item.ObjectId);
        await using var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string name = reader.GetString(0);
            if (!reader.IsDBNull(9)) { columns.Add($"    {name} AS {reader.GetString(9)}"); continue; }
            string type = reader.GetString(2);
            string size = type switch
            {
                "nvarchar" or "nchar" => $"({(reader.GetInt16(3) == -1 ? "max" : (reader.GetInt16(3) / 2).ToString(CultureInfo.InvariantCulture))})",
                "varchar" or "char" or "varbinary" or "binary" => $"({(reader.GetInt16(3) == -1 ? "max" : reader.GetInt16(3).ToString(CultureInfo.InvariantCulture))})",
                "decimal" or "numeric" => $"({reader.GetByte(4)},{reader.GetByte(5)})",
                "datetime2" or "datetimeoffset" or "time" => $"({reader.GetByte(5)})",
                _ => ""
            };
            columns.Add($"    {name} {reader.GetString(1)}{size}{(reader.GetBoolean(7) ? " IDENTITY" : "")}{(reader.GetBoolean(6) ? " NULL" : " NOT NULL")}{(reader.IsDBNull(8) ? "" : " DEFAULT " + reader.GetString(8))}");
        }
        return $"-- Column definition for {item.QualifiedName}. Browse Keys and Indexes for constraints/indexes.\nCREATE TABLE {item.QualifiedName}\n(\n{string.Join(",\n", columns)}\n);\n";
    }

    public async Task<QueryExecutionResult> ExecuteAsync(ConnectionProfile profile, string sql, int startLine, CancellationToken cancellationToken)
    {
        IReadOnlyList<SqlBatch> batches = SqlBatchParser.Parse(sql, startLine);
        var results = new List<QueryResultSet>();
        var messages = new QueryMessageBuffer();
        var display = new QueryDisplayBudget();
        var clock = Stopwatch.StartNew();
        bool cancelled = false;
        int batchLine = startLine;
        await using var connection = new SqlConnection(BuildConnectionString(profile));
        connection.InfoMessage += (_, args) =>
        {
            foreach (SqlError error in args.Errors) messages.Add(new QueryMessage(error.Message, error.LineNumber > 0 ? batchLine + error.LineNumber - 1 : null));
        };
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            foreach (SqlBatch batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batchLine = batch.StartLine;
                await using var command = new SqlCommand(batch.Text, connection) { CommandTimeout = 0 };
                using var registration = cancellationToken.Register(() => { try { command.Cancel(); } catch (ObjectDisposedException) { } });
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
                do
                {
                    if (reader.FieldCount <= 0) continue;
                    var buffer = display.BeginResult(Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
                    if (buffer is null)
                    {
                        // Keep consuming to preserve later statements, transactions and GO batches.
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { }
                        continue;
                    }
                    bool acceptingRows = true;
                    int rowCount = 0;
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (!acceptingRows || rowCount >= ResultSetBuffer.MaximumRows || buffer.RemainingBytes < buffer.RowOverheadBytes) { buffer.MarkTruncated(); acceptingRows = false; continue; }
                        var values = new string?[reader.FieldCount];
                        bool cellTruncated = false;
                        long rowBytes = 0;
                        for (int column = 0; column < reader.FieldCount; column++)
                        {
                            if (await reader.IsDBNullAsync(column, cancellationToken).ConfigureAwait(false)) continue;
                            int maxChars = (int)Math.Min(65_536, Math.Max(0, (buffer.RemainingValueBytes - rowBytes) / 2));
                            (values[column], bool clipped) = ReadValue(reader, column, maxChars);
                            cellTruncated |= clipped;
                            rowBytes += (values[column]?.Length ?? 0) * 2L;
                        }
                        acceptingRows = buffer.TryAdd(values, cellTruncated);
                        rowCount++;
                    }
                    QueryResultSet result = buffer.Build($"Result {results.Count + 1}");
                    display.Retain(result);
                    results.Add(result);
                } while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
                if (reader.RecordsAffected >= 0) messages.Add(new QueryMessage($"{reader.RecordsAffected:N0} row(s) affected."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { cancelled = true; messages.Add(new QueryMessage("Query cancelled. Batches that completed before cancellation may have committed changes.")); }
        catch (SqlException) when (cancellationToken.IsCancellationRequested) { cancelled = true; messages.Add(new QueryMessage("Query cancelled. Batches that completed before cancellation may have committed changes.")); }
        catch (SqlException exception)
        {
            foreach (SqlError error in exception.Errors)
                messages.Add(new QueryMessage($"SQL {error.Number}: {error.Message}", error.LineNumber > 0 ? batchLine + error.LineNumber - 1 : null, true));
        }
        return new QueryExecutionResult(results, messages.Snapshot(display.Warning), clock.Elapsed, cancelled,
            display.HasOmittedResults || messages.IsTruncated || results.Any(result => result.IsTruncated));
    }

    private static (string Text, bool Truncated) ReadValue(SqlDataReader reader, int column, int maximumCharacters)
    {
        string type = reader.GetDataTypeName(column).ToLowerInvariant();
        if (type == "xml")
        {
            using var xml = reader.GetXmlReader(column);
            return BoundedXmlFormatter.Read(xml, maximumCharacters);
        }
        if (type is "nvarchar" or "varchar" or "nchar" or "char" or "ntext" or "text")
        {
            using TextReader text = reader.GetTextReader(column);
            var chars = new char[maximumCharacters + 1];
            int count = text.ReadBlock(chars, 0, chars.Length);
            return (new string(chars, 0, Math.Min(count, maximumCharacters)), count > maximumCharacters);
        }
        if (type is "binary" or "varbinary" or "image" or "timestamp")
        {
            using Stream stream = reader.GetStream(column);
            int maximumBytes = Math.Max(0, (maximumCharacters - 2) / 2);
            var bytes = new byte[maximumBytes + 1];
            int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            return ((maximumCharacters < 2 ? "" : "0x") + Convert.ToHexString(bytes.AsSpan(0, Math.Min(count, maximumBytes))), count > maximumBytes);
        }
        string value = Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture) ?? "";
        return value.Length > maximumCharacters ? (value[..maximumCharacters], true) : (value, false);
    }
}
