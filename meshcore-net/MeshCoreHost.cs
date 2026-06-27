using System.Globalization;

namespace MeshCoreNet;

/// <summary>
/// Builds and runs the MeshCore host from the Python-compatible TOML configuration.
/// </summary>
public enum HostServiceMode
{
    All,
    Web,
    Repeater,
    Companion
}

public sealed class MeshHost
{
    private const string CredentialDirectoryPath = "/etc/meshcore-netcore";
    private readonly Dictionary<string, object?> _config;
    private readonly string _configPath;
    private readonly HardwarePlatform _hardware = new();
    private readonly List<SelfIdentity> _selfIdentities = [];
    private readonly List<CliMeshDevice> _relayVisibleDevices = [];

    public MeshHost(Dictionary<string, object?> config, string configPath)
    {
        _config = config;
        _configPath = configPath;
    }

    public async Task RunAsync(CancellationToken cancellationToken, HostServiceMode mode = HostServiceMode.All)
    {
        Console.WriteLine("=== MeshCore .NET host starting ===");
        Console.WriteLine($"MeshCore .NET host version {VersionInfo.AppVersion}");
        Console.WriteLine("Loaded configuration from the MeshCore TOML format.");

        if (mode == HostServiceMode.Web)
        {
            var webOnlyServer = new MeshWebServer(_config, _configPath);
            Console.WriteLine("Running in web-only service mode.");
            await webOnlyServer.StartAsync(cancellationToken);
            return;
        }

        var dispatcher = new MeshDispatcher();
        var devices = BuildDevices(mode);
        List<IMeshInterface> interfaces;
        if (mode != HostServiceMode.All && devices.Count == 0)
        {
            Console.WriteLine($"No devices matched service mode '{mode.ToString().ToLowerInvariant()}'; skipping mesh interface startup to avoid resource contention.");
            interfaces = [];
        }
        else
        {
            interfaces = BuildInterfaces();
        }
        Console.WriteLine($"Mesh service topology: interfaces={interfaces.Count} devices={devices.Count}");
        foreach (var meshInterface in interfaces)
        {
            Console.WriteLine($"Mesh interface active: {meshInterface.Name} ({meshInterface.Type})");
        }

        foreach (var device in devices)
        {
            Console.WriteLine($"Mesh device active: {device.Name} ({device.Type})");
        }

        if (interfaces.Count > 0 && interfaces.All(meshInterface => meshInterface.Type.Equals("mock", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("Mesh warning: only mock interfaces are configured; no over-the-air mesh traffic will be received or transmitted.");
        }

        var gpsTracker = BuildGpsTrackingService();
        var gpsTask = gpsTracker is null
            ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            : gpsTracker.RunAsync(cancellationToken);
        // Internal delivery only makes sense when more than one device shares this process.
        dispatcher.PassInternal = GetBool(GetSection("dispatcher"), "pass_internal", false) && devices.Count > 1;

        foreach (var meshInterface in interfaces)
        {
            dispatcher.RegisterInterface(meshInterface);
        }

        foreach (var device in devices)
        {
            dispatcher.RegisterDevice(device);
        }

        await dispatcher.StartAsync(cancellationToken);

        foreach (var meshInterface in interfaces)
        {
            await meshInterface.StartAsync(cancellationToken);
        }

        foreach (var device in devices)
        {
            await device.StartAsync(cancellationToken, dispatcher);
        }

        var webServerTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (mode == HostServiceMode.All || mode == HostServiceMode.Repeater)
        {
            var webServer = new MeshWebServer(
                _config,
                _configPath,
                mode: HostServiceMode.Repeater,
                relaySnapshotProvider: () => BuildRelaySnapshot(dispatcher, interfaces, devices),
                nodeSnapshotProvider: () => BuildNodeSnapshot(dispatcher, devices),
                debugSnapshotProvider: () => dispatcher.GetDebugSnapshot());
            webServerTask = webServer.StartAsync(cancellationToken);
        }
        else if (mode == HostServiceMode.Companion)
        {
            var webServer = new MeshWebServer(
                _config,
                _configPath,
                mode: HostServiceMode.Companion);
            webServerTask = webServer.StartAsync(cancellationToken);
        }

        Console.WriteLine("MeshCore .NET host is running. Press Ctrl+C to stop.");

        try
        {
            await Task.WhenAny(webServerTask, gpsTask, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("Shutdown requested.");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Shutdown requested.");
        }
        finally
        {
            await dispatcher.StopAsync();
        }
    }

    private List<IMeshInterface> BuildInterfaces()
    {
        // Python defaults to mock when no interface list is present.
        var names = GetStringList("interfaces") ?? new List<string> { "mock" };
        var result = new List<IMeshInterface>();

        foreach (var name in names)
        {
            var section = GetSection("interface", name);
            var interfaceType = GetString(section, "type") ?? name;
            var resolvedName = GetString(section, "name") ?? name;

            switch (interfaceType.ToLowerInvariant())
            {
                case "mock":
                    result.Add(new MockMeshInterface(resolvedName, GetString(section, "file"), GetBool(section, "repeat", false)));
                    break;
                case "companion":
                    result.Add(new CompanionRadioMeshInterface(
                        resolvedName,
                        new SerialCompanionRadioFrameLink(
                            GetString(section, "port") ?? "/dev/ttyUSB0",
                            GetInt(section, "speed", 115200))));
                    break;
                case "lora":
                    result.Add(new LinuxLoRaInterface(resolvedName, BuildLoRaOptions(section)));
                    break;
                case "waveshare-hat":
                case "waveshare":
                    result.Add(new LinuxLoRaInterface(
                        resolvedName,
                        BuildLoRaOptions(section, LoRaHardwarePresets.WaveshareHat(GetString(section, "profile") ?? GetString(section, "region") ?? GetString(section, "band")))));
                    break;
                case "dragino-hat":
                case "dragino":
                case "pi-hat":
                    result.Add(new DraginoPiHatInterface(
                        resolvedName,
                        GetString(section, "region") ?? GetString(section, "band"),
                        BuildLoRaOptions(section, LoRaHardwarePresets.DraginoLoRaHatV14(GetString(section, "profile") ?? GetString(section, "region") ?? GetString(section, "band")))));
                    break;
                default:
                    // Unknown interface types stay harmless in development instead of aborting startup.
                    result.Add(new LoopbackMeshInterface(resolvedName, interfaceType));
                    break;
            }
        }

        return result;
    }

    private List<IMeshDevice> BuildDevices(HostServiceMode mode)
    {
        var names = GetStringList("devices") ?? new List<string>();
        var result = new List<IMeshDevice>();

        foreach (var name in names)
        {
            var section = GetSection("device", name);
            if (section is null)
            {
                Console.WriteLine($"Warning: no device section found for '{name}'.");
                continue;
            }

            var deviceType = GetString(section, "type") ?? name;
            var displayName = GetString(section, "name") ?? $"{deviceType} {name}";
            var normalizedDeviceType = deviceType.ToLowerInvariant();

            if (mode == HostServiceMode.Repeater && normalizedDeviceType != "repeater")
            {
                continue;
            }

            if (mode == HostServiceMode.Companion && normalizedDeviceType != "companion")
            {
                continue;
            }

            switch (normalizedDeviceType)
            {
                case "companion":
                    result.Add(BuildCompanionDevice(section, displayName));
                    break;
                case "room":
                    result.Add(BuildRoomDevice(section, displayName));
                    break;
                case "repeater":
                    result.Add(BuildRepeaterDevice(section, displayName));
                    break;
                default:
                    result.Add(new GenericMeshDevice(displayName, deviceType));
                    break;
            }
        }

        return result;
    }

    private IMeshDevice BuildRoomDevice(Dictionary<string, object?> section, string displayName)
    {
        var self = BuildSelfIdentity(section, displayName, MeshAdvertType.Room);
        var neighbours = BuildIdentityStore(section, self.PrivateKey);
        var device = new RoomMeshDevice(self, new IdentityStore(), neighbours, _hardware, BuildAccessConfig(section));
        _relayVisibleDevices.Add(device);
        return device;
    }

    private IMeshDevice BuildCompanionDevice(Dictionary<string, object?> section, string displayName)
    {
        var self = BuildSelfIdentity(section, displayName, MeshAdvertType.Chat, useCredentialFile: true);
        var identities = BuildIdentityStore(section, self.PrivateKey);
        var maxChannels = GetInt(section, "channels", 32);
        var channelFile = GetString(section, "channelfile");
        var addPublic = GetBool(section, "add_public_channel", true);
        var channels = ChannelStore.Load(channelFile, maxChannels, addPublic);
        var appInterface = GetString(section, "interface") ?? "wifi";
        // Device companion interface is app-facing TCP/serial, not the external companion-radio bridge.
        ICompanionAppLink link = appInterface.Equals("serial", StringComparison.OrdinalIgnoreCase)
            ? new SerialCompanionAppLink(
                GetString(section, "serial.port") ?? "/dev/ttyS0",
                GetInt(section, "serial.speed", 115200))
            : new TcpCompanionAppLink(
                GetInt(section, "wifi.port", 5000),
                ParseListenAddress(GetString(section, "wifi.listen")));

        return new CompanionRadioDevice(self, identities, channels, link, _hardware);
    }

    private IMeshDevice BuildRepeaterDevice(Dictionary<string, object?> section, string displayName)
    {
        var self = BuildSelfIdentity(section, displayName, MeshAdvertType.Repeater);
        var neighbours = BuildIdentityStore(section, self.PrivateKey);
        var device = new RepeaterMeshDevice(self, neighbours, _hardware, BuildAccessConfig(section));
        _relayVisibleDevices.Add(device);
        return device;
    }

    private SelfIdentity BuildSelfIdentity(Dictionary<string, object?> section, string displayName, MeshAdvertType advertType, bool useCredentialFile = false)
    {
        MeshEd25519PrivateKey privateKey;
        if (useCredentialFile)
        {
            privateKey = LoadCredentialPrivateKey("private", fallbackSection: section);
        }
        else
        {
            var configuredKey = GetString(section, "privatekey");
            if (!string.IsNullOrWhiteSpace(configuredKey))
            {
                privateKey = new MeshEd25519PrivateKey(Convert.FromHexString(configuredKey));
            }
            else
            {
                privateKey = new MeshEd25519PrivateKey();
                // Match Python by printing newly generated private keys so operators can copy them into config.
                Console.WriteLine($"Created private key: {Convert.ToHexString(privateKey.PrivateKey).ToLowerInvariant()}");
            }
        }

        (double Latitude, double Longitude)? latLon = null;
        if (TryGetDouble(section, "lat", out var lat) && TryGetDouble(section, "lon", out var lon))
        {
            latLon = MeshUtilities.ValidateLatLon(lat, lon);
        }

        Console.WriteLine($"{displayName}, public key: {Convert.ToHexString(privateKey.PublicKey).ToLowerInvariant()}");
        var self = new SelfIdentity(privateKey, displayName, latLon, advertType);
        _selfIdentities.Add(self);
        return self;
    }

    private MeshEd25519PrivateKey LoadCredentialPrivateKey(string name, Dictionary<string, object?>? fallbackSection = null)
    {
        var credentialPath = CredentialPath(name);
        var storedKey = ReadCredentialFile(name);
        if (!string.IsNullOrWhiteSpace(storedKey))
        {
            try
            {
                Console.WriteLine($"Loaded companion private key from {credentialPath}");
                return new MeshEd25519PrivateKey(Convert.FromHexString(storedKey));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: credential key in {credentialPath} is invalid: {ex.Message}");
            }
        }

        var configuredKey = fallbackSection is null ? null : GetString(fallbackSection, "privatekey");
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            return new MeshEd25519PrivateKey(Convert.FromHexString(configuredKey));
        }

        Directory.CreateDirectory(CredentialDirectoryPath);
        var generatedKey = new MeshEd25519PrivateKey();
        File.WriteAllText(credentialPath, Convert.ToHexString(generatedKey.PrivateKey).ToLowerInvariant() + Environment.NewLine);
        Console.WriteLine($"Generated companion private key: {credentialPath}");
        return generatedKey;
    }

    private GpsTrackingService? BuildGpsTrackingService()
    {
        var section = GetSection("gps");
        if (!GetBool(section, "enabled", false))
        {
            return null;
        }

        var mode = (GetString(section, "mode") ?? "average").Trim().ToLowerInvariant() switch
        {
            "roaming" or "mobile" => GpsTrackingMode.Roaming,
            _ => GpsTrackingMode.Average
        };

        var configDirectory = Path.GetDirectoryName(_configPath) ?? Directory.GetCurrentDirectory();
        var historyPath = GetString(section, "history_file") ?? Path.Combine(configDirectory, "gps-history.csv");
        var statePath = GetString(section, "state_file") ?? Path.Combine(configDirectory, "gps-state.json");

        var options = new GpsTrackingOptions(
            SerialDevice: GetString(section, "device") ?? "/dev/serial0",
            BaudRate: GetInt(section, "baud", 9600),
            SampleIntervalSeconds: Math.Max(1, GetInt(section, "sample_interval_seconds", 60)),
            RetentionDays: Math.Max(1, GetInt(section, "retention_days", 365)),
            HistoryFilePath: historyPath,
            StateFilePath: statePath,
            Mode: mode);

        return new GpsTrackingService(options, _selfIdentities);
    }

    private string CredentialPath(string name) => Path.Combine(CredentialDirectoryPath, name);

    private string? ReadCredentialFile(string name)
    {
        var path = CredentialPath(name);
        if (!File.Exists(path))
        {
            return null;
        }

        var value = File.ReadAllText(path).Trim();
        return value.Length == 0 ? null : value;
    }

    private IdentityStore BuildIdentityStore(Dictionary<string, object?> section, MeshEd25519PrivateKey privateKey)
    {
        var contacts = GetString(section, "contacts");
        return string.IsNullOrWhiteSpace(contacts) ? new IdentityStore() : new FileIdentityStore(contacts, privateKey);
    }

    private object BuildRelaySnapshot(MeshDispatcher dispatcher, IReadOnlyList<IMeshInterface> interfaces, IReadOnlyList<IMeshDevice> devices)
    {
        var visibleNodes = BuildVisibleNodesSnapshot();
        return new
        {
            mode = GetStringList("devices") ?? [],
            interfaces = interfaces.Select(meshInterface => new
            {
                name = meshInterface.Name,
                type = meshInterface.Type,
                radio = meshInterface.GetRadioConfig()
            }),
            devices = devices.Select(device => new
            {
                name = device.Name,
                type = device.Type,
                isRepeater = device is BasicMeshDevice basic && basic.IsRepeater,
                publicKey = Convert.ToHexString((device as BasicMeshDevice)?.Self.PublicKey ?? []).ToLowerInvariant(),
                stats = (device as BasicMeshDevice)?.Stats.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value) ?? new Dictionary<string, long>()
            }),
            visibleNodes.Count,
            nodes = visibleNodes,
            queueLength = dispatcher.QueueLength,
            airtimeSeconds = dispatcher.AirtimeSeconds
        };
    }

    private object BuildNodeSnapshot(MeshDispatcher dispatcher, IReadOnlyList<IMeshDevice> devices)
    {
        var visibleNodes = BuildVisibleNodesSnapshot();
        return new
        {
            count = visibleNodes.Count,
            nodes = visibleNodes,
            dispatcher = new
            {
                queueLength = dispatcher.QueueLength,
                airtimeSeconds = dispatcher.AirtimeSeconds
            }
        };
    }

    private IReadOnlyList<object> BuildVisibleNodesSnapshot()
    {
        return _relayVisibleDevices
            .SelectMany(device => device.NeighbourIdentities.GetAll().OfType<MeshIdentity>().Select(identity => new
            {
                key = Convert.ToHexString(identity.PublicKey).ToLowerInvariant(),
                name = identity.Name,
                device = device.Name,
                type = identity.Advert.Type.ToString(),
                ageSeconds = (int)Math.Max(0, (DateTimeOffset.UtcNow - identity.ReceivedAt).TotalSeconds),
                rssi = identity.Rssi,
                snr = identity.Snr,
                pathLength = identity.Path?.Length ?? 0,
                advertPathLength = identity.AdvertPath?.Length ?? 0,
                latLon = identity.LatLon,
                lastMessageTime = identity.LastMessageTime
            }))
            .GroupBy(item => item.key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group.OrderByDescending(item => item.ageSeconds).First();
                return new
                {
                    latest.key,
                    latest.name,
                    latest.type,
                    latest.device,
                    latest.ageSeconds,
                    latest.rssi,
                    latest.snr,
                    latest.pathLength,
                    latest.advertPathLength,
                    latest.latLon,
                    latest.lastMessageTime,
                    seenBy = group.Select(item => item.device).Distinct().OrderBy(item => item).ToArray()
                };
            })
            .OrderByDescending(item => item.ageSeconds)
            .ToArray<object>();
    }

