#!/usr/bin/env bash
# Waits until Envoy routes to all backend instances (D57), or fails after 3 minutes with the instances' last
# log lines. Envoy only routes to instances whose /health/ready passes, so an instance answering /version
# through Envoy is ready. Usage: wait-ready.sh <number of instances>
set -euo pipefail
want=${1:?number of instances}

for _ in $(seq 1 90); do
  seen=$(for _ in $(seq 1 $((want * 3))); do
    curl -sf http://localhost:8080/version | jq -r .instance || true
  done | sort -u | grep -c . || true)
  if [ "$seen" -ge "$want" ]; then
    echo "All $want instances are ready behind Envoy"
    exit 0
  fi
  sleep 2
done
echo "Not all instances became ready. Last log lines:" >&2
tail -n 5 .local/api-*.log >&2
exit 1
