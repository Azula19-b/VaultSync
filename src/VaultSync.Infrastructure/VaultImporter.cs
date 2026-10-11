using VaultSync.Application;
using VaultSync.Core;

namespace VaultSync.Infrastructure;

public sealed class VaultImporter(IVaultObjectStore objects, IVaultMetadataStore metadata)
{
    public async Task<int> RebuildIndexAsync(UnlockedVault vault, CancellationToken cancellationToken)
    {
        await metadata.InitializeAsync(vault.RootPath, cancellationToken).ConfigureAwait(false);
        var paths = await objects.ListManifestPathsAsync(vault, cancellationToken).ConfigureAwait(false);
        var manifests = new List<(SnapshotManifest, string)>(paths.Count);
        foreach (var path in paths)
            manifests.Add((await objects.ReadManifestAsync(vault, path, cancellationToken).ConfigureAwait(false), path));
        await metadata.RebuildAsync(manifests, cancellationToken).ConfigureAwait(false);
        return manifests.Count;
    }
}

