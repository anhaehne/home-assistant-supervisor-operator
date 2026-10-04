using k8s;
using KubeOps.Operator;
using SupervisorOperator.Foundation;

namespace SupervisorOperator.Lifecycle;

public static class LifecycleRegistration
{
    public static IServiceCollection AddLifecycleOperator(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Operator:Enabled")) return services;
        var ns = OperatorFoundation.ValidateInstalledConfiguration(configuration);
        if (configuration.GetValue<bool>("Operator:EnableFrameworkProbe"))
            throw new InvalidOperationException("Run the disposable framework probe separately from the installed lifecycle operator.");
        services.AddSingleton(_ => KubernetesClientConfiguration.InClusterConfig());
        services.AddSingleton(new Installation(ns));
        services.AddSingleton<PodNetworkDiscovery>();
        services.AddSingleton<CoreLifecycle>();
        services.AddKubernetesOperator(settings =>
        {
            settings.Name = "home-assistant-supervisor-operator";
            settings.Namespace = ns;
            settings.ValidateRegistrations = true;
            settings.ParallelReconciliation.MaxParallelReconciliations = 1;
        }).AddController<InstanceController, HomeAssistantInstance>()
            .AddFinalizer<InstanceFinalizer, HomeAssistantInstance>(HomeAssistantInstance.Finalizer);
        services.AddHostedService<InstanceResync>();
        return services;
    }
}
