using System.Diagnostics;
using System.Text.Json;
using WpfStudio.Database.Models;
using WpfStudio.Database.Services;

namespace WpfStudio.Database.Tests;

public sealed class LocalDbFactAttribute : FactAttribute
{
    internal static string? Tool => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server"))
        ? Directory.GetDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server")).Select(path => Path.Combine(path, "Tools", "Binn", "SqlLocalDB.exe")).FirstOrDefault(File.Exists)
        : null;
    public LocalDbFactAttribute() { if (Tool is null) Skip = "SQL Server LocalDB is not installed."; }
}

public sealed class LocalDbIntegrationTests
{
    [LocalDbFact]
    public async Task RealSqlServerHandlesSessionsResultsCapsErrorsAndCancellation()
    {
        string instance = "WpfStudioTests" + Guid.NewGuid().ToString("N");
        string tool = LocalDbFactAttribute.Tool!;
        bool created = false;
        try
        {
            await Run(tool, "create", instance, "-s");
            created = true;
            var profile = new ConnectionProfile { Server = @"(localdb)\" + instance, TrustServerCertificate = true };
            string? performanceReport = Environment.GetEnvironmentVariable("WPFSTUDIO_SQL_PERFORMANCE_REPORT");
            long beforeQuery = performanceReport is null ? 0 : PrivateBytesAfterCollection();
            var service = new SqlDatabaseService();
            var execution = await service.ExecuteAsync(profile, """
                CREATE TABLE #test(Id int);
                BEGIN TRANSACTION;
                INSERT INTO #test VALUES (17);
                GO
                SELECT Id FROM #test;
                ROLLBACK;
                SELECT COUNT(*) AS RemainingRows FROM #test;
                SELECT CAST('<root>hello</root>' AS xml) AS XmlValue, CONVERT(varbinary(max), 'hello') AS BinaryValue;
                PRINT 'Finished';
                """, 1, CancellationToken.None);
            if (performanceReport is not null)
            {
                long afterQuery = PrivateBytesAfterCollection();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(performanceReport))!);
                await File.WriteAllTextAsync(performanceReport, JsonSerializer.Serialize(new
                {
                    measuredAtUtc = DateTimeOffset.UtcNow,
                    measurement = "SQL feature: test harness before and after first SqlClient query",
                    runtime = Environment.Version.ToString(),
                    processPrivateBytesBefore = beforeQuery,
                    processPrivateBytesAfter = afterQuery,
                    incrementalPrivateBytes = afterQuery - beforeQuery,
                    displayedResultSets = execution.Results.Count,
                    notes = "Informational process-private-memory delta after full GC. Includes .NET/xUnit test harness and SqlClient connection pooling. Excludes SQL Server LocalDB process, IDE UI, SQL pane rendering, and workspace worker. Three small result sets retained; large result overhead is separately bounded and tested. Run this test alone to avoid concurrent test allocations."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            Assert.DoesNotContain(execution.Messages, message => message.IsError);
            Assert.Equal(3, execution.Results.Count);
            Assert.Equal("17", execution.Results[0].Table.Rows[0][0]);
            Assert.Equal("0", execution.Results[1].Table.Rows[0][0]);
            Assert.Equal("<root>hello</root>", execution.Results[2].Table.Rows[0][0]);
            Assert.Contains(execution.Messages, message => message.Text == "Finished");

            var capped = await service.ExecuteAsync(profile, "SELECT TOP (10001) ROW_NUMBER() OVER (ORDER BY a.object_id) AS N FROM sys.all_objects a CROSS JOIN sys.all_objects b; SELECT REPLICATE(CAST(N'界' AS nvarchar(max)), 70000) AS LongValue;", 1, CancellationToken.None);
            Assert.DoesNotContain(capped.Messages, message => message.IsError);
            Assert.Equal(10_000, capped.Results[0].Table.Rows.Count);
            Assert.True(capped.Results[0].IsTruncated);
            Assert.True(capped.Results[1].IsTruncated);
            Assert.Equal(65_536, ((string)capped.Results[1].Table.Rows[0][0]).Length);

            var byteCap = await service.ExecuteAsync(profile, "SELECT TOP (300) REPLICATE(CAST(N'界' AS nvarchar(max)), 32768) AS TextValue FROM sys.all_objects; SELECT CAST('<root>' + REPLICATE(CAST('a' AS varchar(max)), 100000) + '</root>' AS xml) AS LongXml;", 1, CancellationToken.None);
            Assert.DoesNotContain(byteCap.Messages, message => message.IsError);
            Assert.Equal(256, byteCap.Results[0].Table.Rows.Count);
            Assert.Equal(ResultSetBuffer.MaximumBytes, byteCap.Results[0].DisplayBytes);
            Assert.True(byteCap.Results[0].IsTruncated);
            Assert.True(byteCap.Results[1].IsTruncated);
            Assert.Equal(65_536, ((string)byteCap.Results[1].Table.Rows[0][0]).Length);

            var setsCap = await service.ExecuteAsync(profile, "CREATE TABLE #later(Id int); DECLARE @n int=0; WHILE @n < 40 BEGIN SELECT @n AS Value; SET @n += 1; END;\nGO\nINSERT INTO #later VALUES (1); IF (SELECT COUNT(*) FROM #later) <> 1 THROW 50000, 'Later batch did not execute', 1; PRINT 'Later batch executed';", 1, CancellationToken.None);
            Assert.DoesNotContain(setsCap.Messages, message => message.IsError);
            Assert.Equal(QueryDisplayBudget.MaximumResultSets, setsCap.Results.Count);
            Assert.True(setsCap.DisplayTruncated);
            Assert.Contains(setsCap.Messages, message => message.Text.Contains("8 result set(s) omitted", StringComparison.Ordinal));
            Assert.Contains(setsCap.Messages, message => message.Text == "Later batch executed");

            string memorySql = string.Concat(Enumerable.Repeat("SELECT TOP (300) REPLICATE(CAST(N'界' AS nvarchar(max)), 32768) AS TextValue FROM sys.all_objects;", 5));
            var aggregateCap = await service.ExecuteAsync(profile, memorySql + "PRINT 'After aggregate limit';", 1, CancellationToken.None);
            Assert.DoesNotContain(aggregateCap.Messages, message => message.IsError);
            Assert.Equal(4, aggregateCap.Results.Count);
            Assert.Equal(QueryDisplayBudget.MaximumBytes, aggregateCap.Results.Sum(result => result.DisplayBytes));
            Assert.True(aggregateCap.DisplayTruncated);
            Assert.Contains(aggregateCap.Messages, message => message.Text == "After aggregate limit");
            Assert.Contains(aggregateCap.Messages, message => message.Text.Contains("1 result set(s) omitted", StringComparison.Ordinal));

            var noisy = await service.ExecuteAsync(profile, "DECLARE @n int=0; WHILE @n < 1200 BEGIN PRINT 'Noise'; SET @n+=1; END; SELECT 73 AS FinalValue;", 1, CancellationToken.None);
            Assert.Equal("73", noisy.Results.Single().Table.Rows[0][0]);
            Assert.InRange(noisy.Messages.Count, 1, QueryMessageBuffer.MaximumMessages);
            Assert.True(noisy.DisplayTruncated);

            var error = await service.ExecuteAsync(profile, "SELECT 1;\nGO\nSELECT NoSuchColumn;", 10, CancellationToken.None);
            Assert.Contains(error.Messages, message => message.IsError && message.Line == 12);
            Assert.Single(error.Results);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var cancelled = await service.ExecuteAsync(profile, "WAITFOR DELAY '00:00:10'; SELECT 1;", 1, cancellation.Token);
            Assert.True(cancelled.WasCancelled);
            Assert.True(cancelled.Elapsed < TimeSpan.FromSeconds(5));

            var databases = await service.GetDatabasesAsync(profile, CancellationToken.None);
            Assert.Contains(databases, item => item.Name == "master");
            var categories = await service.GetChildrenAsync(profile, databases.Single(item => item.Name == "master"), CancellationToken.None);
            Assert.Equal(3, categories.Count);
            var auth = await service.ExecuteAsync(profile with { WindowsAuthentication = false, UserName = "no_such_wpfstudio_user", Password = "wrong" }, "SELECT 1", 1, CancellationToken.None);
            Assert.Contains(auth.Messages, message => message.IsError);
        }
        finally
        {
            if (created)
            {
                Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
                await Run(tool, "stop", instance, "-k");
                await Run(tool, "delete", instance);
            }
        }
    }

    private static long PrivateBytesAfterCollection()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private static async Task Run(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, (await output) + (await error));
    }
}
