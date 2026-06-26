using System.Numerics;
using System.Security.Cryptography;

namespace MeshCoreNet;

/// <summary>
/// MeshCore Ed25519 key wrapper that accepts both 32-byte seeds and Python-style 64-byte private keys.
/// </summary>
public sealed class MeshEd25519PrivateKey
{
    private readonly byte[] _key;
    private readonly bool _isMeshCoreKey;
    private byte[]? _publicKey;
    private byte[]? _meshCorePrivateKey;

    public MeshEd25519PrivateKey(byte[]? key = null)
    {
        if (key is null)
        {
            while (true)
            {
                // MeshCore avoids public-key hashes 0x00 and 0xff because those values have routing meaning.
                _key = RandomNumberGenerator.GetBytes(32);
                _isMeshCoreKey = false;
                var publicKey = PublicKey;
                if (publicKey[0] is not 0x00 and not 0xff)
                {
                    break;
                }

                _publicKey = null;
                _meshCorePrivateKey = null;
            }

            return;
        }

        if (key.Length == 32)
        {
            _key = key.ToArray();
            _isMeshCoreKey = false;
            return;
        }

        if (key.Length == 64)
        {
            _key = key.ToArray();
            _isMeshCoreKey = true;
            return;
        }

        throw new ArgumentException("MeshCore Ed25519 private keys must be either 32-byte seeds or 64-byte MeshCore keys.", nameof(key));
    }

    public byte[] PrivateKey => _key.ToArray();

    public byte[] MeshCorePrivateKey => _meshCorePrivateKey ??= _isMeshCoreKey ? _key.ToArray() : SHA512.HashData(_key);

    public byte[] PublicKey => _publicKey ??= Ed25519Math.PublicKey(MeshCorePrivateKey);

    public byte[] Sign(byte[] message) => Ed25519Math.Sign(MeshCorePrivateKey, PublicKey, message);

    public byte[] SharedSecret(byte[] otherPublicKey)
    {
        if (otherPublicKey.Length != 32)
        {
            throw new ArgumentException("Public keys must be 32 bytes.", nameof(otherPublicKey));
        }

        return Ed25519Math.SharedSecret(MeshCorePrivateKey.AsSpan(0, 32), otherPublicKey);
    }

    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey.Length != 32)
        {
            throw new ArgumentException("Public keys must be 32 bytes.", nameof(publicKey));
        }

        if (signature.Length != 64)
        {
            throw new ArgumentException("Ed25519 signatures must be 64 bytes.", nameof(signature));
        }

        return Ed25519Math.Verify(publicKey, message, signature);
    }
}

internal static class Ed25519Math
{
    // This small implementation mirrors the pure-Python math used by the original project.
    // Keep it fixture-covered before replacing it with a library.
    private static readonly BigInteger Q = (BigInteger.One << 255) - 19;
    private static readonly BigInteger L = (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inv(121666));
    private static readonly BigInteger I = BigInteger.ModPow(2, (Q - 1) / 4, Q);
    private static readonly Point BasePoint = CreateBasePoint();
    private static readonly Point Zero = new(0, 1, 1, 0);

    public static byte[] PublicKey(ReadOnlySpan<byte> meshCorePrivateKey)
    {
        var scalar = BytesToClampedScalar(meshCorePrivateKey[..32]);
        return EncodePoint(ScalarMult(BasePoint, scalar));
    }

