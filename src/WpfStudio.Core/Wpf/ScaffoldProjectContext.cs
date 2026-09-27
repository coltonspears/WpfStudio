using System.Diagnostics;
using System.Text.Json;

namespace WpfStudio.Core.Wpf;

/// <summary>Evaluated build conventions, including imported props and central package versions.</summary>
public sealed record ScaffoldProjectContext(string RootNamespace, bool HasToolkit, bool ManageVersionsCentrally, bool SupportsPartialProperties, bool DefaultCompileItems, bool DefaultPageItems, bool EvaluationSucceeded)
{
    public static async Task<ScaffoldProjectContext?> TryEvaluateAsync(string projectPath, CancellationToken token)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(projectPath)!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var name in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildExtensionsPath32", "MSBuildExtensionsPath64", "MSBuildToolsPath", "MSBuildBinPath", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER", "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR" }) start.Environment.Remove(name);
        foreach (var arg in new[] { "msbuild", projectPath, "-nologo", "-getProperty:RootNamespace,LangVersion,TargetFramework,TargetFrameworks,ManagePackageVersionsCentrally,EnableDefaultCompileItems,EnableDefaultPageItems,EnableDefaultItems", "-getItem:PackageReference,PackageVersion" }) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start)!;
            using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
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
        catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
    }
}
