using System.Text;
using Microsoft.Data.SqlClient;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;

namespace WpfStudio.Database.Tests;

public sealed class BufferAndProfileTests
{
    [Fact]
    public void CapsRowsAndMakesDuplicateColumnNamesUnique()
    {
        var buffer = new ResultSetBuffer(["value", "value", ""]);
        for (int i = 0; i < 10_000; i++) Assert.True(buffer.TryAdd(["a", null, "c"]));
        Assert.False(buffer.TryAdd(["overflow", "b", "c"]));
        var result = buffer.Build("test");
        Assert.True(result.IsTruncated);
        Assert.Equal(10_000, result.Table.Rows.Count);
        Assert.Equal("value (2)", result.Table.Columns[1].ColumnName);
        Assert.Equal(DBNull.Value, result.Table.Rows[0][1]);
    }

    [Fact]
    public void CapsUtf16DisplayBytesWithoutKeepingOversizedRows()
    {
        var buffer = new ResultSetBuffer(["text"]);
        string chunk = new('a', 1024 * 1024);
        for (int i = 0; i < 7; i++) Assert.True(buffer.TryAdd([chunk]));
        Assert.False(buffer.TryAdd([chunk]));
        Assert.InRange(buffer.Build("test").DisplayBytes, 14 * 1024 * 1024, ResultSetBuffer.MaximumBytes);
        Assert.True(buffer.IsTruncated);
    }

    [Fact]
    public void NullHeavyWideRowsConsumeBudget()
    {
        var buffer = new ResultSetBuffer(Enumerable.Range(0, 512).Select(index => "Column" + index));
        var values = new string?[512];
        int added = 0;
        while (buffer.TryAdd(values)) added++;
        Assert.InRange(added, 1, ResultSetBuffer.MaximumRows - 1);
        Assert.True(buffer.IsTruncated);
        Assert.InRange(buffer.Build("wide").DisplayBytes, ResultSetBuffer.MaximumBytes - buffer.RowOverheadBytes, ResultSetBuffer.MaximumBytes);
    }

    [Fact]
    public void AggregateBudgetStopsAt32ResultSets()
    {
        var budget = new QueryDisplayBudget();
        for (int index = 0; index < QueryDisplayBudget.MaximumResultSets; index++)
        {
            var buffer = budget.BeginResult(["value"]);
            Assert.NotNull(buffer);
            Assert.True(buffer.TryAdd(["one row"]));
            budget.Retain(buffer.Build("test"));
        }
        Assert.Null(budget.BeginResult(["value"]));
        Assert.Equal(1, budget.OmittedResults);
        Assert.Contains("All SQL batches continued executing", budget.Warning);
    }

    [Fact]
    public void AggregateBudgetLimitsRetainedResultMemory()
    {
        var budget = new QueryDisplayBudget();
        for (int index = 0; index < 4; index++)
        {
            var buffer = budget.BeginResult(["value"]);
            Assert.NotNull(buffer);
            Assert.True(buffer.TryAdd([new string('x', (int)(buffer.RemainingValueBytes / 2))]));
            budget.Retain(buffer.Build("test"));
        }
        Assert.Equal(QueryDisplayBudget.MaximumBytes, budget.RetainedBytes);
        Assert.Null(budget.BeginResult(["value"]));
    }

    [Fact]
    public void MessagesStayBoundedAndDoNotHideSuppressedErrors()
    {
        var messages = new QueryMessageBuffer();
        for (int index = 0; index < 5000; index++) messages.Add(new QueryMessage(new string('x', 1024)));
        messages.Add(new QueryMessage("Late server error", IsError: true));
        var snapshot = messages.Snapshot("Additional result sets were omitted.");
        Assert.InRange(snapshot.Count, 1, QueryMessageBuffer.MaximumMessages);
        Assert.True(snapshot.Sum(message => message.Text.Length * 2L + 64) <= QueryMessageBuffer.MaximumBytes);
        Assert.True(messages.IsTruncated);
        Assert.Contains(snapshot, message => message.IsError && message.Text.Contains("truncated", StringComparison.Ordinal));
        Assert.Contains(snapshot, message => message.Text.Contains("Additional result sets", StringComparison.Ordinal));
    }

    [Fact]
    public void ShortMessagesHaveAnIndependentCountLimit()
    {
        var messages = new QueryMessageBuffer();
        for (int index = 0; index < 5000; index++) messages.Add(new QueryMessage("x"));
        Assert.True(messages.IsTruncated);
        Assert.InRange(messages.Snapshot().Count, 1, QueryMessageBuffer.MaximumMessages);
    }

    [Fact]
    public async Task CsvPreservesQuotesCommasAndNewlines()
    {
        string path = Path.GetTempFileName();
        try
        {
            var buffer = new ResultSetBuffer(["A", "B"]);
            buffer.TryAdd(["hello,\"world\"\nnext", null]);
            await CsvExporter.WriteAsync(buffer.Build("test"), path);
            Assert.Equal("\"A\",\"B\"" + Environment.NewLine + "\"hello,\"\"world\"\"\nnext\",\"\"" + Environment.NewLine, await File.ReadAllTextAsync(path));
            Assert.True((await File.ReadAllBytesAsync(path)).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ProfileStoreProtectsRememberedPasswordAndOmitsUnrememberedPasswords()
    {
        string path = Path.Combine(Path.GetTempPath(), "wpfstudio-profile-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new ConnectionProfileStore(path);
            var remembered = new ConnectionProfile { Name = "Remember", WindowsAuthentication = false, UserName = "test", Password = "secret-value-555", RememberPassword = true };
            var ephemeral = new ConnectionProfile { Name = "Forget", WindowsAuthentication = false, Password = "temporary-secret" };
            await store.SaveAsync([remembered, ephemeral]);
            string disk = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(remembered.Password, disk);
            Assert.DoesNotContain(ephemeral.Password, disk);
            var loaded = await store.LoadAsync();
            Assert.Equal(remembered.Password, loaded[0].Password);
            Assert.Equal("", loaded[1].Password);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConnectionDefaultsEncryptAndValidateCertificates()
    {
        var connection = new SqlConnectionStringBuilder(SqlDatabaseService.BuildConnectionString(new ConnectionProfile()));
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, connection.Encrypt);
        Assert.False(connection.TrustServerCertificate);
        Assert.True(connection.IntegratedSecurity);
        Assert.False(connection.PersistSecurityInfo);
    }
}
