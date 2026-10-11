using Microsoft.Extensions.DependencyInjection;
using VaultSync.Application;

namespace VaultSync.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVaultSync(this IServiceCollection services)
        => services
            .AddSingleton<IFileScanner, FileScanner>()
            .AddSingleton<IVaultObjectStore, VaultObjectStore>()
            .AddSingleton<IVaultMetadataStore, SqliteMetadataStore>()
            .AddSingleton<IVaultManager, VaultManager>()
            .AddSingleton<IBackupService, BackupService>()
            .AddSingleton<IRecoveryService, RecoveryService>()
            .AddSingleton<BackupScheduler>()
            .AddSingleton<VaultImporter>();
}

