using VaultSync.Core;

namespace VaultSync.Application;

public interface IFileScanner
{
    Task<IReadOnlyList<FileCandidate>> ScanAsync(string sourcePath, IReadOnlyCollection<string> exclusions, CancellationToken cancellationToken);
    Task<string> ComputeStableHashAsync(FileCandidate file, CancellationToken cancellationToken);
}

public interface IVaultObjectStore
{
    Task<bool> ContainsObjectAsync(UnlockedVault vault, string contentHash, CancellationToken cancellationToken);
    Task StoreObjectAsync(UnlockedVault vault, string contentHash, string sourcePath, CancellationToken cancellationToken);
    Task RestoreObjectAsync(UnlockedVault vault, string contentHash, string destinationPath, CancellationToken cancellationToken);
    Task<string> WriteManifestAsync(UnlockedVault vault, SnapshotManifest manifest, CancellationToken cancellationToken);
    Task<SnapshotManifest> ReadManifestAsync(UnlockedVault vault, string manifestPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListManifestPathsAsync(UnlockedVault vault, CancellationToken cancellationToken);
    long GetPhysicalBytes(string vaultRoot);
}

public interface IVaultMetadataStore
{
    Task InitializeAsync(string vaultRoot, CancellationToken cancellationToken);
    Task BeginSnapshotAsync(Guid snapshotId, DateTimeOffset createdUtc, CancellationToken cancellationToken);
    Task CompleteSnapshotAsync(SnapshotManifest manifest, long newStoredBytes, string manifestPath, CancellationToken cancellationToken);
    Task FailSnapshotAsync(Guid snapshotId, SnapshotState state, string error, CancellationToken cancellationToken);
    Task<IReadOnlyList<SnapshotSummary>> GetSnapshotsAsync(CancellationToken cancellationToken);
    Task<SnapshotManifest?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DuplicateGroup>> GetDuplicatesAsync(Guid snapshotId, CancellationToken cancellationToken);
    Task<VaultAnalytics> GetAnalyticsAsync(long physicalBytes, CancellationToken cancellationToken);
    Task RebuildAsync(IEnumerable<(SnapshotManifest Manifest, string ManifestPath)> manifests, CancellationToken cancellationToken);
}

public interface IVaultManager
{
    Task<UnlockedVault> CreateAsync(string vaultRoot, string name, string password, int kdfIterations, CancellationToken cancellationToken);
    Task<UnlockedVault> UnlockAsync(string vaultRoot, string password, CancellationToken cancellationToken);
}

public interface IBackupService
{
    Task<BackupResult> RunAsync(UnlockedVault vault, string sourcePath, IReadOnlyCollection<string> exclusions,
        IProgress<BackupProgress>? progress, CancellationToken cancellationToken);
}

public interface IRecoveryService
{
    Task<RestoreResult> RestoreAsync(UnlockedVault vault, Guid snapshotId, IEnumerable<string>? selectedPaths,
        string destinationRoot, bool overwrite, IProgress<BackupProgress>? progress, CancellationToken cancellationToken);
}

