using System.Security.Cryptography;
using System.Text.Json;
using VaultSync.Application;
using VaultSync.Core;

namespace VaultSync.Infrastructure;

public sealed class VaultObjectStore : IVaultObjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<bool> ContainsObjectAsync(UnlockedVault vault, string contentHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(GetObjectPath(vault.RootPath, contentHash)));
    }

    public async Task StoreObjectAsync(UnlockedVault vault, string contentHash, string sourcePath, CancellationToken cancellationToken)
    {
        var destination = GetObjectPath(vault.RootPath, contentHash);
        if (File.Exists(destination)) return;
        var sourceLength = new FileInfo(sourcePath).Length;
        var drive = new DriveInfo(Path.GetPathRoot(vault.RootPath)!);
        if (drive.AvailableFreeSpace < sourceLength + 10L * 1024 * 1024)
            throw new IOException("The vault destination does not have enough free space for this file.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await ChunkedAesGcm.EncryptAsync(input, output, vault.MasterKey, "object:" + contentHash, cancellationToken).ConfigureAwait(false);
            output.Close();
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { File.Delete(temporary); }
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public async Task RestoreObjectAsync(UnlockedVault vault, string contentHash, string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourcePath = GetObjectPath(vault.RootPath, contentHash);
        if (!File.Exists(sourcePath)) throw new VaultCorruptionException($"Stored object {contentHash} is missing.");
        var temporary = destinationPath + ".vaultsync-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await ChunkedAesGcm.DecryptAsync(source, output, vault.MasterKey, "object:" + contentHash, cancellationToken).ConfigureAwait(false);
                output.Position = 0;
                var restoredHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(output, cancellationToken).ConfigureAwait(false));
                if (!restoredHash.Equals(contentHash, StringComparison.Ordinal))
                    throw new VaultCorruptionException("The restored file hash does not match its manifest.");
            }
            File.Move(temporary, destinationPath, true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public async Task<string> WriteManifestAsync(UnlockedVault vault, SnapshotManifest manifest, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var directory = Path.Combine(vault.RootPath, "manifests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{manifest.SnapshotId:N}.{hash}.vsm");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var input = new MemoryStream(bytes, writable: false);
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await ChunkedAesGcm.EncryptAsync(input, output, vault.MasterKey, "manifest:" + hash, cancellationToken).ConfigureAwait(false);
            output.Close();
            File.Move(temporary, path);
            return path;
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public async Task<SnapshotManifest> ReadManifestAsync(UnlockedVault vault, string manifestPath, CancellationToken cancellationToken)
    {
        var parts = Path.GetFileName(manifestPath).Split('.');
        if (parts.Length != 3 || parts[1].Length != 64) throw new VaultCorruptionException("The manifest file name is invalid.");
        await using var input = File.OpenRead(manifestPath);
        await using var output = new MemoryStream();
        await ChunkedAesGcm.DecryptAsync(input, output, vault.MasterKey, "manifest:" + parts[1], cancellationToken).ConfigureAwait(false);
        var bytes = output.ToArray();
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!actualHash.Equals(parts[1], StringComparison.Ordinal)) throw new VaultCorruptionException("The manifest hash is invalid.");
        return JsonSerializer.Deserialize<SnapshotManifest>(bytes, JsonOptions)
            ?? throw new VaultCorruptionException("The manifest content is invalid.");
    }

    public Task<IReadOnlyList<string>> ListManifestPathsAsync(UnlockedVault vault, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(vault.RootPath, "manifests");
        IReadOnlyList<string> paths = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.vsm").Order(StringComparer.Ordinal).ToList()
            : [];
        return Task.FromResult(paths);
    }

    public long GetPhysicalBytes(string vaultRoot)
        => Directory.Exists(vaultRoot)
            ? Directory.EnumerateFiles(vaultRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
            : 0;

    private static string GetObjectPath(string vaultRoot, string hash)
    {
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("The content hash is invalid.", nameof(hash));
        return Path.Combine(vaultRoot, "objects", hash[..2], hash + ".vso");
    }

    private sealed class AsyncStreamLock(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
    public async Task<IAsyncDisposable> AcquireWriteLockAsync(UnlockedVault vault, CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(vault.RootPath, ".vaultsync.lock");
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await stream.WriteAsync([1], cancellationToken).ConfigureAwait(false);
            return new AsyncStreamLock(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("This vault is already being written by another backup operation.", exception);
        }
    }

