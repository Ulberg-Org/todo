# Local development, tier 1 (PLAN §11). Everything goes through Envoy on http://localhost:8080 (D57).
.PHONY: up down status

STATE := .local
INSTANCES := 1 2
DATABASE := Host=localhost;Port=5432;Database=todo;Username=todo_app;Password=local-only-app

## up: Postgres, the collector, Grafana and Envoy in Docker, then two backend instances with hot reload.
up:
	@for i in $(INSTANCES); do \
	  if lsof -iTCP:500$$i -sTCP:LISTEN >/dev/null 2>&1; then \
	    echo "Port 500$$i is already in use (an earlier run?). Run 'make down' first." >&2; exit 1; \
	  fi; \
	done
	docker compose up -d --wait
	@mkdir -p $(STATE)
	@for i in $(INSTANCES); do \
	  INSTANCE_NAME=api-$$i Database__ConnectionString="$(DATABASE)" \
	  OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER=1 \
	  nohup dotnet watch --non-interactive --project backend/src/TodoBackend run \
	    --artifacts-path $(STATE)/api-$$i --urls http://127.0.0.1:500$$i > $(STATE)/api-$$i.log 2>&1 & \
	  echo $$! > $(STATE)/api-$$i.pid; \
	done
	@./local/wait-ready.sh $(words $(INSTANCES))
	@echo "API (through Envoy): http://localhost:8080   Grafana: http://localhost:3000   Logs: $(STATE)/api-*.log"

## down: stops the backend instances and the containers (keeps nothing running).
down:
	@# dotnet watch starts dotnet run, which starts the app; all of them carry the instance's folder.
	@for i in $(INSTANCES); do pkill -9 -f "$(STATE)/api-$$i" 2>/dev/null || true; rm -f $(STATE)/api-$$i.pid; done
	docker compose down

## status: which instances answer through Envoy, and the containers.
status:
	@for i in 1 2 3 4; do curl -s http://localhost:8080/version; echo; done
	@docker compose ps --format 'table {{.Service}}\t{{.Status}}'
