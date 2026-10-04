using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoreGateway;

public static class CoreGatewayApi
{
    public static IServiceCollection AddCoreGateway(this IServiceCollection services, IConfiguration configuration)
    {
        var token = configuration["Gateway:Token"];
        if (string.IsNullOrWhiteSpace(token) || Encoding.UTF8.GetByteCount(token) < 32)
            throw new InvalidOperationException("Gateway__Token must be a random credential of at least 32 bytes.");
        var socketPath = configuration["Gateway:Socket"] ?? "/run/supervisor/core.sock";
        if (!Path.IsPathFullyQualified(socketPath)) throw new InvalidOperationException("Core socket must be an absolute local path.");
        services.AddSingleton(new GatewayCredential(token));
        services.AddSingleton(_ => new CoreSocketClient(socketPath));
        services.AddSingleton<ICoreHttpConfigurationTransport>(_ => new CoreHttpConfigurationTransport(socketPath));
        services.AddSingleton<CoreHttpProxyClient>();
        services.AddControllers().ConfigureApplicationPartManager(manager => manager.ApplicationParts.Clear())
            .AddApplicationPart(typeof(CoreGatewayApi).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        return services;
    }

    public static void MapCoreGateway(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/health/live") { await next(context); return; }
            if (!context.RequestServices.GetRequiredService<GatewayCredential>().Matches(context.Request.Headers["X-Gateway-Token"].ToString()))
            {
                context.Response.StatusCode = 401;
                return;
            }
            if (context.Request.QueryString.HasValue) { context.Response.StatusCode = 400; return; }
            await next(context);
        });
        app.MapControllers();
    }
}

public sealed class GatewayCredential
{
    private readonly byte[] digest;
    public GatewayCredential(string token) => digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    public bool Matches(string? token) => !string.IsNullOrEmpty(token) &&
        CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
