#!/usr/bin/env bash
# Single-instance release: preprovisioned signed bundle, verified backup, migration then exact-image runtime.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
NAMESPACE=deye-solar
IMAGE_NAME="${IMAGE_NAME:-ghcr.io/dmitryshapar3/deye-solar}"
IMAGE_TAG="${IMAGE_TAG:-$(git -C "$PROJECT_DIR" rev-parse --short=12 HEAD)}"
KUBE_CONTEXT="${KUBE_CONTEXT:?Set the intended kubectl context}"
[[ "$IMAGE_TAG" =~ ^[a-zA-Z0-9_.-]+$ ]] || { echo 'Invalid image tag' >&2; exit 1; }
kctl() { kubectl --context "$KUBE_CONTEXT" "$@"; }
TASK_TEMP_DIRECTORY="$(mktemp -d)"
trap 'rm -rf "$TASK_TEMP_DIRECTORY"' EXIT
kctl get secret ghcr-secret -n "$NAMESPACE" >/dev/null
kctl get secret deye-solar-database -n "$NAMESPACE" >/dev/null
kctl get pvc deye-solar-integration-bundle -n "$NAMESPACE" >/dev/null
kctl apply -f "$SCRIPT_DIR/integration-storage.yaml"
docker buildx build --platform linux/amd64 -t "$IMAGE_NAME:$IMAGE_TAG" --push --metadata-file "$TASK_TEMP_DIRECTORY/image.json" "$PROJECT_DIR"
RELEASE_IMAGE="$IMAGE_NAME@$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["containerimage.digest"])' "$TASK_TEMP_DIRECTORY/image.json")"
python3 - "$SCRIPT_DIR" "$TASK_TEMP_DIRECTORY" "$RELEASE_IMAGE" <<'PY'
from pathlib import Path
import sys
for name in ['deployment.yaml', 'migration-job.yaml']:
    source = (Path(sys.argv[1]) / name).read_text()
    (Path(sys.argv[2]) / name).write_text(source.replace('SOLAR_RELEASE_IMAGE', sys.argv[3]))
PY
# Perform the documented coordinated backup before this cutover. Never overlap automation owners.
if kctl get deployment/deye-solar -n "$NAMESPACE" >/dev/null 2>&1; then
  kctl scale deployment/deye-solar --replicas=0 -n "$NAMESPACE"
  kctl wait --for=delete pod -l app=deye-solar -n "$NAMESPACE" --timeout=180s
fi
MIGRATION_JOB="$(kctl create -f "$TASK_TEMP_DIRECTORY/migration-job.yaml" -o jsonpath='{.metadata.name}')"
kctl wait --for=condition=complete "job/$MIGRATION_JOB" -n "$NAMESPACE" --timeout=600s
kctl apply -f "$TASK_TEMP_DIRECTORY/deployment.yaml"
kctl apply -f "$SCRIPT_DIR/service.yaml"
kctl rollout status deployment/deye-solar -n "$NAMESPACE" --timeout=360s
printf 'Deployed exact image: %s\n' "$RELEASE_IMAGE"
