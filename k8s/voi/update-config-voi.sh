#!/bin/bash
# Manual convenience script mirroring k8s/stage/update-config-stage.sh for the Voi production
# environment. Not used by CI (.github/workflows/promote-production.yml with network=voi does the
# equivalent kubectl calls itself) - this is only for applying a manifest/config change by hand
# against an already-configured kubeconfig, without running the promote workflow.
set -euo pipefail

kubectl apply -f deployment-api-voi.yaml -n biatec-scan
kubectl apply -f subscriber-deployment-voi.yaml -n biatec-scan

kubectl create configmap avm-trade-reporter-voi-main-conf --from-file=conf-api-voi -n biatec-scan --dry-run=client -o yaml | kubectl apply -f -
kubectl rollout restart deployment/avm-trade-reporter-voi-app-deployment -n biatec-scan
kubectl rollout status deployment/avm-trade-reporter-voi-app-deployment -n biatec-scan

kubectl rollout restart deployment/avmtradereporter-subscriber-voi -n biatec-scan
kubectl rollout status deployment/avmtradereporter-subscriber-voi -n biatec-scan
