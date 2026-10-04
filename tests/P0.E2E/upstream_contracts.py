"""Exercise the client shipped in stock Core; run inside the guarded kind Pod."""
import asyncio
import json
import os
from importlib.metadata import version
import aiohttp
from aiohasupervisor import SupervisorClient, SupervisorBadRequestError, SupervisorNotFoundError

async def main():
    assert version("aiohasupervisor") == "0.6.0"
    async with aiohttp.ClientSession() as session:
        client = SupervisorClient("http://" + os.environ["SUPERVISOR"], os.environ["SUPERVISOR_TOKEN"], session=session)
        await client.supervisor.ping()
        # HTTP readiness precedes coordinator setup. Require the real initial
        # Core config callback, which runs only after coordinator parsing.
        async with asyncio.timeout(120):
            while (await client.supervisor.info()).country != "US":
                await asyncio.sleep(1)
        root = await client.info()
        assert root.hassos is None and root.supported is False
        core = await client.homeassistant.info()
        assert core.version == "2026.9.4" and core.port == 80
        supervisor = await client.supervisor.info()
        assert supervisor.supported is False and not supervisor.update_available
        assert supervisor.timezone == "UTC" and supervisor.country == "US"
        assert (await client.os.info()).version is None
        assert (await client.host.info()).disk_total > 0
        assert not (await client.store.info()).addons
        assert not (await client.store.info()).repositories
        assert not (await client.network.info()).interfaces
        assert not await client.ingress.panels()
        assert not (await client.mounts.info()).mounts
        jobs = (await client.jobs.info()).jobs
        assert len(jobs) <= 20 and all(job.uuid for job in jobs)
        assert not await client.addons.list()
        assert (await client.resolution.info()).unsupported
        assert (await client.supervisor.stats()).memory_usage > 0
        assert (await client.homeassistant.stats()).memory_usage > 0
        try:
            await client.supervisor.update()
        except SupervisorBadRequestError:
            pass
        else:
            raise AssertionError("No-update request unexpectedly succeeded")
        async with session.get("http://supervisor/core/api/config", headers={"Authorization": "Bearer " + os.environ["SUPERVISOR_TOKEN"]}) as response:
            assert response.status == 200, f"Private socket config read failed: {response.status}"
            configuration = await response.json()
            assert configuration["version"] == "2026.9.4" and "hassio" in configuration["components"]
        async with session.get("http://supervisor/operator/info", headers={"Authorization": "Bearer " + os.environ["SUPERVISOR_TOKEN"]}) as response:
            assert response.status == 200
            assert (await response.json())["data"]["installation"] == "kubernetes"
        async with session.get("http://supervisor/info") as response:
            assert response.status == 401
        for path in ("/host/reboot", "/os/update", "/addons/test/install"):
            async with session.post("http://supervisor" + path, json={}, headers={"Authorization": "Bearer " + os.environ["SUPERVISOR_TOKEN"]}) as response:
                assert response.status == 400 and (await response.json())["result"] == "error"
        for path in ("/core/api/hassio/auth", "/core/api/%252e%252e/auth", "/core/api/config?target=/hassio/auth"):
            async with session.get("http://supervisor" + path, headers={"Authorization": "Bearer " + os.environ["SUPERVISOR_TOKEN"]}) as response:
                assert response.status in (400, 404), f"Unexpected proxy access: {path}: {response.status}"
        print("Native aiohasupervisor parsing, real metrics, private socket authorization, and unsupported-operation checks passed.")

asyncio.run(main())
