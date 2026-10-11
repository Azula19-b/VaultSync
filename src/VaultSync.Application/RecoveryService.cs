using VaultSync.Core;

namespace VaultSync.Application;

public sealed class RecoveryService(IVaultObjectStore objects, IVaultMetadataStore metadata) : IRecoveryService
{
    public async Task<RestoreResult> RestoreAsync(UnlockedVault vault, Guid snapshotId, IEnumerable<string>? selectedPaths,
        string destinationRoot, bool overwrite, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        var manifest = await metadata.GetSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected snapshot does not exist.");
        Directory.CreateDirectory(destinationRoot);
        var selected = selectedPaths?.Select(PathSafety.NormalizeRelativePath).ToHashSet(StringComparer.Ordinal);
        var files = manifest.Files.Where(file => selected is null || selected.Any(path =>
            file.RelativePath.Equals(path, StringComparison.Ordinal) || file.RelativePath.StartsWith(path.TrimEnd('/') + '/', StringComparison.Ordinal))).ToList();
        var failures = new List<RestoreFailure>();
        long bytes = 0;

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = PathSafety.ResolveRestorePath(destinationRoot, file.RelativePath);
                if (File.Exists(target) && !overwrite)
                    throw new IOException("A file already exists at the restore destination.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                progress?.Report(new(BackupStage.Encrypting, index, files.Count, bytes, manifest.LogicalBytes, file.RelativePath));
                await objects.RestoreObjectAsync(vault, file.ContentHash, target, cancellationToken).ConfigureAwait(false);
                bytes += file.Size;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(new(file.RelativePath, exception.Message));
            }
        }

        progress?.Report(new(BackupStage.Completed, files.Count, files.Count, bytes, manifest.LogicalBytes, null));
        return new(files.Count - failures.Count, bytes, failures);
    }
}

