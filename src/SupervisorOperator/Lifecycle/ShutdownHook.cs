using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using SupervisorOperator.Foundation;

namespace SupervisorOperator.Lifecycle;

public static class ShutdownHook
{
    public static async Task Run(IConfiguration configuration)
    {
        var ns = OperatorFoundation.ValidateInstalledConfiguration(configuration);
        using var client = new KubernetesClient(KubernetesClientConfiguration.InClusterConfig());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var instance = await client.GetAsync<HomeAssistantInstance>(HomeAssistantInstance.ResourceName, ns, timeout.Token);
        if (instance is null) return;
        instance.Spec.DesiredState = "Stopped";
        instance.Spec.Command = new CoreCommand { Id = Guid.NewGuid().ToString("N"), Action = "Stop" };
        await client.UpdateAsync(instance, timeout.Token);
        while (await client.GetAsync<V1Pod>("core-0", ns, timeout.Token) is not null)
            await Task.Delay(1000, timeout.Token);
        await client.DeleteAsync<HomeAssistantInstance>(HomeAssistantInstance.ResourceName, ns, timeout.Token);
        while (await client.GetAsync<HomeAssistantInstance>(HomeAssistantInstance.ResourceName, ns, timeout.Token) is not null)
            await Task.Delay(1000, timeout.Token);
    }
}
