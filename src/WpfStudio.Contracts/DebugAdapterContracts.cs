namespace WpfStudio.Contracts;

/// <summary>Finds the debug adapter and installs it when it is missing.</summary>
public interface IDebugAdapterProvider
{
    /// <summary>What prompts call the adapter, with its version.</summary>
    string Description { get; }
    /// <summary>The installed adapter's executable; null when there is none.</summary>
    string? Locate();
    /// <summary>Installs the adapter and returns its executable. Progress reports short status lines.</summary>
    Task<string> InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