    public static byte[] Sign(ReadOnlySpan<byte> meshCorePrivateKey, byte[] publicKey, byte[] message)
    {
        var a = BytesToClampedScalar(meshCorePrivateKey[..32]);
        var inter = meshCorePrivateKey[32..64].ToArray();
        var r = Hint(Concat(inter, message));
        var rBytes = EncodePoint(ScalarMult(BasePoint, r));
        var s = r + Hint(Concat(rBytes, publicKey, message)) * a;
        return Concat(rBytes, ScalarToBytes(s));
    }

    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        try
        {
            var r = DecodePoint(signature[..32]);
            var a = DecodePoint(publicKey);
            var s = BytesToScalar(signature.AsSpan(32, 32));
            var h = Hint(Concat(signature[..32], publicKey, message));
            var v1 = ScalarMult(BasePoint, s);
            var v2 = Add(r, ScalarMult(a, h));
            return EncodePoint(v1).SequenceEqual(EncodePoint(v2));
        }
        catch
        {
            return false;
        }
    }

    public static byte[] SharedSecret(ReadOnlySpan<byte> privateKeyPrefix, ReadOnlySpan<byte> otherPublicKey)
    {
        // MeshCore derives the shared secret through the Montgomery form of the peer Ed25519 key.
        var e = ToBigInteger(privateKeyPrefix);
        e &= (BigInteger.One << 254) - 8;
        e |= BigInteger.One << 254;
        e &= ~BigInteger.Pow(2, 255);

        var edwardsY = ToBigInteger(otherPublicKey);
        edwardsY &= ~(BigInteger.One << 255);

        var x1 = Mod((edwardsY + 1) * Inv(1 - edwardsY));
        var x2 = BigInteger.One;
        var z2 = BigInteger.Zero;
        var x3 = x1;
        var z3 = BigInteger.One;
        var swap = 0;

        for (var pos = 254; pos >= 0; pos--)
        {
            var bit = (int)((e >> pos) & BigInteger.One);
            swap ^= bit;
            if (swap != 0)
            {
                (x2, x3) = (x3, x2);
                (z2, z3) = (z3, z2);
            }

            swap = bit;
            var tmp0 = Mod(x3 - z3);
            var tmp1 = Mod(x2 - z2);
            x2 = Mod(x2 + z2);
            z2 = Mod(x3 + z3);
            z3 = Mod(tmp0 * x2);
            z2 = Mod(z2 * tmp1);
            tmp0 = Mod(tmp1 * tmp1);
            tmp1 = Mod(x2 * x2);
            x3 = Mod(z3 + z2);
            z2 = Mod(z3 - z2);
            x2 = Mod(tmp1 * tmp0);
            tmp1 = Mod(tmp1 - tmp0);
            z2 = Mod(z2 * z2);
            z3 = Mod(tmp1 * 121666);
            x3 = Mod(x3 * x3);
            tmp0 = Mod(tmp0 + z3);
            z3 = Mod(x1 * z2);
            z2 = Mod(tmp1 * tmp0);
        }

        if (swap != 0)
        {
            (x2, x3) = (x3, x2);
            (z2, z3) = (z3, z2);
        }

        z2 = Inv(z2);
        x2 = Mod(x2 * z2);
        return ToLittleEndian(x2, 32);
    }

    private static Point CreateBasePoint()
    {
        var by = Mod(4 * Inv(5));
        var bx = XRecover(by);
        return FromAffine(bx, by);
    }

    private static BigInteger XRecover(BigInteger y)
    {
        var xx = Mod((y * y - 1) * Inv(D * y * y + 1));
        var x = BigInteger.ModPow(xx, (Q + 3) / 8, Q);
        if (Mod(x * x - xx) != 0)
        {
            x = Mod(x * I);
        }

        if (!x.IsEven)
        {
            x = Q - x;
        }

        return x;
    }

    private static Point FromAffine(BigInteger x, BigInteger y) => new(Mod(x), Mod(y), 1, Mod(x * y));

    private static (BigInteger X, BigInteger Y) ToAffine(Point point)
    {
        var invZ = Inv(point.Z);
        return (Mod(point.X * invZ), Mod(point.Y * invZ));
    }

    private static Point Double(Point point)
    {
        var a = Mod(point.X * point.X);
        var b = Mod(point.Y * point.Y);
        var c = Mod(2 * point.Z * point.Z);
        var d = Mod(-a);
        var j = Mod(point.X + point.Y);
        var e = Mod(j * j - a - b);
        var g = Mod(d + b);
        var f = Mod(g - c);
        var h = Mod(d - b);
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Add(Point left, Point right)
    {
        var a = Mod((left.Y - left.X) * (right.Y - right.X));
        var b = Mod((left.Y + left.X) * (right.Y + right.X));
        var c = Mod(left.T * 2 * D * right.T);
        var d = Mod(left.Z * 2 * right.Z);
        var e = Mod(b - a);
        var f = Mod(d - c);
        var g = Mod(d + c);
        var h = Mod(b + a);
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point ScalarMult(Point point, BigInteger scalar)
    {
        var result = Zero;
        var addend = point;
        var n = scalar;
        while (n > 0)
        {
            if (!n.IsEven)
            {
                result = Add(result, addend);
            }

            addend = Double(addend);
            n >>= 1;
        }

        return result;
    }

    private static byte[] EncodePoint(Point point)
    {
        var (x, y) = ToAffine(point);
        var output = ToLittleEndian(y, 32);
        if (!x.IsEven)
        {
            output[31] |= 0x80;
        }

        return output;
    }

    private static Point DecodePoint(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != 32)
        {
            throw new ArgumentException("Encoded points must be 32 bytes.", nameof(encoded));
        }

        var bytes = encoded.ToArray();
        var sign = (bytes[31] & 0x80) != 0;
        bytes[31] &= 0x7f;
        var y = ToBigInteger(bytes);
        var x = XRecover(y);
        if (!x.IsEven != sign)
        {
            x = Q - x;
        }

        if (!IsOnCurve(x, y))
        {
            throw new InvalidOperationException("Decoded point is not on the Ed25519 curve.");
        }

        return FromAffine(x, y);
    }

    private static bool IsOnCurve(BigInteger x, BigInteger y)
    {
        return Mod(-x * x + y * y - 1 - D * x * x * y * y) == 0;
    }

    private static BigInteger BytesToScalar(ReadOnlySpan<byte> value) => ToBigInteger(value);

    private static BigInteger BytesToClampedScalar(ReadOnlySpan<byte> value)
    {
        // Standard Ed25519 scalar clamping.
        var scalar = BytesToScalar(value);
        var andClamp = (BigInteger.One << 254) - 1 - 7;
        var orClamp = BigInteger.One << 254;
        return (scalar & andClamp) | orClamp;
    }

    private static byte[] ScalarToBytes(BigInteger value) => ToLittleEndian(ModL(value), 32);

    private static BigInteger Hint(byte[] message) => ToBigInteger(SHA512.HashData(message));

    private static BigInteger Inv(BigInteger value) => BigInteger.ModPow(Mod(value), Q - 2, Q);

    private static BigInteger Mod(BigInteger value)
    {
        var result = value % Q;
        return result.Sign < 0 ? result + Q : result;
    }

    private static BigInteger ModL(BigInteger value)
    {
        var result = value % L;
        return result.Sign < 0 ? result + L : result;
    }

    private static BigInteger ToBigInteger(ReadOnlySpan<byte> littleEndian)
    {
        return new BigInteger(littleEndian, isUnsigned: true, isBigEndian: false);
    }

    private static byte[] ToLittleEndian(BigInteger value, int length)
    {
        var output = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        if (output.Length == length)
        {
            return output;
        }

        if (output.Length > length)
        {
            return output[..length];
        }

        Array.Resize(ref output, length);
        return output;
    }

    private static byte[] Concat(params byte[][] values)
    {
        var length = values.Sum(value => value.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var value in values)
        {
            value.CopyTo(result.AsSpan(offset));
            offset += value.Length;
        }

        return result;
    }

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);
}