    private DeviceAccessConfig BuildAccessConfig(Dictionary<string, object?> section)
    {
        return new DeviceAccessConfig
        {
            AdminPassword = GetString(section, "admin.password"),
            AdminKeys = GetStringSet(section, "admin.keys", "admin.pubkeys"),
            GuestOpen = GetBool(section, "guest.open", true),
            GuestPassword = GetString(section, "guest.password"),
            GuestKeys = GetStringSet(section, "guest.keys", "guest.pubkeys"),
            WriterPassword = GetString(section, "writer.password"),
            WriterKeys = GetStringSet(section, "writer.keys", "writer.pubkeys"),
            ReadOnly = GetBool(section, "readonly", false),
            Welcome = GetString(section, "welcome")
        };
    }

    private LoRaOptions BuildLoRaOptions(Dictionary<string, object?>? section, LoRaOptions? defaults = null)
    {
        var fallback = defaults ?? LoRaHardwarePresets.GenericPiSx126x(GetString(section, "profile") ?? GetString(section, "region") ?? GetString(section, "band"));

        return new LoRaOptions(
            SpiBus: GetInt(section, "spi", fallback.SpiBus),
            ChipSelect: GetInt(section, "cs", fallback.ChipSelect),
            ResetPin: GetInt(section, "reset", fallback.ResetPin),
            BusyPin: GetInt(section, "busy", fallback.BusyPin),
            IrqPin: GetInt(section, "irq", fallback.IrqPin),
            TxEnablePin: GetInt(section, "txen", fallback.TxEnablePin),
            RxEnablePin: GetInt(section, "rxen", fallback.RxEnablePin),
            WakePin: GetInt(section, "wake", fallback.WakePin),
            Frequency: (uint)GetInt(section, "frequency", (int)fallback.Frequency),
            SpreadingFactor: (byte)GetInt(section, "sf", fallback.SpreadingFactor),
            Bandwidth: (uint)GetInt(section, "bw", (int)fallback.Bandwidth),
            CodingRate: (byte)GetInt(section, "cr", fallback.CodingRate),
            TxPower: (sbyte)GetInt(section, "txpower", fallback.TxPower),
            AirtimeDutyCycle: GetDouble(section, "airtime", fallback.AirtimeDutyCycle),
            Dio2RfSwitch: GetBool(section, "dio2.rfswitch", fallback.Dio2RfSwitch),
            Dio3Voltage: TryGetDouble(section, "dio3.voltage", out var voltage) ? voltage : null,
            Dio3TcxoDelay: TryGetDouble(section, "dio3.tcxo_delay", out var delay) ? delay : null,
            ChipKind: GetString(section, "chip") ?? GetString(section, "hal") ?? fallback.ChipKind ?? "sx126x",
            RequireHardware: GetBool(section, "require_hardware", fallback.RequireHardware),
            ChipSelectPin: TryGetInt(section, "nss", out var nssPin)
                ? nssPin
                : TryGetInt(section, "cs.pin", out var chipSelectPin)
                    ? chipSelectPin
                    : fallback.ChipSelectPin);
    }

