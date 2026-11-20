# todo

A todo app built infrastructure-first and AI-first. The plan is in [PLAN.md](PLAN.md), the working rules in
[AGENTS.md](AGENTS.md); the platform it runs on lives in [Ulberg-Org/platform](https://github.com/Ulberg-Org/platform).

## Run it locally

Needs Docker (12 GiB of memory), kind, kubectl, helm, jq, and a checkout of
[Ulberg-Org/platform](https://github.com/Ulberg-Org/platform) next to this one (`../platform`).

```
make up       # a local Kubernetes cluster with the platform, Postgres and two backend instances (hot reload)
make sync     # after changing platform.yaml
make status   # what Argo CD runs, and which instance answers
make down     # remove everything
```

| What | Where |
|---|---|
| Public API (always through the gateway) | http://localhost:9875 (no endpoints yet) |
| Health and version (management port, not routed) | `kubectl --kubeconfig .local/kubeconfig -n todo port-forward deploy/backend 8081`, then http://localhost:8081/version |
| Grafana (logs, traces, metrics) | http://localhost:9876, sign in as `admin` / `local-only-admin` |
| kubectl | `kubectl --kubeconfig .local/kubeconfig` |

Code changes reach the running instances by themselves (hot reload). Claude reads the same data through its own
read-only Grafana account (see AGENTS.md §5).

## Tests

```
dotnet run --project backend/test/TodoBackend.Test
```
