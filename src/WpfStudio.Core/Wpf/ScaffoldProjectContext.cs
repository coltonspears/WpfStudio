using System.Diagnostics;
using System.Text.Json;

namespace WpfStudio.Core.Wpf;

/// <summary>Evaluated build conventions, including imported props and central package versions.</summary>
public sealed record ScaffoldProjectContext(string RootNamespace, bool HasToolkit, bool ManageVersionsCentrally, bool SupportsPartialProperties, bool DefaultCompileItems, bool DefaultPageItems, bool EvaluationSucceeded)
{
    /// <summary>Evaluation normally takes seconds, and scaffolding can go on without it, so a wedged MSBuild must not hold it up.</summary>
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromMinutes(1);

    public static async Task<ScaffoldProjectContext?> TryEvaluateAsync(string projectPath, CancellationToken token, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(projectPath)!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var name in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildExtensionsPath32", "MSBuildExtensionsPath64", "MSBuildToolsPath", "MSBuildBinPath", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER", "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR" }) start.Environment.Remove(name);
        foreach (var arg in new[] { "msbuild", projectPath, "-nologo", "-getProperty:RootNamespace,LangVersion,TargetFramework,TargetFrameworks,ManagePackageVersionsCentrally,EnableDefaultCompileItems,EnableDefaultPageItems,EnableDefaultItems", "-getItem:PackageReference,PackageVersion" }) start.ArgumentList.Add(arg);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? EvaluationTimeout);
        try
        {
            using var process = Process.Start(start)!;
            using var cancellation = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false); await error.ConfigureAwait(false);
            var begin = text.IndexOf('{');
            if (process.ExitCode != 0 || begin < 0) return null;
            using var json = JsonDocument.Parse(text[begin..]);
            var properties = json.RootElement.GetProperty("Properties");
            string Property(string key) => properties.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
            var items = json.RootElement.GetProperty("Items");
            var references = items.GetProperty("PackageReference").EnumerateArray().Where(e => e.GetProperty("Identity").GetString()?.Equals("CommunityToolkit.Mvvm", StringComparison.OrdinalIgnoreCase) == true).ToArray();
            var versions = items.GetProperty("PackageVersion").EnumerateArray().Where(e => e.GetProperty("Identity").GetString()?.Equals("CommunityToolkit.Mvvm", StringComparison.OrdinalIgnoreCase) == true).ToArray();
            var versionText = references.Concat(versions).Select(e => e.TryGetProperty("Version", out var value) ? value.GetString() : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var toolkitSupportsPartial = references.Length == 0 || Version.TryParse(versionText?.Split('-')[0], out var version) && version >= new Version(8, 4);
            var language = Property("LangVersion");
            // MSBuild resolves normal default language versions to a number. Unknown/latest/preview use the safe classic form.
            var compilerSupportsPartialInitializer = int.TryParse(language.Split('.')[0], out var major) && major >= 14;
            var defaults = !Property("EnableDefaultItems").Equals("false", StringComparison.OrdinalIgnoreCase);
            return new ScaffoldProjectContext(Property("RootNamespace"), references.Length > 0, Property("ManagePackageVersionsCentrally").Equals("true", StringComparison.OrdinalIgnoreCase),
                toolkitSupportsPartial && compilerSupportsPartialInitializer, defaults && !Property("EnableDefaultCompileItems").Equals("false", StringComparison.OrdinalIgnoreCase),
                defaults && !Property("EnableDefaultPageItems").Equals("false", StringComparison.OrdinalIgnoreCase), true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; } // Out of time: the same as an evaluation that failed.
        catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
    }
}
