using WpfStudio.Database.Models;

namespace WpfStudio.Database.Services;

/// <summary>Caps an entire execution in addition to each individual result set.</summary>
public sealed class QueryDisplayBudget
{
    public const int MaximumResultSets = 32;
    public const long MaximumBytes = 64 * 1024 * 1024;
    public int RetainedResults { get; private set; }
    public long RetainedBytes { get; private set; }
    public long OmittedResults { get; private set; }
    public bool HasOmittedResults => OmittedResults != 0;
    public ResultSetBuffer? BeginResult(IEnumerable<string> columns)
    {
        if (RetainedResults >= MaximumResultSets || RetainedBytes >= MaximumBytes) { OmittedResults++; return null; }
        try { return new ResultSetBuffer(columns, Math.Min(ResultSetBuffer.MaximumBytes, MaximumBytes - RetainedBytes)); }
        catch (ArgumentException) { OmittedResults++; return null; }
    }
    public void Retain(QueryResultSet result)
    {
        if (result.DisplayBytes > MaximumBytes - RetainedBytes || RetainedResults >= MaximumResultSets)
            throw new InvalidOperationException("The result exceeds the execution display budget.");
        RetainedResults++; RetainedBytes += result.DisplayBytes;
    }
    public string? Warning => HasOmittedResults
        ? $"Display limit reached: {OmittedResults:N0} result set(s) omitted (maximum 32 result sets / 64 MiB estimated display memory). All SQL batches continued executing; only displayed data was limited."
        : null;
}

/// <summary>Bounds PRINT/info/error messages while retaining a visible truncation indicator.</summary>
public sealed class QueryMessageBuffer
{
    public const int MaximumMessages = 1000;
    public const int MaximumBytes = 256 * 1024;
    private const int ReservedSummaryBytes = 2048;
    private const int EstimatedMessageOverhead = 64;
    private readonly object _gate = new();
    private readonly List<QueryMessage> _messages = [];
    private int _bytes;
    private bool _suppressedError;
    public bool IsTruncated { get; private set; }
    public void Add(QueryMessage message)
    {
        lock (_gate)
        {
            int available = MaximumBytes - ReservedSummaryBytes - _bytes - EstimatedMessageOverhead;
            if (_messages.Count >= MaximumMessages - 2 || available < 0)
            {
                IsTruncated = true; _suppressedError |= message.IsError; return;
            }
            if ((long)message.Text.Length * sizeof(char) > available)
            {
                message = message with { Text = message.Text[..(available / sizeof(char))] };
                IsTruncated = true;
            }
            _messages.Add(message);
            _bytes += EstimatedMessageOverhead + message.Text.Length * sizeof(char);
        }
    }
    public IReadOnlyList<QueryMessage> Snapshot(string? displayWarning = null)
    {
        lock (_gate)
        {
            var result = new List<QueryMessage>(_messages);
            if (IsTruncated) result.Add(new QueryMessage("Additional message output was truncated (1,000 messages / 256 KiB limit).", IsError: _suppressedError));
            if (displayWarning is not null) result.Add(new QueryMessage(displayWarning.Length <= 512 ? displayWarning : displayWarning[..512]));
            return result;
        }
    }
}
