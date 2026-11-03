namespace WpfStudio.Core;

public static class AppPaths
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("WPFSTUDIO_DATA_DIRECTORY") is { Length: > 0 } directory
        ? Path.GetFullPath(directory)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfStudio");
}
