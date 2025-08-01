kubectl apply -f deployment-api.yaml -n biatec-scan
kubectl delete configmap avm-trade-reporter-main-conf -n biatec-scan
kubectl create configmap avm-trade-reporter-main-conf --from-file=conf-api -n biatec-scan
kubectl rollout restart deployment/avm-trade-reporter-app-deployment -n biatec-scan
kubectl rollout status deployment/avm-trade-reporter-app-deployment -n biatec-scan
