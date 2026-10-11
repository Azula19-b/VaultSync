using Microsoft.EntityFrameworkCore;

namespace VaultSync.Infrastructure;

public sealed class VaultDbContext(DbContextOptions<VaultDbContext> options) : DbContext(options)
{
    public DbSet<VaultEntity> Vaults => Set<VaultEntity>();
    public DbSet<ProtectedFolderEntity> ProtectedFolders => Set<ProtectedFolderEntity>();
    public DbSet<BackupJobEntity> BackupJobs => Set<BackupJobEntity>();
    public DbSet<SnapshotEntity> BackupSnapshots => Set<SnapshotEntity>();
    public DbSet<SnapshotFileEntity> SnapshotFiles => Set<SnapshotFileEntity>();
    public DbSet<StoredObjectEntity> StoredObjects => Set<StoredObjectEntity>();
    public DbSet<BackupEventEntity> BackupEvents => Set<BackupEventEntity>();
    public DbSet<ApplicationSettingEntity> ApplicationSettings => Set<ApplicationSettingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VaultEntity>().HasIndex(entity => entity.ExternalId).IsUnique();
        modelBuilder.Entity<ProtectedFolderEntity>().HasIndex(entity => new { entity.VaultEntityId, entity.SourcePath }).IsUnique();
        modelBuilder.Entity<SnapshotEntity>().HasIndex(entity => entity.CreatedUtc);
        modelBuilder.Entity<SnapshotFileEntity>().HasIndex(entity => new { entity.SnapshotEntityId, entity.RelativePath }).IsUnique();
        modelBuilder.Entity<SnapshotFileEntity>().HasIndex(entity => entity.ContentHash);
        modelBuilder.Entity<StoredObjectEntity>().HasIndex(entity => entity.ContentHash).IsUnique();
        modelBuilder.Entity<BackupEventEntity>().HasIndex(entity => entity.CreatedUtc);
        modelBuilder.Entity<ApplicationSettingEntity>().HasIndex(entity => entity.Key).IsUnique();
    }
}

public sealed class VaultEntity
{
    public long Id { get; set; }
    public Guid ExternalId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class ProtectedFolderEntity
{
    public long Id { get; set; }
    public long VaultEntityId { get; set; }
    public required string SourcePath { get; set; }
    public bool AutomaticBackup { get; set; }
    public int IntervalMinutes { get; set; }
}

public sealed class BackupJobEntity
{
    public long Id { get; set; }
    public Guid ExternalId { get; set; }
    public required string State { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? Error { get; set; }
}

public sealed class SnapshotEntity
{
    public long Id { get; set; }
    public Guid ExternalId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public required string State { get; set; }
    public required string SourceName { get; set; }
    public int FileCount { get; set; }
    public long LogicalBytes { get; set; }
    public long NewStoredBytes { get; set; }
    public string? ManifestPath { get; set; }
    public string? Error { get; set; }
    public List<SnapshotFileEntity> Files { get; set; } = [];
}

public sealed class SnapshotFileEntity
{
    public long Id { get; set; }
    public long SnapshotEntityId { get; set; }
    public required string RelativePath { get; set; }
    public required string ContentHash { get; set; }
    public long Size { get; set; }
    public DateTimeOffset LastWriteUtc { get; set; }
}

public sealed class StoredObjectEntity
{
    public long Id { get; set; }
    public required string ContentHash { get; set; }
    public long PlaintextSize { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class BackupEventEntity
{
    public long Id { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public required string Severity { get; set; }
    public required string Message { get; set; }
}

public sealed class ApplicationSettingEntity
{
    public long Id { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
}

