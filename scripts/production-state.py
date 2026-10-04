#!/usr/bin/env python3
"""Quiesced SQL + volume backups with authenticated encryption, and restore to a fresh target."""
import argparse
import hashlib
import hmac
import io
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import tarfile
import tempfile

DOCKER = os.environ.get("DOCKER", "docker")

def run(*args, data=None):
    result = subprocess.run(args, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        # Docker/server diagnostics can contain deployment data; preserve them only in caller-owned logs.
        raise RuntimeError(f"Command failed: {args[0]} {args[1] if len(args) > 1 else ''} (exit {result.returncode})")
    return result.stdout

def sql(container, statement):
    return run(DOCKER, "exec", "-e", "SOLAR_SQL_STATEMENT=" + statement, container, "bash", "-c",
        'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -I -r 1 -W -h -1 -s "|" -Q "$SOLAR_SQL_STATEMENT"').decode()

def validate_database(name):
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{0,63}", name):
        raise ValueError("Database must be a simple identifier")

def fingerprint(container, database):
    return sql(container, f"""USE [{database}]; SET NOCOUNT ON;
        SELECT COUNT(*) FROM [__EFMigrationsHistory];
        SELECT COUNT(*) FROM [BillingAccounts];
        SELECT COUNT(*) FROM [AppleSubscriptions];
        SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(nvarchar(max),
            (SELECT UserId, AppAccountToken, TrialStartedAt FROM BillingAccounts ORDER BY UserId FOR XML RAW))), 2);
        SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(nvarchar(max),
            (SELECT * FROM AppleSubscriptions ORDER BY OriginalTransactionId FOR XML RAW))), 2);
        """).strip()

def archive_volume(image, volume):
    return run(DOCKER, "run", "--rm", "--user", "0", "--entrypoint", "tar", "-v", volume + ":/volume:ro", image,
               "-C", "/volume", "-cf", "-", ".")

def key(passphrase_file, salt):
    secret = Path(passphrase_file).read_bytes().rstrip(b"\r\n")
    if len(secret) < 24:
        raise ValueError("Backup passphrase file must contain at least 24 bytes")
    return hashlib.pbkdf2_hmac("sha256", secret, bytes.fromhex(salt), 200000, 32)

def backup(args):
    validate_database(args.database)
    output = Path(args.output).resolve()
    output.mkdir(mode=0o700, parents=True, exist_ok=False)
    os.chmod(output, 0o700)
    image = run(DOCKER, "inspect", "--format", "{{.Image}}", args.app).decode().strip()
    was_running = run(DOCKER, "inspect", "--format", "{{.State.Running}}", args.app).decode().strip() == "true"
    remote = "/var/opt/mssql/backup/solar-" + secrets.token_hex(10) + ".bak"
    if was_running:
        run(DOCKER, "stop", "--time", "60", args.app)
    try:
        # Reject a second local writer instead of taking an inconsistent snapshot.
        for volume in args.volume:
            if run(DOCKER, "ps", "--filter", "volume=" + volume, "--format", "{{.Names}}").strip():
                raise ValueError("All containers using application volumes must be stopped")
        with tempfile.TemporaryDirectory(prefix="solar-state-") as temporary:
            root = Path(temporary).resolve()
            run(DOCKER, "exec", args.sql, "mkdir", "-p", "/var/opt/mssql/backup")
            sql(args.sql, f"BACKUP DATABASE [{args.database}] TO DISK=N'{remote}' WITH COPY_ONLY, CHECKSUM, INIT; RESTORE VERIFYONLY FROM DISK=N'{remote}' WITH CHECKSUM;")
            run(DOCKER, "cp", args.sql + ":" + remote, str(root / "database.bak"))
            state = fingerprint(args.sql, args.database)
            for index, volume in enumerate(args.volume):
                (root / f"volume-{index}.tar").write_bytes(archive_volume(image, volume))
            if args.bundle:
                shutil.copytree(args.bundle, root / "release-bundle")
            if args.bootstrap_config:
                shutil.copyfile(args.bootstrap_config, root / "integration-bootstrap.json")
            manifest = {"version": 1, "database": args.database, "image": image, "volume_count": len(args.volume), "billing_fingerprint": state}
            manifest["files"] = {str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest()
                                 for path in root.rglob("*") if path.is_file()}
            (root / "manifest.json").write_text(json.dumps(manifest))
            plain = root / "state.tar"
            with tarfile.open(plain, "w") as archive:
                for path in root.iterdir():
                    if path != plain:
                        archive.add(path, arcname=path.name)
            encrypted = output / "state.tar.enc"
            run("openssl", "enc", "-aes-256-cbc", "-salt", "-pbkdf2", "-iter", "200000", "-in", str(plain),
                "-out", str(encrypted), "-pass", "file:" + str(Path(args.passphrase_file).resolve()))
            os.chmod(encrypted, 0o600)
            salt = secrets.token_hex(32)
            authentication = hmac.new(key(args.passphrase_file, salt), encrypted.read_bytes(), hashlib.sha256).hexdigest()
            (output / "authentication.json").write_text(json.dumps({"salt": salt, "hmac_sha256": authentication}))
            os.chmod(output / "authentication.json", 0o600)
            print("Verified encrypted backup completed; SQL and application volumes share a quiesced snapshot.")
    finally:
        try:
            run(DOCKER, "exec", args.sql, "rm", "-f", remote)
        finally:
            if was_running:
                run(DOCKER, "start", args.app)

def restore(args):
    output = Path(args.backup).resolve()
    encrypted = output / "state.tar.enc"
    authentication = json.loads((output / "authentication.json").read_text())
    actual = hmac.new(key(args.passphrase_file, authentication["salt"]), encrypted.read_bytes(), hashlib.sha256).hexdigest()
    if not hmac.compare_digest(actual, authentication["hmac_sha256"]):
        raise ValueError("Backup authentication failed; no target state was changed")
    with tempfile.TemporaryDirectory(prefix="solar-restore-") as temporary:
        root = Path(temporary).resolve()
        plain = root / "state.tar"
        run("openssl", "enc", "-d", "-aes-256-cbc", "-pbkdf2", "-iter", "200000", "-in", str(encrypted),
            "-out", str(plain), "-pass", "file:" + str(Path(args.passphrase_file).resolve()))
        with tarfile.open(plain) as archive:
            for entry in archive.getmembers():
                target = (root / entry.name).resolve()
                if root not in target.parents or not (entry.isfile() or entry.isdir()):
                    raise ValueError("Unsafe authenticated backup archive")
            archive.extractall(root)
        manifest = json.loads((root / "manifest.json").read_text())
        if manifest["version"] != 1 or manifest["volume_count"] != len(args.volume):
            raise ValueError("Backup format or volume mapping does not match")
        for name, digest in manifest["files"].items():
            if hashlib.sha256((root / name).read_bytes()).hexdigest() != digest:
                raise ValueError("Backup contents failed checksum verification")
        for index in range(len(args.volume)):
            with tarfile.open(root / f"volume-{index}.tar") as volume_archive:
                for entry in volume_archive.getmembers():
                    if Path(entry.name).is_absolute() or ".." in Path(entry.name).parts or not (entry.isfile() or entry.isdir()):
                        raise ValueError("Unsafe authenticated volume archive")
        database = manifest["database"]
        validate_database(database)
        if sql(args.sql, f"SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'{database}') IS NULL THEN 0 ELSE 1 END;").strip() != "0":
            raise ValueError("Restore requires a fresh database target; existing data is never replaced")
        for volume in args.volume:
            if run(DOCKER, "ps", "--filter", "volume=" + volume, "--format", "{{.Names}}").strip():
                raise ValueError("Restore volumes must have no active application writers")
            run(DOCKER, "volume", "create", volume)
            content = run(DOCKER, "run", "--rm", "--user", "0", "--entrypoint", "find", "-v", volume + ":/restore", args.image,
                          "/restore", "-mindepth", "1", "-print", "-quit")
            if content.strip():
                raise ValueError("Restore requires fresh empty volumes")
        remote = "/var/opt/mssql/backup/solar-restore-" + secrets.token_hex(10) + ".bak"
        run(DOCKER, "exec", args.sql, "mkdir", "-p", "/var/opt/mssql/backup")
        run(DOCKER, "cp", str(root / "database.bak"), args.sql + ":" + remote)
        run(DOCKER, "exec", "--user", "0", args.sql, "chown", "mssql", remote)
        try:
            listing = sql(args.sql, f"RESTORE FILELISTONLY FROM DISK=N'{remote}';")
            moves = []
            for row in listing.splitlines():
                fields = row.split("|")
                if len(fields) > 2 and fields[2].strip() in ("D", "L"):
                    logical, kind = fields[0].strip().replace("'", "''"), fields[2].strip()
                    extension = "mdf" if kind == "D" else "ldf"
                    moves.append(f"MOVE N'{logical}' TO N'/var/opt/mssql/data/{database}_restored_{len(moves)}.{extension}'")
            if len(moves) != 2:
                raise ValueError("Restore tool currently supports one data file and one log file")
            sql(args.sql, f"RESTORE VERIFYONLY FROM DISK=N'{remote}' WITH CHECKSUM; RESTORE DATABASE [{database}] FROM DISK=N'{remote}' WITH " + ", ".join(moves) + ", CHECKSUM;")
            for index, volume in enumerate(args.volume):
                run(DOCKER, "run", "--rm", "-i", "--user", "0", "--entrypoint", "tar", "-v", volume + ":/restore", args.image,
                    "-C", "/restore", "-xf", "-", data=(root / f"volume-{index}.tar").read_bytes())
                # Earlier images ran as root. Transfer application state ownership to the target non-root UID.
                run(DOCKER, "run", "--rm", "--user", "0", "--entrypoint", "chown", "-v", volume + ":/restore", args.image,
                    "-R", f"{args.application_uid}:{args.application_uid}", "/restore")
            if fingerprint(args.sql, database) != manifest["billing_fingerprint"]:
                raise ValueError("Restored billing trial/token/subscription state differs")
            if args.bundle_output and (root / "release-bundle").exists():
                shutil.copytree(root / "release-bundle", args.bundle_output)
            if args.bootstrap_output and (root / "integration-bootstrap.json").exists():
                destination = Path(args.bootstrap_output)
                destination.parent.mkdir(parents=True, exist_ok=True)
                if destination.exists():
                    raise ValueError("Bootstrap output must be a new file")
                shutil.copyfile(root / "integration-bootstrap.json", destination)
            print("Restore verified: schema, trial dates, account tokens, subscription state and durable volumes preserved.")
        finally:
            run(DOCKER, "exec", args.sql, "rm", "-f", remote)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ["backup", "restore"]:
        command = commands.add_parser(name)
        command.add_argument("--sql", required=True)
        command.add_argument("--volume", action="append", required=True, help="Ordered protection-key and package volumes")
        command.add_argument("--passphrase-file", required=True)
    command = commands.choices["backup"]
    command.add_argument("--app", required=True)
    command.add_argument("--database", required=True)
    command.add_argument("--output", required=True)
    command.add_argument("--bundle")
    command.add_argument("--bootstrap-config")
    command = commands.choices["restore"]
    command.add_argument("--backup", required=True)
    command.add_argument("--image", required=True)
    command.add_argument("--bundle-output")
    command.add_argument("--bootstrap-output")
    command.add_argument("--application-uid", type=int, default=1654, help="Non-root UID/GID for restored application volumes")
    arguments = parser.parse_args()
    if arguments.command == "restore" and not 0 < arguments.application_uid < 2147483647:
        parser.error("Application UID must be a positive non-root identifier")
    try:
        backup(arguments) if arguments.command == "backup" else restore(arguments)
    except (RuntimeError, ValueError) as error:
        parser.exit(1, str(error) + "\n")
