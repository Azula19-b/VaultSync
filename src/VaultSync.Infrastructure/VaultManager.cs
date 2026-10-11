using System.Security.Cryptography;
using System.Text.Json;
using VaultSync.Application;
using VaultSync.Core;

namespace VaultSync.Infrastructure;

public sealed class VaultManager : IVaultManager
{
    public const string HeaderFileName = "vault.json";
    public const int DefaultKdfIterations = 600_000;

    public async Task<UnlockedVault> CreateAsync(string vaultRoot, string name, string password, int kdfIterations,
        CancellationToken cancellationToken)
    {
        ValidatePassword(password);
        if (kdfIterations < 210_000) throw new ArgumentOutOfRangeException(nameof(kdfIterations), "The KDF work factor is too low.");
        vaultRoot = Path.GetFullPath(vaultRoot);
        Directory.CreateDirectory(vaultRoot);
        var headerPath = Path.Combine(vaultRoot, HeaderFileName);
        if (File.Exists(headerPath)) throw new InvalidOperationException("A vault already exists at this location.");

        var vaultId = Guid.NewGuid();
        var salt = RandomNumberGenerator.GetBytes(32);
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var encryptedKey = new byte[masterKey.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var kek = Rfc2898DeriveBytes.Pbkdf2(password, salt, kdfIterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(kek, tag.Length);
            aes.Encrypt(nonce, masterKey, encryptedKey, tag, vaultId.ToByteArray());
            var header = new VaultHeader(1, vaultId, string.IsNullOrWhiteSpace(name) ? "VaultSync Vault" : name.Trim(),
                salt, kdfIterations, nonce, encryptedKey, tag, DateTimeOffset.UtcNow);
            var temporaryPath = headerPath + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(header, JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, headerPath);
            Directory.CreateDirectory(Path.Combine(vaultRoot, "objects"));
            Directory.CreateDirectory(Path.Combine(vaultRoot, "manifests"));
            return new(vaultId, vaultRoot, masterKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public async Task<UnlockedVault> UnlockAsync(string vaultRoot, string password, CancellationToken cancellationToken)
    {
        var headerPath = Path.Combine(Path.GetFullPath(vaultRoot), HeaderFileName);
        if (!File.Exists(headerPath)) throw new FileNotFoundException("No VaultSync header was found.", headerPath);
        var json = await File.ReadAllTextAsync(headerPath, cancellationToken).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<VaultHeader>(json, JsonOptions)
            ?? throw new VaultCorruptionException("The vault header is invalid.");
        if (header.FormatVersion != 1) throw new VaultCorruptionException("This vault format is not supported.");

        var kek = Rfc2898DeriveBytes.Pbkdf2(password, header.Salt, header.KdfIterations, HashAlgorithmName.SHA256, 32);
        var masterKey = new byte[32];
        try
        {
            using var aes = new AesGcm(kek, header.KeyTag.Length);
            aes.Decrypt(header.KeyNonce, header.EncryptedMasterKey, header.KeyTag, masterKey, header.VaultId.ToByteArray());
            return new(header.VaultId, Path.GetFullPath(vaultRoot), masterKey);
        }
        catch (AuthenticationTagMismatchException exception)
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw new VaultAuthenticationException("The password is incorrect or the vault header was modified.") { Source = exception.Source };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
            throw new ArgumentException("Use a password with at least 10 characters.", nameof(password));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

