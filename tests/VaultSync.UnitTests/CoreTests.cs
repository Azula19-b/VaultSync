using System.Security.Cryptography;
using System.Text;
using VaultSync.Application;
using VaultSync.Core;
using VaultSync.Infrastructure;

namespace VaultSync.UnitTests;

public sealed class CoreTests
{
    [Fact]
    public async Task Sha256_HashMatchesKnownVector()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "abc.txt");
        await File.WriteAllTextAsync(path, "abc");
        var info = new FileInfo(path);
        var candidate = new FileCandidate(path, "abc.txt", info.Length, info.LastWriteTimeUtc);

        var hash = await new FileScanner().ComputeStableHashAsync(candidate, CancellationToken.None);

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("nested/../../secret.txt")]
    [InlineData("/absolute.txt")]
    public void RestorePath_RejectsTraversal(string path)
        => Assert.ThrowsAny<Exception>(() => PathSafety.ResolveRestorePath(Path.GetTempPath(), path));

    [Fact]
    public void SourceAndVault_CannotContainEachOther()
    {
        using var temporary = new TemporaryDirectory();
        var nested = Path.Combine(temporary.Path, "vault");
        Assert.Throws<InvalidOperationException>(() => PathSafety.ValidateSourceAndVault(temporary.Path, nested));
    }

    [Fact]
    public async Task VaultUnlock_RejectsWrongPassword()
    {
        using var temporary = new TemporaryDirectory();
        var manager = new VaultManager();
        using var vault = await manager.CreateAsync(temporary.Path, "Test", "correct-password", 210_000, CancellationToken.None);

        await Assert.ThrowsAsync<VaultAuthenticationException>(() =>
            manager.UnlockAsync(temporary.Path, "wrong-password", CancellationToken.None));
    }

    [Fact]
    public void Scheduler_RejectsUnsafeIntervals()
    {
        var scheduler = new BackupScheduler();
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Start(TimeSpan.FromSeconds(5), _ => Task.CompletedTask));
        Assert.False(scheduler.IsRunning);
    }

    [Fact]
    public async Task Recovery_ContainsTraversalAsPartialFailure()
    {
        using var temporary = new TemporaryDirectory();
        using var vault = new UnlockedVault(Guid.NewGuid(), temporary.Path, RandomNumberGenerator.GetBytes(32));
        var snapshotId = Guid.NewGuid();
        var manifest = new SnapshotManifest(1, vault.VaultId, snapshotId, DateTimeOffset.UtcNow, "source",
            [new SnapshotFile("../escape.txt", new string('a', 64), 1, DateTimeOffset.UtcNow)], 1);
        var service = new RecoveryService(new NoOpObjectStore(), new StubMetadataStore(manifest));

        var result = await service.RestoreAsync(vault, snapshotId, null, temporary.Path, false, null, CancellationToken.None);

        Assert.Equal(0, result.RestoredFiles);
        Assert.Single(result.Failures);
    }

    private sealed class NoOpObjectStore : IVaultObjectStore
    {
        public Task<IAsyncDisposable> AcquireWriteLockAsync(UnlockedVault vault, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ContainsObjectAsync(UnlockedVault vault, string contentHash, CancellationToken cancellationToken) => throw new NotSupportedException();
        public long GetPhysicalBytes(string vaultRoot) => 0;
        public Task<IReadOnlyList<string>> ListManifestPathsAsync(UnlockedVault vault, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SnapshotManifest> ReadManifestAsync(UnlockedVault vault, string manifestPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RestoreObjectAsync(UnlockedVault vault, string contentHash, string destinationPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoreObjectAsync(UnlockedVault vault, string contentHash, string sourcePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> WriteManifestAsync(UnlockedVault vault, SnapshotManifest manifest, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubMetadataStore(SnapshotManifest manifest) : IVaultMetadataStore
    {
        public Task BeginSnapshotAsync(Guid snapshotId, DateTimeOffset createdUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteSnapshotAsync(SnapshotManifest value, long newStoredBytes, string manifestPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailSnapshotAsync(Guid snapshotId, SnapshotState state, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VaultAnalytics> GetAnalyticsAsync(long physicalBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DuplicateGroup>> GetDuplicatesAsync(Guid snapshotId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SnapshotManifest?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken) => Task.FromResult<SnapshotManifest?>(manifest);
        public Task<IReadOnlyList<SnapshotSummary>> GetSnapshotsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InitializeAsync(string vaultRoot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RebuildAsync(IEnumerable<(SnapshotManifest Manifest, string ManifestPath)> manifests, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vaultsync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
