#!/usr/bin/env python3
"""Fail release validation when direct or transitive NuGet dependencies have known vulnerabilities."""
import json
import subprocess
import sys

result = subprocess.run(["dotnet", "list", "DeyeSolar.sln", "package", "--vulnerable", "--include-transitive", "--format", "json"],
    text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
if result.returncode:
    sys.stderr.write(result.stderr)
    raise SystemExit(result.returncode)
report = json.loads(result.stdout)
vulnerable = []
for project in report.get("projects", []):
    for framework in project.get("frameworks", []):
        for kind in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(kind, []):
                for advisory in package.get("vulnerabilities", []):
                    vulnerable.append((package["id"], package["resolvedVersion"], advisory["severity"], advisory["advisoryurl"]))
for package, version, severity, advisory in sorted(set(vulnerable)):
    print(f"{package} {version}: {severity} — {advisory}", file=sys.stderr)
if vulnerable:
    raise SystemExit("Release blocked by known NuGet vulnerabilities.")
if report.get("problems"):
    raise SystemExit("NuGet vulnerability audit did not complete successfully.")
print("NuGet audit passed: no known direct or transitive vulnerabilities.")
