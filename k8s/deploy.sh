#!/bin/bash

# DeyeSolar Kubernetes Deployment Script
# Usage: ./deploy.sh [--help]

set -e

# ============ CONFIGURATION ============
REMOTE_HOST="157.250.198.4"
REMOTE_USER="root"
REMOTE_PASSWORD="${KUBE_PASSWORD:-8zCV5cA\$}"

NAMESPACE="deye-solar"
IMAGE_NAME="ghcr.io/dmitryshapar3/deye-solar"
IMAGE_TAG="${IMAGE_TAG:-latest}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
# =======================================

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

print_status() { echo -e "${GREEN}[OK] $1${NC}"; }
print_info()   { echo -e "${BLUE}[..] $1${NC}"; }
print_warn()   { echo -e "${YELLOW}[!!] $1${NC}"; }
print_error()  { echo -e "${RED}[ERR] $1${NC}"; }

# Help
if [[ "$1" == "--help" ]] || [[ "$1" == "-h" ]]; then
    echo "DeyeSolar Kubernetes Deployment"
    echo ""
    echo "Usage: $0 [OPTIONS]"
    echo ""
    echo "Options:"
    echo "  --help, -h       Show this help"
    echo ""
    echo "Environment Variables:"
    echo "  KUBE_PASSWORD    SSH password (default: hardcoded)"
    echo "  IMAGE_TAG        Docker image tag (default: latest)"
    echo ""
    echo "What it does:"
    echo "  1. Builds Docker image locally"
    echo "  2. Pushes to ghcr.io/dmitryshapar3/deye-solar"
    echo "  3. Applies K8s manifests on remote server"
    echo "  4. Restarts deployment to pull new image"
    exit 0
fi

# Check prerequisites
check_prerequisites() {
    local missing=false

    if ! command -v docker &> /dev/null; then
        print_error "Docker not found"
        missing=true
    fi

    if ! command -v sshpass &> /dev/null; then
        print_error "sshpass not found. Install: brew install hudochenkov/sshpass/sshpass"
        missing=true
    fi

    if $missing; then exit 1; fi
}

# Execute kubectl on remote server
remote_kubectl() {
    sshpass -p "$REMOTE_PASSWORD" ssh -o StrictHostKeyChecking=no "$REMOTE_USER@$REMOTE_HOST" "kubectl $*"
}

# Execute command on remote server
remote_exec() {
    sshpass -p "$REMOTE_PASSWORD" ssh -o StrictHostKeyChecking=no "$REMOTE_USER@$REMOTE_HOST" "$*"
}

# Apply yaml file on remote server
remote_apply() {
    cat "$1" | sshpass -p "$REMOTE_PASSWORD" ssh -o StrictHostKeyChecking=no "$REMOTE_USER@$REMOTE_HOST" "kubectl apply -f -"
}

# Test SSH connection
test_connection() {
    print_info "Testing SSH connection to $REMOTE_HOST..."
    if sshpass -p "$REMOTE_PASSWORD" ssh -o StrictHostKeyChecking=no -o ConnectTimeout=10 "$REMOTE_USER@$REMOTE_HOST" "echo ok" > /dev/null 2>&1; then
        print_status "SSH connection successful"
    else
        print_error "Cannot connect to $REMOTE_HOST"
        exit 1
    fi
}

# Build and push Docker image
build_and_push() {
    print_info "Building Docker image for linux/amd64: $IMAGE_NAME:$IMAGE_TAG"
    docker buildx build --platform linux/amd64 -t "$IMAGE_NAME:$IMAGE_TAG" --push "$PROJECT_DIR"
    print_status "Image built and pushed: $IMAGE_NAME:$IMAGE_TAG"
}

# Deploy K8s manifests
deploy_manifests() {
    print_info "Applying K8s manifests..."

    remote_apply "$SCRIPT_DIR/namespace.yaml"
    print_status "Namespace applied"

    # Copy GHCR pull secret from production namespace
    remote_exec "kubectl get secret ghcr-secret -n production -o json | jq 'del(.metadata.namespace,.metadata.resourceVersion,.metadata.uid,.metadata.creationTimestamp,.metadata.annotations)' | jq '.metadata.namespace=\"$NAMESPACE\"' | kubectl apply -f - 2>/dev/null || true"
    print_status "Image pull secret ensured"

    remote_apply "$SCRIPT_DIR/deployment.yaml"
    print_status "Deployment applied"

    remote_apply "$SCRIPT_DIR/service.yaml"
    print_status "Service applied"
}

# Seed credentials
seed_credentials() {
    print_info "Seeding credentials (waiting for app to create tables)..."
    sleep 15

    if [ -f "$SCRIPT_DIR/../scripts/restore-creds.sh" ]; then
        bash "$SCRIPT_DIR/../scripts/restore-creds.sh" --k8s
    else
        print_warn "restore-creds.sh not found, skipping credential seed"
    fi
}

# Restart deployment to pick up new image
restart_deployment() {
    print_info "Restarting deployment to pull new image..."
    remote_kubectl rollout restart deployment/deye-solar -n "$NAMESPACE"
    print_status "Restart triggered"

    print_info "Waiting for rollout..."
    remote_kubectl rollout status deployment/deye-solar -n "$NAMESPACE" --timeout=120s
    print_status "Deployment ready"
}

# Show status
show_status() {
    echo ""
    echo "========================================="
    print_status "DeyeSolar deployed successfully!"
    echo "========================================="
    echo ""
    echo "Access: http://$REMOTE_HOST:30880"
    echo ""
    print_info "Pod status:"
    remote_kubectl get pods -n "$NAMESPACE" -o wide
    echo ""
    print_info "Service:"
    remote_kubectl get svc -n "$NAMESPACE"
}

# ============ MAIN ============
echo ""
echo "==============================="
echo "  DeyeSolar K8s Deployment"
echo "==============================="
echo ""

check_prerequisites
test_connection
build_and_push
deploy_manifests
restart_deployment
seed_credentials
show_status
