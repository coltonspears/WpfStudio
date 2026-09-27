[CmdletBinding()]
param(
    [ValidateRange(1, 100)][int]$Projects = 20,
    [ValidateRange(2, 2000)][int]$FilesPerProject = 250,
    [ValidateRange(5, 100)][int]$CompletionSamples = 25,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/performance'))
$fixture = Join-Path $root 'fixture'
$harness = Join-Path $root 'harness'
New-Item -ItemType Directory -Path $fixture, $harness -Force | Out-Null

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

# This fixture owns only generated artifacts. It contains 249 C# files and one
# resource dictionary per project by default: 20 * 250 = 5,000 source files.
$projectEntries = [Collections.Generic.List[string]]::new()
for ($project = 0; $project -lt $Projects; $project++) {
    $name = 'Project{0:D2}' -f $project
    $folder = Join-Path $fixture $name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $projectFile = Join-Path $folder ($name + '.csproj')
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath $projectFile -Encoding utf8
    for ($file = 0; $file -lt ($FilesPerProject - 1); $file++) {
        $type = 'File{0:D4}' -f $file
        @"
namespace $name;
public sealed class $type
{
    public string Title { get; set; } = nameof($type);
    public int Count { get; set; }
    public event System.EventHandler? Changed;
    public void Refresh() { System.Console.WriteLine(Title); Changed?.Invoke(this, System.EventArgs.Empty); }
}
"@ | Set-Content -LiteralPath (Join-Path $folder ($type + '.cs')) -Encoding utf8
    }
    '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><SolidColorBrush x:Key="AccentBrush" Color="#2579C8" /></ResourceDictionary>' | Set-Content -LiteralPath (Join-Path $folder 'Resources.xaml') -Encoding utf8
    $projectEntries.Add("  <Project Path=`"$name/$name.csproj`" />")
}
$solution = Join-Path $fixture 'Performance.slnx'
('<Solution>' + [Environment]::NewLine + ($projectEntries -join [Environment]::NewLine) + [Environment]::NewLine + '</Solution>') | Set-Content -LiteralPath $solution -Encoding utf8

$workspaceProject = [Security.SecurityElement]::Escape((Join-Path $repository 'src/WpfStudio.Workspace/WpfStudio.Workspace.csproj'))
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$workspaceProject" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $harness 'PerformanceHarness.csproj') -Encoding utf8

@'
using System.Diagnostics;
using System.Text.Json;
using WpfStudio.Contracts;
using WpfStudio.Workspace;

string solution = args[0], host = args[1], output = args[2];
int samples = int.Parse(args[3]);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
await using var client = new WorkspaceClient(host);
var started = DateTime.UtcNow;
var timer = Stopwatch.StartNew();
WorkspaceSnapshot workspace = await client.LoadAsync(new LoadWorkspaceRequest(solution), timeout.Token);
double loadMilliseconds = timer.Elapsed.TotalMilliseconds;
string file = Path.Combine(Path.GetDirectoryName(solution)!, "Project00", "File0000.cs");
string source = await File.ReadAllTextAsync(file);
await client.UpdateDocumentAsync(new UpdateDocumentRequest(file, source, 1), timeout.Token);
var request = new DocumentPositionRequest(file, source.IndexOf("WriteLine", StringComparison.Ordinal), 1);
await client.GetCompletionsAsync(request, timeout.Token);
var measurements = new List<double>();
int completionCount = 0;
for (int index = 0; index < samples; index++)
{
    timer.Restart();
    var completions = await client.GetCompletionsAsync(request, timeout.Token);
    measurements.Add(timer.Elapsed.TotalMilliseconds);
    completionCount = completions.Items.Count;
}
measurements.Sort();
using var self = Process.GetCurrentProcess();
self.Refresh();
long harnessPrivateBytes = self.PrivateMemorySize64;
long? workerPrivateBytes = null;
var workers = Process.GetProcessesByName("WpfStudio.WorkspaceHost").Where(process => process.StartTime.ToUniversalTime() >= started).ToArray();
if (workers.Length == 1) { workers[0].Refresh(); workerPrivateBytes = workers[0].PrivateMemorySize64; }
foreach (var worker in workers) worker.Dispose();
var report = new
{
    measuredUtc = DateTime.UtcNow,
    fixture = solution,
    sdkVersion = workspace.SdkVersion,
    projects = workspace.Projects.Count,
    evaluatedFiles = workspace.Projects.Sum(project => project.Files.Count),
    workerLoadMilliseconds = loadMilliseconds,
    completionSamples = samples,
    completionItems = completionCount,
    completionMedianMilliseconds = measurements[measurements.Count / 2],
    completionP95Milliseconds = measurements[(int)Math.Ceiling(measurements.Count * .95) - 1],
    harnessPrivateBytes,
    workerPrivateBytes,
    issues = workspace.Issues,
    note = "Measures restored fixture worker load and warm C# completion. Harness memory is not shell memory; UI startup/tree timings require separate measurement."
};
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(output, json);
Console.WriteLine(json);
if (completionCount == 0) throw new InvalidOperationException("No C# completion items were returned; timing is not a valid completion measurement.");
'@ | Set-Content -LiteralPath (Join-Path $harness 'Program.cs') -Encoding utf8

Push-Location $repository
try {
    Invoke-DotNet @('restore', $solution, '--verbosity', 'minimal')
    Invoke-DotNet @('build', 'src/WpfStudio.WorkspaceHost/WpfStudio.WorkspaceHost.csproj', '-c', $Configuration, '--verbosity', 'minimal')
    $hostPath = Join-Path $repository "src/WpfStudio.WorkspaceHost/bin/$Configuration/net10.0/WpfStudio.WorkspaceHost.exe"
    $report = Join-Path $root 'results.json'
    Invoke-DotNet @('run', '--project', (Join-Path $harness 'PerformanceHarness.csproj'), '-c', $Configuration, '--', $solution, $hostPath, $report, "$CompletionSamples")
    Write-Output "Performance report: $report"
} finally { Pop-Location }
