# todo

A todo app built infrastructure-first and AI-first. The plan is in [PLAN.md](PLAN.md), the working rules in
[AGENTS.md](AGENTS.md); the platform it runs on lives in [Ulberg-Org/platform](https://github.com/Ulberg-Org/platform).

## Run it locally

Needs Docker and the .NET 11 SDK.

```
make up       # Postgres, telemetry, Envoy, and two backend instances with hot reload
make status   # which instance answers, and the containers
make down     # stop everything
```

| What | Where |
|---|---|
| API (always through the load balancer) | http://localhost:8080 (`/health/live`, `/health/ready`, `/version`) |
| Grafana (logs, traces, metrics) | http://localhost:3000 |
| Backend logs | `.local/api-1.log`, `.local/api-2.log` |

## Tests

```
dotnet run --project backend/test/TodoBackend.Test
```
