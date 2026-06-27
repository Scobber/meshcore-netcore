namespace MeshCoreNet;

/// <summary>
/// LoRa channel profiles grounded in verified real-world MeshCore community deployments.
/// Sources: meshcore-pi Python reference config, SDRAngel MeshCore modem plugin,
/// CubeCell-MeshCore firmware presets, and supply-drop-bbs Mesh-America presets.
///
/// CR (coding rate) is the 4/x denominator: 5 = 4/5, 6 = 4/6, 7 = 4/7, 8 = 4/8.
/// All bandwidth values are in Hz.  Frequencies are in Hz.
/// TxPower is in dBm (SX126x/SX127x PA output before antenna gain).
/// </summary>
public sealed record LoRaProfile(string Name, uint Frequency, byte SpreadingFactor, uint Bandwidth, byte CodingRate, sbyte TxPower)
{
    /// <summary>The canonical name of the default fallback profile.</summary>
    public const string DefaultProfileName = "eu-narrow";

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

        return Catalog[DefaultProfileName];
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

        // ── EU / UK ────────────────────────────────────────────────────────────────
        // EU/UK Narrow — THE recommended MeshCore default channel.
        // Confirmed: meshcore-pi Python ref (cr=8), SDRAngel EU_NARROW (parityBits=4),
        // CubeCell-MeshCore preset comment "BW: 62.5 kHz | SF: 8 | CR: 4/8".
        Add(map, new LoRaProfile("eu-narrow", 869_618_000, 8, 62_500, 8, 22),
            "eu868-narrow", "eu868", "eu", "868", "europe", "uk");

        // EU/UK Long Range — SF11, 250 kHz.
        // SDRAngel EU_LONG_RANGE (869525000, BW250000, SF11, CR 4/5).
        Add(map, new LoRaProfile("eu-long-range", 869_525_000, 11, 250_000, 5, 22),
            "eu868-wide", "eu-wide", "europe-wide");

        // EU/UK Medium Range — SF10, 250 kHz.
        // SDRAngel EU_MEDIUM_RANGE (869525000, BW250000, SF10, CR 4/5).
        Add(map, new LoRaProfile("eu-medium-range", 869_525_000, 10, 250_000, 5, 22));

        // Switzerland — same channel as EU Narrow per SDRAngel CH preset.
        Add(map, new LoRaProfile("ch", 869_618_000, 8, 62_500, 8, 22),
            "switzerland");

        // Czech Republic — SDRAngel CZ_NARROW (869525000, BW62500, SF7, CR 4/5).
        Add(map, new LoRaProfile("cz", 869_525_000, 7, 62_500, 5, 22),
            "czechrepublic", "czech");

        // Portugal 868 MHz — SDRAngel PT_868 (869618000, BW62500, SF7, CR 4/6).
        Add(map, new LoRaProfile("pt868", 869_618_000, 7, 62_500, 6, 22),
            "portugal-868", "portugal", "pt");

        // ── EU 433 MHz ─────────────────────────────────────────────────────────────
        // EU 433 MHz Long Range — SDRAngel EU_433_LONG_RANGE (433650000, BW250000, SF11, CR 4/5).
        Add(map, new LoRaProfile("eu433", 433_650_000, 11, 250_000, 5, 20),
            "eu433-narrow", "eu433-wide", "eu433-long-range", "433");

        // Portugal 433 MHz — SDRAngel PT_433 (433375000, BW62500, SF9, CR 4/6).
        Add(map, new LoRaProfile("pt433", 433_375_000, 9, 62_500, 6, 20),
            "portugal-433");

        // ── Australia ──────────────────────────────────────────────────────────────
        // Australia main preset — SDRAngel AU + supply-drop-bbs (915800000, BW250000, SF10, CR 4/5).
        // Used in VIC, NSW, ACT, TAS, and northern WA/NT.
        Add(map, new LoRaProfile("au", 915_800_000, 10, 250_000, 5, 22),
            "au915", "australia", "au-wide", "au915-wide");

        // Australia Victoria narrow — SDRAngel AU_VICTORIA (916575000, BW62500, SF7, CR 4/8).
        Add(map, new LoRaProfile("au-victoria", 916_575_000, 7, 62_500, 8, 22),
            "au915-narrow", "au-narrow", "victoria");

