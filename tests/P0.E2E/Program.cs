using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Playwright;

if (args is ["browser-smoke"])
{
    using var smoke = await Playwright.CreateAsync();
    foreach (var engine in new[] { smoke.Chromium, smoke.Firefox, smoke.Webkit })
    {
        await using var instance = await engine.LaunchAsync(new() { Headless = true });
        var smokePage = await instance.NewPageAsync();
        await smokePage.SetContentAsync("<button onclick=\"document.querySelector('output').textContent='clicked'\">Check</button><output>ready</output>");
        await smokePage.GetByRole(AriaRole.Button, new() { Name = "Check", Exact = true }).ClickAsync();
        Require(await smokePage.Locator("output").InnerTextAsync() == "clicked", "Browser launch/render/JavaScript/click smoke failed");
        Console.WriteLine($"Browser dependency smoke {engine.Name} passed.");
    }
    return;
}

if (args.Length is < 3 or > 4 || args[0] is not ("fresh" or "existing"))
    throw new ArgumentException("Usage: P0.E2E fresh|existing STATE_DIRECTORY ARTIFACT_DIRECTORY [default|http-migration]");
var mode = args[0];
var httpMigration = args.Length == 4 && args[3] == "http-migration";
var lifecycle = args.Length == 4 && args[3] == "lifecycle";
var state = Path.GetFullPath(args[1]);
var artifacts = Path.GetFullPath(args[2]);
Directory.CreateDirectory(artifacts);
var credentialsPath = Path.Combine(state, "browser-credentials.json");
BrowserCredentials credentials;
if (mode == "fresh")
{
    credentials = new("P0 Tester", "p0tester", Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
    File.WriteAllText(credentialsPath, JsonSerializer.Serialize(credentials));
}
else credentials = JsonSerializer.Deserialize<BrowserCredentials>(File.ReadAllText(credentialsPath))!;
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
await using var context = await browser.NewContextAsync();
var page = await context.NewPageAsync();
page.SetDefaultTimeout(30_000);
var failures = new List<string>();
page.PageError += (_, error) => failures.Add(error);
try
{
    await page.GotoAsync("http://127.0.0.1:18123", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120_000 });
    if (mode == "fresh")
    {
        Console.WriteLine("Opening native onboarding.");
        var start = page.GetByRole(AriaRole.Button, new() { Name = "Create my smart home", Exact = true });
        await start.ClickAsync();
        await page.Locator("input[name=name]").FillAsync(credentials.Name);
        await page.Locator("input[name=username]").FillAsync(credentials.Username);
        await page.Locator("input[name=password]").FillAsync(credentials.Password);
        await page.Locator("input[name=password_confirm]").FillAsync(credentials.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account", Exact = true }).ClickAsync();
        for (var step = 0; step < 8; step++)
        {
            if (await HasHass(page)) break;
            var next = page.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true });
            var finish = page.GetByRole(AriaRole.Button, new() { Name = "Finish", Exact = true });
            var skip = page.GetByRole(AriaRole.Button, new() { Name = "Skip", Exact = true });
            await next.Or(finish).Or(skip).Or(page.Locator("home-assistant")).First.WaitForAsync(new() { Timeout = 120_000 });
            if (await HasHass(page)) break;
            if (await next.CountAsync() > 0) await next.ClickAsync();
            else if (await finish.CountAsync() > 0) await finish.ClickAsync();
            else if (await skip.CountAsync() > 0) await skip.ClickAsync();
            else throw new InvalidOperationException("Unrecognized native onboarding step: " + await page.Locator("body").InnerTextAsync());
            await page.WaitForTimeoutAsync(500);
        }
    }
    else
    {
        await page.Locator("input[name=username]").FillAsync(credentials.Username);
        await page.Locator("input[name=password]").FillAsync(credentials.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
    }
    await page.WaitForFunctionAsync("() => Boolean(document.querySelector('home-assistant')?.hass?.connection)", null, new() { Timeout = 120_000 });
    var http = await Command(page, new { type = "http/config" });
    var migrationTrial = httpMigration && mode == "fresh";
    Require(http.GetProperty("active_config_type").GetString() == (migrationTrial ? "pending" : "stable"), "Unexpected native HTTP configuration slot");
    Require((http.GetProperty("pending").ValueKind != JsonValueKind.Null) == migrationTrial, "Unexpected unconfirmed HTTP settings");
    var settings = http.GetProperty(migrationTrial ? "pending" : "stable");
    Require(settings.GetProperty("server_port").GetInt32() == 80, "Native HTTP port is incorrect");
    var proxiesEnabled = settings.TryGetProperty("use_x_forwarded_for", out var forwarded) && forwarded.GetBoolean();
    Require(proxiesEnabled == httpMigration, "Native trusted-proxy settings changed");
    var transfer = await page.EvaluateAsync<JsonElement>("""
        async () => {
            const hass = document.querySelector('home-assistant').hass;
            const headers = {Authorization: `Bearer ${hass.auth.accessToken}`};
            const form = new FormData();
            form.append('file', new Blob(['P0 upload fixture'], {type:'application/octet-stream'}), 'p0-certificate.pem');
            const upload = await fetch('/api/file_upload', {method:'POST', headers, body:form});
            if (!upload.ok) throw new Error(`Native multipart upload failed: ${upload.status}`);
            const data = await upload.json();
            const remove = await fetch('/api/file_upload', {method:'DELETE', headers:{...headers,'Content-Type':'application/json'}, body:JSON.stringify(data)});
            const proxy = await fetch('/api/', {headers:{...headers,'X-Forwarded-For':'198.51.100.42'}});
            return {upload:upload.status, file_id:data.file_id, cleanup:remove.status, forwarded:proxy.status};
        }
        """);
    Require(transfer.GetProperty("upload").GetInt32() == 200 && transfer.GetProperty("file_id").GetString()!.Length == 32,
        "Native upload did not return a valid file ID");
    Require(transfer.GetProperty("cleanup").GetInt32() == 200, "Native upload cleanup failed");
    Require(transfer.GetProperty("forwarded").GetInt32() == (httpMigration ? 200 : 400), "Trusted proxy authorization is incorrect");
    if (migrationTrial)
    {
        // Confirm the native migration only after verifying its listener and
        // trusted proxy behavior; do not edit private Core storage files.
        await Command(page, new { type = "http/config/promote" });
        var confirmed = await Command(page, new { type = "http/config" });
        Require(confirmed.GetProperty("pending").ValueKind == JsonValueKind.Null &&
            confirmed.GetProperty("stable").GetProperty("use_x_forwarded_for").GetBoolean(), "Native HTTP migration promotion failed");
    }
    var entries = await Command(page, new { type = "config_entries/get" });
    var entry = entries.EnumerateArray().Single(item => item.GetProperty("domain").GetString() == "hassio");
    Require(entry.GetProperty("state").GetString() == "loaded", "Native hassio integration is not loaded");
    var root = await Supervisor(page, "/info");
    Require(root.GetProperty("hassos").ValueKind == JsonValueKind.Null && !root.GetProperty("supported").GetBoolean(), "Installation must not claim HAOS/upstream support");
    var panels = await Command(page, new { type = "get_panels" });
    Require(panels.TryGetProperty("config", out _), "Settings panel is missing");
    var issues = await Command(page, new { type = "repairs/list_issues" });
    Require(issues.GetRawText().Contains("deprecated_method", StringComparison.Ordinal), "Native installation warning is missing");
    Require(issues.GetRawText().Contains("Kubernetes operator preview", StringComparison.Ordinal), "Project installation limitations are missing from native repairs");
    await page.GotoAsync("http://127.0.0.1:18123/config");
    var apps = page.GetByRole(AriaRole.Link, new() { Name = "Apps", Exact = false });
    await apps.First.WaitForAsync();
    await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, mode + "-settings.png"), FullPage = true });
    // The released frontend discovers its native Apps link from Settings.
    await apps.First.ClickAsync();
    var store = page.GetByRole(AriaRole.Button, new() { Name = "Install app", Exact = true })
        .Or(page.GetByRole(AriaRole.Link, new() { Name = "Install app", Exact = true }));
    await store.WaitForAsync();
    await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, mode + "-apps.png"), FullPage = true });
    await store.ClickAsync();
    await page.GetByText("App store", new() { Exact = true }).First.WaitForAsync();
    var catalog = await Supervisor(page, "/store");
    Require(catalog.GetProperty("addons").GetArrayLength() == 0, "P0 must not advertise installable add-ons");
    await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, mode + "-store.png"), FullPage = true });
    Require(failures.Count == 0, "Browser JavaScript errors: " + string.Join("; ", failures));
    if (lifecycle)
    {
        await page.GotoAsync("http://127.0.0.1:18123/config/system");
        await page.GetByRole(AriaRole.Button, new() { Name = "Restart Home Assistant", Exact = true }).ClickAsync();
        await page.GetByText("Interrupts all running automations and scripts.", new() { Exact = true }).ClickAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, "p1-restart-dialog.png"), FullPage = true });
        await page.GetByRole(AriaRole.Button, new() { Name = "Restart", Exact = true }).ClickAsync();
        await page.WaitForFunctionAsync("() => !document.querySelector('home-assistant')?.hass?.connection?.connected", null,
            new() { Timeout = 120_000 });
        Console.WriteLine("Native frontend restart button reached the installed lifecycle operator.");
    }
    File.WriteAllText(Path.Combine(artifacts, mode + "-result.json"), JsonSerializer.Serialize(new
    {
        mode, passed = true, core = "2026.9.4", integration = "loaded", http_migration = httpMigration,
        file_upload = "passed", trusted_proxies = "passed", native_ui = new[] { "onboarding/login", "settings", "apps", "store", "repairs" }
    }));
    Console.WriteLine($"P0 native browser {mode} scenario passed.");
}
catch
{
    await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, mode + "-failure.png"), FullPage = true });
    File.WriteAllText(Path.Combine(artifacts, mode + "-page.txt"), await page.Locator("body").InnerTextAsync());
    File.WriteAllText(Path.Combine(artifacts, mode + "-inputs.json"), (await page.Locator("input").EvaluateAllAsync<JsonElement>("els => els.map(e => ({id:e.id,name:e.name,type:e.type,label:e.getAttribute('aria-label'),labels:Array.from(e.labels||[]).map(l=>l.textContent),host:e.getRootNode().host?.tagName,hostLabel:e.getRootNode().host?.label}))")).GetRawText());
    throw;
}

static async Task<bool> HasHass(IPage page) => await page.EvaluateAsync<bool>("() => Boolean(document.querySelector('home-assistant')?.hass?.connection)");
static async Task<JsonElement> Command(IPage page, object command) => await page.EvaluateAsync<JsonElement>("command => document.querySelector('home-assistant').hass.callWS(command)", command);
static Task<JsonElement> Supervisor(IPage page, string endpoint) => Command(page, new { type = "supervisor/api", endpoint, method = "get" });
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
record BrowserCredentials(string Name, string Username, string Password);
