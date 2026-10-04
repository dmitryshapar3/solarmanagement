#!/usr/bin/env python3
"""Generate local deployment credentials without printing their values. Never overwrite existing secrets."""
import argparse
from pathlib import Path
import os
import secrets

parser = argparse.ArgumentParser()
parser.add_argument("directory", type=Path, help="New directory outside the repository")
args = parser.parse_args()
root = args.directory.resolve()
repository = Path(__file__).resolve().parents[1]
if root == repository or repository in root.parents:
    parser.error("Credentials must stay outside the repository")
root.mkdir(mode=0o700, parents=True, exist_ok=False)
admin = "Sa!" + secrets.token_urlsafe(36) + "Aa1"
runtime = "Rt!" + secrets.token_urlsafe(36) + "Aa1"
bootstrap = "Admin!" + secrets.token_urlsafe(32) + "Aa1"
files = {
    "migration-connection.txt": f"Server=sqlserver,1433;Database=DeyeSolar;User Id=sa;Password={admin};Encrypt=True;TrustServerCertificate=True",
    "runtime-connection.txt": f"Server=sqlserver,1433;Database=DeyeSolar;User Id=solar_runtime;Password={runtime};Encrypt=True;TrustServerCertificate=True",
    "runtime-password.txt": runtime,
    "bootstrap-password.txt": bootstrap,
    "compose.env": f"MSSQL_SA_PASSWORD={admin}\nSOLAR_SECRETS_DIRECTORY={root}\n",
}
for name, content in files.items():
    path = root / name
    # Compose file-backed secrets are bind mounts: UID 1654 needs read access to its mounted files.
    # The containing 0700 directory prevents local account traversal; compose.env stays 0600.
    mode = 0o600 if name == "compose.env" else 0o644
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, mode)
    with os.fdopen(fd, "w") as output:
        output.write(content)
print(f"Created protected deployment directory: {root}")
print(f"Use docker compose --env-file '{root / 'compose.env'}' up --build -d")
print("Read bootstrap-password.txt privately to sign in as admin.")
