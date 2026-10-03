"""Inspect pinned source registrations and the client/frontend shipped in Core.

Runs in stock Core with a tar stream of release-pinned route registrations on
stdin. Registration stubs never instantiate upstream runtime objects or make
requests; version conditionals and dynamic log prefixes execute normally.
"""
import ast
import builtins
import dataclasses
import functools
import hashlib
import inspect
import json
import pathlib
import pkgutil
import importlib
import re
import sys
import tarfile
from importlib.metadata import version
import aiohasupervisor.models
import hass_frontend

source_archive = tarfile.open(fileobj=sys.stdin.buffer, mode="r|*")
sources = {entry.name: source_archive.extractfile(entry).read().decode() for entry in source_archive if entry.isfile()}
source = sources["supervisor/__init__.py"]

class Stub:
    def __init__(self, name="upstream"): self.name = name
    def __getattr__(self, field): return Stub(self.name + "." + field)
    def __call__(self, *args, **kwargs): return self
    def __repr__(self): return self.name

class Globals(dict):
    def __missing__(self, name):
        value = Stub(name)
        self[name] = value
        return value

class App:
    def __init__(self, api_version): self.api_version, self.routes = api_version, []
    def add_routes(self, routes): self.routes.extend(routes)

class Web:
    def __getattr__(self, method):
        if method == "route": return lambda verb, path, handler: [(str(verb), path, str(handler))]
        return lambda path, handler, **kwargs: [(method.upper(), path, handler_name(handler))]

def handler_name(handler):
    # Function repr embeds process-specific addresses. Preserve the callable
    # and partial arguments explicitly so independent inventories compare.
    if isinstance(handler, functools.partial):
        arguments = [repr(argument) for argument in handler.args]
        arguments.extend(f"{name}={value!r}" for name, value in handler.keywords.items())
        callable_name = repr(handler.func) if isinstance(handler.func, Stub) else handler_name(handler.func)
        return "functools.partial(" + ", ".join([callable_name, *arguments]) + ")"
    return str(getattr(handler, "__name__", handler))

class AppVersion:
    V1, V2 = "v1", "v2"

parsed = ast.parse(source)
original = next(node for node in parsed.body if isinstance(node, ast.ClassDef) and node.name == "RestAPI")
methods = [node for node in original.body if isinstance(node, ast.FunctionDef) and node.name.startswith("_register_") and node.name != "_register_panel"]
module = ast.Module(body=[ast.ImportFrom(module="__future__", names=[ast.alias(name="annotations")], level=0),
    ast.ClassDef(name="Harness", bases=[], keywords=[], body=methods, decorator_list=[])], type_ignores=[])
namespace = Globals(vars(builtins))
namespace.update(web=Web(), AppVersion=AppVersion, partial=functools.partial,
    api_process=lambda handler: handler, api_process_raw=lambda *args, **kwargs: lambda handler: handler,
    hdrs=type("Headers", (), {"METH_ANY": "*"}), __name__="contract_inventory")
exec(compile(ast.fix_missing_locations(module), "released_routes", "exec"), namespace)
harness = namespace["Harness"]()
harness.coresys = Stub()
harness._api_host = Stub("api_host")
harness.versions = {"v1": App("v1"), "v2": App("v2")}
for api_version, app in harness.versions.items():
    for method in methods:
        if method.name != "_register_advanced_logs": getattr(harness, method.name)(app)

supported_get = {"/info", "/supervisor/ping", "/supervisor/info", "/supervisor/stats", "/host/info", "/os/info",
    "/network/info", "/core/info", "/core/stats", "/homeassistant/info", "/homeassistant/stats", "/store", "/store/addons",
    "/store/repositories", "/addons", "/jobs/info", "/mounts", "/ingress/panels", "/resolution/info", "/available_updates",
    "/supervisor/available_updates", "/core/api/", "/core/api/config", "/homeassistant/api/", "/homeassistant/api/config",
    "/discovery", "/services", "/services/{service}", "/backups", "/backups/info"}
supported_post = {"/supervisor/options", "/core/options", "/homeassistant/options", "/supervisor/update"}
route_rows = []
for api_version, app in harness.versions.items():
    for route in app.routes:
        for method, path, handler in route:
            supported = api_version == "v1" and ((method == "GET" and path in supported_get) or (method == "POST" and path in supported_post))
            excluded = api_version == "v2" or (method != "GET" and path.startswith(("/host/", "/os/", "/network/", "/docker/")))
            route_rows.append(dict(version=api_version, method=method, path=path, handler=handler,
                status="supported" if supported else "unavailable" if excluded else "deferred"))
route_rows.append(dict(version="v1", method="GET", path="/app/{path:.*}", handler="StaticResourceConfig", status="deferred"))

models = {}
for package in pkgutil.iter_modules(aiohasupervisor.models.__path__, aiohasupervisor.models.__name__ + "."):
    module = importlib.import_module(package.name)
    for name, model in vars(module).items():
        if inspect.isclass(model) and dataclasses.is_dataclass(model) and model.__module__ == module.__name__:
            models[name] = [dict(name=field.name, type=str(field.type), required=field.default is dataclasses.MISSING and field.default_factory is dataclasses.MISSING)
                for field in dataclasses.fields(model)]

frontend = pathlib.Path(hass_frontend.__file__).parent
frontend_calls = []
for asset in sorted(frontend.rglob("*.js")):
    text = asset.read_text()
    paths = sorted(set(re.findall(r'["\x27`](/(?:hassio|config/apps|config/app|config/addons|supervisor|core|os|host|store|addons|network|mounts|ingress|jobs|resolution)[^"\x27`\s]*)["\x27`]', text)))
    if paths: frontend_calls.append(dict(asset=str(asset.relative_to(frontend)), sha256=hashlib.sha256(asset.read_bytes()).hexdigest(), literal_paths=paths))
assert frontend_calls, "No bundled frontend calls found; check the pinned asset layout"

result = dict(baseline=dict(core="2026.9.4", supervisor="2026.09.3", client=version("aiohasupervisor"), frontend=version("home-assistant-frontend")),
    sources={name: hashlib.sha256(text.encode()).hexdigest() for name, text in sources.items()},
    routes=sorted(route_rows, key=lambda route: (route["version"], route["path"], route["method"])),
    client_models=models, bundled_frontend=frontend_calls,
    authorization_scope="Core credential only; ping public; per-add-on identities and route roles deferred",
    proxy_read_subset=sorted(path for path in supported_get if "/api/" in path),
    project_routes=["GET /health/live", "GET /operator/info"],
    notes=["GET registrations also permit HEAD upstream; HEAD is deferred in this spike.",
        "P0 options support the bootstrap timezone/country/diagnostics and Core port/ssl/null refresh-token subset.",
        "Supervisor update supports the no-upgrade response only.", "Static Supervisor /app assets are deferred; stock Core supplies the tested frontend.",
        "Bundled literal paths are a static inventory, not a claim that all frontend flows are supported."])
json.dump(result, sys.stdout, indent=2)
print()
