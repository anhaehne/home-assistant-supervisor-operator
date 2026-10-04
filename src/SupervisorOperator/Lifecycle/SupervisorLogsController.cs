using k8s;
using KubeOps.KubernetesClient;
using Microsoft.AspNetCore.Mvc;

namespace SupervisorOperator.Lifecycle;

[ApiController]
public sealed class SupervisorLogsController(IServiceProvider services, IConfiguration configuration) : SupervisorControllerBase
{
    [HttpGet("/supervisor/logs")]
    [HttpGet("/supervisor/logs/latest")]
    public async Task<IActionResult> Logs(CancellationToken cancellation)
    {
        var client = services.GetService<IKubernetesClient>();
        var installation = services.GetService<Installation>();
        var name = configuration["Instance:OperatorPodName"];
        if (client is null || installation is null || string.IsNullOrEmpty(name)) return ApiError("Supervisor logs require the installed operator");
        var stream = await client.ApiClient.CoreV1.ReadNamespacedPodLogAsync(name, installation.Namespace,
            container: "supervisor", tailLines: 1000, cancellationToken: cancellation);
        return File(stream, "text/plain");
    }
}
