using VaultSync.Core;

namespace VaultSync.Application;

public sealed class BackupService(IFileScanner scanner, IVaultObjectStore objects, IVaultMetadataStore metadata) : IBackupService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<BackupResult> RunAsync(UnlockedVault vault, string sourcePath, IReadOnlyCollection<string> exclusions,
        IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourcePath)) throw new DirectoryNotFoundException(sourcePath);
        PathSafety.ValidateSourceAndVault(sourcePath, vault.RootPath);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A backup is already running for this application instance.");

        var snapshotId = Guid.NewGuid();
        var began = false;
        try
        {
            await using var vaultLock = await objects.AcquireWriteLockAsync(vault, cancellationToken).ConfigureAwait(false);
            await metadata.InitializeAsync(vault.RootPath, cancellationToken).ConfigureAwait(false);
            await metadata.BeginSnapshotAsync(snapshotId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            began = true;

            progress?.Report(new(BackupStage.Scanning, 0, 0, 0, 0, null));
            var files = await scanner.ScanAsync(sourcePath, exclusions, cancellationToken).ConfigureAwait(false);
            var totalBytes = files.Sum(file => file.Size);
            long processedBytes = 0;
            long storedBytes = 0;
            var manifestFiles = new List<SnapshotFile>(files.Count);

            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[index];
                progress?.Report(new(BackupStage.Hashing, index, files.Count, processedBytes, totalBytes, file.RelativePath));
                var hash = await scanner.ComputeStableHashAsync(file, cancellationToken).ConfigureAwait(false);
                if (!await objects.ContainsObjectAsync(vault, hash, cancellationToken).ConfigureAwait(false))
                {
                    progress?.Report(new(BackupStage.Encrypting, index, files.Count, processedBytes, totalBytes, file.RelativePath));
                    await objects.StoreObjectAsync(vault, hash, file.FullPath, cancellationToken).ConfigureAwait(false);
                    storedBytes += file.Size;
                }

                manifestFiles.Add(new(file.RelativePath, hash, file.Size, file.LastWriteUtc));
                processedBytes += file.Size;
            }

            var manifest = new SnapshotManifest(1, vault.VaultId, snapshotId, DateTimeOffset.UtcNow,
                new DirectoryInfo(sourcePath).Name, manifestFiles, totalBytes);
            progress?.Report(new(BackupStage.Finalizing, files.Count, files.Count, totalBytes, totalBytes, null));
            var manifestPath = await objects.WriteManifestAsync(vault, manifest, cancellationToken).ConfigureAwait(false);
            await metadata.CompleteSnapshotAsync(manifest, storedBytes, manifestPath, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(BackupStage.Completed, files.Count, files.Count, totalBytes, totalBytes, null));
            return new(snapshotId, files.Count, totalBytes, storedBytes);
        }
        catch (OperationCanceledException) when (began)
        {
            await metadata.FailSnapshotAsync(snapshotId, SnapshotState.Cancelled, "Backup cancelled.", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (began)
        {
            await metadata.FailSnapshotAsync(snapshotId, SnapshotState.Failed, exception.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
