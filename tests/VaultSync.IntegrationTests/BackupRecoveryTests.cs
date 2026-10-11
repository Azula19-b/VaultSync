using VaultSync.Application;
using VaultSync.Core;
using VaultSync.Infrastructure;

namespace VaultSync.IntegrationTests;

public sealed class BackupRecoveryTests
{
    [Fact]
    public async Task IncrementalBackup_DeduplicatesVersionsAndRestoresDeletedFile()
    {
        using var root = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        var vaultPath = Directory.CreateDirectory(Path.Combine(root.Path, "vault")).FullName;
        var restore = Directory.CreateDirectory(Path.Combine(root.Path, "restore")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "original.txt"), "version one");
        await File.WriteAllTextAsync(Path.Combine(source, "duplicate.txt"), "version one");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "note.txt"), "nested content");

        var services = CreateServices();
        using var vault = await services.Manager.CreateAsync(vaultPath, "Integration", "integration-password", 210_000, CancellationToken.None);
        var first = await services.Backup.RunAsync(vault, source, [], null, CancellationToken.None);
        var second = await services.Backup.RunAsync(vault, source, [], null, CancellationToken.None);

        Assert.Equal(0, second.NewStoredBytes);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(vaultPath, "objects"), "*.vso", SearchOption.AllDirectories).Length);
        var duplicates = await services.Metadata.GetDuplicatesAsync(first.SnapshotId, CancellationToken.None);
        Assert.Single(duplicates);
        Assert.Equal(2, duplicates[0].Paths.Count);

        await File.WriteAllTextAsync(Path.Combine(source, "original.txt"), "version two");
        File.Delete(Path.Combine(source, "duplicate.txt"));
        var third = await services.Backup.RunAsync(vault, source, [], null, CancellationToken.None);
        Assert.True(third.NewStoredBytes > 0);

        var result = await services.Recovery.RestoreAsync(vault, first.SnapshotId, ["duplicate.txt"], restore, false, null,
            CancellationToken.None);
        Assert.Empty(result.Failures);
        Assert.Equal("version one", await File.ReadAllTextAsync(Path.Combine(restore, "duplicate.txt")));
    }

    [Fact]
    public async Task CorruptedCiphertext_IsRejectedWithoutFinalizingOutput()
    {
        using var root = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        var vaultPath = Directory.CreateDirectory(Path.Combine(root.Path, "vault")).FullName;
        var restore = Directory.CreateDirectory(Path.Combine(root.Path, "restore")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "important.txt"), "do not corrupt me");
        var services = CreateServices();
        using var vault = await services.Manager.CreateAsync(vaultPath, "Integration", "integration-password", 210_000, CancellationToken.None);
        var snapshot = await services.Backup.RunAsync(vault, source, [], null, CancellationToken.None);
        var objectPath = Directory.GetFiles(Path.Combine(vaultPath, "objects"), "*.vso", SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(objectPath);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(objectPath, bytes);

        var result = await services.Recovery.RestoreAsync(vault, snapshot.SnapshotId, null, restore, false, null,
            CancellationToken.None);

        Assert.Single(result.Failures);
        Assert.False(File.Exists(Path.Combine(restore, "important.txt")));
        Assert.Empty(Directory.GetFiles(restore, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RebuildIndex_RecoversHistoryFromAuthenticatedManifests()
    {
        using var root = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        var vaultPath = Directory.CreateDirectory(Path.Combine(root.Path, "vault")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "history.txt"), "recoverable metadata");
        var services = CreateServices();
        using var vault = await services.Manager.CreateAsync(vaultPath, "Integration", "integration-password", 210_000, CancellationToken.None);
        await services.Backup.RunAsync(vault, source, [], null, CancellationToken.None);
        File.Delete(Path.Combine(vaultPath, "vault-index.db"));

        var rebuilt = await services.Importer.RebuildIndexAsync(vault, CancellationToken.None);

        Assert.Equal(1, rebuilt);
        Assert.Single(await services.Metadata.GetSnapshotsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InterruptedBackup_DoesNotDamageCompletedSnapshot()
    {
        using var root = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        var vaultPath = Directory.CreateDirectory(Path.Combine(root.Path, "vault")).FullName;
        var restore = Directory.CreateDirectory(Path.Combine(root.Path, "restore")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "stable.txt"), "completed snapshot");
        var services = CreateServices();
        using var vault = await services.Manager.CreateAsync(vaultPath, "Integration", "integration-password", 210_000, CancellationToken.None);
        var completed = await services.Backup.RunAsync(vault, source, [], null, CancellationToken.None);
        var interrupted = new BackupService(new CancellingScanner(), new VaultObjectStore(), services.Metadata);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            interrupted.RunAsync(vault, source, [], null, CancellationToken.None));

        var snapshots = await services.Metadata.GetSnapshotsAsync(CancellationToken.None);
        Assert.Contains(snapshots, snapshot => snapshot.State == SnapshotState.Completed);
        Assert.Contains(snapshots, snapshot => snapshot.State == SnapshotState.Cancelled);
        var result = await services.Recovery.RestoreAsync(vault, completed.SnapshotId, null, restore, false, null, CancellationToken.None);
        Assert.Empty(result.Failures);
        Assert.Equal("completed snapshot", await File.ReadAllTextAsync(Path.Combine(restore, "stable.txt")));
    }

    private static Services CreateServices()
    {
        var scanner = new FileScanner();
        var objects = new VaultObjectStore();
        var metadata = new SqliteMetadataStore();
        return new(new VaultManager(), metadata, new BackupService(scanner, objects, metadata),
            new RecoveryService(objects, metadata), new VaultImporter(objects, metadata));
    }

    private sealed record Services(VaultManager Manager, SqliteMetadataStore Metadata, BackupService Backup,
        RecoveryService Recovery, VaultImporter Importer);

    private sealed class CancellingScanner : IFileScanner
    {
        public Task<string> ComputeStableHashAsync(FileCandidate file, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<FileCandidate>> ScanAsync(string sourcePath, IReadOnlyCollection<string> exclusions,
            CancellationToken cancellationToken)
            => Task.FromException<IReadOnlyList<FileCandidate>>(new OperationCanceledException("Simulated interruption."));
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vaultsync-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
