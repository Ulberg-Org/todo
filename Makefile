# Local development (PLAN §11, D61): the platform CLI runs the app in a local Kubernetes cluster, the way it runs
# in Azure. Until the CLI ships as an image (D63), it runs from a checkout of the platform repo next to this one.
PLATFORM ?= ../platform/cli/platform
KUBECTL := kubectl --kubeconfig .local/kubeconfig
.PHONY: up sync down status

## up: a fresh cluster with the platform, the database and two backend instances with hot reload.
up:
	$(PLATFORM) up .

## sync: hands Argo CD a changed platform.yaml (code changes reload by themselves).
sync:
	$(PLATFORM) sync .

## down: removes the cluster and the database.
down:
	$(PLATFORM) down .

## status: what Argo CD runs, and which instance answers through the gateway.
status:
	@$(KUBECTL) -n argocd get applications
	@for i in 1 2 3 4; do curl -s http://localhost:9875/version; echo; done
