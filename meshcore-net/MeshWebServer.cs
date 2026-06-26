using System.Globalization;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeshCoreNet;

public sealed class MeshWebServer
{
    private const string CredentialDirectoryPath = "/etc/meshcore-netcore";
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureAdminCredentialFiles();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(_port);
        });
        builder.Services.AddSingleton(this);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });

        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            await next();
        });

        app.MapGet("/", () => Results.Redirect("/settings"));
        app.MapGet("/login", async (HttpContext context) =>
        {
            if (context.User?.Identity?.IsAuthenticated is true)
            {
                context.Response.Redirect("/settings");
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(LoginPageHtml());
        });

        app.MapPost("/login", (Delegate)LoginHandler);
        app.MapGet("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Redirect("/login");
        });

        app.MapGet("/settings", [Authorize] async (HttpContext context) =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(SettingsPageHtml());
        });

        app.MapGet("/api/status", [Authorize] () => Results.Json(new { status = "ok", port = _port, authenticated = true, version = VersionInfo.AppVersion }));
        app.MapGet("/api/config", [Authorize] () => Results.Json(_config, new JsonSerializerOptions { WriteIndented = true }));
        app.MapPost("/api/config", [Authorize] async (HttpContext context) =>
        {
            var document = await JsonDocument.ParseAsync(context.Request.Body);
            var newConfig = JsonElementToObject(document.RootElement);
            if (newConfig is not Dictionary<string, object?> configDictionary)
            {
                return Results.BadRequest(new { error = "Invalid configuration payload" });
            }

            _config.Clear();
            foreach (var pair in configDictionary)
            {
                _config[pair.Key] = pair.Value;
            }

            await SaveConfigAsync(cancellationToken);
            return Results.Ok(new { saved = true });
        });

        app.MapGet("/api/interfaces", [Authorize] () => Results.Json(GetSection("interface") ?? new Dictionary<string, object?>()));
        app.MapGet("/api/devices", [Authorize] () => Results.Json(GetSection("device") ?? new Dictionary<string, object?>()));
        app.MapGet("/api/nodes", [Authorize] () => Results.Json(_nodeSnapshotProvider?.Invoke() ?? new { count = 0, nodes = Array.Empty<object>() }));
        app.MapGet("/api/node/{name}", [Authorize] (string name) =>
        {
            var snapshot = _nodeSnapshotProvider?.Invoke();
            if (snapshot is null)
            {
                var section = GetSection("device", name);
                return section is null ? Results.NotFound(new { error = "Node not found" }) : Results.Json(section);
            }

            return Results.Json(snapshot);
        });
        app.MapPost("/api/node/{name}", [Authorize] async (string name, HttpContext context) =>
        {
            var section = GetSection("device", name);
            if (section is null)
            {
                section = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                SetSection("device", name, section);
            }

            var document = await JsonDocument.ParseAsync(context.Request.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(new { error = "Invalid node payload" });
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                section[property.Name] = JsonElementToObject(property.Value);
            }

            await SaveConfigAsync(cancellationToken);
            return Results.Ok(new { saved = true, node = name });
        });

        app.MapDelete("/api/node/{name}", [Authorize] async (string name) =>
        {
            var nodes = GetSection("device");
            if (nodes is null || !nodes.Remove(name))
            {
                return Results.NotFound(new { error = "Node not found" });
            }

            await SaveConfigAsync(cancellationToken);
            return Results.Ok(new { removed = name });
        });

        app.MapGet("/api/relay", [Authorize] () => Results.Json(_relaySnapshotProvider?.Invoke() ?? new
        {
            server = GetSection("server"),
            dispatcher = GetSection("dispatcher"),
            devices = GetSection("device"),
            interfaces = GetSection("interface")
        }));

        app.MapGet("/api/debug", [Authorize] () => Results.Json(_debugSnapshotProvider?.Invoke() ?? new
        {
            dispatcher = GetSection("dispatcher"),
            packets = Array.Empty<object>()
        }));

        app.MapGet("/api/diagnostics", [Authorize] () => Results.Json(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            version = VersionInfo.AppVersion,
            configPath = _configPath,
            config = _config,
            relay = _relaySnapshotProvider?.Invoke(),
            nodes = _nodeSnapshotProvider?.Invoke(),
            debug = _debugSnapshotProvider?.Invoke()
        }, new JsonSerializerOptions { WriteIndented = true }));

        app.MapPost("/api/reload", [Authorize] () => Results.Json(new { reloaded = false, message = "Reload is not supported without restarting the host." }));

        app.MapGet("/api/login-info", [Authorize] async (HttpContext context) =>
        {
            var server = GetSection("server") ?? new Dictionary<string, object?>();
            var loginSection = GetSection("server", "admin");
            var filePassword = ReadCredentialFile("password");
            var filePublic = ReadCredentialFile("public");
            return Results.Json(new
            {
                server,
                appVersion = VersionInfo.AppVersion,
                adminEnabled = loginSection is not null || GetString(server, "admin.password") is not null || GetString(server, "admin.keys") is not null || !string.IsNullOrWhiteSpace(filePassword) || !string.IsNullOrWhiteSpace(filePublic)
            });
        });

        Console.WriteLine($"MeshCore web management server starting on http://0.0.0.0:{_port}");
        await app.RunAsync(cancellationToken);
    }

    private async Task<IResult> LoginHandler(HttpContext context)
    {
        var loginRequest = await ReadLoginRequestAsync(context);
        if (loginRequest is null)
        {
            return Results.BadRequest(new { error = "Invalid login request" });
        }

        if (!IsAdminLogin(loginRequest))
        {
            return Results.Unauthorized();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "meshcore-admin"),
            new Claim(ClaimTypes.Role, "Admin")
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return Results.Redirect("/settings");
    }

    private static async Task<LoginRequest?> ReadLoginRequestAsync(HttpContext context)
    {
        if (context.Request.HasJsonContentType())
        {
            return await JsonSerializer.DeserializeAsync<LoginRequest>(context.Request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        var form = await context.Request.ReadFormAsync();
        return new LoginRequest(
            PublicKey: form["publicKey"].FirstOrDefault(),
            Password: form["password"].FirstOrDefault());
    }

    private bool IsAdminLogin(LoginRequest request)
    {
        var serverSection = GetSection("server");
        var adminPassword = serverSection is null ? null : GetString(serverSection, "admin.password");
        adminPassword ??= ReadCredentialFile("password");
        if (!string.IsNullOrWhiteSpace(request.Password) && request.Password == adminPassword)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(request.PublicKey))
        {
            var adminKeys = serverSection is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : GetStringSet(serverSection, "admin.keys", "admin.pubkeys");
            var filePublic = ReadCredentialFile("public");
            if (!string.IsNullOrWhiteSpace(filePublic))
            {
                adminKeys.Add(filePublic);
            }
            if (adminKeys.Contains(request.PublicKey, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private Dictionary<string, object?>? GetSection(params string[] path)
    {
        object? current = _config;
        foreach (var segment in path)
        {
            if (current is not Dictionary<string, object?> dictionary)
            {
                return null;
            }

            if (!dictionary.TryGetValue(segment, out current) || current is null)
            {
                return null;
            }
        }

        return current as Dictionary<string, object?>;
    }

    private static string? GetString(Dictionary<string, object?> section, string key)
    {
        if (!section.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value.ToString();
    }

    private string CredentialDirectory => CredentialDirectoryPath;

    private string ReadCredentialPath(string name) => Path.Combine(CredentialDirectory, name);

    private string? ReadCredentialFile(string name)
    {
        var path = ReadCredentialPath(name);
        if (!File.Exists(path))
        {
            return null;
        }

        var value = File.ReadAllText(path).Trim();
        return value.Length == 0 ? null : value;
    }

    private void EnsureAdminCredentialFiles()
    {
        try
        {
            Directory.CreateDirectory(CredentialDirectory);
            var passwordPath = ReadCredentialPath("password");
            var publicPath = ReadCredentialPath("public");
            var privatePath = ReadCredentialPath("private");

            if (!File.Exists(passwordPath) || string.IsNullOrWhiteSpace(File.ReadAllText(passwordPath)))
            {
                var passwordBytes = RandomNumberGenerator.GetBytes(24);
                var password = Convert.ToBase64String(passwordBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                File.WriteAllText(passwordPath, password + Environment.NewLine);
                Console.WriteLine($"Generated admin password file: {passwordPath}");
            }

            if (!File.Exists(privatePath) || string.IsNullOrWhiteSpace(File.ReadAllText(privatePath)) || !File.Exists(publicPath) || string.IsNullOrWhiteSpace(File.ReadAllText(publicPath)))
            {
                var key = new MeshEd25519PrivateKey();
                var privateHex = Convert.ToHexString(key.PrivateKey).ToLowerInvariant();
                var publicHex = Convert.ToHexString(key.PublicKey).ToLowerInvariant();
                File.WriteAllText(privatePath, privateHex + Environment.NewLine);
                File.WriteAllText(publicPath, publicHex + Environment.NewLine);
                Console.WriteLine($"Generated admin key files: {publicPath}, {privatePath}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unable to initialize admin credential files in {CredentialDirectory}: {ex.Message}");
        }
    }

    private void SetSection(string sectionName, string key, Dictionary<string, object?> section)
    {
        if (_config.TryGetValue(sectionName, out var existing) && existing is Dictionary<string, object?> existingSection)
        {
            existingSection[key] = section;
            return;
        }

        _config[sectionName] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [key] = section
        };
    }

    private async Task SaveConfigAsync(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(_configPath, MeshTomlWriter.Write(_config), Encoding.UTF8, cancellationToken);
    }

    private static ISet<string> GetStringSet(Dictionary<string, object?> section, params string[] keys)
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

    private static int GetInt(Dictionary<string, object?>? section, string key, int defaultValue)
    {
        if (section is null || !section.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            double doubleValue => (int)doubleValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static string FooterHtml() => $"<footer style=\"margin-top: 1.5rem; font-size: 0.85rem; color: #6a737d;\">MeshCore .NET host version {VersionInfo.AppVersion} — MIT license</footer>";

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static string LoginPageHtml() => $"""
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8" />
    <title>MeshCore Admin Login</title>
</head>
<body>
    <h1>MeshCore Admin Login</h1>
    <form method="post" action="/login">
        <label>Admin public key (hex): <input type="text" name="publicKey" /></label><br />
        <label>Password: <input type="password" name="password" /></label><br />
        <button type="submit">Sign in</button>
    </form>
    {FooterHtml()}
</body>
</html>
""";

    private static string SettingsPageHtml() => """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8" />
    <title>MeshCore Admin Console</title>
    <style>
        body { font-family: Arial, sans-serif; margin: 1rem; background: #f6f8fa; color: #202124; }
        header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 1rem; }
        nav a { margin-right: 1rem; text-decoration: none; color: #1f5bb5; }
        nav a.active { font-weight: bold; }
        section { background: white; border: 1px solid #d7dde4; border-radius: 8px; padding: 1rem; }
        textarea { width: 100%; height: 50vh; font-family: monospace; margin-top: 0.5rem; }
        table { width: 100%; border-collapse: collapse; margin-top: 0.5rem; }
        th, td { padding: 0.5rem; border: 1px solid #e1e4e8; }
        button { margin-right: 0.5rem; }
        .hidden { display: none; }
        .card { padding: 1rem; background: #fff; border: 1px solid #e1e4e8; border-radius: 8px; margin-bottom: 1rem; }
        .summary { margin: 0.25rem 0 0.75rem 0; color: #586069; font-size: 0.95rem; }
        .health-stale { background: #fff5f5; }
        .health-weak { background: #fffbe6; }
        .health-strong { background: #f6ffed; }
    </style>
</head>
<body>
    <header>
        <div>
            <h1>MeshCore Admin Console</h1>
            <p>Manage companion, relay, and node configuration from one place.</p>
        </div>
        <div>
            <a href="/logout">Logout</a>
            <button id="export-diagnostics">Export diagnostics</button>
        </div>
    </header>

    <nav>
        <a href="#" id="tab-relay" class="active">Relay</a>
        <a href="#" id="tab-nodes">Nodes</a>
        <a href="#" id="tab-interfaces">Interfaces</a>
        <a href="#" id="tab-config">Config</a>
        <a href="#" id="tab-debug">Debug</a>
    </nav>

    <section id="view-relay" class="card">
        <h2>Relay overview</h2>
        <div>
            <button id="refresh-relay">Refresh relay</button>
        </div>
        <div id="relay-summary" class="summary">Loading...</div>
        <table>
            <thead><tr><th>Name</th><th>Type</th><th>RSSI</th><th>SNR</th><th>Age</th><th>Seen By</th></tr></thead>
            <tbody id="relay-table"></tbody>
        </table>
        <pre id="relay-json">Loading...</pre>
    </section>

    <section id="view-nodes" class="card hidden">
        <h2>Seen nodes</h2>
        <div>
            <button id="refresh-nodes">Refresh nodes</button>
        </div>
        <div id="node-summary" class="summary">Loading...</div>
        <table>
            <thead><tr><th>Name</th><th>Device</th><th>RSSI</th><th>SNR</th><th>Age</th><th>Last Msg</th><th>Seen By</th></tr></thead>
            <tbody id="nodes-table"></tbody>
        </table>
        <pre id="node-json">Loading...</pre>
    </section>

    <section id="view-interfaces" class="card hidden">
        <h2>Interfaces</h2>
        <pre id="interfaces-json">Loading...</pre>
    </section>

    <section id="view-config" class="card hidden">
        <h2>Configuration</h2>
        <div>
            <button id="reload">Reload config</button>
            <button id="save">Save config</button>
        </div>
        <textarea id="editor"></textarea>
    </section>

    <section id="view-debug" class="card hidden">
        <h2>Debug packet stream</h2>
        <div>
            <button id="refresh-debug">Refresh debug</button>
        </div>
        <div id="debug-summary" class="summary">Loading...</div>
        <pre id="debug-json">Loading...</pre>
        <table>
            <thead><tr><th>Time</th><th>Dir</th><th>Type</th><th>Bytes</th><th>RSSI</th><th>SNR</th><th>Internal</th><th>Queue</th><th>Note</th></tr></thead>
            <tbody id="packet-table"></tbody>
        </table>
    </section>
""" + FooterHtml() + """

    <script>
        const views = {
            relay: document.getElementById('view-relay'),
            nodes: document.getElementById('view-nodes'),
            interfaces: document.getElementById('view-interfaces'),
            config: document.getElementById('view-config'),
            debug: document.getElementById('view-debug')
        };

        const tabs = {
            relay: document.getElementById('tab-relay'),
            nodes: document.getElementById('tab-nodes'),
            interfaces: document.getElementById('tab-interfaces'),
            config: document.getElementById('tab-config'),
            debug: document.getElementById('tab-debug')
        };

        Object.entries(tabs).forEach(([name, tab]) => {
            tab.addEventListener('click', event => {
                event.preventDefault();
                showView(name);
            });
        });

        function showView(name) {
            Object.values(views).forEach(view => view.classList.add('hidden'));
            views[name].classList.remove('hidden');
            Object.values(tabs).forEach(tab => tab.classList.remove('active'));
            tabs[name].classList.add('active');
            refreshVisibleView(name);
        }

        function formatMaybe(value, fallback = '0') {
            return value === null || value === undefined ? fallback : value;
        }

        function healthClass(node) {
            const age = Number(node.ageSeconds ?? 0);
            const rssi = Number(node.rssi ?? -9999);
            const snr = Number(node.snr ?? -9999);
            if (age > 120) {
                return 'health-stale';
            }

            if (rssi < -110 || snr < 3) {
                return 'health-weak';
            }

            return 'health-strong';
        }

        function sortByStrength(a, b) {
            const aAge = a.ageSeconds ?? Number.MAX_SAFE_INTEGER;
            const bAge = b.ageSeconds ?? Number.MAX_SAFE_INTEGER;
            if (aAge !== bAge) {
                return aAge - bAge;
            }

            const aSignal = Number(a.rssi ?? -9999) + Number(a.snr ?? -9999);
            const bSignal = Number(b.rssi ?? -9999) + Number(b.snr ?? -9999);
            return bSignal - aSignal;
        }

        async function loadInterfaces() {
            const response = await fetch('/api/interfaces');
            const json = await response.json();
            document.getElementById('interfaces-json').textContent = JSON.stringify(json, null, 2);
        }

        async function loadRelay() {
            const response = await fetch('/api/relay');
            const json = await response.json();
            const nodes = [...(json.nodes ?? [])].sort(sortByStrength);
            document.getElementById('relay-summary').textContent = `nodes=${nodes.length} queue=${json.queueLength ?? 0} airtime_s=${Number(json.airtimeSeconds ?? 0).toFixed(3)}`;
            document.getElementById('relay-json').textContent = JSON.stringify(json, null, 2);
            document.getElementById('relay-table').innerHTML = nodes.map(node => `
                <tr class="${healthClass(node)}">
                    <td>${node.name ?? node.key}</td>
                    <td>${node.type ?? 'unknown'}</td>
                    <td>${formatMaybe(node.rssi, '-')}</td>
                    <td>${formatMaybe(node.snr, '-')}</td>
                    <td>${node.ageSeconds ?? 0}s</td>
                    <td>${(node.seenBy ?? []).join(', ')}</td>
                </tr>`).join('');
        }

        async function loadNodes() {
            const response = await fetch('/api/nodes');
            const json = await response.json();
            const nodes = [...(json.nodes ?? [])].sort(sortByStrength);
            document.getElementById('node-summary').textContent = `nodes=${nodes.length} dispatcher_queue=${json.dispatcher?.queueLength ?? 0}`;
            document.getElementById('node-json').textContent = JSON.stringify(json, null, 2);
            document.getElementById('nodes-table').innerHTML = nodes.map(node => `
                <tr class="${healthClass(node)}">
                    <td>${node.name ?? node.key}</td>
                    <td>${node.device ?? '-'}</td>
                    <td>${formatMaybe(node.rssi, '-')}</td>
                    <td>${formatMaybe(node.snr, '-')}</td>
                    <td>${node.ageSeconds ?? 0}s</td>
                    <td>${node.lastMessageTime ?? 0}</td>
                    <td>${(node.seenBy ?? []).join(', ')}</td>
                </tr>`).join('');
        }

        async function loadDebug() {
            const response = await fetch('/api/debug');
            const json = await response.json();
            const packets = json.packets ?? [];
            document.getElementById('debug-summary').textContent = `packets=${packets.length} queue=${json.dispatcher?.queueLength ?? 0}`;
            document.getElementById('debug-json').textContent = JSON.stringify({
                dispatcher: json.dispatcher,
                packetCount: packets.length
            }, null, 2);
            document.getElementById('packet-table').innerHTML = packets.map(packet => `
                <tr>
                    <td>${packet.at ?? ''}</td>
                    <td>${packet.direction ?? ''}</td>
                    <td>${packet.type ?? ''}</td>
                    <td>${packet.bytes ?? 0}</td>
                    <td>${packet.rssi ?? 0}</td>
                    <td>${packet.snr ?? 0}</td>
                    <td>${packet.internal ? 'yes' : 'no'}</td>
                    <td>${packet.queueLength ?? 0}</td>
                    <td>${packet.note ?? ''}</td>
                </tr>`).join('');
        }

        async function loadConfig() {
            const response = await fetch('/api/config');
            if (!response.ok) {
                alert('Unable to load configuration.');
                return;
            }
            const json = await response.json();
            document.getElementById('editor').value = JSON.stringify(json, null, 2);
        }

        async function saveConfig() {
            try {
                const config = JSON.parse(document.getElementById('editor').value);
                const response = await fetch('/api/config', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(config)
                });
                if (!response.ok) {
                    const error = await response.json();
                    throw new Error(error?.error || 'Save failed');
                }
                alert('Configuration saved.');
            } catch (exception) {
                alert(exception.message);
            }
        }

        document.getElementById('refresh-relay').addEventListener('click', loadRelay);
        document.getElementById('refresh-nodes').addEventListener('click', loadNodes);
        document.getElementById('refresh-debug').addEventListener('click', loadDebug);
        document.getElementById('reload').addEventListener('click', loadConfig);
        document.getElementById('save').addEventListener('click', saveConfig);
        document.getElementById('export-diagnostics').addEventListener('click', async () => {
            const response = await fetch('/api/diagnostics');
            if (!response.ok) {
                alert('Unable to export diagnostics.');
                return;
            }

            const blob = await response.blob();
            const url = URL.createObjectURL(blob);
            const anchor = document.createElement('a');
            anchor.href = url;
            anchor.download = `meshcore-diagnostics-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
            document.body.appendChild(anchor);
            anchor.click();
            anchor.remove();
            URL.revokeObjectURL(url);
        });

        async function refreshVisibleView(name) {
            if (name === 'relay') {
                await loadRelay();
            } else if (name === 'nodes') {
                await loadNodes();
            } else if (name === 'debug') {
                await loadDebug();
            }
        }

        setInterval(() => {
            if (document.hidden) {
                return;
            }

            const active = Object.entries(tabs).find(([, tab]) => tab.classList.contains('active'))?.[0] ?? 'relay';
            refreshVisibleView(active);
        }, 5000);

        loadInterfaces();
        loadRelay();
        loadNodes();
        loadDebug();
        loadConfig();
    </script>
</body>
</html>
""";
    private sealed record LoginRequest(string? PublicKey, string? Password);
}

public static class MeshTomlWriter
{
    public static string Write(Dictionary<string, object?> config)
    {
        var builder = new StringBuilder();
        WriteSection(builder, config, Array.Empty<string>());
        return builder.ToString();
    }

    private static void WriteSection(StringBuilder builder, Dictionary<string, object?> section, IReadOnlyList<string> prefix)
    {
        var scalarKeys = section.Keys.Where(key => section[key] is not Dictionary<string, object?>).OrderBy(key => key).ToList();
        foreach (var key in scalarKeys)
        {
            if (section[key] is Dictionary<string, object?>)
            {
                continue;
            }

            builder.Append(key);
            builder.Append(" = ");
            AppendValue(builder, section[key]);
            builder.AppendLine();
        }

        foreach (var nested in section.Where(kvp => kvp.Value is Dictionary<string, object?>).OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sectionPath = prefix.Append(nested.Key).ToArray();
            builder.AppendLine();
            builder.Append('[');
            builder.Append(string.Join('.', sectionPath));
            builder.AppendLine("]");
            WriteSection(builder, (Dictionary<string, object?>)nested.Value!, sectionPath);
        }
    }

    private static void AppendValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case bool boolValue:
                builder.Append(boolValue ? "true" : "false");
                break;
            case int or long or double:
                builder.AppendConvertInvariant(value);
                break;
            case string stringValue:
                builder.Append('"');
                builder.Append(stringValue.Replace("\"", "\\\""));
                builder.Append('"');
                break;
            case List<object?> listValue:
                builder.Append('[');
                builder.Append(string.Join(", ", listValue.Select(FormatValue)));
                builder.Append(']');
                break;
            default:
                builder.Append('"');
                builder.Append(value.ToString()?.Replace("\"", "\\\"") ?? string.Empty);
                builder.Append('"');
                break;
        }
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool boolValue => boolValue ? "true" : "false",
            int or long or double => value.ToString() ?? string.Empty,
            string stringValue => '"' + stringValue.Replace("\"", "\\\"") + '"',
            _ => '"' + value.ToString()?.Replace("\"", "\\\"") + '"'
        };
    }

    private static void AppendConvertInvariant(this StringBuilder builder, object value)
    {
        builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
    }
}