    private Dictionary<string, object?>? GetSection(params string[] path)
    {
        object? current = _config;
        foreach (var segment in path)
        {
            if (current is not Dictionary<string, object?> currentMap || !currentMap.TryGetValue(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current as Dictionary<string, object?>;
    }

    private string? GetString(Dictionary<string, object?>? section, string key)
    {
        return section is not null && section.TryGetValue(key, out var value) && value is not null
            ? value.ToString()
            : null;
    }

    private bool GetBool(Dictionary<string, object?>? section, string key, bool defaultValue)
    {
        if (section is null || !section.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            bool boolValue => boolValue,
            string stringValue when bool.TryParse(stringValue, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private int GetInt(Dictionary<string, object?>? section, string key, int defaultValue)
    {
        if (section is null || !section.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        if (value is int intValue)
        {
            return intValue;
        }

        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private bool TryGetInt(Dictionary<string, object?>? section, string key, out int value)
    {
        if (section is not null && section.TryGetValue(key, out var raw) && raw is not null &&
            int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private double GetDouble(Dictionary<string, object?>? section, string key, double defaultValue)
    {
        if (section is null)
        {
            return defaultValue;
        }

        return TryGetDouble(section, key, out var value) ? value : defaultValue;
    }

    private static System.Net.IPAddress? ParseListenAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return System.Net.IPAddress.TryParse(value, out var parsed) ? parsed : null;
    }

    private bool TryGetDouble(Dictionary<string, object?>? section, string key, out double value)
    {
        if (section is not null && section.TryGetValue(key, out var raw) && raw is not null &&
            double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private IReadOnlySet<string> GetStringSet(Dictionary<string, object?> section, params string[] keys)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (!section.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            if (value is List<object?> values)
            {
                foreach (var item in values)
                {
                    if (item is not null)
                    {
                        result.Add(item.ToString()!);
                    }
                }
            }
            else
            {
                result.Add(value.ToString()!);
            }
        }

        return result;
    }

    private List<string>? GetStringList(params string[] path)
    {
        var section = GetSection(path[..^1]);
        var key = path[^1];
        if (section is null || !section.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is List<object?> items)
        {
            return items.Select(item => item?.ToString() ?? string.Empty).Where(item => item.Length > 0).ToList();
        }

        return [value.ToString()!];
    }
}

public static class SimpleTomlParser
{
    // This parser intentionally handles only the TOML subset used by the existing MeshCore config files.
    public static Dictionary<string, object?> ParseFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Configuration file '{path}' was not found.", path);
        }

        var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var currentPath = Array.Empty<string>();
        var currentSection = root;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentPath = ParseSectionName(line[1..^1]);
                currentSection = GetOrCreateSection(root, currentPath);
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var valueText = line[(separatorIndex + 1)..].Trim();
            currentSection[key] = ParseValue(valueText);
        }

        return root;
    }

    private static Dictionary<string, object?> GetOrCreateSection(Dictionary<string, object?> root, IReadOnlyList<string> path)
    {
        Dictionary<string, object?> current = root;
        foreach (var segment in path)
        {
            if (!current.TryGetValue(segment, out var nextValue) || nextValue is not Dictionary<string, object?> nextSection)
            {
                nextSection = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                current[segment] = nextSection;
            }

            current = nextSection;
        }

        return current;
    }

    private static string[] ParseSectionName(string sectionText)
    {
        return sectionText
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static object? ParseValue(string valueText)
    {
        if (valueText.StartsWith('"') && valueText.EndsWith('"'))
        {
            return valueText[1..^1];
        }

        if (valueText.StartsWith('[') && valueText.EndsWith(']'))
        {
            var arrayContent = valueText[1..^1].Trim();
            if (arrayContent.Length == 0)
            {
                return new List<object?>();
            }

            var values = new List<object?>();
            var current = new System.Text.StringBuilder();
            var inQuotes = false;

            foreach (var ch in arrayContent)
            {
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    current.Append(ch);
                    continue;
                }

                if (ch == ',' && !inQuotes)
                {
                    values.Add(ParseSingleValue(current.ToString().Trim()));
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            if (current.Length > 0)
            {
                values.Add(ParseSingleValue(current.ToString().Trim()));
            }

            return values;
        }

        return ParseSingleValue(valueText);
    }

    private static object? ParseSingleValue(string valueText)
    {
        if (bool.TryParse(valueText, out var boolValue))
        {
            return boolValue;
        }

        if (int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            return intValue;
        }

        if (double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        if (valueText.StartsWith('"') && valueText.EndsWith('"'))
        {
            return valueText[1..^1];
        }

        return valueText;
    }
}
