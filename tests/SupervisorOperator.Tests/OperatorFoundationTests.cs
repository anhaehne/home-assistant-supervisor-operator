using k8s;
using KubeOps.Abstractions.Reconciliation.Finalizer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupervisorOperator.Foundation;

namespace SupervisorOperator.Tests;

public sealed class OperatorFoundationTests
{
    [Fact]
    public void RegisteredFinalizerKeyMatchesFrameworkAutoAttachment()
    {
        var finalizer = new ProbeFinalizer(null!, new ProbeInstallation("haso-p0"));
        Assert.Equal(FrameworkProbe.Finalizer, finalizer.GetIdentifierName(new FrameworkProbe()));
    }

    [Fact]
    public void LocalApiDoesNotRegisterKubernetesControllersOrCredentials()
    {
        var services = new ServiceCollection();
        services.AddOperatorFoundation(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kubernetes:Namespace"] = "ambient-namespace"
        }).Build());
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("haso-p0", "false")]
    [InlineData("", "true")]
    [InlineData("*", "true")]
    [InlineData("all/namespaces", "true")]
    [InlineData("INVALID", "true")]
    public void ProbeRejectsMissingOptInAndInvalidNamespaceBeforeClientRegistration(string? ns, string? credentials)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operator:EnableFrameworkProbe"] = "true",
            ["Kubernetes:Namespace"] = ns,
            ["Kubernetes:UseInClusterCredentials"] = credentials
        }).Build();
        Assert.Throws<InvalidOperationException>(() => services.AddOperatorFoundation(configuration));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(KubernetesClientConfiguration));
    }

    [Theory]
    [InlineData("other", "framework", "uid")]
    [InlineData("haso-p0", "other", "uid")]
    [InlineData("haso-p0", "framework", "")]
    public void QueuedResourcesCannotCrossInstallationIdentity(string ns, string name, string uid)
    {
        var probe = new FrameworkProbe { Metadata = new() { NamespaceProperty = ns, Name = name, Uid = uid } };
        Assert.Throws<InvalidOperationException>(() => new ProbeInstallation("haso-p0").Validate(probe));
    }
}
