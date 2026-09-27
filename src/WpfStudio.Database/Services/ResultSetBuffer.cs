using System.Data;
using System.Globalization;
using System.Text;
using WpfStudio.Database.Models;

namespace WpfStudio.Database.Services;

/// <summary>Caps retained result data before it reaches UI-bound collections.</summary>
public sealed class ResultSetBuffer
{
    public const int MaximumRows = 10_000;
    public const long MaximumBytes = 16 * 1024 * 1024;
    public const int EstimatedRowOverhead = 64;
    public const int EstimatedCellOverhead = 32;
    public const int EstimatedColumnOverhead = 256;
    private readonly DataTable _table = new();
    private readonly long _maximumBytes;
    private long _bytes;
    public bool IsTruncated { get; private set; }
    public long RemainingBytes => Math.Max(0, _maximumBytes - _bytes);
    public long RowOverheadBytes => EstimatedRowOverhead + (long)_table.Columns.Count * EstimatedCellOverhead;
    public long RemainingValueBytes => Math.Max(0, RemainingBytes - RowOverheadBytes);

    public ResultSetBuffer(IEnumerable<string> columns, long maximumBytes = MaximumBytes)
    {
        _maximumBytes = Math.Min(MaximumBytes, maximumBytes);
        foreach (string name in columns)
        {
            string baseName = string.IsNullOrWhiteSpace(name) ? "Column" : name;
            string unique = baseName;
            int suffix = 2;
            while (_table.Columns.Contains(unique)) unique = $"{baseName} ({suffix++})";
            long columnBytes = EstimatedColumnOverhead + (long)unique.Length * sizeof(char);
            if (_bytes + columnBytes > _maximumBytes) throw new ArgumentException("Result metadata exceeds the remaining display budget.", nameof(columns));
            _table.Columns.Add(unique, typeof(string));
            _bytes += columnBytes;
        }
    }
    public bool TryAdd(IReadOnlyList<string?> values, bool cellTruncated = false)
    {
        if (values.Count != _table.Columns.Count) throw new ArgumentException("Result value count does not match the schema.", nameof(values));
        long bytes = RowOverheadBytes + values.Sum(value => value is null ? 0L : (long)value.Length * sizeof(char));
        if (_table.Rows.Count >= MaximumRows || bytes > RemainingBytes) { IsTruncated = true; return false; }
        _table.Rows.Add(values.Select(value => value is null ? DBNull.Value : (object)value).ToArray());
        _bytes += bytes;
        IsTruncated |= cellTruncated;
        return true;
    }
    public void MarkTruncated() => IsTruncated = true;
    public QueryResultSet Build(string name) => new(name, _table, IsTruncated, _bytes);
}

public static class CsvExporter
{
    public static async Task WriteAsync(QueryResultSet result, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new StreamWriter(path, false, new UTF8Encoding(true));
        await stream.WriteLineAsync(string.Join(",", result.Table.Columns.Cast<DataColumn>().Select(column => Escape(column.ColumnName))).AsMemory(), cancellationToken);
        foreach (DataRow row in result.Table.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteLineAsync(string.Join(",", row.ItemArray.Select(value => Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""))).AsMemory(), cancellationToken);
        }
    }
    public static string Escape(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
