namespace WpfStudio.Core;

/// <summary>
/// Where WpfStudio keeps per-user data. Configured as options so tests and portable installs can redirect all of it at
/// once; by default it follows <see cref="AppPaths.DataDirectory"/>, which honours WPFSTUDIO_DATA_DIRECTORY.
/// </summary>
public sealed class StudioPaths
{
    public string DataDirectory { get; set; } = AppPaths.DataDirectory;
    public string SqlConnectionsFile => Path.Combine(DataDirectory, "sql-connections.json");
    public string SqlRecoveryFile => Path.Combine(DataDirectory, "sql-recovery.json");
}
