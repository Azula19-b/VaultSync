using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VaultSync.Core;

namespace VaultSync.Infrastructure;

internal static class ChunkedAesGcm
{
    private static readonly byte[] Magic = "VSO1"u8.ToArray();
    private const int DefaultChunkSize = 1024 * 1024;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static async Task<string> EncryptAsync(Stream plaintext, Stream destination, byte[] key, string context,
        CancellationToken cancellationToken)
    {
        var length = plaintext.Length - plaintext.Position;
        await destination.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
        await WriteInt32Async(destination, DefaultChunkSize, cancellationToken).ConfigureAwait(false);
        await WriteInt64Async(destination, length, cancellationToken).ConfigureAwait(false);

        var buffer = ArrayPool<byte>.Shared.Rent(DefaultChunkSize);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunkIndex = 0;
            while (true)
            {
                var read = await ReadChunkAsync(plaintext, buffer, DefaultChunkSize, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                var ciphertext = new byte[read];
                var tag = new byte[TagSize];
                aes.Encrypt(nonce, buffer.AsSpan(0, read), ciphertext, tag, BuildAssociatedData(context, chunkIndex, read));
                await destination.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
                await WriteInt32Async(destination, read, cancellationToken).ConfigureAwait(false);
                await destination.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
                await destination.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
                chunkIndex++;
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, DefaultChunkSize));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task DecryptAsync(Stream source, Stream plaintext, byte[] key, string context,
        CancellationToken cancellationToken)
    {
        var magic = new byte[Magic.Length];
        await ReadExactlyAsync(source, magic, cancellationToken).ConfigureAwait(false);
        if (!magic.SequenceEqual(Magic)) throw new VaultCorruptionException("The encrypted object header is invalid.");
        var chunkSize = await ReadInt32Async(source, cancellationToken).ConfigureAwait(false);
        var expectedLength = await ReadInt64Async(source, cancellationToken).ConfigureAwait(false);
        if (chunkSize is < 4096 or > 16 * 1024 * 1024 || expectedLength < 0)
            throw new VaultCorruptionException("The encrypted object dimensions are invalid.");

        long written = 0;
        var chunkIndex = 0;
        using var aes = new AesGcm(key, TagSize);
        try
        {
            while (written < expectedLength)
            {
                var nonce = new byte[NonceSize];
                await ReadExactlyAsync(source, nonce, cancellationToken).ConfigureAwait(false);
                var length = await ReadInt32Async(source, cancellationToken).ConfigureAwait(false);
                if (length <= 0 || length > chunkSize || written + length > expectedLength)
                    throw new VaultCorruptionException("An encrypted chunk has an invalid length.");
                var ciphertext = new byte[length];
                var tag = new byte[TagSize];
                var cleartext = new byte[length];
                await ReadExactlyAsync(source, ciphertext, cancellationToken).ConfigureAwait(false);
                await ReadExactlyAsync(source, tag, cancellationToken).ConfigureAwait(false);
                aes.Decrypt(nonce, ciphertext, tag, cleartext, BuildAssociatedData(context, chunkIndex, length));
                await plaintext.WriteAsync(cleartext, cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(cleartext);
                written += length;
                chunkIndex++;
            }
            if (source.ReadByte() != -1) throw new VaultCorruptionException("The encrypted object contains trailing data.");
            await plaintext.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new VaultCorruptionException("Encrypted content authentication failed.", exception);
        }
        catch (EndOfStreamException exception)
        {
            throw new VaultCorruptionException("The encrypted object is truncated.", exception);
        }
    }

    private static byte[] BuildAssociatedData(string context, int chunkIndex, int length)
    {
        var contextBytes = Encoding.UTF8.GetBytes(context);
        var aad = new byte[contextBytes.Length + 8];
        contextBytes.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(contextBytes.Length), chunkIndex);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(contextBytes.Length + 4), length);
        return aad;
    }

    private static async Task<int> ReadChunkAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
        => await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);

    private static async Task WriteInt32Async(Stream stream, int value, CancellationToken token)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        await stream.WriteAsync(buffer, token).ConfigureAwait(false);
    }

    private static async Task<int> ReadInt32Async(Stream stream, CancellationToken token)
    {
        var buffer = new byte[4];
        await ReadExactlyAsync(stream, buffer, token).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task WriteInt64Async(Stream stream, long value, CancellationToken token)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        await stream.WriteAsync(buffer, token).ConfigureAwait(false);
    }

    private static async Task<long> ReadInt64Async(Stream stream, CancellationToken token)
    {
        var buffer = new byte[8];
        await ReadExactlyAsync(stream, buffer, token).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }
}
