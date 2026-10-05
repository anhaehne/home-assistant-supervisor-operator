using System.Text.Json;
using System.Text.RegularExpressions;

namespace SupervisorOperator.Addons;

/// <summary>
/// Fail-closed capability screening of a normalized upstream manifest. This is
/// not installation authorization: catalog validation and runtime adapters must
/// also succeed before an add-on can be offered as installable.
/// </summary>
public static partial class AddonCapabilityPolicy
{
    private static readonly HashSet<string> MetadataFields = new(StringComparer.Ordinal)
    {
        "name", "version", "slug", "description", "url", "arch", "image",
        "startup", "boot", "init", "timeout", "options", "schema", "panel_icon",
        "panel_title", "stage", "homeassistant", "hassio_role", "ports_description",
        "webui", "watchdog", "services", "discovery", "environment", "map", "ports",
        "ingress", "ingress_port", "ingress_entry", "ingress_stream", "panel_admin",
        "auth_api", "hassio_api", "homeassistant_api", "host_network", "host_pid",
        "host_ipc", "host_dbus", "host_uts", "full_access", "docker_api", "privileged",
        "devices", "uart", "usb", "udev", "gpio", "audio", "video", "kernel_modules",
        "devicetree", "apparmor", "stdin", "journald", "advanced", "build"
    };

    // These need an adapter or policy that the initial prebuilt profile does not have.
    private static readonly string[] UnsupportedFlags =
    [
        "host_network", "host_pid", "host_ipc", "host_dbus", "host_uts", "full_access",
        "docker_api", "uart", "usb", "udev", "gpio", "audio", "video", "kernel_modules",
        "devicetree", "stdin", "journald", "ingress", "auth_api", "hassio_api",
        "homeassistant_api"
    ];

    public static AddonCapabilityResult Evaluate(JsonElement manifest, string architecture)
    {
        var reasons = new List<AddonCapabilityIssue>();
        void Reject(string field, string message) => reasons.Add(new(field, message));
        if (manifest.ValueKind != JsonValueKind.Object)
            return new(null, [new("manifest", "The add-on manifest must be an object.")]);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in manifest.EnumerateObject())
        {
            if (!seen.Add(field.Name)) Reject(field.Name, "Duplicate manifest fields are ambiguous.");
            if (!MetadataFields.Contains(field.Name))
                Reject(field.Name, "This manifest field has no reviewed Kubernetes translation.");
        }

        var version = RequiredString("version");
        var image = RequiredString("image");
        RequiredString("name");
        RequiredString("description");
        var slug = RequiredString("slug");
        if (slug is not null && !SlugPattern().IsMatch(slug)) Reject("slug", "Use a lowercase add-on slug containing letters, digits or underscores.");
        if (version is not null && (!TagPattern().IsMatch(version) || version == "latest"))
            Reject("version", "A concrete version usable as an image tag is required.");

        var supportedArchitecture = architecture is "amd64" or "aarch64" or "armv7" or "armhf" or "i386";
        if (!supportedArchitecture) Reject("arch", "The requested Supervisor architecture is unknown.");
        if (!manifest.TryGetProperty("arch", out var arch) || arch.ValueKind != JsonValueKind.Array ||
            arch.GetArrayLength() == 0 || arch.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            Reject("arch", "The manifest must declare a nonempty list of architectures.");
        else if (!arch.EnumerateArray().Any(item => item.GetString() == architecture))
            Reject("arch", "The image is not declared for the requested architecture.");

        string? reference = null;
        if (image is not null)
        {
            var resolved = image.Replace("{arch}", architecture, StringComparison.Ordinal);
            if (!ImagePattern().IsMatch(resolved))
                Reject("image", "Declare a prebuilt image repository without a tag, digest or unsupported template.");
            else if (supportedArchitecture && version is not null && TagPattern().IsMatch(version) && version != "latest")
                reference = $"{resolved}:{version}";
        }

        foreach (var field in UnsupportedFlags)
        {
            if (!manifest.TryGetProperty(field, out var value)) continue;
            if (value.ValueKind != JsonValueKind.False)
                Reject(field, "This capability requires an adapter that is not implemented in the initial prebuilt profile; only false is supported.");
        }
        if (!manifest.TryGetProperty("init", out var init) || init.ValueKind != JsonValueKind.False)
            Reject("init", "Supervisor defaults init to true; this profile requires explicit init: false until an equivalent init process is implemented.");
        foreach (var field in new[] { "watchdog", "webui" })
            if (manifest.TryGetProperty(field, out _))
                Reject(field, "This runtime integration has no implemented adapter.");
        if (manifest.TryGetProperty("startup", out var startup) &&
            (startup.ValueKind != JsonValueKind.String || startup.GetString() != "application"))
            Reject("startup", "Startup ordering is not implemented; only the application stage is supported.");
        if (!manifest.TryGetProperty("boot", out var boot) ||
            boot.ValueKind != JsonValueKind.String || boot.GetString() != "manual")
            Reject("boot", "Supervisor defaults boot to auto; this profile requires explicit manual boot until automatic startup is implemented.");
        if (manifest.TryGetProperty("homeassistant", out _))
            Reject("homeassistant", "Minimum Core version constraints require catalog version validation that is not implemented yet.");
        foreach (var field in new[] { "privileged", "devices", "services", "discovery", "map" })
        {
            if (!manifest.TryGetProperty(field, out var value)) continue;
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0)
                Reject(field, "This capability requires a reviewed runtime adapter; only an empty list is supported.");
        }
        if (manifest.TryGetProperty("ports", out var ports) &&
            (ports.ValueKind != JsonValueKind.Object || ports.EnumerateObject().Any()))
            Reject("ports", "Published ports require an explicit exposure policy; only an empty mapping is supported.");
        if (manifest.TryGetProperty("environment", out var environment) &&
            (environment.ValueKind != JsonValueKind.Object || environment.EnumerateObject().Any()))
            Reject("environment", "Environment variables require reserved-name validation; only an empty mapping is supported.");
        if (manifest.TryGetProperty("apparmor", out var apparmor) && apparmor.ValueKind != JsonValueKind.True)
            Reject("apparmor", "Disabling or selecting an AppArmor profile requires an explicit security policy.");
        if (manifest.TryGetProperty("hassio_role", out var role) &&
            (role.ValueKind != JsonValueKind.String || role.GetString() != "default"))
            Reject("hassio_role", "Elevated Supervisor roles require route authorization that is not implemented yet.");
        // Shape checking is deliberately separate from full Supervisor option-schema validation.
        foreach (var field in new[] { "options", "schema" })
            if (manifest.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Object)
                Reject(field, "This profile requires an object; full option-schema validation is a separate gate.");
            else if (manifest.TryGetProperty(field, out value) && value.EnumerateObject().Any())
                Reject(field, "Nonempty options and schemas require the Supervisor option-schema validator, which is not implemented yet.");

        return new(reasons.Count == 0 ? reference : null, reasons);

        string? RequiredString(string field)
        {
            if (manifest.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
            Reject(field, field == "image" ? "Build-only add-ons are unavailable; a prebuilt image is required." : "A nonempty string is required.");
            return null;
        }
    }

    [GeneratedRegex(@"\A[a-z0-9_]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
    [GeneratedRegex(@"\A(?:[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]+)?/)?[a-z0-9]+(?:(?:[._-]|/)[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();
}

public sealed record AddonCapabilityIssue(string Field, string Message);
public sealed record AddonCapabilityResult(string? ImageReference, IReadOnlyList<AddonCapabilityIssue> Issues)
{
    public bool CompatibleWithProfile => Issues.Count == 0;
}
