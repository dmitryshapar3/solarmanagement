#!/usr/bin/env python3
"""Run the production image with real isolated Docker SQL, signed packages and a verified restore drill."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--image", default="solar-management:production-smoke")
parser.add_argument("--skip-build", action="store_true")
parser.add_argument("--keep", action="store_true", help="Keep isolated containers, volumes and private diagnostics on failure")
args = parser.parse_args()
repository = Path(__file__).resolve().parents[1]
docker = os.environ.get("DOCKER", "docker")
docker_platform = "linux/amd64"
prefix = "solar-smoke-" + secrets.token_hex(5)
root = Path(tempfile.mkdtemp(prefix=prefix + "-", dir=os.environ.get("RUNNER_TEMP")))
os.chmod(root, 0o700)
containers, volumes = [], []
network = prefix + "-network"
image = args.image
password = "Sql!" + secrets.token_urlsafe(36) + "Aa1"
runtime_password = "Rt!" + secrets.token_urlsafe(36) + "Aa1"
admin_password = "Admin!" + secrets.token_urlsafe(32) + "Aa1"
bundle_source = root / "bundle"
config_source = root / "integration-bootstrap.json"
failed = False


def command(*arguments, check=True, data=None, environment=None):
    process = subprocess.run(arguments, cwd=repository, input=data, env=environment,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if check and process.returncode:
        diagnostic = (process.stdout + process.stderr).decode(errors="replace")
        for value in [password, runtime_password, admin_password]:
            diagnostic = diagnostic.replace(value, "[redacted]")
        (root / "command-failure.log").write_text(diagnostic)
        os.chmod(root / "command-failure.log", 0o600)
        raise RuntimeError(f"{arguments[0]} {arguments[1] if len(arguments) > 1 else ''} failed; protected diagnostics: {root}")
    return process


def sql(container, statement):
    result = command(docker, "exec", "-e", "SOLAR_SQL_STATEMENT=" + statement, container, "bash", "-c",
        'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -I -r 1 -W -h -1 -Q "$SOLAR_SQL_STATEMENT"')
    return result.stdout.decode().strip()


def start_sql(name):
    containers.append(name)
    command(docker, "run", "-d", "--name", name, "--platform", docker_platform, "--network", network,
        "-e", "ACCEPT_EULA=Y", "-e", "MSSQL_PID=Developer", "-e", "MSSQL_MEMORY_LIMIT_MB=2048",
        "-e", "MSSQL_SA_PASSWORD=" + password, "mcr.microsoft.com/mssql/server:2022-latest")
    deadline = time.monotonic() + 150
    while time.monotonic() < deadline:
        probe = command(docker, "exec", name, "bash", "-c",
            'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -l 2 -Q "SELECT 1"', check=False)
        if probe.returncode == 0:
            return
        time.sleep(2)
    raise RuntimeError("Isolated SQL did not become ready")


def prepare_bundle():
    bundle = root / "bundle"
    bundle.mkdir()
    command("dotnet", "run", "--project", "tools/SolarManagement.IntegrationPackager", "-c", "Release", "--",
        "--generate-key", str(root / "publisher-private.pem"), str(bundle / "publisher-public-key.pem"))
    entries, origins = [], []
    package_pin = None
    for project in sorted((repository / "integrations").glob("SolarManagement.Providers.*")):
        manifest = project / "manifest.json"
        if not manifest.exists():
            identifier = {"SolarManagement.Providers.DeyeCloud": "deye.cloud", "SolarManagement.Providers.ShellyCloud": "shelly.cloud"}[project.name]
            manifest = repository / "integrations" / "manifests" / (identifier + ".json")
        definition = json.loads(manifest.read_text())
        published = root / ("worker-" + definition["providerId"])
        command("dotnet", "publish", str(project), "-c", "Release", "-o", str(published))
        archive = bundle / (definition["providerId"] + "-" + definition["packageVersion"] + ".zip")
        command("dotnet", "run", "--project", "tools/SolarManagement.IntegrationPackager", "-c", "Release", "--",
            str(published), str(manifest), str(root / "publisher-private.pem"), str(archive))
        entries.append({"ArchivePath": "/app/integration-bundle/" + archive.name, "ExpectedSha256": hashlib.sha256(archive.read_bytes()).hexdigest().upper()})
        if package_pin is None:
            with zipfile.ZipFile(archive) as packaged:
                signed_manifest = packaged.read("manifest.json").decode()
            descriptor_start = signed_manifest.index('"descriptor":') + len('"descriptor":')
            _, descriptor_length = json.JSONDecoder().raw_decode(signed_manifest[descriptor_start:])
            package_pin = {"providerId": definition["providerId"], "digest": hashlib.sha256(archive.read_bytes()).hexdigest().upper(),
                "version": definition["packageVersion"],
                "configurationVersion": definition["descriptor"]["configurationVersion"],
                "descriptorDigest": hashlib.sha256(signed_manifest[descriptor_start:descriptor_start + descriptor_length].encode()).hexdigest().upper()}
        origins.extend(definition["allowedOrigins"])
    config = {"Integrations": {"KeyRingPath": "/data/keys/integrations"}, "IntegrationRuntime": {
        "PackageDirectory": "/data/integration-packages", "TrustedPublisherPublicKeyFiles": {"solar-management": "/app/integration-bundle/publisher-public-key.pem"},
        "ApprovedOrigins": sorted(set(origins)), "BootstrapPackages": entries}}
    (root / "integration-bootstrap.json").write_text(json.dumps(config))
    return len(entries), package_pin


def new_volumes(suffix):
    result = [prefix + "-keys-" + suffix, prefix + "-packages-" + suffix]
    for name in result:
        volumes.append(name)
        command(docker, "volume", "create", name)
    return result


def app_arguments(name, sql_name, state_volumes, privileged=False, corrupt=False):
    secret_dir = root / (name + "-secrets")
    secret_dir.mkdir()
    user, credential = ("sa", password) if privileged else ("solar_runtime", runtime_password)
    (secret_dir / "connection.txt").write_text(f"Server={sql_name},1433;Database=DeyeSolar;User Id={user};Password={credential};Encrypt=True;TrustServerCertificate=True")
    (secret_dir / "bootstrap.txt").write_text(admin_password)
    (secret_dir / "runtime.txt").write_text(runtime_password)
    for path in secret_dir.iterdir():
        os.chmod(path, 0o644)
    config = config_source
    if corrupt:
        invalid = json.loads(config.read_text())
        invalid["IntegrationRuntime"]["BootstrapPackages"][0]["ExpectedSha256"] = "A" * 64
        config = root / "invalid-bootstrap.json"
        config.write_text(json.dumps(invalid))
    return [docker, "run", "--name", name, "--platform", docker_platform, "--network", network,
        "--mount", f"type=bind,source={secret_dir},target=/run/secrets,readonly",
        "--mount", f"type=bind,source={bundle_source},target=/app/integration-bundle,readonly",
        "--mount", f"type=bind,source={config},target=/app/integration-bootstrap.json,readonly",
        "-v", state_volumes[0] + ":/data/keys", "-v", state_volumes[1] + ":/data/integration-packages",
        "-e", "ConnectionStrings__DefaultConnectionFile=/run/secrets/connection.txt", "-e", "Auth__RegistrationEnabled=false",
        "-e", "Auth__BootstrapAdminPasswordFile=/run/secrets/bootstrap.txt", "-e", "Operations__RuntimeDatabaseUser=solar_runtime",
        "-e", "Operations__RuntimeDatabasePasswordFile=/run/secrets/runtime.txt", "-e", "Auth__PublicBaseUrl=https://smoke.example.test"]


def migrate(name, sql_name, state_volumes, corrupt=False, expect_failure=False):
    containers.append(name)
    result = command(*app_arguments(name, sql_name, state_volumes, privileged=True, corrupt=corrupt), image, "--migrate-only", check=not expect_failure)
    if expect_failure:
        assert result.returncode != 0, "Invalid bootstrap was unexpectedly accepted"


def request(address, path, body=None, token=None, method=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    data = json.dumps(body).encode() if body is not None else None
    try:
        with urllib.request.urlopen(urllib.request.Request(address + path, data=data, headers=headers, method=method), timeout=8) as response:
            return response.status, json.loads(response.read() or b"null")
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read() or b"null")


def wait_ready(address):
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        try:
            if request(address, "/health/ready")[0] == 200:
                return
        except (OSError, urllib.error.URLError, TimeoutError):
            pass
        time.sleep(1)
    raise RuntimeError("Production readiness did not recover")


def start_app(name, sql_name, state_volumes):
    containers.append(name)
    command(*app_arguments(name, sql_name, state_volumes), "-d", "-p", "127.0.0.1::8080", image)
    address = app_address(name)
    wait_ready(address)
    return address


def app_address(name):
    # Docker may assign a new ephemeral host port after a restart.
    port = json.loads(command(docker, "inspect", "--format", "{{json .NetworkSettings.Ports}}", name).stdout)["8080/tcp"][0]["HostPort"]
    return "http://127.0.0.1:" + port


try:
    if not args.skip_build:
        print("Building production image", flush=True)
        command(docker, "build", "--platform", docker_platform, "-t", image, ".")
    print("Preparing signed provider release", flush=True)
    provider_count, package_pin = prepare_bundle()
    command(docker, "network", "create", network)
    source_sql = prefix + "-sql"
    print("Starting isolated SQL and verifying preflight before schema changes", flush=True)
    start_sql(source_sql)
    state_volumes = new_volumes("source")
    migrate(prefix + "-invalid-migration", source_sql, state_volumes, corrupt=True, expect_failure=True)
    assert sql(source_sql, "SET NOCOUNT ON; SELECT CASE WHEN DB_ID('DeyeSolar') IS NULL THEN 0 ELSE 1 END;") == "0"
    migrate(prefix + "-migration", source_sql, state_volumes)
    assert sql(source_sql, "USE DeyeSolar; SET NOCOUNT ON; SELECT COUNT(*) FROM Installations WHERE Id='legacy';") == "0"
    assert sql(source_sql, "USE DeyeSolar; SET NOCOUNT ON; SELECT COUNT(*) FROM sys.tables WHERE name='IntegrationDeviceAliases';") == "0"
    assert sql(source_sql, "USE DeyeSolar; SET NOCOUNT ON; SELECT COUNT(*) FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id WHERE c.name='InstallationId';") == "0"
    installation_id = sql(source_sql, "USE DeyeSolar; SET NOCOUNT ON; SELECT m.InstallationId FROM InstallationMemberships m JOIN AspNetUsers u ON u.Id=m.UserId WHERE u.NormalizedUserName='ADMIN';")
    assert len(installation_id) == 32, "Bootstrap administrator needs an independently provisioned installation"
    sql(source_sql, f"""USE DeyeSolar;
        INSERT IntegrationInstances (Id,InstallationId,ProviderId,Name,PackageVersion,PackageDigest,DescriptorDigest,
            ConfigurationVersion,Revision,Generation,State,CreatedAt,UpdatedAt)
        VALUES (NEWID(),'{installation_id}','{package_pin['providerId']}','Pinned package fixture','{package_pin['version']}','{package_pin['digest']}',
            '{package_pin['descriptorDigest']}',{package_pin['configurationVersion']},1,1,'draft',SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET());""")
    billing_after_provisioning = sql(source_sql, "USE DeyeSolar; SET NOCOUNT ON; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(nvarchar(max),(SELECT * FROM BillingAccounts ORDER BY UserId FOR XML RAW))),2);")
    print("Verifying repeat migration preserves the package pin and account trial", flush=True)
    migrate(prefix + "-repeat-migration", source_sql, state_volumes)
    assert billing_after_provisioning == sql(source_sql, "USE DeyeSolar; SET NOCOUNT ON; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(nvarchar(max),(SELECT * FROM BillingAccounts ORDER BY UserId FOR XML RAW))),2);")
    assert sql(source_sql, f"USE DeyeSolar; SET NOCOUNT ON; SELECT COUNT(*) FROM IntegrationInstances WHERE Name='Pinned package fixture' AND PackageVersion='{package_pin['version']}';") == "1"
    # Runtime cannot use the privileged account, and validate mode never repairs schema implicitly.
    privileged = prefix + "-privileged-runtime"
    containers.append(privileged)
    rejected = command(*app_arguments(privileged, source_sql, state_volumes, privileged=True), image, check=False)
    assert rejected.returncode != 0, "Administrative runtime login was accepted"
    app_name = prefix + "-app"
    address = start_app(app_name, source_sql, state_volumes)
    assert command(docker, "exec", app_name, "id", "-u").stdout.strip() != b"0"
    assert request(address, "/health/live")[0] == 200
    removed_routes = [("GET", "/api/unknown-endpoint"), ("POST", "/api/devices/state"),
        ("PUT", "/api/settings/deye"), ("PUT", "/api/settings/shelly"), ("POST", "/api/settings/test/deye")]
    for method, path in removed_routes:
        status, error = request(address, path, method=method)
        assert status == 404 and error["code"] == "endpoint_not_found", "Unknown API route reached account or page fallback"
    status, session = request(address, "/api/auth/login", {"username": "admin", "password": admin_password})
    assert status == 200, "Production administrator could not sign in"
    token = session["token"]
    for method, path in removed_routes:
        status, error = request(address, path, token=token, method=method)
        assert status == 404 and error["code"] == "endpoint_not_found", "Authenticated unknown API route resolved private services"
    assert request(address, "/api/billing/access", token=token)[1]["status"] == "trial"
    assert len(request(address, "/api/v2/integration-providers", token=token)[1]["providers"]) == provider_count
    assert request(address, "/api/v2/integrations", token=token)[0] == 200
    print("Verifying production settings reader/writer ports and persisted readback", flush=True)
    status, settings = request(address, "/api/settings", token=token)
    assert status == 200, "Production settings reader failed"
    assert request(address, "/api/settings/site", token=token)[0] == 200, "Production site settings reader failed"
    # Change the value rather than rewriting the default, so restart/restore verifies persisted SQL state.
    settings_timezone = "Europe/Warsaw" if settings["display"]["timeZoneId"] == "UTC" else "UTC"
    assert request(address, "/api/settings/display", {"timeZoneId": settings_timezone}, token, method="PUT")[0] == 204
    assert request(address, "/api/settings", token=token)[1]["display"]["timeZoneId"] == settings_timezone
    sql(source_sql, """USE DeyeSolar;
        IF NOT EXISTS (SELECT 1 FROM AspNetRoles WHERE NormalizedName='PLATFORMOPERATOR')
            INSERT AspNetRoles(Id,Name,NormalizedName,ConcurrencyStamp) VALUES ('smoke-operator','PlatformOperator','PLATFORMOPERATOR','smoke');
        INSERT AspNetUserRoles(UserId,RoleId)
            SELECT u.Id,r.Id FROM AspNetUsers u CROSS JOIN AspNetRoles r
            WHERE u.NormalizedUserName='ADMIN' AND r.NormalizedName='PLATFORMOPERATOR'
            AND NOT EXISTS (SELECT 1 FROM AspNetUserRoles existing WHERE existing.UserId=u.Id AND existing.RoleId=r.Id);""")
    assert request(address, "/api/v2/integration-packages/approved-origins", {"origin": "https://api.open-meteo.com"}, token)[0] == 200
    billing_before = sql(source_sql, "USE DeyeSolar; SET NOCOUNT ON; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(nvarchar(max),(SELECT * FROM BillingAccounts ORDER BY UserId FOR XML RAW))),2);")
    print("Verifying restart, durable sessions and readiness during SQL outage", flush=True)
    command(docker, "restart", app_name)
    address = app_address(app_name)
    wait_ready(address)
    assert request(address, "/api/billing/access", token=token)[0] == 200, "Bearer session did not survive runtime restart"
    assert request(address, "/api/settings", token=token)[1]["display"]["timeZoneId"] == settings_timezone
    command(docker, "stop", source_sql)
    started = time.monotonic()
    assert request(address, "/health/ready")[0] == 503
    assert time.monotonic() - started < 7, "Readiness SQL outage exceeded its deadline"
    assert request(address, "/health/live")[0] == 200
    command(docker, "start", source_sql)
    wait_ready(address)
    passphrase = root / "backup-passphrase.txt"
    passphrase.write_text(secrets.token_urlsafe(48))
    os.chmod(passphrase, 0o600)
    environment = dict(os.environ, DOCKER=docker)
    print("Taking encrypted coordinated backup and restoring a fresh SQL/volume target", flush=True)
    command(sys.executable, "scripts/production-state.py", "backup", "--app", app_name, "--sql", source_sql, "--database", "DeyeSolar",
        "--volume", state_volumes[0], "--volume", state_volumes[1], "--output", str(root / "backup"), "--bundle", str(root / "bundle"),
        "--passphrase-file", str(passphrase), "--bootstrap-config", str(config_source), environment=environment)
    restore_sql = prefix + "-restored-sql"
    start_sql(restore_sql)
    restored_volumes = new_volumes("restored")
    command(sys.executable, "scripts/production-state.py", "restore", "--sql", restore_sql, "--volume", restored_volumes[0], "--volume", restored_volumes[1],
        "--backup", str(root / "backup"), "--image", image, "--passphrase-file", str(passphrase),
        "--bundle-output", str(root / "restored-release" / "bundle"), "--bootstrap-output", str(root / "restored-release" / "integration-bootstrap.json"), environment=environment)
    bundle_source = root / "restored-release" / "bundle"
    config_source = root / "restored-release" / "integration-bootstrap.json"
    migrate(prefix + "-restored-migration", restore_sql, restored_volumes)
    assert billing_before == sql(restore_sql, "USE DeyeSolar; SET NOCOUNT ON; SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(nvarchar(max),(SELECT * FROM BillingAccounts ORDER BY UserId FOR XML RAW))),2);")
    restored_address = start_app(prefix + "-restored-app", restore_sql, restored_volumes)
    assert request(restored_address, "/api/billing/access", token=token)[0] == 200
    assert request(restored_address, "/api/settings", token=token)[1]["display"]["timeZoneId"] == settings_timezone
    assert request(restored_address, "/api/settings/site", token=token)[0] == 200
    assert len(request(restored_address, "/api/v2/integration-providers", token=token)[1]["providers"]) == provider_count
    assert sql(restore_sql, f"USE DeyeSolar; SET NOCOUNT ON; SELECT COUNT(*) FROM IntegrationInstances WHERE Name='Pinned package fixture' AND PackageVersion='{package_pin['version']}';") == "1"
    print("PASS: production image, fresh independent installation, unknown API JSON404, signed package pin, idempotent migration, restricted runtime, trial, settings read/write persistence, durable sessions, restart, bounded outage and encrypted restore", flush=True)
except Exception:
    failed = True
    for name in containers:
        logs = command(docker, "logs", name, check=False).stdout.decode(errors="replace")
        for secret in [password, runtime_password, admin_password]:
            logs = logs.replace(secret, "[redacted]")
        path = root / (name + ".log")
        path.write_text(logs)
        os.chmod(path, 0o600)
    print(f"Private smoke diagnostics: {root}", file=sys.stderr, flush=True)
    raise
finally:
    if not args.keep:
        for name in reversed(containers):
            command(docker, "rm", "-f", name, check=False)
        for name in reversed(volumes):
            command(docker, "volume", "rm", name, check=False)
        command(docker, "network", "rm", network, check=False)
        # Fixture keys/passphrases never remain in the repository or Docker image.
        if failed:
            # Retain only redacted diagnostics; destroy credentials, publisher keys and backup passphrases.
            for path in root.iterdir():
                if path.is_dir(): shutil.rmtree(path, ignore_errors=True)
                elif path.suffix != ".log": path.unlink(missing_ok=True)
        else:
            shutil.rmtree(root, ignore_errors=True)
