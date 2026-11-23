using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace WpfStudio.Core.Updates;

/// <summary>What the closing WpfStudio asks the staged one to do: replace <see cref="Target"/> once process <see cref="WaitForProcessId"/> has exited.</summary>
public sealed record ApplyRequest(string Target, int WaitForProcessId, string PreviousVersion);

/// <summary>
/// The command lines between the three processes of an update: the running WpfStudio starts the staged package's
/// WpfStudio.exe with <c>--apply-update</c>; that one replaces the installed files and starts the installed WpfStudio.exe
/// with <c>--updated-from</c>, which says so in the status bar.
/// </summary>
public static class UpdateCommandLine
{
    public const string ApplyOption = "--apply-update";
    public const string UpdatedFromOption = "--updated-from";

    public static ProcessStartInfo ApplyStart(StagedUpdate staged, string target, int processId, string previousVersion)
    {
        var start = new ProcessStartInfo(staged.Executable) { WorkingDirectory = staged.Directory };
        foreach (var argument in new[] { ApplyOption, Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)), processId.ToString(CultureInfo.InvariantCulture), previousVersion }) start.ArgumentList.Add(argument);
        return start;
    }

    public static bool TryParseApply(IReadOnlyList<string> arguments, [NotNullWhen(true)] out ApplyRequest? request)
    {
        request = null;
        if (arguments is not [ApplyOption, var target, var process, var previous]) return false;
        if (!Path.IsPathFullyQualified(target) || !int.TryParse(process, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0) return false;
        request = new ApplyRequest(target, processId, previous);
        return true;
    }

    /// <summary>
    /// Starts the installed package again; the session it reopens is the one the closing WpfStudio saved. Without
    /// <paramref name="previousVersion"/>, for when the update failed and the previous version was kept, it says nothing about an update.
    /// </summary>
    public static ProcessStartInfo RestartStart(string target, string? previousVersion)
    {
        var start = new ProcessStartInfo(Path.Combine(target, InstalledPackage.ExecutableName)) { WorkingDirectory = target };
        if (previousVersion is not null)
        {
            start.ArgumentList.Add(UpdatedFromOption);
            start.ArgumentList.Add(previousVersion);
        }
        return start;
    }

    /// <summary>The version an update replaced, when WpfStudio was started by one.</summary>
    public static string? UpdatedFrom(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
            if (arguments[i] == UpdatedFromOption) return arguments[i + 1];
        return null;
    }
}
