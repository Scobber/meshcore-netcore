using System.Globalization;

namespace MeshCoreNet;

public sealed record GpsFix(double Latitude, double Longitude, bool IsValid, DateTimeOffset Timestamp);

public static class GpsNmeaParser
{
    public static bool TryParse(string? sentence, out GpsFix fix)
    {
        fix = default!;

        if (string.IsNullOrWhiteSpace(sentence))
        {
            return false;
        }

        var trimmed = sentence.Trim();
        if (!trimmed.StartsWith("$GPGGA", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = trimmed.Split(',', StringSplitOptions.None);
        if (parts.Length < 7)
        {
            return false;
        }

        var quality = ParseInt(parts[6]);
        if (quality <= 0)
        {
            fix = new GpsFix(0, 0, false, DateTimeOffset.UtcNow);
            return false;
        }

        if (!TryParseCoordinate(parts[2], parts[3], out var latitude) ||
            !TryParseCoordinate(parts[4], parts[5], out var longitude))
        {
            return false;
        }

        fix = new GpsFix(latitude, longitude, true, DateTimeOffset.UtcNow);
        return true;
    }

    private static bool TryParseCoordinate(string? value, string? hemisphere, out double coordinate)
    {
        coordinate = 0;
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(hemisphere))
        {
            return false;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
        {
            return false;
        }

        var degrees = (int)(raw / 100d);
        var minutes = raw % 100d;
        var result = degrees + (minutes / 60d);

        if (hemisphere.Equals("S", StringComparison.OrdinalIgnoreCase) || hemisphere.Equals("W", StringComparison.OrdinalIgnoreCase))
        {
            result *= -1;
        }

        coordinate = result;
        return true;
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }
}
