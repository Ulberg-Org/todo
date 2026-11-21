# Local development (PLAN §11, D61): the platform CLI runs the app in a local Kubernetes cluster, the way it runs
# in Azure. The CLI is a public image (D63), pinned by digest, which runs only after cosign has checked that the
# platform repo's release workflow signed it. Bump the pin with each platform release (its summary lists it).
# Developing the platform itself: PLATFORM=../platform/cli/platform make up (runs it from that checkout).
PLATFORM_IMAGE := ghcr.io/ulberg-org/platform-cli:v0.2.1@sha256:a82011b80d9920fb35ee80563c5daac8784b52370f1cdbab2f0c44b2b842cd8a
COSIGN ?= cosign
# The machine's proxy, if it has one (Claude's sandbox, D81): empty variables pass nothing.
PROXY := -e HTTP_PROXY -e HTTPS_PROXY -e NO_PROXY -e http_proxy -e https_proxy -e no_proxy
SIGNED_BY := --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity-regexp '^https://github\.com/Ulberg-Org/platform/\.github/workflows/release\.yml@refs/tags/v'
PLATFORM ?= $(COSIGN) verify $(PLATFORM_IMAGE) $(SIGNED_BY) >/dev/null 2>&1 \
  || { echo "The platform CLI image isn't signed by Ulberg-Org/platform's release (is cosign installed?)" >&2; exit 1; }; \
  docker run --rm -i $(PROXY) -v /var/run/docker.sock:/var/run/docker.sock -v "$(CURDIR):$(CURDIR)" -w "$(CURDIR)" \
  $(PLATFORM_IMAGE)
KUBECTL := kubectl --kubeconfig .local/kubeconfig
.PHONY: up sync test-platform down status

## up: a fresh cluster with the platform, the database and two backend instances with hot reload.
up:
	$(PLATFORM) up .

## sync: hands Argo CD a changed platform.yaml (code changes reload by themselves).
sync:
	$(PLATFORM) sync .

## test-platform: checks the network rules against the running cluster (D66): who may reach the backend, the
## database, the telemetry collector, the Kubernetes API and the internet.
test-platform:
	$(PLATFORM) test .

## down: removes the cluster and the database.
down:
	$(PLATFORM) down .

## status: what Argo CD runs, and which instance answers through the gateway.
status:
	@$(KUBECTL) -n argocd get applications
	@for i in 1 2 3 4; do curl -s http://localhost:9875/version; echo; done
