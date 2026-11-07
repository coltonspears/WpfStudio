namespace WpfStudio.Workspace.Runtimes;

/// <summary>Where the runtime check learns what is installed; a seam for tests, which cannot change the machine's .NET.</summary>
public interface IDotNetInventorySource
{
    Task<DotNetInventory> ReadAsync(string directory, CancellationToken cancellationToken);
    Task<SdkRequirement?> FindMissingSdkAsync(string directory, CancellationToken cancellationToken);
    bool DirectoryExists(string path);
}

/// <summary>The <c>dotnet</c> on PATH, and the disk.</summary>
public sealed class DotNetInventorySource : IDotNetInventorySource
{
    public Task<DotNetInventory> ReadAsync(string directory, CancellationToken cancellationToken) => RuntimeRequirements.ReadInventoryAsync(directory, cancellationToken);
    public Task<SdkRequirement?> FindMissingSdkAsync(string directory, CancellationToken cancellationToken) => RuntimeRequirements.FindMissingSdkAsync(directory, cancellationToken);
    public bool DirectoryExists(string path) => Directory.Exists(path);
}
