using System.Security.Cryptography;

namespace MeshCoreNet;

/// <summary>
/// MeshCore-compatible symmetric crypto helpers.
/// </summary>
public static class MeshCrypto
{
    public static byte[] MacAndDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> mac, ReadOnlySpan<byte> encrypted)
    {
        if (key.Length < 16)
        {
            throw new ArgumentException("MeshCore encryption keys must be at least 16 bytes.", nameof(key));
        }

        if (mac.Length != MeshCoreLimits.CipherMacSize)
        {
            throw new ArgumentException("MeshCore MAC values are two bytes.", nameof(mac));
        }

        // MeshCore stores only the first two HMAC bytes on the wire.
        var expected = HMACSHA256.HashData(key, encrypted);
        if (!CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, MeshCoreLimits.CipherMacSize), mac))
        {
            return [];
        }

        // AES-ECB with no padding is retained for wire compatibility with Python/MeshCore.
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key[..16].ToArray();
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted.ToArray(), 0, encrypted.Length);
    }

    public static bool TryMacAndDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> mac, ReadOnlySpan<byte> encrypted, out byte[] plaintext)
    {
        plaintext = MacAndDecrypt(key, mac, encrypted);
        return plaintext.Length != 0 || encrypted.Length == 0;
    }

    public static byte[] EncryptAndMac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
    {
        if (key.Length < 16)
        {
            throw new ArgumentException("MeshCore encryption keys must be at least 16 bytes.", nameof(key));
        }

        // The plaintext is null padded before ECB encryption; callers own semantic trimming.
        var padded = MeshUtilities.PadToBlock(message, 16);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key[..16].ToArray();
        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(padded, 0, padded.Length);
        var hmac = HMACSHA256.HashData(key, encrypted);

        var result = new byte[MeshCoreLimits.CipherMacSize + encrypted.Length];
        hmac.AsSpan(0, MeshCoreLimits.CipherMacSize).CopyTo(result);
        encrypted.CopyTo(result, MeshCoreLimits.CipherMacSize);
        return result;
    }

    public static byte[] AckHash(ReadOnlySpan<byte> data)
    {
        // ACKs use the first four SHA-256 bytes of the original packet data.
        var hash = SHA256.HashData(data);
        return hash[..4];
    }
}
