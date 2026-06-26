using System.Buffers.Binary;
using System.Text;

namespace MeshCoreNet;

/// <summary>
/// Small protocol helpers shared across packet, advert, channel, and app protocol code.
/// </summary>
public static class MeshUtilities
{
    private static long _uniqueTime;

    public static uint UniqueUnixTime()
    {
        // MeshCore timestamps often double as message ids, so keep them monotonic within the process.
        while (true)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var previous = Interlocked.Read(ref _uniqueTime);
            var next = Math.Max(now, previous + 1);
            if (Interlocked.CompareExchange(ref _uniqueTime, next, previous) == previous)
            {
                return checked((uint)next);
            }
        }
    }

    public static byte[] Pad(ReadOnlySpan<byte> data, int length)
    {
        if (data.Length >= length)
        {
            return data.ToArray();
        }

        var result = new byte[length];
        data.CopyTo(result);
        return result;
    }

    public static byte[] Pad(string text, int length) => Pad(Encoding.UTF8.GetBytes(text), length);

    public static byte[] PadToBlock(ReadOnlySpan<byte> data, int blockSize)
    {
        var padding = (blockSize - data.Length % blockSize) & (blockSize - 1);
        if (padding == 0)
        {
            return data.ToArray();
        }

        var result = new byte[data.Length + padding];
        data.CopyTo(result);
        return result;
    }

    public static IReadOnlyList<byte[]> SplitUtf8String(string value, int maxSize)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var chunks = new List<byte[]>();
        var start = 0;

        while (start < bytes.Length)
        {
            var end = start + maxSize;
            if (end >= bytes.Length)
            {
                chunks.Add(bytes[start..]);
                break;
            }

            var boundary = end;
            // Avoid splitting in the middle of a UTF-8 continuation byte.
            while (boundary > start && (bytes[boundary] & 0xC0) == 0x80)
            {
                boundary--;
            }

            // Prefer word boundaries when possible, matching Python's human-facing text splitting.
            var spaceBoundary = LastIndexOf(bytes.AsSpan(start, boundary - start), (byte)' ');
            if (spaceBoundary >= 0)
            {
                boundary = start + spaceBoundary;
            }

            chunks.Add(bytes[start..boundary]);
            start = boundary + 1;
        }

        return chunks;
    }

    public static (double Latitude, double Longitude) ValidateLatLon(object latitude, object longitude)
    {
        var lat = Convert.ToDouble(latitude, System.Globalization.CultureInfo.InvariantCulture);
        var lon = Convert.ToDouble(longitude, System.Globalization.CultureInfo.InvariantCulture);
        if (lat is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "Latitude must be between -90 and 90.");
        }

        if (lon is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), "Longitude must be between -180 and 180.");
        }

        return (lat, lon);
    }

    public static string PathString(IReadOnlyCollection<byte>? path, bool flood = false)
    {
        if (path is null)
        {
            return "Flood";
        }

        if (path.Count == 0)
        {
            return flood ? "0-hop" : "Direct";
        }

        return string.Join(",", path.Select(hop => hop.ToString("x2")));
    }

    public static byte[] UInt32LittleEndian(uint value)
    {
        var result = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(result, value);
        return result;
    }

    private static int LastIndexOf(ReadOnlySpan<byte> span, byte value)
    {
        for (var index = span.Length - 1; index >= 0; index--)
        {
            if (span[index] == value)
            {
                return index;
            }
        }

        return -1;
    }
}
