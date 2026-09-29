"""Exercise the real local HTTP app in demo mode; never calls Oxylabs.

Run after a Release build: python3 scripts/smoke_test.py [--dotnet path/to/dotnet]
No Python packages required. Uses an isolated temporary job directory.
"""
import argparse
import csv
import io
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

parser = argparse.ArgumentParser()
parser.add_argument("--dotnet", default="dotnet")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
app = root / "src/AmazonProductExplorer"
dll = app / "bin/Release/net10.0/AmazonProductExplorer.dll"
assert dll.exists(), "Build the application in Release first."
with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    port = listener.getsockname()[1]
base = f"http://localhost:{port}"
terminal = {"completed", "partial", "failed", "cancelled", "interrupted"}


def request(path, payload=None, headers=None):
    body = None if payload is None else json.dumps(payload).encode()
    req = Request(base + path, data=body, headers={"Content-Type": "application/json", **(headers or {})})
    try:
        response = urlopen(req, timeout=5)
    except HTTPError as error:
        response = error
    with response:
        raw = response.read().decode()
        data = json.loads(raw) if "application/json" in response.headers.get("Content-Type", "") else raw
        return response.status, data


def wait_job(job_id):
    for _ in range(150):
        status, job = request(f"/api/jobs/{job_id}")
        assert status == 200
        if job["status"] in terminal:
            return job
        time.sleep(0.15)
    raise AssertionError("Demo job did not finish within the test deadline")


with tempfile.TemporaryDirectory(prefix="amazon-explorer-check-") as directory:
    env = dict(os.environ, ASPNETCORE_ENVIRONMENT="Production", Oxylabs__Username="", Oxylabs__Password="",
               Storage__Directory=directory, DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    process = None
    with open(Path(directory) / "server.log", "w+") as log:
        def start():
            global process
            process = subprocess.Popen([args.dotnet, str(dll), "--urls", base], cwd=app, env=env,
                                       stdout=log, stderr=subprocess.STDOUT)
            for _ in range(100):
                if process.poll() is not None:
                    log.seek(0)
                    raise AssertionError("Server exited: " + log.read())
                try:
                    if request("/api/config")[0] == 200:
                        return
                except (URLError, OSError):
                    pass
                time.sleep(0.1)
            raise AssertionError("Server did not start")

        def stop():
            if process and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()

        try:
            start()
            status, config = request("/api/config")
            assert not config["liveConfigured"], "Smoke checks must never use real credentials"
            assert request("/")[0] == 200
            valid = {"query": "wireless headphones", "limit": 10, "zipCode": "10001", "mode": "demo"}
            for change in [{"query": ""}, {"limit": 61}, {"zipCode": "bad"}, {"mode": "bad"}]:
                assert request("/api/jobs", {**valid, **change})[0] == 400
            assert request("/api/jobs", {**valid, "mode": "live"})[0] in {400, 503}
            assert request("/api/jobs", valid, {"Origin": "https://untrusted.example"})[0] == 403
            print("PASS input validation, missing live credentials and cross-origin protection")

            status, created = request("/api/jobs", valid)
            assert status == 202, created
            job_id = created["id"]
            job = wait_job(job_id)
            assert job["status"] == "completed", job
            assert len(job["products"]) == 10 and job["mode"] == "demo"
            assert len({p["asin"] for p in job["products"]}) == 10
            assert all(p["detailStatus"] == "complete" for p in job["products"])
            assert all(not p["url"] for p in job["products"]), "Demo listings must not link to real products"
            status, export = request(f"/api/jobs/{job_id}/export")
            assert status == 200 and len(list(csv.reader(io.StringIO(export)))) == 11
            print("PASS demo search, product details, unique ASINs and CSV export")

            _, created = request("/api/jobs", {**valid, "limit": 60})
            cancel_id = created["id"]
            assert request(f"/api/jobs/{cancel_id}/cancel", {})[0] in {200, 202}
            assert wait_job(cancel_id)["status"] == "cancelled"
            print("PASS job cancellation")

            stop()
            start()
            assert request(f"/api/jobs/{job_id}")[1]["status"] == "completed"
            assert request(f"/api/jobs/{cancel_id}")[1]["status"] == "cancelled"
            print("PASS persisted results survive application restart")
        finally:
            stop()
print("All HTTP smoke checks passed; no paid API requests were sent.")
