using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WpfStudio.Contracts;
using WpfStudio.Workspace;

return await Benchmark.RunAsync(args);

internal static class Benchmark
{
    private const string Prefix = "WpfStudio-XamlBench-";
    private const string Models = "namespace Bench; public sealed class Customer { public string Name { get; set; } = \"Ada\"; } public sealed class Order { public string Title { get; set; } = \"Draft\"; }";
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private static readonly string Consumer = $"<UserControl {Ns}><UserControl.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"Resources/First.xaml\" /></ResourceDictionary.MergedDictionaries></ResourceDictionary></UserControl.Resources><TextBlock Text=\"{{Binding Source={{StaticResource Current}}, Path=Name}}\" /></UserControl>";
    private static readonly int Position = Consumer.IndexOf("Path=Name", StringComparison.Ordinal) + 5;

    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        try
        {
            if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Usage); return 0; }
            options = Options.Parse(args);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        { Console.Error.WriteLine(exception.Message + Environment.NewLine + Usage); return 2; }

        var report = new Report(DateTimeOffset.UtcNow, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount, options.Host ?? "WorkspaceClient default discovery", options.Iterations,
            "Synthetic SDK WPF projects; sequential named-pipe calls including serialization. First call follows restore/workspace load, not a cold machine. Warm OS/SDK caches and hardware affect results. No editor, rendering, typing latency, concurrency or physical-input measurement.");
        if (options.Host is { } selectedHost)
            foreach (var file in new[] { selectedHost, Path.Combine(Path.GetDirectoryName(selectedHost)!, "WpfStudio.Workspace.dll"),
                         Path.Combine(Path.GetDirectoryName(selectedHost)!, "WpfStudio.Contracts.dll") }.Distinct(StringComparer.OrdinalIgnoreCase))
                if (File.Exists(file))
                {
                    using var input = File.OpenRead(file);
                    report.WorkerFileHashes[Path.GetFileName(file)] = Convert.ToHexString(SHA256.HashData(input));
                }
        using var interrupt = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; interrupt.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            foreach (int size in options.Sizes)
            {
                if (interrupt.IsCancellationRequested) break;
                Console.WriteLine($"XAML files: {size}; warm iterations: {options.Iterations}");
                var fixture = new FixtureResult(size);
                report.Fixtures.Add(fixture);
                await MeasureFixtureAsync(fixture, options, interrupt.Token);
                Console.WriteLine($"  {fixture.Samples.Count} observations, {fixture.Samples.Count(sample => !sample.Correct)} incorrect; {fixture.Error ?? "completed"}");
            }
            report.Cancelled = interrupt.IsCancellationRequested;
            report.FinishedUtc = DateTimeOffset.UtcNow;
            string output = Path.GetFullPath(options.Output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(output);
            return report.Cancelled || report.Fixtures.Any(fixture => fixture.Error is not null || fixture.Samples.Any(sample => !sample.Correct)) ? 1 : 0;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task MeasureFixtureAsync(FixtureResult result, Options options, CancellationToken interrupt)
    {
        string directory = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(interrupt);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        await using var client = new WorkspaceClient(options.Host);
        try
        {
            string project = Path.Combine(directory, "Bench.csproj");
            string view = Path.Combine(directory, "View.xaml");
            string data = Path.Combine(directory, "Resources", "Nested", "Data.xaml");
            await WriteAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable></PropertyGroup></Project>", token);
            await WriteAsync(Path.Combine(directory, "Models.cs"), Models, token);
            await WriteAsync(view, Consumer, token);
            await WriteAsync(Path.Combine(directory, "Resources", "First.xaml"), Merge("Nested/Second.xaml"), token);
            await WriteAsync(Path.Combine(directory, "Resources", "Nested", "Second.xaml"), Merge("Data.xaml"), token);
            await WriteAsync(data, Dictionary("Customer"), token);
            for (int index = 0; index < result.RequestedXamlFiles - 4; index++)
            {
                string text = UnrelatedView(index);
                result.UnrelatedMinimumBytes = Math.Min(result.UnrelatedMinimumBytes ?? int.MaxValue, Encoding.UTF8.GetByteCount(text));
                result.UnrelatedMaximumBytes = Math.Max(result.UnrelatedMaximumBytes ?? 0, Encoding.UTF8.GetByteCount(text));
                await WriteAsync(Path.Combine(directory, "Views", $"View{index:D4}.xaml"), text, token);
            }
            var watch = Stopwatch.StartNew();
            var restore = await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"), cancellationToken: token);
            result.RestoreMilliseconds = watch.Elapsed.TotalMilliseconds;
            if (restore.ExitCode != 0 || restore.Cancelled) throw new InvalidOperationException($"Restore failed (exit {restore.ExitCode}, cancelled {restore.Cancelled}). " + string.Join(" ", restore.Diagnostics.Take(8).Select(item => item.Message)));
            watch.Restart();
            var workspace = await client.LoadAsync(new(project, "Release"), token);
            result.LoadMilliseconds = watch.Elapsed.TotalMilliseconds;
            result.SdkVersion = workspace.SdkVersion;
            result.WorkspaceIssues = workspace.Issues;
            result.EvaluatedXamlFiles = workspace.Projects.SelectMany(item => item.Files)
                .Count(file => !file.IsGenerated && Path.GetExtension(file.Path).Equals(".xaml", StringComparison.OrdinalIgnoreCase));
            if (result.EvaluatedXamlFiles != result.RequestedXamlFiles)
                throw new InvalidOperationException($"Expected {result.RequestedXamlFiles} evaluated XAML files, received {result.EvaluatedXamlFiles}.");
            if (workspace.Issues.Any(issue => issue.Severity == "Error"))
                throw new InvalidOperationException("Workspace loading reported an error. See workspaceIssues.");

            IReadOnlyList<XamlDocumentOverlay> overlays = [new(view, Consumer, 1)];
            await CompleteAsync("first-completion", 0, "Name", overlays);
            await AnalyzeAsync("first-analysis", 0, false, overlays);
            for (int index = 0; index < options.Iterations; index++)
            {
                await CompleteAsync("warm-completion", index, "Name", overlays);
                await AnalyzeAsync("warm-analysis", index, false, overlays);
            }
            for (int index = 0; index < options.Iterations; index++)
            {
                // Only dependency bytes/version change. The consumer remains
                // byte-identical at version 1 and the dictionary is never saved.
                bool order = index % 2 == 0;
                overlays = [new(view, Consumer, 1), new(data, Dictionary(order ? "Order" : "Customer"), index + 2)];
                var cycle = Stopwatch.StartNew();
                var completion = await CompleteAsync("dependency-update-completion", index, order ? "Title" : "Name", overlays);
                var analysis = await AnalyzeAsync("dependency-update-analysis", index, order, overlays);
                result.Samples.Add(new("dependency-update-cycle", index, cycle.Elapsed.TotalMilliseconds,
                    completion.Correct && analysis.Correct, order ? "Title completion and missing Name diagnostic" : "Name completion and no binding error",
                    completion.Correct && analysis.Correct ? null : "See completion/analysis observations for this iteration."));
            }
            if (await File.ReadAllTextAsync(data, token) != Dictionary("Customer"))
                throw new InvalidOperationException("The dependency-only overlay unexpectedly changed disk text.");

            async Task<Sample> CompleteAsync(string phase, int index, string expected, IReadOnlyList<XamlDocumentOverlay> current)
            {
                var timer = Stopwatch.StartNew();
                var answer = await client.GetXamlCompletionsAsync(new(view, Consumer, Position, 1, project, current), token);
                double milliseconds = timer.Elapsed.TotalMilliseconds;
                string forbidden = expected == "Name" ? "Title" : "Name";
                bool correct = answer.Available && answer.Status is null && answer.Completion is { Version: 1 } completion
                    && completion.Items.Any(item => item.DisplayText == expected)
                    && !completion.Items.Any(item => item.DisplayText == forbidden);
                var sample = new Sample(phase, index, milliseconds, correct, expected + " present; " + forbidden + " absent",
                    $"Available={answer.Available}; Version={answer.Completion?.Version}; Items={string.Join(",", answer.Completion?.Items.Select(item => item.DisplayText).Take(20) ?? [])}; Status={answer.Status}");
                result.Samples.Add(sample);
                return sample;
            }
            async Task<Sample> AnalyzeAsync(string phase, int index, bool missing, IReadOnlyList<XamlDocumentOverlay> current)
            {
                var timer = Stopwatch.StartNew();
                var answer = await client.AnalyzeXamlAsync(new(view, Consumer, 1, project, current), token);
                double milliseconds = timer.Elapsed.TotalMilliseconds;
                var bindingErrors = answer.Diagnostics.Where(item => item.Id == "XAMLBIND001").ToArray();
                bool correct = answer.Accepted && answer.Status is null && answer.Version == 1 && (missing
                    ? bindingErrors.Length == 1 && bindingErrors[0].Start == Position && bindingErrors[0].Length == 4
                    : answer.Diagnostics.Count == 0);
                var sample = new Sample(phase, index, milliseconds, correct, missing ? "Missing Name at its exact source span" : "No diagnostics",
                    $"Accepted={answer.Accepted}; Version={answer.Version}; Diagnostics={string.Join(" | ", answer.Diagnostics.Take(8).Select(item => item.Id + ": " + item.Message))}; Status={answer.Status}");
                result.Samples.Add(sample);
                return sample;
            }
        }
        catch (Exception exception) { result.Error = exception.GetType().Name + ": " + exception.Message; }
        finally
        {
            await client.DisposeAsync();
            try { await DeleteFixtureAsync(directory); }
            catch (Exception exception) { result.Error = (result.Error is null ? "" : result.Error + " | ") + "Cleanup failed: " + exception.Message; }
            result.Distributions = result.Samples.GroupBy(sample => sample.Phase).ToDictionary(group => group.Key, group => Distribution.Create(group.ToArray()));
        }
    }

    private static string Merge(string source) => $"<ResourceDictionary {Ns}><ResourceDictionary.MergedDictionaries><ResourceDictionary Source=\"{source}\" /></ResourceDictionary.MergedDictionaries></ResourceDictionary>";
    private static string Dictionary(string type) => $"<ResourceDictionary {Ns} xmlns:m=\"clr-namespace:Bench\"><m:{type} x:Key=\"Current\" /></ResourceDictionary>";
    private static string UnrelatedView(int index)
    {
        var text = new StringBuilder($"<UserControl {Ns}><StackPanel Margin=\"16\"><TextBlock Text=\"Catalog {index:D4}\" FontSize=\"24\" FontWeight=\"SemiBold\" Margin=\"0,0,0,16\" />");
        for (int row = 0; text.Length < 5900; row++)
            text.Append($"<Border BorderBrush=\"#D3D8E0\" BorderThickness=\"1\" CornerRadius=\"4\" Padding=\"12\" Margin=\"0,0,0,8\"><StackPanel><TextBlock Text=\"Item {row:D2}\" FontWeight=\"SemiBold\"/><TextBlock Text=\"A representative catalog description with wrapping and a short detail line.\" TextWrapping=\"Wrap\" Margin=\"0,4,0,0\"/></StackPanel></Border>\n");
        return text.Append("</StackPanel></UserControl>").ToString();
    }
    private static async Task WriteAsync(string path, string text, CancellationToken token)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), token); }
    private static async Task DeleteFixtureAsync(string directory)
    {
        string path = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a directory outside this benchmark's owned temporary root.");
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch (IOException) when (attempt < 6) { await Task.Delay(50 << attempt); }
        }
    }

    private const string Usage = "XamlLanguageBench --output <results.json> [--host <WorkspaceHost.exe-or-dll>] [--sizes 20,200,600] [--iterations 12]\nRequires Windows and the .NET 10 SDK. Generates/restores temporary WPF projects. Exit 1 means cancellation, setup failure, or incorrect/unavailable language results; latency alone is not success.";
    private sealed record Options(string Output, string? Host, int[] Sizes, int Iterations)
    {
        public static Options Parse(string[] args)
        {
            string? output = null, host = null;
            int[] sizes = [20, 200, 600]; int iterations = 12;
            for (int index = 0; index < args.Length; index++)
            {
                string flag = args[index];
                if (++index >= args.Length) throw new ArgumentException("Missing value for " + flag);
                switch (flag)
                {
                    case "--output": output = Path.GetFullPath(args[index]); break;
                    case "--host": host = Path.GetFullPath(args[index]); if (!File.Exists(host)) throw new ArgumentException("The selected worker does not exist: " + host); break;
                    case "--sizes": sizes = args[index].Split(',').Select(int.Parse).Distinct().ToArray(); break;
                    case "--iterations": iterations = int.Parse(args[index]); break;
                    default: throw new ArgumentException("Unknown option " + flag);
                }
            }
            if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("An explicit --output JSON path is required.");
            if (sizes.Length is < 1 or > 12 || sizes.Any(size => size is < 5 or > 2000)) throw new ArgumentException("Supply 1–12 sizes, each between 5 and 2000 evaluated XAML files.");
            if (iterations is < 1 or > 1000) throw new ArgumentException("Iterations must be between 1 and 1000.");
            return new(output, host, sizes, iterations);
        }
    }
    private sealed record Report(DateTimeOffset StartedUtc, string OperatingSystem, string Runtime, int LogicalProcessors, string Host, int Iterations, string Limits)
    {
        public DateTimeOffset FinishedUtc { get; set; }
        public bool Cancelled { get; set; }
        public Dictionary<string, string> WorkerFileHashes { get; } = [];
        public List<FixtureResult> Fixtures { get; } = [];
    }
    private sealed class FixtureResult(int size)
    {
        public int RequestedXamlFiles { get; } = size;
        public int EvaluatedXamlFiles { get; set; }
        public int? UnrelatedMinimumBytes { get; set; }
        public int? UnrelatedMaximumBytes { get; set; }
        public double RestoreMilliseconds { get; set; }
        public double LoadMilliseconds { get; set; }
        public string? SdkVersion { get; set; }
        public IReadOnlyList<WorkspaceIssue> WorkspaceIssues { get; set; } = [];
        public string? Error { get; set; }
        public List<Sample> Samples { get; } = [];
        public Dictionary<string, Distribution> Distributions { get; set; } = [];
    }
    private sealed record Sample(string Phase, int Iteration, double Milliseconds, bool Correct, string Expected, string? Observation);
    private sealed record Distribution(int Count, int CorrectCount, double P50Milliseconds, double P95Milliseconds, double MaximumMilliseconds)
    {
        public static Distribution Create(Sample[] samples)
        {
            var sorted = samples.Select(sample => sample.Milliseconds).Order().ToArray();
            double Percentile(double fraction) => sorted[Math.Max(0, (int)Math.Ceiling(fraction * sorted.Length) - 1)];
            return new(samples.Length, samples.Count(sample => sample.Correct), Percentile(.5), Percentile(.95), sorted[^1]);
        }
    }
}
