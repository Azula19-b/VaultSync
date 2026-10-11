namespace VaultSync.Core;

public static class PathSafety
{
    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A relative path is required.", nameof(relativePath));

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (Path.IsPathRooted(relativePath) || normalized.Split('/').Any(segment => segment is ".." or "." or ""))
            throw new InvalidDataException($"Unsafe relative path: {relativePath}");
        return normalized;
    }

    public static string ResolveRestorePath(string destinationRoot, string relativePath)
    {
        var root = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var safeRelative = NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(root, safeRelative));
        if (!target.StartsWith(root, PathComparison))
            throw new InvalidDataException("The restore path escapes the selected destination.");
        return target;
    }

    public static void ValidateSourceAndVault(string sourcePath, string vaultPath)
    {
        var source = WithSeparator(sourcePath);
        var vault = WithSeparator(vaultPath);
        if (source.Equals(vault, PathComparison) || source.StartsWith(vault, PathComparison) || vault.StartsWith(source, PathComparison))
            throw new InvalidOperationException("The source and vault must not contain one another.");
    }

    private static string WithSeparator(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

