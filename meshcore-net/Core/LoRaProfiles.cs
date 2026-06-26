namespace MeshCoreNet;

public sealed record LoRaProfile(string Name, uint Frequency, byte SpreadingFactor, uint Bandwidth, byte CodingRate, sbyte TxPower)
{
    public static IReadOnlyDictionary<string, LoRaProfile> Catalog { get; } = BuildCatalog();

    public static bool TryResolve(string? profileOrRegion, out LoRaProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profileOrRegion))
        {
            profile = default!;
            return false;
        }

        return Catalog.TryGetValue(Normalize(profileOrRegion), out profile!);
    }

    public static LoRaProfile ResolveOrDefault(string? profileOrRegion)
    {
        if (TryResolve(profileOrRegion, out var profile))
        {
            return profile;
        }

        return Catalog["eu868-narrow"];
    }

    private static Dictionary<string, LoRaProfile> BuildCatalog()
    {
        var map = new Dictionary<string, LoRaProfile>(StringComparer.OrdinalIgnoreCase);

        static void Add(Dictionary<string, LoRaProfile> target, LoRaProfile profile, params string[] aliases)
        {
            target[Normalize(profile.Name)] = profile;
            foreach (var alias in aliases)
            {
                target[Normalize(alias)] = profile;
            }
        }

        Add(map, new LoRaProfile("eu868-narrow", 869_618_000, 8, 62_500, 8, 22), "eu868", "eu", "eu-narrow", "868", "europe");
        Add(map, new LoRaProfile("eu868-wide", 868_100_000, 8, 125_000, 8, 22), "eu-wide", "europe-wide");

        Add(map, new LoRaProfile("eu433-narrow", 433_175_000, 8, 62_500, 8, 20), "eu433", "eu433-low", "433", "eu433-default");
        Add(map, new LoRaProfile("eu433-wide", 433_175_000, 8, 125_000, 8, 20), "eu433-high", "eu433w");

        Add(map, new LoRaProfile("us915-narrow", 915_000_000, 8, 62_500, 8, 22), "us915", "us", "na915", "us-narrow", "915", "northamerica");
        Add(map, new LoRaProfile("us915-wide", 915_000_000, 8, 125_000, 8, 22), "us-wide", "na915-wide", "northamerica-wide");

        Add(map, new LoRaProfile("au915-narrow", 916_800_000, 8, 62_500, 8, 22), "au915", "au", "australia", "australia-narrow", "au-narrow");
        Add(map, new LoRaProfile("au915-wide", 916_800_000, 8, 125_000, 8, 22), "australia-wide", "au-wide", "au915-wide");

        Add(map, new LoRaProfile("as923-narrow", 923_200_000, 8, 62_500, 8, 16), "as923", "asia", "as923-1", "asia-narrow");
        Add(map, new LoRaProfile("as923-wide", 923_200_000, 8, 125_000, 8, 16), "asia-wide");

        Add(map, new LoRaProfile("in865-narrow", 865_062_500, 8, 62_500, 8, 20), "in865", "india", "india-narrow");
        Add(map, new LoRaProfile("in865-wide", 865_062_500, 8, 125_000, 8, 20), "india-wide");

        Add(map, new LoRaProfile("kr920-narrow", 922_100_000, 8, 62_500, 8, 14), "kr920", "kr", "korea", "korea-narrow");
        Add(map, new LoRaProfile("kr920-wide", 922_100_000, 8, 125_000, 8, 14), "korea-wide");

        Add(map, new LoRaProfile("ru864-narrow", 864_100_000, 8, 62_500, 8, 20), "ru864", "ru", "russia", "russia-narrow");
        Add(map, new LoRaProfile("ru864-wide", 864_100_000, 8, 125_000, 8, 20), "russia-wide");

        Add(map, new LoRaProfile("cn470-narrow", 470_300_000, 8, 62_500, 8, 17), "cn470", "cn", "china", "china-narrow");
        Add(map, new LoRaProfile("cn470-wide", 470_300_000, 8, 125_000, 8, 17), "china-wide");

        Add(map, new LoRaProfile("cn779-narrow", 779_500_000, 8, 62_500, 8, 10), "cn779", "china779", "cn779-default");
        Add(map, new LoRaProfile("cn779-wide", 779_500_000, 8, 125_000, 8, 10), "cn779w");

        Add(map, new LoRaProfile("jp920-narrow", 920_800_000, 8, 62_500, 8, 13), "jp920", "jp", "japan", "japan-narrow");
        Add(map, new LoRaProfile("jp920-wide", 920_800_000, 8, 125_000, 8, 13), "japan-wide");

        return map;
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant().Replace('_', '-');
    }
}
