using System.Net;
using System.Security.Cryptography;
using System.Text;
using k8s;
using k8s.Autorest;
using k8s.Models;
using KubeOps.KubernetesClient;
using Supervisor.Contracts;

namespace SupervisorOperator.Lifecycle;

// All mutations target the installation's fixed singleton. No caller supplies
// a Kubernetes object name, namespace, image, node, or exec command.
public sealed class CoreLifecycle(IKubernetesClient client, Installation installation, GatewayClient gateway, IConfiguration configuration, PodNetworkDiscovery? discovery = null)
{
    public const string Owner = "ha-operator.io/instance-uid";
    public const string Fence = "ha-operator.io/generation";
    public const string TemplateHash = "ha-operator.io/template-hash";

    public Task<HomeAssistantInstance?> Instance(CancellationToken cancellation) =>
        client.GetAsync<HomeAssistantInstance>(HomeAssistantInstance.ResourceName, installation.Namespace, cancellation);

    public Task<V1Pod?> Pod(CancellationToken cancellation) => client.GetAsync<V1Pod>("core-0", installation.Namespace, cancellation);

    public async Task<string> Accept(string action, string? requestKey, CancellationToken cancellation)
    {
        if (requestKey is { Length: > 128 } || requestKey?.Any(char.IsControl) == true)
            throw new ApiValidationException("Invalid Idempotency-Key");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var instance = await Instance(cancellation) ?? throw new ApiValidationException("The instance is unavailable");
            if (instance.Metadata.DeletionTimestamp is not null) throw new ApiValidationException("The instance is being removed");
            if (action == "Restart" && requestKey is null && instance.Spec.DesiredState == "Running" && ManagedProxy(instance))
            {
                var applying = await client.GetAsync<HomeAssistantOperation>(CommandId(instance), installation.Namespace, cancellation);
                // The native callback also occurs under GitOps ownership. It
                // joins this durable operation without changing GitOps intent.
                if (applying?.Spec.TargetUid == instance.Metadata.Uid && applying.Status.HttpProxyFingerprint is not null && !applying.Status.Terminal)
                    return applying.Spec.Id;
            }
            if (instance.Spec.Ownership == "GitOps") throw new ApiValidationException("Core lifecycle is managed by GitOps; change the instance specification");
            if (requestKey is not null)
            {
                var previous = (await Operations(cancellation)).FirstOrDefault(op => op.Metadata.Annotations?.TryGetValue("ha-operator.io/request-key", out var key) == true && key == requestKey);
                if (previous is not null)
                {
                    if (previous.Spec.Action != action || previous.Spec.TargetUid != instance.Metadata.Uid)
                        throw new ApiValidationException("Idempotency-Key was used for a different request");
                    return previous.Spec.Id;
                }
                if (instance.Spec.Command?.RequestKey == requestKey)
                {
                    if (instance.Spec.Command.Action != action) throw new ApiValidationException("Idempotency-Key was used for a different request");
                    return instance.Spec.Command.Id;
                }
            }
            var currentId = CommandId(instance);
            var current = await client.GetAsync<HomeAssistantOperation>(currentId, installation.Namespace, cancellation);
            if (instance.Status.OperationId != currentId || current is null || !current.Status.Terminal)
            {
                if (requestKey is not null) throw new ApiValidationException("Another Core operation is in progress; retry this request after it completes");
                if (instance.Spec.Command?.Action == action) return currentId;
                throw new ApiValidationException("Another Core operation is in progress");
            }
            var id = Guid.NewGuid().ToString("N");
            instance.Spec.Command = new CoreCommand { Id = id, Action = action, RequestKey = requestKey, AcceptedGeneration = (instance.Metadata.Generation ?? 1) + 1 };
            instance.Spec.DesiredState = action == "Stop" ? "Stopped" : "Running";
            try
            {
                // The instance write is the acceptance commit. A disconnected
                // HTTP handler cannot discard it; reconciliation creates the job record.
                await client.UpdateAsync(instance, cancellation);
                return id;
            }
            catch (HttpOperationException exception) when (exception.Response.StatusCode == HttpStatusCode.Conflict) { }
        }
        throw new ApiValidationException("Concurrent instance changes; retry the request");
    }

    public async Task Wait(string id, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(configuration.GetValue("Operator:OperationWaitSeconds", 360)));
        try
        {
            while (true)
            {
                var operation = await client.GetAsync<HomeAssistantOperation>(id, installation.Namespace, timeout.Token);
                if (operation?.Status.Phase == "Succeeded")
                {
                    var instance = await Instance(timeout.Token);
                    // The job and instance status are separate durable writes.
                    // Do not acknowledge the current command until both are
                    // visible, or a following command can be rejected as busy.
                    if (instance is not null && CompletionPublished(instance, operation)) return;
                }
                if (operation?.Status.Phase == "Failed") throw new ApiValidationException(operation.Status.Error ?? "Core operation failed");
                await Task.Delay(500, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ApiValidationException($"Core operation {id} remains pending; inspect /jobs/info");
        }
    }

    public async Task<IReadOnlyList<HomeAssistantOperation>> Operations(CancellationToken cancellation) =>
        (await client.ListAsync<HomeAssistantOperation>(installation.Namespace, cancellationToken: cancellation)).OrderByDescending(op => op.Spec.CreatedAt).ToArray();

    public static string CommandId(HomeAssistantInstance instance)
    {
        if (instance.Spec.Ownership == "Ui" && instance.Spec.Command is { } command)
        {
            if (command.AcceptedGeneration == instance.Metadata.Generation &&
                (instance.Status.ProxyCommandId != command.Id || instance.Status.ProxyCommandNetworkRevision == instance.Status.PodNetworkRevision) ||
                !ManagedProxy(instance) && !instance.Status.HasManagedHttpProxy)
                return command.Id;
            // Each managed specification generation is a distinct intent, including
            // policy rollbacks and releasing management. Never revive an older job.
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{instance.Metadata.Uid}:{instance.Metadata.Generation}:{instance.Spec.DesiredState}" + (instance.Status.PodNetworkRevision == 0 ? "" : $":pod-network:{instance.Status.PodNetworkRevision}"))))[..32].ToLowerInvariant();
    }

    private static bool ManagedProxy(HomeAssistantInstance instance) => instance.Spec.TrustPodNetwork || instance.Spec.HttpProxy is not null;

    private async Task<bool> RefreshPodNetwork(HomeAssistantInstance instance, CancellationToken cancellation)
    {
        string[] networks;
        try
        {
            networks = instance.Spec.TrustPodNetwork
                ? instance.Spec.DesiredState == "Stopped" ? instance.Status.DiscoveredPodNetworks ?? []
                    : await (discovery ?? new PodNetworkDiscovery(client)).Resolve(cancellation)
                : [];
            // Validate the complete union before changing any durable intent.
            if (instance.Spec.DesiredState != "Stopped")
                HttpProxyValidation.Normalize((instance.Spec.HttpProxy?.TrustedProxies ?? []).Concat(networks).ToArray());
        }
        catch (Exception exception) when (!cancellation.IsCancellationRequested &&
            exception is InvalidOperationException or ArgumentException or HttpOperationException or HttpRequestException or TaskCanceledException)
        {
            await Condition(instance, "PodNetworkUnavailable", "Cannot discover usable Pod networks; verify node Pod CIDRs and node-read permissions, or disable trustPodNetwork and supply explicit trusted proxies", cancellation);
            return false;
        }
        var changed = !(instance.Status.DiscoveredPodNetworks ?? []).SequenceEqual(networks);
        if (changed && instance.Spec.TrustPodNetwork)
        {
            var active = await client.GetAsync<HomeAssistantOperation>(CommandId(instance), installation.Namespace, cancellation);
            if (active?.Spec.TargetUid == instance.Metadata.Uid && active.Spec.TargetGeneration == instance.Metadata.Generation &&
                active.Status.HttpProxyFingerprint is not null && !active.Status.Terminal)
            {
                // Finish the already accepted native trial before processing a
                // later topology change. It must not become an unrelated pending trial.
                networks = instance.Status.DiscoveredPodNetworks ?? [];
                changed = false;
            }
        }
        if (changed)
        {
            instance.Status.DiscoveredPodNetworks = networks;
            instance.Status.PodNetworkRevision = checked(instance.Status.PodNetworkRevision + 1);
            instance.Status.Conditions = [new V1Condition { Type = "Ready", Status = "False", Reason = "PodNetworkChanged",
                Message = "Discovered Pod networks changed; waiting for native HTTP configuration to converge",
                ObservedGeneration = instance.Metadata.Generation, LastTransitionTime = DateTime.UtcNow }];
        }
        if (ManagedProxy(instance) && !instance.Status.HasManagedHttpProxy)
        {
            instance.Status.HasManagedHttpProxy = true;
            changed = true;
        }
        if (instance.Spec.Command is { } command && command.AcceptedGeneration == instance.Metadata.Generation &&
            instance.Status.ProxyCommandId != command.Id && ManagedProxy(instance))
        {
            // Bind a newly accepted API command to the first discovered snapshot.
            // Discovery between acceptance and reconciliation cannot lose its ID.
            instance.Status.ProxyCommandId = command.Id;
            instance.Status.ProxyCommandNetworkRevision = instance.Status.PodNetworkRevision;
            changed = true;
        }
        if (changed)
        {
            var updated = await client.UpdateStatusAsync(instance, cancellation);
            instance.Metadata = updated.Metadata;
        }
        return true;
    }

    public static bool CompletionPublished(HomeAssistantInstance instance, HomeAssistantOperation operation) =>
        operation.Spec.TargetUid == instance.Metadata.Uid && operation.Status.Phase == "Succeeded" &&
        (CommandId(instance) != operation.Spec.Id ||
         instance.Status.OperationId == operation.Spec.Id && instance.Status.ObservedGeneration == instance.Metadata.Generation &&
         instance.Status.Conditions.Any(condition => condition.Type == "Ready" && condition.Status == "True" && condition.Reason == "Succeeded"));

    public static bool Ready(V1Pod? pod) => pod?.Metadata.DeletionTimestamp is null &&
        pod?.Status?.Conditions?.Any(condition => condition.Type == "Ready" && condition.Status == "True") == true;

    public async Task Step(HomeAssistantInstance instance, CancellationToken cancellation)
    {
        if (instance.Metadata.Name != HomeAssistantInstance.ResourceName || instance.Metadata.NamespaceProperty != installation.Namespace)
            throw new InvalidOperationException("Instance identity is outside this installation");
        if (instance.Spec.DesiredState != "Stopped" && instance.Spec.HttpProxy is { } explicitProxy)
            HttpProxyValidation.Normalize(explicitProxy.TrustedProxies);
        if (!await RefreshPodNetwork(instance, cancellation)) return;
        var proxySettings = ManagedProxy(instance) && instance.Spec.DesiredState != "Stopped" ? new HttpProxySettings(HttpProxyValidation.Normalize(
            (instance.Spec.HttpProxy?.TrustedProxies ?? []).Concat(instance.Status.DiscoveredPodNetworks ?? []).ToArray())) : null;
        var id = CommandId(instance);
        foreach (var previous in (await Operations(cancellation)).Where(op => op.Spec.TargetUid == instance.Metadata.Uid && op.Spec.Id != id && !op.Status.Terminal))
        {
            previous.Status.Error = "Superseded by a newer durable instance intent";
            await Phase(previous, "Failed", cancellation);
        }
        var operation = await client.GetAsync<HomeAssistantOperation>(id, installation.Namespace, cancellation);
        if (operation is null)
        {
            operation = new HomeAssistantOperation
            {
                Metadata = new V1ObjectMeta { Name = id, NamespaceProperty = installation.Namespace,
                    Annotations = instance.Spec.Ownership == "Ui" && instance.Spec.Command?.Id == id && instance.Spec.Command.RequestKey is { } key ? new Dictionary<string, string> { ["ha-operator.io/request-key"] = key } : null },
                Spec = new OperationSpec { Id = id, TargetUid = instance.Metadata.Uid, TargetGeneration = instance.Metadata.Generation ?? 1,
                    Action = instance.Spec.Ownership == "Ui" && instance.Spec.Command?.Id == id ? instance.Spec.Command.Action : "Reconcile", DesiredState = instance.Spec.DesiredState, CreatedAt = DateTimeOffset.UtcNow }
            };
            operation = await client.CreateAsync(operation, cancellation);
        }
        if (operation.Spec.TargetUid != instance.Metadata.Uid) throw new InvalidOperationException("Operation target UID mismatch");
        var stateful = await client.GetAsync<V1StatefulSet>("core", installation.Namespace, cancellation);
        var pod = await Pod(cancellation);
        if (stateful is null && pod is not null)
        {
            await Condition(instance, "FenceRequired", "Core Pod survives without its StatefulSet; verify its termination before recreating the workload", cancellation);
            return;
        }
        if (pod is not null && pod.Metadata.DeletionTimestamp is not null)
        {
            await Condition(instance, "WaitingForTermination", "Waiting for the old Core Pod to terminate; never force-delete an unreachable Pod", cancellation);
            return;
        }
        // Workload updates are fenced by the instance UID and monotonic generation
        // on the SAME resourceVersion-checked StatefulSet write as replica changes.
        if (stateful is not null)
        {
            try
            {
                ValidateFence(stateful, instance);
                if (pod is not null) ValidatePodFence(pod, stateful, instance);
            }
            catch (InvalidOperationException exception)
            {
                await Condition(instance, "FenceRequired", exception.Message, cancellation);
                return;
            }
        }
        if (instance.Spec.TrustPodNetwork && instance.Spec.DesiredState != "Stopped" &&
            !(operation.Status.HttpProxyFingerprint is not null && !operation.Status.Terminal) &&
            pod?.Status?.PodIP is { Length: > 0 } podAddress &&
            !(instance.Status.DiscoveredPodNetworks ?? []).Any(network => System.Net.IPNetwork.Parse(network).Contains(IPAddress.Parse(podAddress))))
        {
            await Condition(instance, "PodNetworkUnavailable", "Declared Node Pod CIDRs do not cover the Core Pod address; disable trustPodNetwork and supply the authoritative CNI network or explicit proxy addresses", cancellation);
            return;
        }
        var template = await Template(instance, cancellation);
        var templateChanged = stateful is not null &&
            (stateful.Metadata.Annotations?.TryGetValue(TemplateHash, out var appliedHash) != true || appliedHash != template.Metadata.Annotations[TemplateHash] ||
             !stateful.Spec.Template.Spec.Containers.Select(container => container.Image).SequenceEqual(template.Spec.Template.Spec.Containers.Select(container => container.Image)));
        if (operation.Status.Terminal)
        {
            if (operation.Status.Phase == "Failed")
            {
                await Project(instance, operation, pod, cancellation);
                await Prune(id, cancellation);
                return;
            }
            // Repair drift, but upgrades to the workload template always stop first.
            if (templateChanged) await Phase(operation, "Stopping", cancellation);
            else if (stateful?.Spec.Replicas != (instance.Spec.DesiredState == "Running" ? 1 : 0))
                await WriteWorkload(stateful ?? template, instance, instance.Spec.DesiredState == "Running" ? 1 : 0, cancellation);
            else if (instance.Spec.DesiredState == "Running" && (!Ready(pod) || instance.Status.PodUid != pod?.Metadata.Uid))
            {
                // A replacement Pod must pass application health again, even
                // though the original operation previously succeeded.
                await Phase(operation, "Starting", cancellation);
                await Project(instance, operation, pod, cancellation);
            }
            else if (instance.Spec.DesiredState == "Running" && ManagedProxy(instance))
            {
                // Helm changes on a retained PVC must configure the live native
                // HTTP store, not rewrite YAML that Core has already imported.
                await ProxyConfiguration(instance, operation, pod!, proxySettings!, cancellation);
                await Project(instance, operation, pod, cancellation);
            }
            else await Project(instance, operation, pod, cancellation);
            await Prune(id, cancellation);
            return;
        }
        if (operation.Status.Phase == "Accepted")
        {
            var needsStop = operation.Spec.Action == "Restart" || instance.Spec.DesiredState == "Stopped" || templateChanged;
            await Phase(operation, needsStop ? "Stopping" : "Starting", cancellation);
            return;
        }
        if (operation.Status.Phase == "ApplyingHttpConfiguration")
        {
            if (!ManagedProxy(instance))
            {
                await Phase(operation, "WaitingForHealth", cancellation);
            }
            else
            {
                var currentProxy = await gateway.HttpProxy("status", proxySettings!, null, cancellation);
                if (!currentProxy.PendingExists && currentProxy.DesiredFingerprint != operation.Status.HttpProxyFingerprint)
                {
                    // No trial exists yet. Preserve any newer user HTTP settings
                    // in the snapshot before making the first native write.
                    operation.Status.HttpProxyFingerprint = currentProxy.DesiredFingerprint;
                    await Phase(operation, "ApplyingHttpConfiguration", cancellation);
                    return;
                }
                var proxy = await gateway.HttpProxy("stage", proxySettings!, null, cancellation);
                if (proxy.StableMatches && !proxy.PendingExists)
                {
                    operation.Status.HttpProxyFingerprint = null;
                    operation.Status.HttpProxyStagedPodUid = null;
                    await Phase(operation, "WaitingForHealth", cancellation);
                }
                else if (!proxy.PendingMatches || proxy.PendingError || proxy.PendingFingerprint != operation.Status.HttpProxyFingerprint)
                    await Condition(instance, "HttpProxyConflict", "Native HTTP settings changed during proxy configuration; no trial will be promoted", cancellation);
                else await Phase(operation, "Stopping", cancellation);
            }
            await ProjectUnlessBlocked(instance, operation, pod, cancellation);
            return;
        }
        if (operation.Status.Phase == "Stopping")
        {
            if (stateful is not null && stateful.Spec.Replicas != 0)
            {
                await WriteWorkload(stateful, instance, 0, cancellation);
                return;
            }
            if (pod is not null) return;
            if (instance.Spec.DesiredState == "Stopped") await Phase(operation, "Succeeded", cancellation);
            else await Phase(operation, "Starting", cancellation);
            return;
        }
        if (operation.Status.Phase == "Starting")
        {
            // No template replacement while any old Core process may still exist.
            if (templateChanged && pod is not null) return;
            if (templateChanged)
            {
                // Immutable StatefulSet fields cannot be patched. Recreate only
                // after the old Pod is gone; the PVC is independently retained.
                await DeleteWorkload(stateful!, cancellation);
                return;
            }
            await WriteWorkload(stateful ?? template, instance, 1, cancellation);
            await Phase(operation, "WaitingForHealth", cancellation);
            return;
        }
        if (operation.Status.Phase == "WaitingForHealth")
        {
            if (Ready(pod))
            {
                try
                {
                    var core = await gateway.Read<System.Text.Json.JsonElement>("/core/config", cancellation);
                    if (core.TryGetProperty("state", out var state) && state.GetString() == "RUNNING")
                    {
                        if (!ManagedProxy(instance)) await Phase(operation, "Succeeded", cancellation);
                        else await ProxyConfiguration(instance, operation, pod!, proxySettings!, cancellation);
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested) { }
            }
            if (!operation.Status.Terminal && DateTimeOffset.UtcNow - (operation.Status.StartedAt ?? operation.Spec.CreatedAt) > TimeSpan.FromSeconds(configuration.GetValue("Operator:StartupTimeoutSeconds", 600)))
            {
                operation.Status.Error = "Core did not reach application health before the startup timeout; its data is retained";
                await Phase(operation, "Failed", cancellation);
            }
        }
        await Project(instance, operation, pod, cancellation);
        await Prune(operation.Spec.Id, cancellation);
    }

    private async Task ProxyConfiguration(HomeAssistantInstance instance, HomeAssistantOperation operation, V1Pod pod, HttpProxySettings proxySettings, CancellationToken cancellation)
    {
        var core = await gateway.Read<System.Text.Json.JsonElement>("/core/config", cancellation);
        if (!core.TryGetProperty("state", out var state) || state.GetString() != "RUNNING")
        {
            await Phase(operation, "WaitingForHealth", cancellation);
            return;
        }
        var proxy = await gateway.HttpProxy("status", proxySettings, null, cancellation);
        if (proxy.StableMatches && !proxy.PendingExists)
        {
            var needsCompletion = !operation.Status.Terminal || operation.Status.HttpProxyFingerprint is not null || operation.Status.HttpProxyStagedPodUid is not null;
            operation.Status.HttpProxyFingerprint = null;
            operation.Status.HttpProxyStagedPodUid = null;
            if (needsCompletion) await Phase(operation, "Succeeded", cancellation);
            return;
        }
        if (operation.Status.HttpProxyFingerprint is null)
        {
            if (proxy.PendingExists && (!proxy.PendingMatches || proxy.PendingError))
                throw new InvalidOperationException("An unrelated native HTTP configuration trial is pending");
            operation.Status.HttpProxyFingerprint = proxy.DesiredFingerprint;
            operation.Status.HttpProxyStagedPodUid = pod.Metadata.Uid;
            operation.Status.StartedAt = DateTimeOffset.UtcNow;
            // Persist restart ownership before configure invokes the native
            // Supervisor restart callback or the gateway response is lost.
            await Phase(operation, "ApplyingHttpConfiguration", cancellation);
            return;
        }
        if (pod.Metadata.Uid == operation.Status.HttpProxyStagedPodUid)
        {
            // A lost stage response is recovered by retrying the idempotent
            // native command, then proceeding through ordinary termination.
            await Phase(operation, "ApplyingHttpConfiguration", cancellation);
            return;
        }
        if (!proxy.PendingMatches || !proxy.PendingActive || proxy.PendingError ||
            proxy.PendingFingerprint != operation.Status.HttpProxyFingerprint)
            throw new InvalidOperationException("Replacement Core did not activate the expected native HTTP trial");
        var confirmed = await gateway.HttpProxy("confirm", proxySettings, operation.Status.HttpProxyFingerprint, cancellation);
        if (!confirmed.StableMatches || confirmed.PendingExists)
            throw new InvalidOperationException("Native HTTP settings were not confirmed");
        operation.Status.HttpProxyFingerprint = null;
        operation.Status.HttpProxyStagedPodUid = null;
        await Phase(operation, "Succeeded", cancellation);
    }

    private Task ProjectUnlessBlocked(HomeAssistantInstance instance, HomeAssistantOperation operation, V1Pod? pod, CancellationToken cancellation) =>
        instance.Status.Conditions.FirstOrDefault()?.Reason == "HttpProxyConflict" ? Task.CompletedTask : Project(instance, operation, pod, cancellation);

    private async Task<V1StatefulSet> Template(HomeAssistantInstance instance, CancellationToken cancellation)
    {
        var template = await client.GetAsync<V1ConfigMap>("core-workload", installation.Namespace, cancellation)
            ?? throw new InvalidOperationException("Core workload template is missing");
        var workload = KubernetesYaml.Deserialize<V1StatefulSet>(template.Data["core.yaml"]);
        workload.Metadata.Name = "core";
        workload.Metadata.NamespaceProperty = installation.Namespace;
        workload.Metadata.Annotations ??= new Dictionary<string, string>();
        workload.Metadata.Annotations[TemplateHash] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template.Data["core.yaml"])));
        return workload;
    }

    public static void ValidateFence(V1StatefulSet workload, HomeAssistantInstance instance)
    {
        if (workload.Metadata.Annotations?.TryGetValue(Owner, out var owner) != true || owner != instance.Metadata.Uid)
            throw new InvalidOperationException("Core workload belongs to another instance; refusing adoption");
        if (workload.Metadata.Annotations.TryGetValue(Fence, out var fence) &&
            (!long.TryParse(fence, out var revision) || revision > instance.Metadata.Generation))
            throw new InvalidOperationException("A stale controller cannot change the Core workload");
    }

    private async Task WriteWorkload(V1StatefulSet workload, HomeAssistantInstance instance, int replicas, CancellationToken cancellation)
    {
        workload.Metadata.Annotations ??= new Dictionary<string, string>();
        workload.Metadata.Annotations[Owner] = instance.Metadata.Uid;
        workload.Metadata.Annotations[Fence] = (instance.Metadata.Generation ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        workload.Spec.Template.Metadata ??= new V1ObjectMeta();
        workload.Spec.Template.Metadata.Annotations ??= new Dictionary<string, string>();
        workload.Spec.Template.Metadata.Annotations[Owner] = instance.Metadata.Uid;
        workload.Spec.Replicas = replicas;
        if (workload.Metadata.ResourceVersion is null) await client.CreateAsync(workload, cancellation);
        else await client.UpdateAsync(workload, cancellation);
    }

    public static void ValidatePodFence(V1Pod pod, V1StatefulSet workload, HomeAssistantInstance instance)
    {
        if (string.IsNullOrEmpty(workload.Metadata.Uid) ||
            pod.Metadata.OwnerReferences?.Any(owner => owner.Controller == true && owner.Kind == "StatefulSet" &&
                owner.Name == "core" && owner.Uid == workload.Metadata.Uid) != true ||
            pod.Metadata.Annotations?.TryGetValue(Owner, out var instanceUid) != true || instanceUid != instance.Metadata.Uid)
            throw new InvalidOperationException("Core Pod does not belong to this instance and StatefulSet; verify termination before proceeding");
    }

    private Task<V1Status> DeleteWorkload(V1StatefulSet workload, CancellationToken cancellation) =>
        client.ApiClient.AppsV1.DeleteNamespacedStatefulSetAsync("core", installation.Namespace,
            body: new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = workload.Metadata.Uid, ResourceVersion = workload.Metadata.ResourceVersion } },
            propagationPolicy: "Foreground", cancellationToken: cancellation);

    private async Task Phase(HomeAssistantOperation operation, string phase, CancellationToken cancellation)
    {
        operation.Status.Phase = phase;
        operation.Status.CompletedAt = operation.Status.Terminal ? DateTimeOffset.UtcNow : null;
        if (phase == "Starting") operation.Status.StartedAt = DateTimeOffset.UtcNow;
        var updated = await client.UpdateStatusAsync(operation, cancellation);
        operation.Metadata = updated.Metadata;
    }

    private async Task Project(HomeAssistantInstance instance, HomeAssistantOperation operation, V1Pod? pod, CancellationToken cancellation)
    {
        var state = Ready(pod) ? "Running" : pod is null ? "Stopped" : "Starting";
        var healthy = operation.Status.Phase == "Succeeded" && (instance.Spec.DesiredState == "Stopped" ? pod is null : Ready(pod));
        var reason = operation.Status.Phase == "Succeeded" && !healthy ? "Recovering" : operation.Status.Phase;
        if (instance.Status.State == state && instance.Status.OperationId == operation.Spec.Id &&
            instance.Status.ObservedGeneration == instance.Metadata.Generation && instance.Status.PodUid == pod?.Metadata.Uid &&
            instance.Status.Conditions.FirstOrDefault()?.Reason == reason) return;
        instance.Status = new InstanceStatus { ProxyCommandId = instance.Status.ProxyCommandId, ProxyCommandNetworkRevision = instance.Status.ProxyCommandNetworkRevision, PodNetworkRevision = instance.Status.PodNetworkRevision, DiscoveredPodNetworks = instance.Status.DiscoveredPodNetworks, HasManagedHttpProxy = instance.Status.HasManagedHttpProxy, State = state, OperationId = operation.Spec.Id, ObservedGeneration = instance.Metadata.Generation ?? 1,
            PodUid = pod?.Metadata.Uid, Conditions = [new V1Condition { Type = "Ready", Status = healthy ? "True" : "False",
                Reason = reason, Message = operation.Status.Error ?? $"Core lifecycle: {reason}",
                ObservedGeneration = instance.Metadata.Generation, LastTransitionTime = DateTime.UtcNow }] };
        await client.UpdateStatusAsync(instance, cancellation);
    }

    private async Task Condition(HomeAssistantInstance instance, string reason, string message, CancellationToken cancellation)
    {
        if (instance.Status.Conditions.FirstOrDefault()?.Reason == reason) return;
        instance.Status.Conditions = [new V1Condition { Type = "Ready", Status = "False", Reason = reason, Message = message,
            ObservedGeneration = instance.Metadata.Generation, LastTransitionTime = DateTime.UtcNow }];
        await client.UpdateStatusAsync(instance, cancellation);
    }

    public async Task ReportFailure(string uid, CancellationToken cancellation)
    {
        try
        {
            var current = await Instance(cancellation);
            if (current?.Metadata.Uid == uid)
                await Condition(current, "ReconciliationBlocked", "A reconciliation dependency failed; retrying without changing the accepted intent", cancellation);
        }
        catch (Exception) when (!cancellation.IsCancellationRequested) { /* The API can itself be unavailable. Requeue remains durable in the running process. */ }
    }

    private async Task Prune(string current, CancellationToken cancellation)
    {
        var operations = await Operations(cancellation);
        foreach (var old in operations.Where(op => op.Spec.Id != current && op.Status.Terminal).Skip(19))
            await client.DeleteAsync(old, cancellation);
    }

    public async Task<bool> Finalize(HomeAssistantInstance instance, CancellationToken cancellation)
    {
        var workload = await client.GetAsync<V1StatefulSet>("core", installation.Namespace, cancellation);
        var pod = await Pod(cancellation);
        if (workload is not null)
        {
            try
            {
                ValidateFence(workload, instance);
                if (pod is not null) ValidatePodFence(pod, workload, instance);
            }
            catch (InvalidOperationException exception)
            {
                await Condition(instance, "FenceRequired", exception.Message, cancellation);
                return false;
            }
            if (workload.Spec.Replicas != 0) { await WriteWorkload(workload, instance, 0, cancellation); return false; }
        }
        // An orphaned Pod can outlive its StatefulSet. Its absence is required
        // even when there is no workload left to scale or delete.
        if (pod is not null)
        {
            if (workload is null)
                await Condition(instance, "FenceRequired", "Orphaned Core Pod must terminate before instance removal", cancellation);
            return false;
        }
        if (workload is not null) await DeleteWorkload(workload, cancellation);
        foreach (var operation in (await Operations(cancellation)).Where(op => op.Spec.TargetUid == instance.Metadata.Uid && !op.Status.Terminal))
        {
            var stopped = operation.Spec.Action == "Stop" && operation.Spec.DesiredState == "Stopped";
            if (!stopped) operation.Status.Error = "Instance removed after graceful Core termination";
            await Phase(operation, stopped ? "Succeeded" : "Failed", cancellation);
        }
        await Prune(CommandId(instance), cancellation);
        return true; // PVCs have no CR owner references and are never deleted here.
    }
}
