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
using Microsoft.Extensions.Logging;

namespace MeshCoreNet;

public sealed partial class MeshWebServer
{
    private WebApplicationBuilder BuildWebApplicationBuilder()
    {
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
        return builder;
    }

    private void ConfigureMiddleware(WebApplication app)
    {
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
    }

    private void MapRoutes(WebApplication app, CancellationToken cancellationToken)
    {
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

        app.MapGet("/api/login-info", [Authorize] () =>
        {
            var server = GetSection("server") ?? new Dictionary<string, object?>();
            var loginSection = GetSection("server", "admin");
            var filePassword = ReadCredentialFile("password");
            var filePublic = ReadCredentialFile("public");
            return Results.Json(new
            {
                server,
                appVersion = VersionInfo.AppVersion,
                adminEnabled = loginSection is not null
                    || GetString(server, "admin.password") is not null
                    || GetString(server, "admin.keys") is not null
                    || !string.IsNullOrWhiteSpace(filePassword)
                    || !string.IsNullOrWhiteSpace(filePublic)
            });
        });
    }
}
