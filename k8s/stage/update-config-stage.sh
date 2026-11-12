#!/bin/bash
# Manual convenience script mirroring k8s/main/update-config.sh for the stage environment. Not used by
# CI (.github/workflows/deploy.yml does the equivalent kubectl calls itself on every push) - this is
# only for applying a manifest/config change by hand against an already-configured kubeconfig, without
# waiting for a push to master.
set -euo pipefail

kubectl apply -f deployment-api-stage.yaml -n biatec-scan
kubectl apply -f subscriber-deployment-stage.yaml -n biatec-scan

kubectl create configmap avm-trade-reporter-stage-main-conf --from-file=conf-api-stage -n biatec-scan --dry-run=client -o yaml | kubectl apply -f -
kubectl rollout restart deployment/avm-trade-reporter-stage-app-deployment -n biatec-scan
kubectl rollout status deployment/avm-trade-reporter-stage-app-deployment -n biatec-scan

kubectl rollout restart deployment/avmtradereporter-subscriber-stage -n biatec-scan
kubectl rollout status deployment/avmtradereporter-subscriber-stage -n biatec-scan
