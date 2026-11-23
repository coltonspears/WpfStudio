namespace WpfStudio.Core.Updates;

/// <summary>
/// The answer to "is there a newer WpfStudio": the newest release, whether it is newer than this one, and the file to
/// install it from. <see cref="CannotInstall"/> says why a newer release cannot replace this copy in place.
/// </summary>
public sealed record UpdateCheck(ReleaseInfo? Release, bool IsNewer, ReleaseAsset? Asset, string? CannotInstall)
{
    public bool CanInstall => IsNewer && Asset is not null && CannotInstall is null;
}

/// <summary>Checks GitHub for a newer release of the running package and stages it; see <see cref="UpdateApplier"/> for the rest.</summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>Where a build without a package marker looks for releases.</summary>
    public const string DefaultRepository = "coltonspears/WpfStudio";
    private readonly UpdateDownloader _downloader;
    private readonly string? _markerProblem;

    public UpdateService(HttpClient http, string applicationDirectory, string updatesDirectory, string? currentVersion, string defaultRepository = DefaultRepository, Uri? apiAddress = null)
    {
        ApplicationDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        Package = InstalledPackage.TryRead(ApplicationDirectory, out _markerProblem);
        CurrentVersion = ReleaseVersion.TryParse(currentVersion, out var version) ? version : null;
        Feed = new ReleaseFeed(http, Package?.Repository ?? defaultRepository, CurrentVersion?.ToString(), apiAddress);
        _downloader = new UpdateDownloader(http, updatesDirectory);
    }

    public string ApplicationDirectory { get; }
    /// <summary>The running package's marker; null for a build from source.</summary>
    public InstalledPackage? Package { get; }
    /// <summary>This WpfStudio's version; null when it carries none, and then any release counts as newer.</summary>
    public ReleaseVersion? CurrentVersion { get; }
    public ReleaseFeed Feed { get; }
    public string UpdatesDirectory => _downloader.UpdatesDirectory;

    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var release = await Feed.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        if (release is null) return new UpdateCheck(null, false, null, null);
        if (CurrentVersion is not null && release.Version <= CurrentVersion) return new UpdateCheck(release, false, null, null);
        if (Package is null)
            return new UpdateCheck(release, true, null, _markerProblem
                ?? "This WpfStudio was built from source rather than installed from a release, so it does not replace itself. Download the release, or pull and build the newer source.");
        var name = Package.AssetName(release.Version);
        var asset = release.Assets.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (asset is null) return new UpdateCheck(release, true, null, $"The release has no {name}, the kind of package installed here. Download it from the release page instead.");
        if (!Package.CanWrite())
            return new UpdateCheck(release, true, asset, $"Your Windows account cannot change {ApplicationDirectory}. Download the release and replace the folder yourself, or move WpfStudio to a folder you can write to.");
        return new UpdateCheck(release, true, asset, null);
    }

    /// <summary>Downloads, verifies and unpacks the release a check found. Earlier staged updates are removed first.</summary>
    public Task<StagedUpdate> DownloadAsync(UpdateCheck check, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        if (!check.CanInstall || check.Release is null || check.Asset is null || Package is null)
            throw new InvalidOperationException(check.CannotInstall ?? "There is no newer release to install.");
        _downloader.Release();
        _downloader.RemoveStaleUpdates();
        return _downloader.DownloadAsync(check.Release, check.Asset, Package.Kind, progress, cancellationToken);
    }

    /// <summary>Lets other windows remove the update this one staged. Ending the process does the same.</summary>
    public void Dispose() => _downloader.Dispose();

    /// <summary>What finished updates leave: the replaced files kept in the package and the unpacked download. Run at startup.</summary>
    public void RemoveLeftovers()
    {
        UpdateApplier.RemoveBackups(ApplicationDirectory);
        _downloader.RemoveStaleUpdates();
    }
}
