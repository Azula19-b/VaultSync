using Microsoft.EntityFrameworkCore;
using VaultSync.Application;
using VaultSync.Core;

namespace VaultSync.Infrastructure;

public sealed class SqliteMetadataStore : IVaultMetadataStore
{
    private string? _databasePath;

    public async Task InitializeAsync(string vaultRoot, CancellationToken cancellationToken)
    {
        _databasePath = Path.Combine(Path.GetFullPath(vaultRoot), "vault-index.db");
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task BeginSnapshotAsync(Guid snapshotId, DateTimeOffset createdUtc, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        context.BackupSnapshots.Add(new SnapshotEntity
        {
            ExternalId = snapshotId,
            CreatedUtc = createdUtc,
            State = SnapshotState.Pending.ToString(),
            SourceName = string.Empty
        });
        context.BackupEvents.Add(new() { CreatedUtc = createdUtc, Severity = "Info", Message = $"Backup {snapshotId:N} started." });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteSnapshotAsync(SnapshotManifest manifest, long newStoredBytes, string manifestPath,
        CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await context.BackupSnapshots.SingleAsync(entity => entity.ExternalId == manifest.SnapshotId, cancellationToken).ConfigureAwait(false);
        snapshot.State = SnapshotState.Completed.ToString();
        snapshot.SourceName = manifest.SourceName;
        snapshot.FileCount = manifest.Files.Count;
        snapshot.LogicalBytes = manifest.LogicalBytes;
        snapshot.NewStoredBytes = newStoredBytes;
        snapshot.ManifestPath = manifestPath;
        var knownHashes = (await context.StoredObjects.Select(entity => entity.ContentHash).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            snapshot.Files.Add(new()
            {
                RelativePath = file.RelativePath,
                ContentHash = file.ContentHash,
                Size = file.Size,
                LastWriteUtc = file.LastWriteUtc
            });
            if (knownHashes.Add(file.ContentHash))
                context.StoredObjects.Add(new() { ContentHash = file.ContentHash, PlaintextSize = file.Size, CreatedUtc = manifest.CreatedUtc });
        }
        context.BackupEvents.Add(new() { CreatedUtc = DateTimeOffset.UtcNow, Severity = "Success", Message = $"Backup completed with {manifest.Files.Count} files." });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailSnapshotAsync(Guid snapshotId, SnapshotState state, string error, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        var snapshot = await context.BackupSnapshots.SingleOrDefaultAsync(entity => entity.ExternalId == snapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return;
        snapshot.State = state.ToString();
        snapshot.Error = error;
        context.BackupEvents.Add(new() { CreatedUtc = DateTimeOffset.UtcNow, Severity = "Error", Message = error });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SnapshotSummary>> GetSnapshotsAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        return await context.BackupSnapshots.AsNoTracking().OrderByDescending(entity => entity.CreatedUtc)
            .Select(entity => new SnapshotSummary(entity.ExternalId, entity.CreatedUtc,
                Enum.Parse<SnapshotState>(entity.State), entity.FileCount, entity.LogicalBytes, entity.NewStoredBytes, entity.Error))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SnapshotManifest?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        var snapshot = await context.BackupSnapshots.AsNoTracking().Include(entity => entity.Files)
            .SingleOrDefaultAsync(entity => entity.ExternalId == snapshotId && entity.State == nameof(SnapshotState.Completed), cancellationToken)
            .ConfigureAwait(false);
        return snapshot is null ? null : new(1, Guid.Empty, snapshot.ExternalId, snapshot.CreatedUtc, snapshot.SourceName,
            snapshot.Files.OrderBy(file => file.RelativePath).Select(file => new SnapshotFile(file.RelativePath, file.ContentHash, file.Size, file.LastWriteUtc)).ToList(),
            snapshot.LogicalBytes);
    }

    public async Task<IReadOnlyList<DuplicateGroup>> GetDuplicatesAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        var files = await context.SnapshotFiles.AsNoTracking()
            .Where(file => file.SnapshotEntityId == context.BackupSnapshots.Where(snapshot => snapshot.ExternalId == snapshotId).Select(snapshot => snapshot.Id).Single())
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return files.GroupBy(file => file.ContentHash, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new DuplicateGroup(group.Key, group.First().Size, group.Select(file => file.RelativePath).Order(StringComparer.Ordinal).ToList()))
            .OrderByDescending(group => group.PotentialSavings).ToList();
    }

    public async Task<VaultAnalytics> GetAnalyticsAsync(long physicalBytes, CancellationToken cancellationToken)
    {
        var snapshots = await GetSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        var completed = snapshots.Where(snapshot => snapshot.State == SnapshotState.Completed).ToList();
        var latest = completed.FirstOrDefault();
        var attempts = snapshots.Count(snapshot => snapshot.State != SnapshotState.Pending);
        var rate = attempts == 0 ? 0 : completed.Count * 100d / attempts;
        long uniqueBytes = 0;
        if (latest is not null)
        {
            await using var context = CreateContext();
            uniqueBytes = await context.SnapshotFiles.AsNoTracking()
                .Where(file => file.SnapshotEntityId == context.BackupSnapshots.Where(snapshot => snapshot.ExternalId == latest.Id).Select(snapshot => snapshot.Id).Single())
                .GroupBy(file => file.ContentHash).Select(group => group.Max(file => file.Size)).SumAsync(cancellationToken).ConfigureAwait(false);
        }
        return new(latest?.FileCount ?? 0, completed.Count, latest?.LogicalBytes ?? 0, physicalBytes,
            Math.Max(0, (latest?.LogicalBytes ?? 0) - uniqueBytes), latest?.CreatedUtc, rate, snapshots.Take(10).ToList());
    }

    public async Task RebuildAsync(IEnumerable<(SnapshotManifest Manifest, string ManifestPath)> manifests, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        context.SnapshotFiles.RemoveRange(context.SnapshotFiles);
        context.BackupSnapshots.RemoveRange(context.BackupSnapshots);
        context.StoredObjects.RemoveRange(context.StoredObjects);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var knownHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in manifests.OrderBy(item => item.Manifest.CreatedUtc))
        {
            var entity = new SnapshotEntity
            {
                ExternalId = item.Manifest.SnapshotId,
                CreatedUtc = item.Manifest.CreatedUtc,
                State = SnapshotState.Completed.ToString(),
                SourceName = item.Manifest.SourceName,
                FileCount = item.Manifest.Files.Count,
                LogicalBytes = item.Manifest.LogicalBytes,
                ManifestPath = item.ManifestPath,
                Files = item.Manifest.Files.Select(file => new SnapshotFileEntity
                {
                    RelativePath = file.RelativePath,
                    ContentHash = file.ContentHash,
                    Size = file.Size,
                    LastWriteUtc = file.LastWriteUtc
                }).ToList()
            };
            context.BackupSnapshots.Add(entity);
            foreach (var file in item.Manifest.Files)
                if (knownHashes.Add(file.ContentHash))
                    context.StoredObjects.Add(new() { ContentHash = file.ContentHash, PlaintextSize = file.Size, CreatedUtc = item.Manifest.CreatedUtc });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private VaultDbContext CreateContext()
    {
        if (_databasePath is null) throw new InvalidOperationException("Initialize the metadata store before using it.");
        var options = new DbContextOptionsBuilder<VaultDbContext>()
            .UseSqlite($"Data Source={_databasePath};Mode=ReadWriteCreate;Cache=Shared")
            .EnableSensitiveDataLogging(false).Options;
        return new(options);
    }
}
