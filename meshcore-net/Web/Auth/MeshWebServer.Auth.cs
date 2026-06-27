using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace MeshCoreNet;

public sealed partial class MeshWebServer
{
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

    private sealed record LoginRequest(string? PublicKey, string? Password);
}
