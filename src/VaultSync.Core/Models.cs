using System.Security.Cryptography;

namespace VaultSync.Core;

public enum SnapshotState { Pending, Completed, Failed, Cancelled }
public enum BackupStage { Scanning, Hashing, Encrypting, Finalizing, Completed }

public sealed record VaultHeader(
    int FormatVersion,
    Guid VaultId,
    string Name,
    byte[] Salt,
    int KdfIterations,
    byte[] KeyNonce,
    byte[] EncryptedMasterKey,
    byte[] KeyTag,
    DateTimeOffset CreatedUtc);

public sealed class UnlockedVault : IDisposable
{
    public UnlockedVault(Guid vaultId, string rootPath, byte[] masterKey)
        => (VaultId, RootPath, MasterKey) = (vaultId, rootPath, masterKey);

    public Guid VaultId { get; }
    public string RootPath { get; }
    public byte[] MasterKey { get; }

    public void Dispose() => CryptographicOperations.ZeroMemory(MasterKey);
}

public sealed record FileCandidate(
    string FullPath,
    string RelativePath,
    long Size,
    DateTimeOffset LastWriteUtc);

public sealed record SnapshotFile(
    string RelativePath,
    string ContentHash,
    long Size,
    DateTimeOffset LastWriteUtc);

public sealed record SnapshotManifest(
    int FormatVersion,
    Guid VaultId,
    Guid SnapshotId,
    DateTimeOffset CreatedUtc,
    string SourceName,
    IReadOnlyList<SnapshotFile> Files,
    long LogicalBytes);

public sealed record SnapshotSummary(
    Guid Id,
    DateTimeOffset CreatedUtc,
    SnapshotState State,
    int FileCount,
    long LogicalBytes,
    long NewStoredBytes,
    string? Error);

public sealed record BackupProgress(
    BackupStage Stage,
    int ProcessedFiles,
    int TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    string? CurrentPath)
{
    public double Percent => TotalBytes == 0 ? (Stage == BackupStage.Completed ? 100 : 0)
        : Math.Clamp(ProcessedBytes * 100d / TotalBytes, 0, 100);
}

public sealed record BackupResult(Guid SnapshotId, int FileCount, long LogicalBytes, long NewStoredBytes);
public sealed record RestoreFailure(string RelativePath, string Message);
public sealed record RestoreResult(int RestoredFiles, long RestoredBytes, IReadOnlyList<RestoreFailure> Failures);
public sealed record DuplicateGroup(string ContentHash, long FileSize, IReadOnlyList<string> Paths)
{
    public long PotentialSavings => Math.Max(0, Paths.Count - 1) * FileSize;
}

public sealed record VaultAnalytics(
    int ProtectedFiles,
    int SnapshotCount,
    long LogicalBytes,
    long PhysicalBytes,
    long DeduplicatedBytes,
    DateTimeOffset? LastSuccessfulBackup,
    double SuccessRate,
    IReadOnlyList<SnapshotSummary> RecentSnapshots);

public sealed class VaultAuthenticationException(string message) : CryptographicException(message);
public sealed class VaultCorruptionException(string message, Exception? inner = null) : IOException(message, inner);
public sealed class SourceChangedException(string path) : IOException($"The file changed while it was being backed up: {path}");

