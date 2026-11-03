namespace WpfStudio.Core;

/// <summary>The experimental Code Assist feature. It stays off until someone turns it on, including for settings files older than it.</summary>
public sealed record CodeAssistSettings
{
    /// <summary>A model version that follows the newest one installed.</summary>
    public const string LatestVersion = "latest";
    public bool Enabled { get; init; }
    public string MemberModelVersion { get; init; } = LatestVersion;
    /// <summary>Grey line continuations, separately off: the current line model is known to be weak.</summary>
    public bool LineSuggestions { get; init; }
    public string LineModelVersion { get; init; } = LatestVersion;
}
