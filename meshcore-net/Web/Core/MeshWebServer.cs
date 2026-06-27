namespace MeshCoreNet;

public sealed partial class MeshWebServer
{
    private const string CredentialDirectoryPath = "/etc/meshcore-netcore";
    private const string DataProtectionDirectoryPath = "/var/lib/meshcore/dataprotection-keys";
    private const string FaviconSvg = """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
    <rect width="64" height="64" rx="14" fill="#0f1724"/>
    <circle cx="32" cy="32" r="20" fill="#0a9396" opacity="0.28"/>
    <path d="M14 40l10-18 8 13 8-9 10 14" fill="none" stroke="#e8f0f5" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
""";

    private readonly Dictionary<string, object?> _config;
    private readonly string _configPath;
    private readonly int _port;
    private readonly Func<object?>? _relaySnapshotProvider;
    private readonly Func<object?>? _nodeSnapshotProvider;
    private readonly Func<object?>? _debugSnapshotProvider;

    public MeshWebServer(
        Dictionary<string, object?> config,
        string configPath,
        Func<object?>? relaySnapshotProvider = null,
        Func<object?>? nodeSnapshotProvider = null,
        Func<object?>? debugSnapshotProvider = null)
    {
        _config = config;
        _configPath = configPath;
        _relaySnapshotProvider = relaySnapshotProvider;
        _nodeSnapshotProvider = nodeSnapshotProvider;
        _debugSnapshotProvider = debugSnapshotProvider;
        _port = GetInt(GetSection("server", "web"), "port", 80);
    }
}
