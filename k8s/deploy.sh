#!/bin/bash

# DeyeSolar Kubernetes Deployment Script
# Builds and pushes the Docker image, then deploys with the local kubectl
# context (no SSH access to the cluster host required).
# Usage: ./deploy.sh [--help]

set -e

# ============ CONFIGURATION ============
NAMESPACE="deye-solar"
IMAGE_NAME="ghcr.io/dmitryshapar3/deye-solar"
IMAGE_TAG="${IMAGE_TAG:-latest}"
KUBE_CONTEXT="${KUBE_CONTEXT:-kubernetes-admin@kubernetes}"
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
    echo "  IMAGE_TAG        Docker image tag (default: latest)"
    echo "  KUBE_CONTEXT     kubectl context to deploy with (default: kubernetes-admin@kubernetes)"
    echo ""
    echo "What it does:"
    echo "  1. Builds the Docker image locally (linux/amd64)"
    echo "  2. Pushes to $IMAGE_NAME"
    echo "  3. Applies the K8s manifests with the local kubectl context"
    echo "  4. Restarts the deployment to pull the new image"
    exit 0
fi

kctl() {
    kubectl --context "$KUBE_CONTEXT" "$@"
}

# Check prerequisites
check_prerequisites() {
    local missing=false

    if ! command -v docker &> /dev/null; then
        print_error "Docker not found"
        missing=true
    fi

    if ! command -v kubectl &> /dev/null; then
        print_error "kubectl not found"
        missing=true
    fi

    if $missing; then exit 1; fi
}

# Test cluster connectivity
test_connection() {
    print_info "Testing cluster access via kubectl context '$KUBE_CONTEXT'..."
    if kctl get nodes --request-timeout=15s > /dev/null 2>&1; then
        print_status "Cluster reachable"
    else
        print_error "Cannot reach the cluster with context '$KUBE_CONTEXT' (check kubeconfig / VPN)"
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

    kctl apply -f "$SCRIPT_DIR/namespace.yaml"
    print_status "Namespace applied"

    # The image pull secret is provisioned once (copied from the production
    # namespace); just verify it is still there.
    if kctl get secret ghcr-secret -n "$NAMESPACE" > /dev/null 2>&1; then
        print_status "Image pull secret present"
    else
        print_warn "Secret 'ghcr-secret' missing in namespace '$NAMESPACE' - image pulls will fail."
        print_warn "Copy it from the production namespace, e.g.:"
        print_warn "  kubectl get secret ghcr-secret -n production -o yaml | sed 's/namespace: production/namespace: $NAMESPACE/' | kubectl apply -f -"
    fi

    kctl apply -f "$SCRIPT_DIR/deployment.yaml"
    print_status "Deployment applied"

    kctl apply -f "$SCRIPT_DIR/service.yaml"
    print_status "Service applied"
}

# Restart deployment to pick up new image
restart_deployment() {
    print_info "Restarting deployment to pull new image..."
    kctl rollout restart deployment/deye-solar -n "$NAMESPACE"
    print_status "Restart triggered"

    print_info "Waiting for rollout..."
    kctl rollout status deployment/deye-solar -n "$NAMESPACE" --timeout=180s
    print_status "Deployment ready"
}

# Show status
show_status() {
    echo ""
    echo "========================================="
    print_status "DeyeSolar deployed successfully!"
    echo "========================================="
    echo ""

    local node_ip node_port
    node_ip="$(kctl get nodes -o jsonpath='{.items[0].status.addresses[?(@.type=="InternalIP")].address}' 2>/dev/null || true)"
    node_port="$(kctl get svc deye-solar -n "$NAMESPACE" -o jsonpath='{.spec.ports[0].nodePort}' 2>/dev/null || true)"
    if [[ -n "$node_ip" && -n "$node_port" ]]; then
        echo "Access: http://$node_ip:$node_port"
        echo ""
    fi

    print_info "Pod status:"
    kctl get pods -n "$NAMESPACE" -o wide
    echo ""
    print_info "Service:"
    kctl get svc -n "$NAMESPACE"
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
show_status
