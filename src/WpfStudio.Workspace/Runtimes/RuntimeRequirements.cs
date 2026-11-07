using System.Text.Json;
using System.Text.RegularExpressions;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace.Runtimes;

/// <summary>The shared frameworks a .NET program runs on, as <c>dotnet --list-runtimes</c> names them.</summary>
public static class SharedFrameworks
{
    public const string Core = "Microsoft.NETCore.App", Desktop = "Microsoft.WindowsDesktop.App", AspNet = "Microsoft.AspNetCore.App";
    public static string DisplayName(string framework) => framework switch { Desktop => ".NET Desktop Runtime", AspNet => "ASP.NET Core Runtime", _ => ".NET Runtime" };
}

/// <summary>A shared framework some projects need to run. <paramref name="Channel"/> is its major and minor version, such as 8.0.</summary>
public sealed record RuntimeRequirement(string Framework, Version Channel, IReadOnlyList<string> Projects)
{
    public string Title => $"{SharedFrameworks.DisplayName(Framework)} {Channel.ToString(2)}";
}

/// <summary>A .NET Framework project built by Visual Studio's MSBuild, which needs the framework's reference assemblies installed.</summary>
public sealed record TargetingPackRequirement(string Version, IReadOnlyList<string> Projects)
{
    public string Title => $".NET Framework {Version} Developer Pack";
    public Uri DownloadPage => new($"https://dotnet.microsoft.com/download/dotnet-framework/net{Version.Replace(".", "", StringComparison.Ordinal)}");
}

/// <summary>The SDK the workspace's global.json asks for, which the .NET CLI could not find. A null version means no SDK at all.</summary>
public sealed record SdkRequirement(string? Version, string? GlobalJsonPath, string Reason)
{
    public string Title => Version is null ? ".NET SDK" : $".NET SDK {Version}";
}

public sealed record InstalledRuntime(string Framework, string Version);
public sealed record DotNetInventory(IReadOnlyList<InstalledRuntime> Runtimes, IReadOnlyList<string> Sdks);

/// <summary>What the projects of a workspace need from the machine to build and run, and which of it is missing.</summary>
public static partial class RuntimeRequirements
{
    /// <summary>
    /// The runtimes the projects that run need: executables and test projects. A library runs on whatever runtime loads
    /// it, so it adds nothing. Every .NET project needs the base runtime; WPF and Windows Forms add the desktop one.
    /// </summary>
    public static IReadOnlyList<RuntimeRequirement> For(IEnumerable<WorkspaceProject> projects)
    {
        var needs = new Dictionary<(string Framework, Version Channel), List<string>>();
        foreach (var project in projects.Where(Runs))
        {
            var dependencies = project.Dependencies ?? [];
            var frameworks = dependencies.Where(dependency => dependency.Kind == "Framework").Select(dependency => dependency.Name).ToArray();
            var targets = project.TargetFramework is { Length: > 0 } single ? [single] : frameworks.Where(name => name.StartsWith("net", StringComparison.OrdinalIgnoreCase)).ToArray();
            var desktop = frameworks.Any(name => name.StartsWith(SharedFrameworks.Desktop, StringComparison.OrdinalIgnoreCase))
                // A snapshot without framework references still shows a WPF project by its markup.
                || !frameworks.Any(name => name.StartsWith("Microsoft.", StringComparison.Ordinal)) && project.Files.Any(file => file.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
            var web = frameworks.Any(name => name.Equals(SharedFrameworks.AspNet, StringComparison.OrdinalIgnoreCase));
            foreach (var target in targets)
            {
                if (ChannelOf(target) is not { } channel) continue;
                void Need(string framework)
                {
                    if (!needs.TryGetValue((framework, channel), out var names)) needs[(framework, channel)] = names = [];
                    if (!names.Contains(project.Name)) names.Add(project.Name);
                }
                Need(SharedFrameworks.Core);
                if (desktop && target.Contains("-windows", StringComparison.OrdinalIgnoreCase)) Need(SharedFrameworks.Desktop);
                if (web) Need(SharedFrameworks.AspNet);
            }
        }
        return needs.Select(pair => new RuntimeRequirement(pair.Key.Framework, pair.Key.Channel, pair.Value)).OrderByDescending(requirement => requirement.Channel).ThenBy(requirement => requirement.Framework).ToArray();
    }

    private static bool Runs(WorkspaceProject project) => project.IsExecutable
        || (project.Dependencies ?? []).Any(dependency => dependency.Kind == "Package" && dependency.Name is "Microsoft.NET.Test.Sdk" or "MSTest.Sdk" or "Microsoft.Testing.Platform");

    /// <summary>
    /// The requirements no installed runtime meets. A program rolls forward to a later minor version of the same major
    /// one, never to the next major. The desktop installer brings the base runtime of its version with it, so a missing
    /// base runtime is folded into a missing desktop one.
    /// </summary>
    public static IReadOnlyList<RuntimeRequirement> Missing(IReadOnlyList<RuntimeRequirement> required, DotNetInventory installed)
    {
        bool Met(RuntimeRequirement requirement) => installed.Runtimes.Any(runtime => runtime.Framework.Equals(requirement.Framework, StringComparison.OrdinalIgnoreCase)
            && ParseVersion(runtime.Version) is { } version && version.Major == requirement.Channel.Major && version.Minor >= requirement.Channel.Minor);
        var missing = required.Where(requirement => !Met(requirement)).ToList();
        foreach (var desktop in missing.Where(requirement => requirement.Framework == SharedFrameworks.Desktop).ToArray())
            if (missing.FirstOrDefault(requirement => requirement.Framework == SharedFrameworks.Core && requirement.Channel == desktop.Channel) is { } core)
            {
                missing.Remove(core);
                missing[missing.IndexOf(desktop)] = desktop with { Projects = desktop.Projects.Union(core.Projects).ToArray() };
            }
        return missing;
    }

    /// <summary>Legacy .NET Framework projects whose reference assemblies are not installed. SDK-style projects get them from a package instead.</summary>
    public static IReadOnlyList<TargetingPackRequirement> MissingTargetingPacks(IEnumerable<WorkspaceProject> projects, Func<string, bool> directoryExists)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Reference Assemblies", "Microsoft", "Framework", ".NETFramework");
        return projects.Where(project => !project.IsSdkStyle && project.TargetFrameworkMoniker?.StartsWith(".NETFramework,Version=v", StringComparison.Ordinal) == true)
            .GroupBy(project => project.TargetFrameworkMoniker![".NETFramework,Version=v".Length..])
            .Where(group => !directoryExists(Path.Combine(root, "v" + group.Key)))
            .Select(group => new TargetingPackRequirement(group.Key, group.Select(project => project.Name).ToArray())).ToArray();
    }

