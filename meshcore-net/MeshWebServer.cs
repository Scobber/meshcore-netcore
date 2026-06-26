using System.Globalization;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshCoreNet;

public sealed class MeshWebServer
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureAdminCredentialFiles();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(_port);
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authorization", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Warning);

        Directory.CreateDirectory(DataProtectionDirectoryPath);
        builder.Services
            .AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(DataProtectionDirectoryPath))
            .SetApplicationName("meshcore-web");

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
        app.Use(async (context, next) =>
        {
            var started = Stopwatch.StartNew();
            Exception? caught = null;
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                caught = ex;
                throw;
            }
            finally
            {
                started.Stop();
                if (caught is null)
                {
                    Console.WriteLine($"HTTP {context.Request.Method} {context.Request.Path}{context.Request.QueryString} -> {context.Response.StatusCode} {started.ElapsedMilliseconds}ms");
                }
                else
                {
                    Console.Error.WriteLine($"HTTP {context.Request.Method} {context.Request.Path}{context.Request.QueryString} -> 500 {started.ElapsedMilliseconds}ms ({caught.GetType().Name}: {caught.Message})");
                }
            }
        });

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            await next();
        });

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/", () => Results.Redirect("/settings"));
        app.MapGet("/favicon.svg", () => Results.Text(FaviconSvg, "image/svg+xml", Encoding.UTF8));
        app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.svg"));
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
        Console.WriteLine("MeshCore web middleware profile: accesslog-v2 favicon-svg dataprotection-local");
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

    private static string LoginPageHtml() => """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>MeshCore Admin Login</title>
    <link rel="icon" type="image/svg+xml" href="/favicon.svg" />
    <style>
        @import url('https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@400;500;700&family=IBM+Plex+Mono:wght@500&display=swap');

        :root {
            --bg-deep: #0f1724;
            --bg-mid: #1d2d44;
            --bg-glow: #0a9396;
            --card: rgba(14, 24, 38, 0.82);
            --card-edge: rgba(164, 214, 214, 0.28);
            --text-main: #e8f0f5;
            --text-soft: #b9cad6;
            --field-bg: rgba(8, 14, 22, 0.65);
            --field-border: rgba(138, 177, 189, 0.45);
            --field-border-focus: #5de5d8;
            --button: linear-gradient(120deg, #0a9396 0%, #4cc9f0 100%);
            --button-text: #062029;
            --shadow: 0 24px 48px rgba(0, 0, 0, 0.45);
        }

        * { box-sizing: border-box; }

        body {
            margin: 0;
            min-height: 100vh;
            font-family: 'Space Grotesk', 'Segoe UI', sans-serif;
            color: var(--text-main);
            background:
                radial-gradient(1200px 800px at -10% -20%, rgba(76, 201, 240, 0.22) 0%, transparent 60%),
                radial-gradient(900px 600px at 110% 10%, rgba(10, 147, 150, 0.24) 0%, transparent 60%),
                linear-gradient(145deg, var(--bg-deep) 0%, var(--bg-mid) 100%);
            display: grid;
            place-items: center;
            padding: 1.2rem;
        }

        .scene {
            width: min(460px, 100%);
            animation: rise-in 360ms ease-out;
        }

        .card {
            border: 1px solid var(--card-edge);
            background: var(--card);
            backdrop-filter: blur(10px);
            border-radius: 18px;
            padding: 1.3rem;
            box-shadow: var(--shadow);
        }

        .badge {
            display: inline-block;
            font-family: 'IBM Plex Mono', monospace;
            letter-spacing: 0.06em;
            font-size: 0.72rem;
            color: #91e6dc;
            border: 1px solid rgba(145, 230, 220, 0.35);
            border-radius: 999px;
            padding: 0.22rem 0.58rem;
            margin-bottom: 0.75rem;
        }

        h1 {
            margin: 0 0 0.35rem 0;
            font-size: clamp(1.5rem, 4vw, 2rem);
            line-height: 1.1;
            letter-spacing: -0.02em;
        }

        p {
            margin: 0 0 1.15rem 0;
            color: var(--text-soft);
            font-size: 0.96rem;
        }

        form {
            display: grid;
            gap: 0.85rem;
        }

        label {
            display: grid;
            gap: 0.35rem;
            font-size: 0.9rem;
            color: #d9e8f0;
        }

        input {
            width: 100%;
            border: 1px solid var(--field-border);
            border-radius: 11px;
            background: var(--field-bg);
            color: var(--text-main);
            font: inherit;
            padding: 0.72rem 0.8rem;
            transition: border-color 120ms ease, box-shadow 120ms ease, transform 120ms ease;
        }

        input:focus {
            outline: none;
            border-color: var(--field-border-focus);
            box-shadow: 0 0 0 3px rgba(93, 229, 216, 0.2);
            transform: translateY(-1px);
        }

        input::placeholder {
            color: #8ea8b8;
        }

        .actions {
            margin-top: 0.2rem;
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 0.8rem;
            flex-wrap: wrap;
        }

        .hint {
            font-size: 0.82rem;
            color: #a8bfcc;
        }

        button {
            border: 0;
            border-radius: 11px;
            background: var(--button);
            color: var(--button-text);
            font: 700 0.95rem 'Space Grotesk', sans-serif;
            letter-spacing: 0.01em;
            padding: 0.72rem 1.1rem;
            cursor: pointer;
            transition: transform 120ms ease, filter 120ms ease;
        }

        button:hover { filter: brightness(1.06); }
        button:active { transform: translateY(1px); }

        .footer-wrap {
            margin-top: 0.8rem;
            opacity: 0.9;
        }

        @keyframes rise-in {
            from {
                opacity: 0;
                transform: translateY(10px) scale(0.98);
            }
            to {
                opacity: 1;
                transform: translateY(0) scale(1);
            }
        }

        @media (max-width: 520px) {
            .card {
                border-radius: 14px;
                padding: 1rem;
            }

            .actions {
                justify-content: stretch;
            }

            button {
                width: 100%;
            }
        }
    </style>
</head>
<body>
    <main class="scene">
        <section class="card">
            <div class="badge">MESHCORE ADMIN</div>
            <h1>Sign In To Router Control</h1>
            <p>Authenticate with your admin password or approved public key to manage relay and companion settings.</p>
            <form method="post" action="/login">
                <label>
                    Admin public key (hex)
                    <input type="text" name="publicKey" placeholder="Optional" autocomplete="off" />
                </label>
                <label>
                    Password
                    <input type="password" name="password" placeholder="Enter admin password" autocomplete="current-password" />
                </label>
                <div class="actions">
                    <span class="hint">Use either a valid password or a configured key.</span>
                    <button type="submit">Sign in</button>
                </div>
            </form>
            <div class="footer-wrap">
""" + FooterHtml() + """
            </div>
        </section>
    </main>
</body>
</html>
""";

    private static string SettingsPageHtml() => """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8" />
    <title>MeshCore Admin Console</title>
    <link rel="icon" type="image/svg+xml" href="/favicon.svg" />
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
        #settings-form { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 0.6rem 1rem; }
        #settings-form h3 { grid-column: 1 / -1; margin: 0.6rem 0 0.2rem 0; }
        #settings-form label { display: flex; flex-direction: column; font-size: 0.92rem; gap: 0.2rem; }
        #settings-form input, #settings-form select { padding: 0.4rem; }
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
            <button id="reload">Reload settings</button>
            <button id="save">Save settings</button>
            <button id="save-raw">Save raw JSON</button>
        </div>
        <div id="config-status" class="summary">Loading settings...</div>

        <form id="settings-form">
            <h3>Web service</h3>
            <label>Web port:
                <input id="cfg-web-port" type="number" min="1" max="65535" />
            </label>

            <h3>Repeater service</h3>
            <label>
                <input id="cfg-repeater-enabled" type="checkbox" /> Enable repeater service
            </label>
            <label>Repeater name:
                <input id="cfg-repeater-name" type="text" />
            </label>
            <label>LoRa interface name:
                <input id="cfg-repeater-interface-name" type="text" />
            </label>
            <label>LoRa chip:
                <select id="cfg-repeater-chip">
                    <option value="sx126x">sx126x</option>
                    <option value="sx127x">sx127x</option>
                </select>
            </label>
            <label>LoRa region/profile:
                <select id="cfg-repeater-profile">
                    <option value="eu868-narrow">EU868 Narrow</option>
                    <option value="eu868-wide">EU868 Wide</option>
                    <option value="eu433-narrow">EU433 Narrow</option>
                    <option value="eu433-wide">EU433 Wide</option>
                    <option value="us915-narrow">US915 Narrow</option>
                    <option value="us915-wide">US915 Wide</option>
                    <option value="au915-narrow">AU915 Narrow</option>
                    <option value="au915-wide">AU915 Wide</option>
                    <option value="as923-narrow">AS923 Narrow</option>
                    <option value="as923-wide">AS923 Wide</option>
                    <option value="in865-narrow">IN865 Narrow</option>
                    <option value="in865-wide">IN865 Wide</option>
                    <option value="kr920-narrow">KR920 Narrow</option>
                    <option value="kr920-wide">KR920 Wide</option>
                    <option value="ru864-narrow">RU864 Narrow</option>
                    <option value="ru864-wide">RU864 Wide</option>
                    <option value="cn470-narrow">CN470 Narrow</option>
                    <option value="cn470-wide">CN470 Wide</option>
                    <option value="cn779-narrow">CN779 Narrow</option>
                    <option value="cn779-wide">CN779 Wide</option>
                    <option value="jp920-narrow">JP920 Narrow</option>
                    <option value="jp920-wide">JP920 Wide</option>
                    <option value="custom">Custom (manual)</option>
                </select>
            </label>
            <label>Profile actions:
                <button id="cfg-repeater-apply-profile" type="button">Apply profile defaults</button>
            </label>
            <label>LoRa frequency (Hz):
                <input id="cfg-repeater-frequency" type="number" min="100000000" max="1000000000" />
            </label>
            <label>LoRa bandwidth (Hz):
                <input id="cfg-repeater-bandwidth" type="number" min="7800" max="500000" />
            </label>
            <label>LoRa spreading factor:
                <input id="cfg-repeater-sf" type="number" min="5" max="12" />
            </label>
            <label>LoRa coding rate:
                <input id="cfg-repeater-cr" type="number" min="5" max="8" />
            </label>
            <label>LoRa TX power (dBm):
                <input id="cfg-repeater-txpower" type="number" min="-4" max="27" />
            </label>
            <label>
                <input id="cfg-guest-open" type="checkbox" /> Allow guest access
            </label>
            <label>
                <input id="cfg-readonly" type="checkbox" /> Read-only repeater mode
            </label>
            <label>Admin password:
                <input id="cfg-admin-password" type="text" />
            </label>
            <label>Welcome text:
                <input id="cfg-welcome" type="text" />
            </label>

            <h3>Companion service</h3>
            <label>
                <input id="cfg-companion-enabled" type="checkbox" /> Enable companion service
            </label>
            <label>Companion name:
                <input id="cfg-companion-name" type="text" />
            </label>
            <label>Companion app interface:
                <select id="cfg-companion-app-interface">
                    <option value="wifi">wifi</option>
                    <option value="serial">serial</option>
                </select>
            </label>
            <label>Companion WiFi port:
                <input id="cfg-companion-wifi-port" type="number" min="1" max="65535" />
            </label>
            <label>Companion WiFi listen:
                <input id="cfg-companion-wifi-listen" type="text" placeholder="0.0.0.0" />
            </label>
            <label>Companion serial port:
                <input id="cfg-companion-serial-port" type="text" placeholder="/dev/ttyS0" />
            </label>
            <label>Companion serial speed:
                <input id="cfg-companion-serial-speed" type="number" min="1200" max="3000000" />
            </label>
            <label>Companion channels:
                <input id="cfg-companion-channels" type="number" min="1" max="64" />
            </label>
            <label>
                <input id="cfg-companion-add-public" type="checkbox" /> Add public channel automatically
            </label>

            <h3>GPS tracking</h3>
            <label>
                <input id="cfg-gps-enabled" type="checkbox" /> Enable GPS tracking
            </label>
            <label>GPS mode:
                <select id="cfg-gps-mode">
                    <option value="average">average</option>
                    <option value="roaming">roaming</option>
                </select>
            </label>
            <label>GPS device:
                <input id="cfg-gps-device" type="text" />
            </label>
            <label>GPS baud:
                <input id="cfg-gps-baud" type="number" min="1200" max="115200" />
            </label>
            <label>Sample interval (seconds):
                <input id="cfg-gps-sample" type="number" min="1" max="86400" />
            </label>
            <label>Retention days:
                <input id="cfg-gps-retention" type="number" min="1" max="3650" />
            </label>
        </form>

        <details>
            <summary>Advanced raw JSON</summary>
            <textarea id="editor" style="height: 28vh;"></textarea>
        </details>
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

        let currentConfig = {};

        function getConfigStatus() {
            return document.getElementById('config-status');
        }

        const loraProfiles = {
            'eu868-narrow': { frequency: 869618000, bw: 62500, sf: 8, cr: 8, txpower: 22 },
            'eu868-wide': { frequency: 868100000, bw: 125000, sf: 8, cr: 8, txpower: 22 },
            'eu433-narrow': { frequency: 433175000, bw: 62500, sf: 8, cr: 8, txpower: 20 },
            'eu433-wide': { frequency: 433175000, bw: 125000, sf: 8, cr: 8, txpower: 20 },
            'us915-narrow': { frequency: 915000000, bw: 62500, sf: 8, cr: 8, txpower: 22 },
            'us915-wide': { frequency: 915000000, bw: 125000, sf: 8, cr: 8, txpower: 22 },
            'au915-narrow': { frequency: 916800000, bw: 62500, sf: 8, cr: 8, txpower: 22 },
            'au915-wide': { frequency: 916800000, bw: 125000, sf: 8, cr: 8, txpower: 22 },
            'as923-narrow': { frequency: 923200000, bw: 62500, sf: 8, cr: 8, txpower: 16 },
            'as923-wide': { frequency: 923200000, bw: 125000, sf: 8, cr: 8, txpower: 16 },
            'in865-narrow': { frequency: 865062500, bw: 62500, sf: 8, cr: 8, txpower: 20 },
            'in865-wide': { frequency: 865062500, bw: 125000, sf: 8, cr: 8, txpower: 20 },
            'kr920-narrow': { frequency: 922100000, bw: 62500, sf: 8, cr: 8, txpower: 14 },
            'kr920-wide': { frequency: 922100000, bw: 125000, sf: 8, cr: 8, txpower: 14 },
            'ru864-narrow': { frequency: 864100000, bw: 62500, sf: 8, cr: 8, txpower: 20 },
            'ru864-wide': { frequency: 864100000, bw: 125000, sf: 8, cr: 8, txpower: 20 },
            'cn470-narrow': { frequency: 470300000, bw: 62500, sf: 8, cr: 8, txpower: 17 },
            'cn470-wide': { frequency: 470300000, bw: 125000, sf: 8, cr: 8, txpower: 17 },
            'cn779-narrow': { frequency: 779500000, bw: 62500, sf: 8, cr: 8, txpower: 10 },
            'cn779-wide': { frequency: 779500000, bw: 125000, sf: 8, cr: 8, txpower: 10 },
            'jp920-narrow': { frequency: 920800000, bw: 62500, sf: 8, cr: 8, txpower: 13 },
            'jp920-wide': { frequency: 920800000, bw: 125000, sf: 8, cr: 8, txpower: 13 }
        };

        function applyLoraProfileDefaults(profileKey) {
            if (!profileKey || profileKey === 'custom' || !(profileKey in loraProfiles)) {
                return;
            }

            const profile = loraProfiles[profileKey];
            document.getElementById('cfg-repeater-frequency').value = profile.frequency;
            document.getElementById('cfg-repeater-bandwidth').value = profile.bw;
            document.getElementById('cfg-repeater-sf').value = profile.sf;
            document.getElementById('cfg-repeater-cr').value = profile.cr;
            document.getElementById('cfg-repeater-txpower').value = profile.txpower;
        }

        function getPath(root, path, fallback = null) {
            let current = root;
            for (const key of path) {
                if (current === null || current === undefined || typeof current !== 'object' || !(key in current)) {
                    return fallback;
                }

                current = current[key];
            }

            return current ?? fallback;
        }

        function ensurePath(root, path) {
            let current = root;
            for (const key of path) {
                if (!(key in current) || current[key] === null || typeof current[key] !== 'object' || Array.isArray(current[key])) {
                    current[key] = {};
                }

                current = current[key];
            }

            return current;
        }

        function setStatus(message, isError = false) {
            const status = getConfigStatus();
            status.textContent = message;
            status.style.color = isError ? '#b42318' : '#586069';
        }

        function populateSettingsForm(config) {
            const devices = Array.isArray(config.devices) ? config.devices : [];
            const interfaces = Array.isArray(config.interfaces) ? config.interfaces : [];

            const repeaterEnabled = devices.includes('repeater');
            const companionEnabled = devices.includes('companion');

            const repeaterInterfaceName = interfaces.length > 0 ? interfaces[0] : 'lora';
            const repeaterInterfaceSection = getPath(config, ['interface', repeaterInterfaceName], {});
            const repeaterDeviceSection = getPath(config, ['device', 'repeater'], {});
            const companionDeviceSection = getPath(config, ['device', 'companion'], {});
            const gps = getPath(config, ['gps'], {});

            document.getElementById('cfg-web-port').value = getPath(config, ['server', 'web', 'port'], 80);

            document.getElementById('cfg-repeater-enabled').checked = repeaterEnabled;
            document.getElementById('cfg-repeater-name').value = repeaterDeviceSection.name ?? 'Mesh Relay';
            document.getElementById('cfg-repeater-interface-name').value = repeaterInterfaceName;
            document.getElementById('cfg-repeater-chip').value = repeaterInterfaceSection.chip ?? 'sx126x';

            const configuredProfile = (repeaterInterfaceSection.profile ?? repeaterInterfaceSection.region ?? repeaterInterfaceSection.band ?? 'eu868-narrow').toString().toLowerCase();
            const profileInput = document.getElementById('cfg-repeater-profile');
            profileInput.value = configuredProfile in loraProfiles ? configuredProfile : 'custom';

            document.getElementById('cfg-repeater-frequency').value = repeaterInterfaceSection.frequency ?? 869618000;
            document.getElementById('cfg-repeater-bandwidth').value = repeaterInterfaceSection.bw ?? 62500;
            document.getElementById('cfg-repeater-sf').value = repeaterInterfaceSection.sf ?? 8;
            document.getElementById('cfg-repeater-cr').value = repeaterInterfaceSection.cr ?? 8;
            document.getElementById('cfg-repeater-txpower').value = repeaterInterfaceSection.txpower ?? 22;
            document.getElementById('cfg-guest-open').checked = !!repeaterDeviceSection['guest.open'];
            document.getElementById('cfg-readonly').checked = !!repeaterDeviceSection['readonly'];
            document.getElementById('cfg-admin-password').value = repeaterDeviceSection['admin.password'] ?? '';
            document.getElementById('cfg-welcome').value = repeaterDeviceSection['welcome'] ?? '';

            document.getElementById('cfg-companion-enabled').checked = companionEnabled;
            document.getElementById('cfg-companion-name').value = companionDeviceSection.name ?? 'Companion Radio';
            document.getElementById('cfg-companion-app-interface').value = companionDeviceSection.interface ?? 'wifi';
            document.getElementById('cfg-companion-wifi-port').value = companionDeviceSection['wifi.port'] ?? 5000;
            document.getElementById('cfg-companion-wifi-listen').value = companionDeviceSection['wifi.listen'] ?? '0.0.0.0';
            document.getElementById('cfg-companion-serial-port').value = companionDeviceSection['serial.port'] ?? '/dev/ttyS0';
            document.getElementById('cfg-companion-serial-speed').value = companionDeviceSection['serial.speed'] ?? 115200;
            document.getElementById('cfg-companion-channels').value = companionDeviceSection.channels ?? 32;
            document.getElementById('cfg-companion-add-public').checked = companionDeviceSection.add_public_channel ?? true;

            document.getElementById('cfg-gps-enabled').checked = !!gps.enabled;
            document.getElementById('cfg-gps-mode').value = gps.mode ?? 'average';
            document.getElementById('cfg-gps-device').value = gps.device ?? '/dev/serial0';
            document.getElementById('cfg-gps-baud').value = gps.baud ?? 9600;
            document.getElementById('cfg-gps-sample').value = gps.sample_interval_seconds ?? 60;
            document.getElementById('cfg-gps-retention').value = gps.retention_days ?? 365;
        }

        function buildConfigFromForm() {
            const config = JSON.parse(JSON.stringify(currentConfig ?? {}));
            const webPort = Number(document.getElementById('cfg-web-port').value || 80);
            const repeaterEnabled = document.getElementById('cfg-repeater-enabled').checked;
            const companionEnabled = document.getElementById('cfg-companion-enabled').checked;
            const repeaterInterfaceName = (document.getElementById('cfg-repeater-interface-name').value || 'lora').trim();

            const devices = [];
            if (repeaterEnabled) {
                devices.push('repeater');
            }

            if (companionEnabled) {
                devices.push('companion');
            }

            if (devices.length === 0) {
                devices.push('repeater');
            }

            config.interfaces = repeaterEnabled ? [repeaterInterfaceName] : [];
            config.devices = devices;

            const serverWeb = ensurePath(config, ['server', 'web']);
            serverWeb.port = Math.min(65535, Math.max(1, webPort));

            const interfaceRoot = ensurePath(config, ['interface']);
            if (repeaterEnabled) {
                if (!(repeaterInterfaceName in interfaceRoot) || typeof interfaceRoot[repeaterInterfaceName] !== 'object' || Array.isArray(interfaceRoot[repeaterInterfaceName])) {
                    interfaceRoot[repeaterInterfaceName] = {};
                }

                interfaceRoot[repeaterInterfaceName].type = 'lora';
                interfaceRoot[repeaterInterfaceName].chip = document.getElementById('cfg-repeater-chip').value;
                const selectedProfile = document.getElementById('cfg-repeater-profile').value;
                if (selectedProfile && selectedProfile !== 'custom') {
                    interfaceRoot[repeaterInterfaceName].profile = selectedProfile;
                    delete interfaceRoot[repeaterInterfaceName].region;
                    delete interfaceRoot[repeaterInterfaceName].band;
                } else {
                    delete interfaceRoot[repeaterInterfaceName].profile;
                }
                interfaceRoot[repeaterInterfaceName].frequency = Number(document.getElementById('cfg-repeater-frequency').value || 869618000);
                interfaceRoot[repeaterInterfaceName].bw = Number(document.getElementById('cfg-repeater-bandwidth').value || 62500);
                interfaceRoot[repeaterInterfaceName].sf = Number(document.getElementById('cfg-repeater-sf').value || 8);
                interfaceRoot[repeaterInterfaceName].cr = Number(document.getElementById('cfg-repeater-cr').value || 8);
                interfaceRoot[repeaterInterfaceName].txpower = Number(document.getElementById('cfg-repeater-txpower').value || 22);
            }

            const deviceRoot = ensurePath(config, ['device']);
            if (!("repeater" in deviceRoot) || typeof deviceRoot.repeater !== 'object' || Array.isArray(deviceRoot.repeater)) {
                deviceRoot.repeater = {};
            }

            deviceRoot.repeater.type = 'repeater';
            deviceRoot.repeater.name = (document.getElementById('cfg-repeater-name').value || 'Mesh Relay').trim();
            deviceRoot.repeater['guest.open'] = document.getElementById('cfg-guest-open').checked;
            deviceRoot.repeater['readonly'] = document.getElementById('cfg-readonly').checked;

            const adminPassword = document.getElementById('cfg-admin-password').value.trim();
            if (adminPassword.length > 0) {
                deviceRoot.repeater['admin.password'] = adminPassword;
            } else {
                delete deviceRoot.repeater['admin.password'];
            }

            const welcome = document.getElementById('cfg-welcome').value.trim();
            if (welcome.length > 0) {
                deviceRoot.repeater.welcome = welcome;
            } else {
                delete deviceRoot.repeater.welcome;
            }

            if (!("companion" in deviceRoot) || typeof deviceRoot.companion !== 'object' || Array.isArray(deviceRoot.companion)) {
                deviceRoot.companion = {};
            }

            deviceRoot.companion.type = 'companion';
            deviceRoot.companion.name = (document.getElementById('cfg-companion-name').value || 'Companion Radio').trim();
            deviceRoot.companion.interface = document.getElementById('cfg-companion-app-interface').value;
            deviceRoot.companion['wifi.port'] = Number(document.getElementById('cfg-companion-wifi-port').value || 5000);
            deviceRoot.companion['wifi.listen'] = (document.getElementById('cfg-companion-wifi-listen').value || '0.0.0.0').trim();
            deviceRoot.companion['serial.port'] = (document.getElementById('cfg-companion-serial-port').value || '/dev/ttyS0').trim();
            deviceRoot.companion['serial.speed'] = Number(document.getElementById('cfg-companion-serial-speed').value || 115200);
            deviceRoot.companion.channels = Number(document.getElementById('cfg-companion-channels').value || 32);
            deviceRoot.companion.add_public_channel = document.getElementById('cfg-companion-add-public').checked;

            const gps = ensurePath(config, ['gps']);
            gps.enabled = document.getElementById('cfg-gps-enabled').checked;
            gps.mode = document.getElementById('cfg-gps-mode').value;
            gps.device = (document.getElementById('cfg-gps-device').value || '/dev/serial0').trim();
            gps.baud = Number(document.getElementById('cfg-gps-baud').value || 9600);
            gps.sample_interval_seconds = Number(document.getElementById('cfg-gps-sample').value || 60);
            gps.retention_days = Number(document.getElementById('cfg-gps-retention').value || 365);

            return config;
        }

        async function postConfig(config) {
            const response = await fetch('/api/config', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(config)
            });

            if (!response.ok) {
                const error = await response.json();
                throw new Error(error?.error || 'Save failed');
            }

            currentConfig = config;
            document.getElementById('editor').value = JSON.stringify(config, null, 2);
        }

        async function loadConfig() {
            const response = await fetch('/api/config');
            if (!response.ok) {
                alert('Unable to load configuration.');
                setStatus('Unable to load settings.', true);
                return;
            }
            const json = await response.json();
            currentConfig = json;
            populateSettingsForm(json);
            document.getElementById('editor').value = JSON.stringify(json, null, 2);
            setStatus('Settings loaded.');
        }

        async function saveConfig() {
            try {
                const config = buildConfigFromForm();
                await postConfig(config);
                setStatus('Settings saved.');
            } catch (exception) {
                setStatus(exception.message, true);
            }
        }

        async function saveRawConfig() {
            try {
                const config = JSON.parse(document.getElementById('editor').value);
                await postConfig(config);
                populateSettingsForm(config);
                setStatus('Raw JSON saved.');
            } catch (exception) {
                setStatus(exception.message, true);
            }
        }

        document.getElementById('refresh-relay').addEventListener('click', loadRelay);
        document.getElementById('refresh-nodes').addEventListener('click', loadNodes);
        document.getElementById('refresh-debug').addEventListener('click', loadDebug);
        document.getElementById('reload').addEventListener('click', loadConfig);
        document.getElementById('save').addEventListener('click', saveConfig);
        document.getElementById('save-raw').addEventListener('click', saveRawConfig);
        document.getElementById('cfg-repeater-profile').addEventListener('change', event => {
            const selected = event.target.value;
            if (selected === 'custom') {
                setStatus('Custom profile selected. Manual LoRa values are unchanged.');
            } else {
                setStatus('Profile selected. Click "Apply profile defaults" to update LoRa values.');
            }
        });
        document.getElementById('cfg-repeater-apply-profile').addEventListener('click', () => {
            const selected = document.getElementById('cfg-repeater-profile').value;
            if (selected === 'custom') {
                setStatus('Custom profile does not apply defaults. Edit values manually.');
                return;
            }

            applyLoraProfileDefaults(selected);
            setStatus(`Applied LoRa defaults for profile: ${selected}.`);
        });
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
