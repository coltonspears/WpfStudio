using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using WpfStudio.Contracts;
using WpfStudio.Hosting;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Runtime.Inspection;

public static class InspectionLaunch
{
    private const string StartupHooksVariable = "DOTNET_STARTUP_HOOKS";
    private const string HookFileName = "WpfStudio.Inspection.StartupHook.dll";
    private static readonly string[] RequiredCompanions =
    [
        "WpfStudio.Inspection.Agent.dll",
        "WpfStudio.Inspection.Protocol.dll",
        "WpfStudio.Wpf.PropertyEditing.dll",
        "WpfStudio.Wpf.Diagnostics.dll"
    ];

    internal static string CurrentConfiguration =>
        typeof(InspectionLaunch).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    public static string FindStartupHook() =>
        FindStartupHook(AppContext.BaseDirectory, [CurrentConfiguration, "Debug", "Release"]);

    internal static string FindStartupHook(string baseDirectory, IEnumerable<string> configurations)
    {
        var folders = new List<string> { Path.Combine(baseDirectory, "Inspection") };
        var accepted = configurations
            .Where(configuration => !string.IsNullOrWhiteSpace(configuration))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (accepted.Length == 0) accepted = [CurrentConfiguration, "Debug", "Release"];

        // Repository fallbacks are available only when the caller is actually inside this
        // checkout, never by probing unrelated installation parent directories for binaries.
        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "src", "WpfStudio.Runtime", "WpfStudio.Runtime.csproj"))) continue;
            foreach (var configuration in accepted)
            {
                folders.Add(Path.Combine(directory.FullName, "src", "WpfStudio.App", "bin", configuration, "net10.0-windows", "Inspection"));
                folders.Add(Path.Combine(directory.FullName, "src", "WpfStudio.Inspection.StartupHook", "bin", configuration, "net8.0"));
            }
            break;
        }
        foreach (var folder in folders)
        {
            var hook = Path.Combine(folder, HookFileName);
            if (File.Exists(hook) && RequiredCompanions.All(companion => File.Exists(Path.Combine(folder, companion))))
                return Path.GetFullPath(hook);
        }
        throw new FileNotFoundException("The runtime inspection startup hook and its companion assemblies are unavailable. Build or reinstall WpfStudio's Inspection components.", Path.Combine(folders[0], HookFileName));
    }

    public static IReadOnlyDictionary<string, string> CreateEnvironment(IReadOnlyDictionary<string, string> profile,
        InspectionSession session, string? startupHookPath = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(session);
        var hook = Path.GetFullPath(startupHookPath ?? FindStartupHook());
        if (!File.Exists(hook)) throw new FileNotFoundException("The runtime inspection startup hook was not found.", hook);
        if (hook.Contains(Path.PathSeparator)) throw new ArgumentException("The startup hook path cannot contain the startup-hook list separator.", nameof(startupHookPath));
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) inherited[key] = value;
        var result = new Dictionary<string, string>(inherited, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in profile) result[pair.Key] = pair.Value;

        // Launch-profile variables replace inherited values, including an explicit empty
        // hook list. Append our hook to that effective value rather than reintroducing hooks
        // the selected profile deliberately removed.
        var hooks = new List<string>();
        AddHooks(result.GetValueOrDefault(StartupHooksVariable));
        AddHooks(hook);
        result[StartupHooksVariable] = string.Join(Path.PathSeparator, hooks);
        result[InspectionProtocol.PipeVariable] = session.PipeName;
        result[InspectionProtocol.TokenVariable] = session.Token;
        result[InspectionProtocol.OwnerVariable] = session.OwnerProcessId.ToString(CultureInfo.InvariantCulture);
        result["ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO"] = "1";
        result["ENABLE_XAML_DIAGNOSTICS_VISUAL_TREE_NOTIFICATIONS"] = "1";
        return result;

        void AddHooks(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (var entry in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                if (!hooks.Any(existing => EquivalentHook(existing, entry))) hooks.Add(entry);
        }
    }

    private static bool EquivalentHook(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second)
                && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    public static ProcessStartInfo CreateStartInfo(ResolvedLaunch launch, IReadOnlyDictionary<string, string> overrides)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(overrides);
        var program = Path.GetFullPath(launch.Program);
        if (!File.Exists(program)) throw new FileNotFoundException("Build the application before launching it with inspection.", program);
        var start = ProcessLaunch.Create(program, Path.GetFullPath(launch.WorkingDirectory), launch.Arguments);
        foreach (var pair in launch.Environment) start.Environment[pair.Key] = pair.Value;
        foreach (var pair in overrides) start.Environment[pair.Key] = pair.Value;
        return start;
    }
}
