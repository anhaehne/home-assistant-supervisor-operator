using System.Text.RegularExpressions;
using k8s;
using KubeOps.Operator;

namespace SupervisorOperator.Foundation;

public static partial class OperatorFoundation
{
    public static IServiceCollection AddOperatorFoundation(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Operator:EnableFrameworkProbe")) return services;
        var installationNamespace = ValidateInstalledConfiguration(configuration);
        // Register before KubeOps' TryAdd defaults. Never use BuildDefaultConfig,
        // ambient kubeconfigs, or an outer development container's service account.
        services.AddSingleton(_ => KubernetesClientConfiguration.InClusterConfig());
        services.AddKubernetesOperator(settings =>
        {
            settings.Name = "haso-framework-probe";
            settings.Namespace = installationNamespace;
            settings.ValidateRegistrations = true;
            settings.ParallelReconciliation.MaxParallelReconciliations = 1;
        })
            .AddController<ProbeController, FrameworkProbe>()
            .AddFinalizer<ProbeFinalizer, FrameworkProbe>(FrameworkProbe.Finalizer);
        services.AddSingleton(new ProbeInstallation(installationNamespace));
        return services;
    }

    public static string ValidateInstalledConfiguration(IConfiguration configuration)
    {
        var installationNamespace = configuration["Kubernetes:Namespace"] ?? "";
        if (!configuration.GetValue<bool>("Kubernetes:UseInClusterCredentials") ||
            installationNamespace.Length > 63 || !NamespacePattern().IsMatch(installationNamespace) ||
            !KubernetesClientConfiguration.IsInCluster())
            throw new InvalidOperationException("The framework probe requires an installed workload with explicit in-cluster credentials and a valid installation namespace.");
        return installationNamespace;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamespacePattern();
}

public sealed record ProbeInstallation(string Namespace)
{
    public void Validate(FrameworkProbe probe)
    {
        if (probe.Metadata.NamespaceProperty != Namespace || probe.Metadata.Name != FrameworkProbe.Name ||
            string.IsNullOrEmpty(probe.Metadata.Uid))
            throw new InvalidOperationException("Framework probe identity is outside this installation.");
    }
}
