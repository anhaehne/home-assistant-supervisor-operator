using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Supervisor.Contracts;

namespace SupervisorOperator;

public static class SupervisorApi
{
    public static IServiceCollection AddSupervisorApi(this IServiceCollection services, IConfiguration configuration)
    {
        var token = configuration["Supervisor:CoreToken"];
        if (string.IsNullOrWhiteSpace(token) || Encoding.UTF8.GetByteCount(token) < 32)
            throw new InvalidOperationException("Configure Supervisor__CoreToken with a random credential of at least 32 bytes.");
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        services.AddControllers().ConfigureApplicationPartManager(manager => manager.ApplicationParts.Clear())
            .AddApplicationPart(typeof(SupervisorApi).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        services.AddSingleton(new CoreCredential(token));
        services.AddSingleton<InstanceOptionsStore>();
        services.AddSingleton<GatewayClient>();
        services.AddSingleton<P0ReadModels>();
        services.AddSingleton<CoreMetrics>();
        return services;
    }

    public static void MapSupervisorApi(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path != "/health/live" && context.Request.Path != "/supervisor/ping" &&
                !context.RequestServices.GetRequiredService<CoreCredential>().Matches(ExtractToken(context.Request)))
            {
                context.Response.StatusCode = 401;
                return;
            }
            try { await next(context); }
            catch (JsonException) { await Error("Invalid json").ExecuteAsync(context); }
            catch (ApiValidationException exception) { await Error(exception.Message).ExecuteAsync(context); }
            catch (k8s.Autorest.HttpOperationException)
            {
                await Results.Json(new ApiError("The installed Kubernetes adapter is unavailable"), statusCode: 503).ExecuteAsync(context);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
            {
                await Results.Json(new ApiError("Core gateway is unavailable"), statusCode: 503).ExecuteAsync(context);
            }
        });
        app.MapControllers();
    }

    private static IResult Error(string message) => Results.Json(new ApiError(message), statusCode: 400);
    private static string? ExtractToken(HttpRequest request)
    {
        foreach (var header in new[] { "X-Supervisor-Token", "X-Hassio-Key" })
            if (request.Headers.TryGetValue(header, out var token) && !string.IsNullOrEmpty(token)) return token.ToString();
        return request.Headers.Authorization.ToString().Split(' ').LastOrDefault();
    }
}

public sealed class ApiValidationException(string message) : Exception(message);
public sealed class CoreCredential
{
    private readonly byte[] digest;
    public CoreCredential(string token) => digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    public bool Matches(string? token) => !string.IsNullOrEmpty(token) &&
        CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