    /// <summary>The runtime version a target framework moniker runs on: 8.0 for <c>net8.0-windows</c>, 3.1 for <c>netcoreapp3.1</c>. Null for .NET Framework and .NET Standard.</summary>
    public static Version? ChannelOf(string targetFramework)
    {
        var match = MonikerPattern().Match(targetFramework.Trim());
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version) && version.Major >= (match.Groups["core"].Success ? 1 : 5) ? new Version(version.Major, Math.Max(0, version.Minor)) : null;
    }
    [GeneratedRegex(@"^net(?<core>coreapp)?(?<version>\d+\.\d+)(?:-.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MonikerPattern();

    /// <summary>The numeric part of a version such as <c>11.0.0-rc.3.26522.148</c>.</summary>
    public static Version? ParseVersion(string text) => Version.TryParse(text.Split('-', '+')[0], out var version) ? version : null;

    /// <summary>Reads <c>dotnet --list-runtimes</c> and <c>dotnet --list-sdks</c> output.</summary>
    public static DotNetInventory ParseInventory(string runtimes, string sdks) => new(
        runtimes.Split('\n').Select(line => ListingPattern().Match(line.Trim())).Where(match => match.Success && match.Groups["name"].Success)
            .Select(match => new InstalledRuntime(match.Groups["name"].Value, match.Groups["version"].Value)).ToArray(),
        sdks.Split('\n').Select(line => ListingPattern().Match(line.Trim())).Where(match => match.Success && !match.Groups["name"].Success).Select(match => match.Groups["version"].Value).ToArray());
    [GeneratedRegex(@"^(?:(?<name>[A-Za-z][\w.]+)\s+)?(?<version>\d+\.\d+\.\d+\S*)\s+\[.+\]$")]
    private static partial Regex ListingPattern();

    /// <summary>The runtimes and SDKs of the <c>dotnet</c> on PATH; empty when there is no <c>dotnet</c> at all.</summary>
    public static async Task<DotNetInventory> ReadInventoryAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            var runtimes = ProcessRunner.CaptureAsync(ProcessRunner.DotNet(directory, "--list-runtimes"), cancellationToken);
            var sdks = ProcessRunner.CaptureAsync(ProcessRunner.DotNet(directory, "--list-sdks"), cancellationToken);
            await Task.WhenAll(runtimes, sdks).ConfigureAwait(false);
            return ParseInventory((await runtimes.ConfigureAwait(false)).Output, (await sdks.ConfigureAwait(false)).Output);
        }
        catch (System.ComponentModel.Win32Exception) { return new([], []); }
    }

    /// <summary>
    /// Asks the CLI which SDK the folder resolves to, which applies global.json and its roll-forward policy exactly as
    /// a build does. Null when it finds one.
    /// </summary>
    public static async Task<SdkRequirement?> FindMissingSdkAsync(string directory, CancellationToken cancellationToken)
    {
        (int ExitCode, string Output, string Error) result;
        try { result = await ProcessRunner.CaptureAsync(ProcessRunner.DotNet(directory, "--version"), cancellationToken).ConfigureAwait(false); }
        catch (System.ComponentModel.Win32Exception) { return new SdkRequirement(null, null, "The .NET SDK is not installed: no dotnet command was found on PATH."); }
        if (result.ExitCode == 0) return null;
        var globalJson = FindGlobalJson(directory);
        var version = globalJson is null ? null : ReadGlobalJsonVersion(globalJson);
        // The CLI's message starts with a general line; the one that says what is wrong comes later.
        var lines = (result.Error + "\n" + result.Output).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var reason = lines.FirstOrDefault(line => line.Contains("SDK was not found", StringComparison.OrdinalIgnoreCase) || line.Contains("SDK not found", StringComparison.OrdinalIgnoreCase))
            ?? lines.FirstOrDefault() ?? "";
        return new SdkRequirement(version, globalJson, reason.Length > 0 ? reason : "The .NET CLI found no SDK for this workspace.");
    }

    private static string? FindGlobalJson(string directory)
    {
        for (var folder = new DirectoryInfo(directory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "global.json"))) return Path.Combine(folder.FullName, "global.json");
        return null;
    }

    /// <summary>The <c>sdk.version</c> of a global.json, or null when it names none.</summary>
    public static string? ReadGlobalJsonVersion(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.TryGetProperty("sdk", out var sdk) && sdk.ValueKind == JsonValueKind.Object && sdk.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