        // Australia QLD/SA/WA/NT — supply-drop-bbs (923125000, BW62500, SF8, CR 4/5).
        // Uses the AS923-1 sub-band allocation for those states.
        Add(map, new LoRaProfile("au-qld", 923_125_000, 8, 62_500, 5, 22),
            "au-sa-wa-qld", "australia-qld");

        // ── New Zealand ────────────────────────────────────────────────────────────
        // New Zealand — SDRAngel NZ + supply-drop-bbs (917375000, BW250000, SF11, CR 4/5).
        Add(map, new LoRaProfile("nz", 917_375_000, 11, 250_000, 5, 22),
            "newzealand");

        // New Zealand Narrow — SDRAngel NZ_NARROW + supply-drop-bbs (917375000, BW62500, SF7, CR 4/5).
        Add(map, new LoRaProfile("nz-narrow", 917_375_000, 7, 62_500, 5, 22),
            "newzealand-narrow");

        // ── USA / Canada ───────────────────────────────────────────────────────────
        // USA/Canada — SDRAngel USA + supply-drop-bbs (910525000, BW62500, SF7, CR 4/5).
        Add(map, new LoRaProfile("usa", 910_525_000, 7, 62_500, 5, 22),
            "us915", "us915-narrow", "us", "na915", "us-narrow", "northamerica", "915",
            "us915-wide", "na915-wide", "northamerica-wide", "us-wide");

        // USA Arizona — supply-drop-bbs (908205000, BW62500, SF10, CR 4/5).
        Add(map, new LoRaProfile("usa-arizona", 908_205_000, 10, 62_500, 5, 22),
            "arizona");

        // ── Vietnam / SE Asia ──────────────────────────────────────────────────────
        // Vietnam — SDRAngel VN + supply-drop-bbs (920250000, BW250000, SF11, CR 4/5).
        // AS923-1 band; 16 dBm to respect Vietnamese EIRP limits.
        // Also serves as the closest real MeshCore preset for the broader AS923 region.
        Add(map, new LoRaProfile("vn", 920_250_000, 11, 250_000, 5, 16),
            "vietnam", "as923", "as923-narrow", "as923-wide", "as923-1",
            "asia", "asia-narrow", "asia-wide");

        // ── Regional approximations ────────────────────────────────────────────────
        // These profiles are based on regional frequency allocations, not verified
        // MeshCore community channel presets.  Use explicit frequency/sf/bw/cr/txpower
        // overrides when a community standard exists for your region.

        // India — 865-867 MHz ISM band.
        Add(map, new LoRaProfile("in865", 865_062_500, 8, 62_500, 8, 20),
            "india", "india-narrow", "in865-narrow", "in865-wide");

        // South Korea — 920.9-923.3 MHz, max 14 dBm.
        Add(map, new LoRaProfile("kr920", 922_100_000, 8, 62_500, 8, 14),
            "kr", "korea", "korea-narrow", "kr920-narrow", "kr920-wide", "korea-wide");

        // Russia — 864-870 MHz band.
        Add(map, new LoRaProfile("ru864", 864_100_000, 8, 62_500, 8, 20),
            "ru", "russia", "russia-narrow", "ru864-narrow", "ru864-wide", "russia-wide");

        // China 470 MHz — 470-510 MHz LoRa band.
        // 'cn' and 'china' map here to match the original Python reference config behaviour.
        Add(map, new LoRaProfile("cn470", 470_300_000, 8, 62_500, 8, 17),
            "cn", "china", "cn470-narrow", "cn470-wide");

        // China 779 MHz — 779-787 MHz band, max 10 dBm.
        Add(map, new LoRaProfile("cn779", 779_500_000, 8, 62_500, 8, 10),
            "china779", "cn779-default", "cn779-narrow", "cn779-wide");

        // Japan — 920-928 MHz, max 13 dBm.
        Add(map, new LoRaProfile("jp920", 920_800_000, 8, 62_500, 8, 13),
            "jp", "japan", "japan-narrow", "jp920-narrow", "jp920-wide", "japan-wide");

        return map;
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant().Replace('_', '-');
    }
}
