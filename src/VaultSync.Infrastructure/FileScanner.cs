using System.Security.Cryptography;
using VaultSync.Application;
using VaultSync.Core;

namespace VaultSync.Infrastructure;

public sealed class FileScanner : IFileScanner
{
    public Task<IReadOnlyList<FileCandidate>> ScanAsync(string sourcePath, IReadOnlyCollection<string> exclusions,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(sourcePath);
        var result = new List<FileCandidate>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var relative = Path.GetRelativePath(root, child).Replace('\\', '/');
                if (IsExcluded(relative, exclusions)) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(child);
                else
                {
                    var info = new FileInfo(child);
                    result.Add(new(info.FullName, PathSafety.NormalizeRelativePath(relative), info.Length, info.LastWriteTimeUtc));
                }
            }
        }
        return Task.FromResult<IReadOnlyList<FileCandidate>>(result.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList());
    }

    public async Task<string> ComputeStableHashAsync(FileCandidate file, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = new FileInfo(file.FullPath);
            var beforeLength = before.Length;
            var beforeWrite = before.LastWriteTimeUtc;
            await using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var after = new FileInfo(file.FullPath);
            if (beforeLength == after.Length && beforeWrite == after.LastWriteTimeUtc)
                return Convert.ToHexStringLower(hash);
        }
        throw new SourceChangedException(file.FullPath);
    }

    private static bool IsExcluded(string relativePath, IReadOnlyCollection<string> exclusions)
    {
        var path = relativePath.Replace('\\', '/');
        foreach (var raw in exclusions)
        {
            var pattern = raw.Trim().Replace('\\', '/').TrimStart('/');
            if (pattern.Length == 0) continue;
            if (pattern.StartsWith("*.", StringComparison.Ordinal) && path.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Equals(pattern.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(pattern.TrimEnd('/') + '/', StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

