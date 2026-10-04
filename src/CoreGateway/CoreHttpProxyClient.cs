using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Supervisor.Contracts;

namespace CoreGateway;

public sealed class HttpProxyConflictException(string message) : Exception(message);

public sealed class CoreHttpProxyClient(ICoreHttpConfigurationTransport transport)
{
    private readonly SemaphoreSlim gate = new(1);

    public async Task<HttpProxyState> Status(HttpProxySettings settings, CancellationToken cancellation)
    {
        var proxies = HttpProxyValidation.Normalize(settings.TrustedProxies);
        return Describe(await transport.Read(cancellation), proxies).State;
    }

    public async Task<HttpProxyState> Stage(HttpProxySettings settings, CancellationToken cancellation)
    {
        var proxies = HttpProxyValidation.Normalize(settings.TrustedProxies);
        await gate.WaitAsync(cancellation);
        try
        {
            var description = Describe(await transport.Read(cancellation), proxies);
            if (description.State.PendingExists)
            {
                if (!description.State.PendingMatches || description.State.PendingError)
                    throw new HttpProxyConflictException("An unrelated or failed native HTTP configuration trial is pending.");
                return description.State;
            }
            if (description.State.StableMatches) return description.State;
            await transport.Configure(description.Desired, cancellation);
            return Describe(await transport.Read(cancellation), proxies).State;
        }
        finally { gate.Release(); }
    }

    public async Task<HttpProxyState> Confirm(HttpProxyConfirmation confirmation, CancellationToken cancellation)
    {
        var proxies = HttpProxyValidation.Normalize(confirmation.TrustedProxies);
        await gate.WaitAsync(cancellation);
        try
        {
            var state = Describe(await transport.Read(cancellation), proxies).State;
            if (confirmation.ExpectedFingerprint != state.DesiredFingerprint)
                throw new HttpProxyConflictException("The intended native HTTP configuration changed before confirmation.");
            if (state.StableMatches && !state.PendingExists) return state;
            if (!state.PendingActive || !state.PendingMatches || state.PendingError)
                throw new HttpProxyConflictException("The intended native HTTP configuration is not an active successful trial.");
            await transport.Promote(cancellation);
            state = Describe(await transport.Read(cancellation), proxies).State;
            if (!state.StableMatches || state.PendingExists)
                throw new HttpProxyConflictException("Native HTTP configuration confirmation was not retained.");
            return state;
        }
        finally { gate.Release(); }
    }

    private static (HttpProxyState State, JsonObject Desired) Describe(JsonObject response, string[] proxies)
    {
        var stable = Clean(response["stable"] as JsonObject ?? response["default"] as JsonObject ?? throw new InvalidDataException("Core returned no stable HTTP configuration."));
        var desired = (JsonObject)stable.DeepClone();
        desired["use_x_forwarded_for"] = proxies.Length > 0;
        desired["trusted_proxies"] = new JsonArray(proxies.Select(proxy => (JsonNode?)JsonValue.Create(proxy)).ToArray());
        var desiredHash = Fingerprint(desired);
        var pending = response["pending"] as JsonObject;
        var pendingHash = pending is null ? null : Fingerprint(Clean(pending));
        return (new HttpProxyState(Fingerprint(stable) == desiredHash, pending is not null,
            pendingHash == desiredHash, response["active_config_type"]?.GetValue<string>() == "pending",
            pending?["error"] is not null, desiredHash, pendingHash), desired);
    }

    private static JsonObject Clean(JsonObject configuration)
    {
        var result = (JsonObject)configuration.DeepClone();
        result.Remove("created_at"); result.Remove("error"); result.Remove("error_message");
        result["use_x_forwarded_for"] = result["use_x_forwarded_for"]?.GetValue<bool>() ?? false;
        var proxies = result["trusted_proxies"] as JsonArray;
        string[] normalized;
        try
        {
            // Existing native configuration may contain all-address networks.
            // Preserve them for comparison so installing a restricted policy or
            // disabling trust can repair that state; new settings prohibit /0.
            normalized = (proxies?.Select(value => value?.GetValue<string>() ?? throw new InvalidDataException("Invalid stored proxy address.")) ?? [])
                .Select(value => value is "0.0.0.0/0" or "::/0" ? value : HttpProxyValidation.Normalize([value])[0])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (ArgumentException exception) { throw new InvalidDataException("Invalid stored proxy configuration.", exception); }
        result["trusted_proxies"] = new JsonArray(normalized.Select(proxy => (JsonNode?)JsonValue.Create(proxy)).ToArray());
        return result;
    }

    private static string Fingerprint(JsonNode value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value).ToJsonString()))).ToLowerInvariant();

    private static JsonNode Canonical(JsonNode value) => value switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Value is null ? null : Canonical(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : Canonical(item)).ToArray()),
        _ => value.DeepClone()
    };
}
