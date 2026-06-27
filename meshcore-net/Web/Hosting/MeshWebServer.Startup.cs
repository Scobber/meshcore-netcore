using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace MeshCoreNet;

public sealed partial class MeshWebServer
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureAdminCredentialFiles();
        var builder = BuildWebApplicationBuilder();

        var app = builder.Build();
        ConfigureMiddleware(app);
        MapRoutes(app, cancellationToken);

        Console.WriteLine($"MeshCore web management server starting on http://0.0.0.0:{_port}");
        Console.WriteLine("MeshCore web middleware profile: accesslog-v2 favicon-svg dataprotection-local");
        await app.RunAsync(cancellationToken);
    }
}
