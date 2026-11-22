# PLAN.md

> **Status: v26 (2026-11-22, D91–D94: every developer's Claude, the database's line between platform and app, its security and monitoring, tooling owned by the platform).** Phases 0–2.5 done; Phase 3 (Azure foundation for dev) next. Phase 0, 0b and 1 done. Phase 2 in progress: the renderer, the platform CLI, Argo CD and the telemetry stack are done; next the infrastructure signals (Phase 2 TODOs).
> Working rules live in `AGENTS.md`. History lives in `.prompts/`.

## 1. Goal

A showcase todo application built entirely with AI that is **infrastructure-first and AI-first**:

- Humans and agents can use every feature equally well, under the same access control.
- Every layer (frontend, backend, database, infrastructure) is locked down as close to zero trust as we can get, and still lets an authorized agent read traces, logs and metrics end to end.
- Features: create, list, get, edit and delete todos, then share a todo with other people by email using permission policies.
- The app is **semi-public**: anyone can sign up, no organization membership needed.
- **Cost: as cheap as is feasible and normal, but security comes first** (see §9).
- **Purpose (D44):** for fun and learning; it has to show that it works, not be always on. One human plus agents is the normal case. The platform must still make sense for a team, but isn't built to serve one now.
- **Portable (D49):** someone else can fork both repos, edit `people.yaml` and a few config files, and run their own platform in their own tenant.

## 2. Repositories and governance

| Repo | Contains | Who can merge |
|---|---|---|
| `Ulberg-Org/todo` (this repo) | F# backend, React frontend, tests, app CI, **`platform.yaml`** | `todo` lead developers |
| `Ulberg-Org/platform` (new) | Pulumi in **F#** (Azure + Entra + GitHub), ArgoCD apps, Helm values, cluster policies, observability stack, the `platform.yaml` schema + renderer, **all access definitions** | `platform` lead developers (a subset of infra developers) |

### 2.1 `platform.yaml`: the contract between app and platform

The app repo *declares* what it needs. The platform repo *decides* how (or whether) to provide it. App developers never get credentials, cluster access or deploy rights.

```yaml
# todo/platform.yaml (illustrative, the schema lives in the platform repo)
apiVersion: platform/v1
app: todo
services:
  - name: backend
    image: todo-backend        # tag is supplied by CI, never written here by hand
    port: 8080
    replicas: { min: 2, max: 4 }
    resources: { cpu: 250m, memory: 256Mi }
    health: { live: /health/live, ready: /health/ready }
    routes: [ /api, /mcp ]
    database: todo             # only services that list the DB get network + identity access to it
  - name: frontend
    image: todo-frontend
    port: 8080
    replicas: { min: 2, max: 2 }
    routes: [ / ]
databases:
  todo: { engine: postgres }
auth:
  spa: { redirectPaths: [ /auth/callback ] }
  api: { scopes: [ todos.read, todos.write, todos.share ] }
egress: []                     # anything outside the cluster has to be listed
```

Flow, from change to prod (D37):
1. **Change.** An app developer opens a PR in `todo`. The app's repo lead approves it as code owner, and it merges to `main`.
2. **Build.** App CI validates `platform.yaml` against the schema, builds and signs the images, and pushes them to ACR.
3. **Deploy to dev.** A GitHub App with **only** "open PR" rights on the platform repo (`ulberg-platform-pr-bot`) opens a PR that sets the new image tags in `apps/todo/dev/` together with the current `platform.yaml`. Platform CI checks it against policy.
   - **Image-tag-only change that passes policy → auto-merge**, and ArgoCD syncs it into dev. Tags are GitOps manifests, not Pulumi, so no environment approval is involved.
   - **A `platform.yaml` change within the policy limits → the app lead's approval; outside them → a platform lead's (D48).** If it needs Azure resources, the Pulumi apply also waits for a platform lead (step 5).
4. **Release to prod.** When the dev deploy is healthy and the post-deploy smoke tests pass, the pr-bot opens (or updates) one **release PR** per app that copies exactly those tags to `apps/todo/prod/`.
   - Policy check: the tags must be the ones running healthy in dev, and the images must be signed.
   - **The app's repo leads approve it** (they are the code owners of `apps/todo/prod/` tags). The merge is the release: ArgoCD syncs prod.
   - A release PR that changes anything besides tags also needs a platform lead.
   - Rollback is a release PR with the previous tags, with the same approval.
5. **Platform CI runs `pulumi up` only after approval.** Each environment's apply job runs in a GitHub **Environment** (`dev`, later `prod`) whose required reviewers are the platform leads. The Azure federated credential trusts *only* `repo:Ulberg@<owner-id>/platform@<repo-id>:environment:<env>` (GitHub's subject carries immutable IDs, so a renamed or recreated repo doesn't match), so a workflow without that approval can't even get an Azure token. PR previews use a separate read-only identity. (This is the reference-infra OIDC pattern, scoped tighter.) Nobody applies from a laptop.

How it's enforced:
- The platform repo's `CODEOWNERS`, generated from `people.yaml` and checked by the `access` stack (§2.2):
  - `apps/<app>/dev/` tags have no owner;
  - `apps/<app>/prod/` tags are owned by that app's repo leads;
  - everything else is owned by the platform leads.
- The ruleset requires code-owner review plus the policy check, with 0 general approvals. No app and no bot gets a ruleset bypass.
- GitHub Apps:
  - `ulberg-platform-admin`: the apply job only, in the `dev` and `prod` environments;
  - `ulberg-platform-reader`: PR previews, read-only;
  - `ulberg-platform-pr-bot`: deploy PRs from todo `main`, and release PRs.

### 2.2 Access defined as code (IaC)

All people and roles live in the platform repo, in one place:

```yaml
# platform/access/people.yaml (illustrative)
people:
  endre: { github: Ulberg, entra: <object-id> }
groups:
  infra-developers: [ endre ]   # Azure/K8s/DB read, infra logs, infra-dev Claude
  app-developers:   [ endre ]   # app logs + metrics + traces only
repo-leads:
  platform: [ endre ]           # may approve/merge in Ulberg-Org/platform
  todo:     [ endre ]           # may approve/merge in Ulberg-Org/todo
```

Pulumi (F#) turns this into:
- Entra security groups + memberships (workforce tenant), which feed the Azure RBAC, AKS RBAC, Postgres and Grafana org mappings.
- GitHub (Pulumi GitHub provider):
  - repository settings and branch rulesets: PR required, code-owner review required, signed commits, no force push, status checks required;
  - a `CODEOWNERS` file in each repo (`* @<leads>`). It is committed by hand and Pulumi fails the run if it doesn't match `people.yaml`, because a bot pushing it to `main` would need a ruleset bypass;
  - teams in the `Ulberg-Org` organization (D43): `<repo>-leads` (maintain on their repo, and the code owners), `infra-developers` (push everywhere), `app-developers` (push to app repos); everyone in `people.yaml` is an org member, and only the bootstrap owner is an org admin.
- Changing who is a lead or an infra developer is a PR to `platform/access/` that needs a platform lead's approval.

**GitHub organization** (D43): both repos live in `Ulberg-Org` (Team plan since 2026-11-19), so `CODEOWNERS` names teams (`* @Ulberg-Org/<repo>-leads`) and environment reviewers are teams. The ruleset bypass (D34) is "Organization admin": GitHub drops repository-role bypasses when a repo is transferred.

**Both repos are public** (D26). gitleaks secret scanning runs in CI as a required check, GitHub push protection and secret scanning are on, and Dependabot handles updates and alerts. **No git pre-commit hooks** (D29).

## 3. Decisions made

| # | Decision | Source |
|---|---|---|
| D1 | `.prompts/` holds one `.txt` per message under `.prompts/<date>/` | Phase 0 |
| D2 | `CLAUDE.md` only imports `AGENTS.md`, so there is one source of truth | Phase 0 |
| D3 | Agent interface is **MCP**. REST and MCP are both thin adapters over the same use-case functions | msg 2 |
| D4 | App users sign in with **Entra External ID** (anyone can sign up). Admins/developers use the **workforce tenant** | msg 2 |
| D5 | Separate **platform repo**, driven by `platform.yaml` (§2.1) | msg 2 |
| D6 | **SPA + MSAL (PKCE), bearer token in `Authorization` header**. No auth cookies, strict CORS | msg 2 |
| D7 | Cheapest *normal* setup with security first (§9). Starts with one `dev` environment; `prod` is added after Phase 6 (D38) | msg 2–3 |
| D8 | Features: create, list, get, edit, delete | msg 2 |
| D9 | Access matrix per group (§5). App devs' Claude gets **app logs + metrics + traces** | msg 2–3 |
| D10 | **No HTTP `QUERY`**; `GET` + query params for reads | msg 2 |
| D11 | Sharing follows the Unix/Linux permission model (§6) | msg 2–3 |
| D12 | Traces in **Tempo**; OTel Collector → Loki / Prometheus / Tempo → Grafana | msg 2 |
| D13 | **Valkey and Keycloak deferred** until a trigger in §8 happens | msg 2 |
| D14 | Local development in three tiers (§11) | msg 2–3 |
| D15 | Repo leads + `CODEOWNERS` + branch rules defined as code in the platform repo (§2.2) | msg 3 |
| D16 | **No Postgres RLS in v1**; it is future work (§13) | msg 3 |
| D17 | The database is private: **no public endpoint; only backend pods can reach it** (§9.1) | msg 3 |
| D18 | Infra is applied **only by platform CI after approval** | msg 3 |
| D19 | **Pulumi in F#** (Pulumi has no official Rust SDK) | msg 3 |
| D20 | No custom domain for now | msg 3 |
| D21 | GitHub owner: **`Ulberg`** (personal account). *Superseded by D43:* the organization `Ulberg-Org` since 2026-11-19 | msg 3 |
| D22 | Sharing never reveals whether a user exists (§6.3) | msg 3 |
| D23 | **Entra ID P2 + PIM is implemented** for infra developers (§7) | msg 4 |
| D24 | **DB auth option B**: Entra-only, split migrator / app / read-only identities, no passwords (§9.2) | msg 4 |
| D25 | A write-only grant appears in the grantee's list as `{ id, permissions }` with no content | msg 4 |
| D26 | **Public repos**, with secret scanning (gitleaks in CI, GitHub push protection + secret scanning, Dependabot) | msg 4–5 |
| D27 | No "leave a share" in v1; it is future work | msg 4 |
| D28 | Azure bootstrap is done by you, following a checklist + script (§4) | msg 4 |
| D29 | **No git pre-commit hooks.** Every check runs in CI (Claude Code hooks for `.prompts/` are not git hooks and are unaffected) | msg 5 |
| D30 | `reference-infra/` is a read-only pattern source like `reference/`; both are git-ignored and never pushed | msg 5 |
| D31 | Adopted reference-infra patterns (§12): nested read ⊂ write ⊂ admin groups, generated RBAC, environment-scoped OIDC, External Secrets Operator, cert-manager | msg 5 |
| D32 | **No service mesh in v1**; add one when a trigger in §8 happens | msg 6 |
| D33 | Infra devs reach the DB through a `db-access` proxy pod, not Teleport (§4.1) | msg 6 |
| D34 | **Bootstrap exception:** while you are the only person (infra dev, admin, app dev and lead of both repos), commits go straight to `main` without required review. GitHub doesn't allow approving your own PR. *Ended 2026-11-19* (platform PR #26): no ruleset bypass for anyone; Claude's app authors PRs (D52), so a solo lead can approve them | msg 7, 2026-11-19 |
| D35 | The owner Microsoft account keeps only its billing role: no Global Admin and no subscription Owner, not even eligible | msg 2026-11-09 |
| D36 | Subscription **Owner only via the break-glass accounts**. `platform-admin` (PIM, 1 h) gets no Owner | msg 2026-11-09 |
| D37 | **Release flow** (§2.1). A PR approved by the app lead merges to app `main`; tag-only changes auto-deploy to `dev`; a release PR copies the dev tags to `prod`, and **the app lead approves it** | msg 2026-11-09 |
| D38 | **`prod` gets its own AKS cluster, Postgres, VNet and Key Vault** (own resource groups, same subscription). It is built after Phase 6, once the solo-dev exception (D34) is gone. *Amended by D45:* started on demand like `dev`. The goal is a platform that handles several environments; strict isolation between them (own subscription, per-environment apply identities) is not a goal | msg 2026-11-09, 2026-11-19 |
| D39 | **Short sessions** (Conditional Access, workforce tenant only): a fresh phishing-resistant sign-in for **every** PIM activation (authentication context); infra developers sign in to Azure portal/CLI/ARM again every **4 h**, with no persistent browser session; all developers and guests sign in to everything else again every **12 h**. App users (External ID tenant) are not affected | msg 2026-11-09 |
| D40 | **Privilege alerts**: PIM emails every activation and every new assignment of Global Admin, `platform-write` and `platform-admin` to the platform alert list (GitHub secret `PLATFORM_ALERT_EMAILS`, kept out of the public repos). Log-based alerts (break-glass sign-ins, role changes not made by the apply identity, PIM/CA policy changes) come with Phase 5 | msg 2026-11-09 |
| D41 | **Claude gets its own read-only identity**, not the developer's: a separate Entra user per developer (`agents` in `people.yaml`, infra developers only for now), linked to its person (manager = the person, employee type `Agent`, "Claude agent of <name> (<handle>)"). In `platform-agents`: Reader, Monitoring Reader, Log Analytics Reader, Cost Management Reader on the subscription, Entra Security Reader; AKS RBAC Reader comes with Phase 3. Never PIM-eligible; standing-access check covers it. Entra ID P2 licence, the same Conditional Access as humans (passkey, 4 h Azure tools, 12 h otherwise). The developer signs it in (own passkey, separate `az` profile), so no secrets on disk and audit logs separate human from agent. A Claude Code hook blocks `az` without the agent profile. Done before Phase 1 (moved up from Phase 3 at your request) | msg 2026-11-09 |
| D42 | **Members can't create anything on their own** in Entra (security groups, apps, tenants, app consent, guest invites, sign-ups), humans and agents alike: tenant authorization policy in the `access` stack. Found by the agent smoke test | msg 2026-11-09 |
| D43 | **Move both repos into a GitHub organization (Free plan)**, so access scales: teams from `people.yaml`, org policies (2FA, token rules, no self-created repos), central audit log, and **one org-owned "Claude" GitHub App** that every developer's Claude uses *on that developer's behalf* (user-to-server token via device flow, 8 h, permissions = app ∩ developer: contents + pull requests only; never workflows, settings, approvals or merges). Commits and PRs show as "<developer> via Claude". Owner bypass (D34) stays. GitHub accounts provisioned from Entra (Enterprise Managed Users) are the documented upgrade path, not built (§13). The organization is **`Ulberg-Org`** | msg 2026-11-09, 2026-11-19 |
| D44 | **Purpose:** fun and learning; show that it works. Solo plus agents is the normal case; team features cost nothing extra or wait | msg 2026-11-19 |
| D45 | **Environments run on demand.** A start workflow (platform-lead approval) creates `dev` or `prod` from code with `pulumi up`; it is **always destroyed after 4 h** by a scheduled workflow (no approval needed to destroy). Persistent, never destroyed: Pulumi state, ACR, Key Vault, identities, telemetry storage (7–14 days retention), and **one static IP + Azure DNS label per environment**, so its URL and redirect URIs never change. Each start proves the environment rebuilds from code; data is seed data | msg 2026-11-19 |
| D46 | **MCP clients in v1: pre-registered ones only** (Claude Code). Claude.ai, Claude Desktop connectors and ChatGPT need Dynamic Client Registration, which Entra External ID lacks; that is the Keycloak trigger (§8) and stays future work | msg 2026-11-19 |
| D47 | **Anything infrastructure is declarable in `platform.yaml`**; app teams own their architecture. v1 building blocks are exactly what todo needs: `services`, `workers` (no routes), `schedules` (CronJobs), `databases` (Postgres), `email` (Azure Communication Services via workload identity, no keys or SMTP credentials). Nothing else until an app asks, by a PR to the platform schema | msg 2026-11-19 |
| D48 | **Policy decides who approves a `platform.yaml` change.** Within the limits (sizes, counts, allowed building blocks) the app lead's approval is enough; outside them a platform lead must approve. The limits are policy code owned by the platform leads. Replaces "any `platform.yaml` change needs a platform lead" (§2.1) | msg 2026-11-19 |
| D49 | **Portability:** everything tenant-specific lives in `people.yaml` and a few config files; nothing in code names your tenant. Bootstrap wizard and a proven run in an empty trial tenant are a late milestone (§13) | msg 2026-11-19 |
| D50 | **Share notifications (app, Phase 10):** only to recipients who already have an account, so third parties can't be spammed. Sent from an outbox table by a worker, never in the request, so the API response and timing don't reveal whether an account exists (D22). One batched email per user per day at 08:00, one time zone for everyone; it contains the sharer's email. No further spam filtering | msg 2026-11-19 |
| D51 | **App users (Phase 6/10):** deleting an account deletes what they own and haven't shared; grants they made survive, no longer linked to them. Grants resolve email → user ID, so a changed email only affects new invites. *Open for Phase 10:* who controls a shared todo after its owner is deleted | msg 2026-11-19 |
| D52 | **Claude's GitHub identity is the app itself, not "on behalf of" a developer** (amends D43). PRs are authored by `ulberg-claude[bot]`, so a human lead can approve them and the D34 bypass can be removed. The app has contents, pull requests and workflows (workflows added the same day, so CI can still change once the bypass is gone; every change needs a lead's approval). Never settings, approvals or deployments; it may merge a PR a lead approved. Its commits are made through GitHub's API so they are verified (`scripts/claude-push.sh`). Its private key is a **non-exportable key in Key Vault**: the agent identity (`platform-agents`) may only *sign* with that one key, so Claude mints 1 h installation tokens without the key ever leaving Key Vault, and every signing is logged under the agent's name. Attribution to the developer is in the PR body and commit trailer | msg 2026-11-19 |
| D53 | **A `shared` Pulumi stack for everything persistent** (D45): ACR now; later telemetry storage, static IPs and DNS labels. Environment stacks read its outputs by stack reference. **Stack order lives in one file**: up = `access` → `shared` → environments, teardown in reverse; an environment's destroy stops at its own stack; a full teardown is a separate approved workflow that stops before `access`/`bootstrap` (tested in the portability milestone, D49) | msg 2026-11-19 |
| D54 | **ACR before Phase 1's image step** (pulled forward from Phase 3): Basic SKU, Entra-only (no admin user, no anonymous pull). Only todo's CI pushes, only from `main`, with its own OIDC identity holding AcrPush and nothing else; PR builds never push; nobody pushes from a laptop. Tags are the commit SHA only and **locked**, never `latest` | msg 2026-11-19 |
| D55 | **Images are signed with cosign keyless** (bound to todo's CI workflow on `main`, recorded in Sigstore's public log), and the SBOM (CycloneDX, by Trivy) is attached as a signed attestation. From Phase 4 the cluster refuses any image not signed that way. **Trivy** fails CI on critical/high vulnerabilities that have a fix; exceptions in a committed file, each with a reason and an expiry | msg 2026-11-19 |
| D56 | **arm64 everywhere**: GitHub's ARM runners build, Microsoft's chiseled .NET image (non-root, read-only file system, invariant globalization) runs it, and AKS nodes are an ARM size (e.g. `Standard_D2ps_v6`). Postgres **18** locally and on Azure | msg 2026-11-19 |
| D57 | **Always at least two instances behind a load balancer**, locally too: two `dotnet watch` instances behind Envoy in tier 1, and in the cluster the platform policy (D48) rejects `replicas.min` below 2 and replicas must sit on different nodes. Nothing may rely on being the only instance (no in-process shared state, nothing run once per process). No inbox/outbox/worker is built now | msg 2026-11-19 |
| D58 | **Claude reads local telemetry and data from day one, through one contract**: `mcp-grafana` (app logs, traces, Postgres's own logs) and a Postgres MCP server as the read-only role only. Labels, role names and tools are defined once in the platform repo; Azure plugs into the same contract in Phase 3/5, with a drift test. *Amended:* no separate contract file or drift test; the D60 model generates local and cloud from one source, and until the cloud side exists there's only one copy (the local files) | msg 2026-11-19 |
| D59 | **The app runs the newest .NET: .NET 11 / F# 11** (supported as long as .NET 10 LTS, until 2028-11-14); the platform repo targets .NET 10 and builds with the same SDK (`rollForward: latestMajor`). SDKs come from Homebrew via the developer's nix-darwin flake. Latest stable packages: Oxpecker 2, Npgsql 10 with **Npgsql.FSharp** on top (as in `reference/`), OpenTelemetry 1.21, Expecto 11, Testcontainers 4; Dependabot keeps NuGet, the SDK and Actions current | msg 2026-11-19 |
| D60 | **One model, every environment derived from it.** `platform.yaml` (what the app needs) plus small overlays (`environments/{local,dev,prod}`) that hold only what must differ: sizes, addresses and how you sign in (password locally, Entra in Azure). The platform's F# renderer generates everything from them: tier 1 (`compose.yaml`, Envoy's config, the number of instances in `make up`, the collector config), tier 2 (kind) and dev/prod (Kubernetes manifests incl. Envoy Gateway routes, Pulumi inputs). Nothing per environment is written by hand; a test fails if an overlay changes something that belongs in the model. Today's hand-written local files are the starting point and are replaced by the renderer in Phase 2 | msg 2026-11-19 |
| D61 | **One local tier: Kubernetes (kind).** It replaces Phase 1's compose setup: backend pods run the SDK image with the source mounted and `dotnet watch` (hot reload); `make up` always starts from a fresh cluster. Same as the cloud: Kubernetes **1.37** pinned in one place for local and AKS (patch may differ), Postgres **outside** the cluster, Cilium, Envoy Gateway (Gateway API), ArgoCD (locally reading the checkout through a git server inside `make up`), and the telemetry stack from the same Helm charts. The one deliberate difference: **no image-signature check locally**. App at http://localhost:9875, Grafana at http://localhost:9876; Docker gets 12 GiB | msg 2026-11-19 |
| D62 | **The renderer outputs plain Kubernetes YAML** for local, dev and prod (one folder per environment, read by ArgoCD), plus generated values for third-party Helm charts. No Kustomize (the renderer is the per-environment mechanism). Environment files live in the platform repo (`environments/`); no app-side overrides until an app needs one. Generated files are never committed | msg 2026-11-19 |
| D63 | **The platform CLI**: one public, signed image from the platform repo (`platform up / down / test`) that sets up the local cluster, installs the platform components and renders and deploys the app. todo's `make up` runs it at a pinned version | msg 2026-11-19 |
| D64 | **Full resource metrics for every service, platform-level**: CPU, memory, network, restarts and throttling per pod (kubelet), Kubernetes state (kube-state-metrics), node CPU/memory/disk, Postgres (connections, size, transactions, locks, cache hits) through a fourth role `todo_monitor` that reads statistics only, and Envoy Gateway per route. Same set locally and in Azure | msg 2026-11-19 |
| D65 | **Dependency updates wait, and are pinned by content.** Dependabot cooldown in both repos for every ecosystem: patch 7 days, minor 14, major 30; security fixes immediately. *Amended 2026-11-20:* GitHub Actions and Docker only take one cooldown for every kind of update, so they wait 14 days. Everything is pinned by hash so a re-published version can't slip in: NuGet lock files (CI restores in locked mode), Docker images by digest, GitHub Actions by commit SHA, Helm charts by digest or checksum, downloaded tools by checksum | msg 2026-11-19 |
| D66 | **Network rules are tested by hand**: `make test-platform` checks them against the local cluster (backend → database allowed; other pods and unlabelled pods blocked). No CI job | msg 2026-11-19 |
| D67 | **Argo CD manages everything after a small bootstrap.** Per environment, only the bootstrap differs: locally the platform CLI creates kind, installs Cilium, Argo CD and one root Application; in Azure, Pulumi creates AKS (which brings Cilium), installs Argo CD and the same root Application. Argo CD then deploys everything else from the platform repo, itself included: the platform components (`components/<name>/`: chart + values per environment + plain manifests per environment), and each app with its own project. Cilium stays in the bootstrap (the network must exist before anything runs) | msg 2026-11-20 |
| D68 | **The renderer runs inside Argo CD**, as its `platform` plugin (a sidecar of the repo server), for apps and for the root application: `platform.yaml` + the environment file + the image tag go in, manifests come out; nothing rendered is ever committed (D62 holds). Locally Argo CD reads snapshots of the developer's checkouts (uncommitted changes included) from a git server in the cluster; in Azure it reads GitHub. So a local test goes through the same rendering, ordering and sync as production | msg 2026-11-20 |
| D69 | **Database credentials come from Entra in Azure.** The app's pods get an Entra workload identity and Npgsql fetches a token as the password; no database secret exists. Locally Postgres can't use Entra, so it stays a password Secret created by the CLI. The environment file chooses which; the backend's Npgsql setup asks Entra for a token when the connection string has no password (Phase 3) | msg 2026-11-20 |
| D70 | **Network rules allow the database by address range in Azure, by name locally.** Name-based rules (and the DNS inspection they need) require AKS's paid Advanced Container Networking Services, so Azure allows the database subnet's range (`dev`: 10.20.2.0/24 in VNet 10.20.0.0/16, created in Phase 3); locally the database is allowed by name. The environment file chooses (`database.range`). Local Cilium is pinned to the version AKS runs (1.19 for Kubernetes 1.36; recheck for 1.37 in Phase 3), not the newest. Every namespace, the platform's included, is deny-by-default; an app's project allows only Deployments, Services, HTTPRoutes and Cilium rules, only in its own namespace and only from its own repo | msg 2026-11-20 |
| D71 | **One stream of telemetry, readable by people and by Claude alike.** Everything the app and the infrastructure emit (traces, logs, metrics, Kubernetes events, network decisions, audit logs, profiles, browser telemetry) goes into the same Grafana. People sign in as themselves (locally `admin`); each Claude reads as its own read-only identity (locally Grafana's `claude` Viewer service account, through `mcp-grafana`), so its reads are attributed to it and it can't change anything. Dashboards and alert rules are code in the platform repo, never made by hand | msg 2026-11-20 |
| D72 | **A local image cache that survives `make down`**: pull-through registry mirrors (Docker Hub, Quay, GHCR, registry.k8s.io) in containers outside the cluster, which the kind nodes use first. Safe because every image is pinned by digest (D65): a cached image is byte-for-byte the pinned one, and a missing mirror falls back to the registry | msg 2026-11-20 |
| D73 | **An app's APIs are split by who may call them, and both layers enforce it.** **Public**: anonymous, from the internet; only what anyone may see (its OpenAPI spec, status). **Internal**: only systems we built (the customer frontend, our other services, an admin app); authenticated as those systems or their users. **External**: customers reaching their own data with API keys; behind a key is an API user of our app, which signs in with a username and password and gets a short-lived token (the delegate/API-user pattern). **Private**: only from declared callers inside the cluster, with stricter access control in both layers; deferred until something needs it (an admin app would use internal and private). Each API is one endpoint group with its own port, authentication and OpenAPI document generated from the code at build time. The renderer routes each API to its own port only (one gateway, a hostname per API), validates tokens at the gateway for authenticated APIs as well as in the app, and allows each port separately in the network rules; private is never routed. Health and version have their own management port, reached only by the kubelet's probes (as on AKS; Azure's load balancer probes the gateway, not the app); `/version` moves to the internal API once it has authentication | msg 2026-11-20 |
| D74 | **A public status page** (e.g. `status.<domain>`) from an external status-page service, fed by synthetic checks and the alerts of D71, so customers see outages without reaching our systems | msg 2026-11-20 |
| D75 | **Who reaches the database.** Of the applications, only the backend (the service that declares it in `platform.yaml`), never from the internet (§9.1: no public endpoint). Of the people: developers read-only, and their Claude read-only; platform leads read-write, and their Claude read-only. Each as their own identity: Entra identities mapped to the database roles in Azure, `todo_readonly` locally. The cluster's baseline rule makes every pod deny-by-default, so no other pod reaches it either; `make test-platform` checks this (D66) | msg 2026-11-20 |
| D76 | **Claude works in a sandbox: Docker Sandboxes** (a micro-VM per sandbox, its own kernel), not a container or devcontainer sharing the host's kernel. Apple's Containerization isolates the same way (a VM per container) but has no network policies or credential handling for agents; revisit if it gets them | msg 2026-11-21 |
| D77 | **Two sandbox profiles, picked by the repo the session opens in, allowed by `access/people.yaml`.** **App** (a session in an app repo): that repo only; `make up` runs the full app and platform locally (the CLI image); Claude reads Grafana (all telemetry) and the app's database read-only locally, and in the cloud the app telemetry the developer may see. **Platform** (a session in the platform repo, only for `infra-developers`): platform and apps; also the CLI from the checkout and the read-only Kubernetes MCP locally, and in the cloud what the developer's agent identity may read (D41). Anyone else asking for the platform profile is refused | msg 2026-11-21 |
| D78 | **The sandbox is Claude's, not the developer's.** Nothing personal enters: not the home folder, keys, browser, or the developer's `gh`/`az`/git identity. It's named after Claude's identity (`<name>-agent`) and commits as `ulberg-claude[bot]`; tokens are minted on the developer's machine and passed in for one session (GitHub 1 h; Azure through a host-side proxy, platform profile only). Network is deny by default: GitHub, the image registries, NuGet, npm, the Helm repos, grafana.com and its storage, Anthropic's API (so Claude Code's web search works), and our own local and cloud environments. No other `curl` or page fetches | msg 2026-11-21 |
| D79 | **Each repo defines its tools in a `flake.nix`**: todo's (Docker, cosign, .NET), the platform's (also kind, kubectl, helm, Pulumi, az). The sandbox image is built from it and developers can use the same (`nix develop`), so Claude and people have the same versions | msg 2026-11-21 |
| D80 | ~~Superseded by D81.~~ **The local cluster runs on the developer's machine, not in the sandbox.** The sandbox's kernel (Docker's, not ours to change) lacks what Cilium needs (policy routing, `NET_CLS_BPF`), and swapping Cilium out would make local unlike AKS. So the developer runs `make up` and `make test-platform`; the sandbox mounts the repo folder (not a clone, so the cluster reloads Claude's edits) and reaches only the developer's Grafana (9876) and gateway (9875), per sandbox. Revisit when the sandbox kernel supports Cilium | msg 2026-11-21 |
| D81 | **Claude's sandbox is a Lima VM of its own, confined by Anthropic's `srt`** (replaces D80): Docker Sandboxes' kernel can't run Cilium, and mounting the developer's repo lets the sandbox plant code their machine runs later (Makefile, `.claude` hooks, `.mcp.json`). So: one persistent VM per developer and profile (Ubuntu, `vz`, about 4 CPUs, 10 GB, 60 GB disk, stopped when idle), **no host mounts**; Claude clones from GitHub inside, runs `make up` and its MCPs against its own cluster, and its work leaves only as a PR. Root in the VM can lift any firewall in it, so egress is enforced on the host: `limactl` runs under `srt` (macOS Seatbelt), which lets Lima's host process connect only to `srt`'s allowlisting proxy (the D78 list) and the VM's SSH port; not the host's other ports or the LAN. Claude's app token (1 h, D52) is copied into the VM and renewed while it runs; adding it on the way out (TLS interception) isn't worth building. Tools from the repo's flake (D79). The platform profile clones the platform repo, and an app repo only when asked (`--app <repo>`); Azure for it waits for Phase 3 | msg 2026-11-21 |
| D82 | **Phase 3 builds dev only, and everything dev needs except External ID**: network, AKS, Postgres, the identities those need, start and destroy. External ID's app registrations move to Phase 6 (auth), prod to Phase 6b | msg 2026-11-21 |
| D83 | **One region: Sweden Central**, where `shared`, ACR and the state already are. Norway East was preferred, but our subscription is offered no ARM sizes there (`az vm list-skus`, 2026-11-21), though Azure sells them; revisit if that changes | msg 2026-11-21 |
| D84 | **AKS: plain and cheap.** Free tier, one node pool of 2 × `Standard_D2ps_v7` (ARM, D56: in Sweden Central our subscription isn't offered `v6` and has no vCPU quota for `v5`; `v7` is 0.127 USD/h; the backend's two instances on different nodes, D57), Azure CNI overlay with Cilium, workload identity, Entra RBAC, no local accounts, a public API server with Entra-only sign-in (CI's runners have changing addresses). Not AKS Automatic: paid tier and 3 system nodes | msg 2026-11-21 |
| D85 | **Postgres: Flexible Server 18 on Burstable B1ms**, private access only (delegated subnet, private DNS zone), Entra-only authentication, rebuilt with each start (seed data, D45). Its Entra admin is a managed identity (`db-admin`); the roles are created by a Kubernetes Job in AKS, run as that identity by Pulumi's bootstrap, from the same `database.sql` as locally (Entra principals instead of passwords), since nothing outside the VNet can reach the database | msg 2026-11-21 |
| D86 | **The strictest network the budget allows.** No public IPs on nodes, outbound through AKS's load balancer; NSGs on both subnets (the database accepts only 5432 from the node subnet; the nodes only 80/443 to the gateway's static IP from the internet); inside the cluster the same default-deny Cilium policies as locally. Outbound filtering by Cilium policy per component, not Azure Firewall (about 9,000 NOK a month) | msg 2026-11-21 |
| D87 | **Database access from a developer's machine without Bastion, Teleport-style** (Phase 4): a `db-proxy` component in the cluster and `platform db <env>`, which opens a port-forward to it and prints the URL with the person's Entra token as the password (for nvim DBUI and the like). All through Entra: the port-forward is an Azure RBAC role on that one pod for the Entra groups, and the group decides the database role (D75) | msg 2026-11-21 |
| D88 | **Every developer gets a Claude agent, and it mirrors its developer's reach, read-only** (amends D41; AGENTS.md §3): an infra developer's Claude gets Azure Reader, AKS RBAC Reader and the database read-only; an app developer's Claude the database read-only (through `platform db`) and telemetry. Claude in the sandbox is for development only: the local cluster, and read-only Azure through a 1 h token copied in and renewed like GitHub's (D81). Claude's cloud database MCP comes with Phase 5 (D58) | msg 2026-11-21 |
| D89 | **`shared` also holds** the telemetry storage now (one account, containers for Loki and Tempo, 14-day lifecycle, no public access; Phase 5 adds the identities that write to it) and a **budget alert** of 500 NOK a month (50 %, 80 %, 100 %, to the alert list, D40). No new Key Vault until a secret needs one (dev needs none: Entra workload identities) | msg 2026-11-21 |
| D90 | **Pulumi tests, lazily**: unit tests for our own logic (what `people.yaml`, `platform.yaml` and the environment files turn into), not mocks of every Azure resource; `preview` on every PR; the done-when access tests as a script against the real dev | msg 2026-11-21 |
| D91 | **Every developer gets a Claude agent, derived from `access/people.yaml`, and brings their own Claude (BYOC)** (refines D41, D88): one `<name>-agent` Entra account per person, no separate list. Agents of `infra-developers` are in `platform-agents` (Azure and the cluster read-only, the database read-only); agents of `app-developers` in a new `app-agents` (the database read-only and telemetry; no Azure or cluster beyond `db-proxy`). The Claude subscription is each developer's own and isn't tied to Entra: a Claude's rights come only from its agent identity and the shared `ulberg-claude` app | msg 2026-11-22 |
| D92 | **The database's line between the platform and the app.** The platform (`database/`, the same locally and in Azure) owns: the database, the roles and who signs in as them, an empty schema owned by the migrator with default privileges (what the migrator creates, the app may read and write, readers may read), access rules and observability. The app owns everything in its schema through migrations run as `<db>_migrator`: tables, indexes, seed data, per-app tuning. In Azure a migrator identity and a migration Job (Argo CD, before each deployment) come with Phase 4. How we'd run the database once data matters (backups, HA, replicas, multi-region): the platform repo's `docs/database-operations.md` | msg 2026-11-22 |
| D93 | **The database's security and monitoring are planned work, not "later if required".** Security now (Phase 3): `pgaudit` (role and privilege changes, DDL, failed sign-ins) and connection logging, and Microsoft Defender for open-source relational databases on the server (about 15 USD/month per server, by the hour). Monitoring and alerts with Phase 5: the server's metrics, logs and Defender's findings in Grafana, with alerts on storage, connections, long queries, failed sign-ins, replication lag, failed backups or drills, and every Defender alert. Customer-managed keys only if compliance requires them | msg 2026-11-22 |
| D94 | **Tooling and infrastructure belong to the platform, in every repo; the platform repo is open to app leads.** In app repos, CODEOWNERS gives `.github/`, `.claude/`, `.mcp.json`, `Makefile` and `flake.nix` to `platform-leads` alone (after `*` for the app's leads; the last matching line wins). In the platform repo, `platform-leads` own everything (incl. `scripts/`, `sandbox/`, `cli/`). App leads, platform engineers (`infra-developers`) and platform leads may contribute to the platform repo (branches, PRs); only `platform-leads` approve. Generated and checked by the `access` stack; may be loosened later | msg 2026-11-22 |

## 4. Sign-off and bootstrap

### 4.1 Database access path for infra devs (D33)

The DB only accepts traffic from backend pods, so infra devs and their Claude reach it through a **`db-access` proxy pod** in its own namespace:
- only `infra-developers` get Kubernetes RBAC to `port-forward` to it;
- only that pod and the backend may reach Postgres;
- Postgres still requires the user's own Entra token, mapped to the read-only role;
- the AKS audit log records every port-forward.

Teleport was considered (it's in the reference) but needs its paid Enterprise edition for Entra SSO. Without it there is no session recording; `pgaudit` (future work) covers that.

### 4.2 Sign-off

**To sign off**, confirm the phase order in §10. Then I start Phase 0. Creating the two public GitHub repos and pushing to them is the first outward-facing step, and I'll do it as soon as you sign off.

**Azure bootstrap steps for you** (they need your payment details and Global Admin rights; I'll write them up as a checklist and a script in Phase 0):
1. Create an Azure account. This creates your workforce tenant + a pay-as-you-go subscription.
2. Set a budget alert (e.g. USD 200/month).
3. Buy Entra ID P2 licenses for the infra developers (one, for you, to start with).
4. Create 2 emergency-access accounts with FIDO2 keys, kept offline.
5. Create the Entra External ID tenant (for app users) and link it to the subscription.
6. Run the bootstrap script: Pulumi state storage + CI identity with GitHub OIDC trust.

Everything after that, including PIM settings and Conditional Access, is Pulumi.

## 5. Identity and access

### 5.1 Access matrix

| Capability | Infra dev (human) | Infra dev's Claude | App dev (human) | App dev's Claude | App users / their agents |
|---|---|---|---|---|---|
| Change infra | Approve PR → CI applies | Writes code, opens PRs; never applies | ✗ | ✗ | ✗ |
| Merge into a repo | If listed as that repo's lead | ✗ (only opens PRs) | If listed as that repo's lead | ✗ | ✗ |
| Release an app to prod (approve its release PR) | If listed as that app repo's lead | ✗ | If listed as that app repo's lead | ✗ | ✗ |
| Azure resources (read) | Reader | Reader | ✗ | ✗ | ✗ |
| Kubernetes (read: pods, events, describe) | Read-only RBAC | Read-only RBAC | ✗ | ✗ | ✗ |
| Database | Read-only role (dev) | Read-only role (dev) | ✗ | ✗ | Own/granted rows via the app |
| Infra logs, audit logs, cluster metrics | ✓ | ✓ | ✗ | ✗ | ✗ |
| App logs, app metrics, app traces | ✓ | ✓ | ✓ | ✓ | ✗ |
| Use the app (REST / MCP) | as a normal user | as a normal user | as a normal user | as a normal user | ✓ |

How it's enforced:
- **Azure / AKS / Postgres**: Entra RBAC on the groups from §2.2. AKS local accounts disabled.
- **Grafana (OSS)**: two organizations, because Grafana OSS has no per-data-source permissions.
  - `platform`: all data sources, for `infra-developers`.
  - `app`: the app tenant in Loki and Tempo + app metrics in Prometheus, for `app-developers`.
  Org membership comes from Entra group → org mapping.
- **Loki / Tempo multi-tenancy**: app and infra telemetry go to separate tenants, so the `app` org can't query infra data even by accident.
- **Claude** uses its own agent account (D41), never the developer's login: read roles only and never PIM-eligible, so a developer's PIM activation never gives Claude write access. Through the Grafana MCP server (`mcp-grafana`). Infra devs' Claude can also use `az` and `kubectl` with the read-only roles. Group membership decides what each Claude sees. No secrets on disk.

### 5.2 App users and their agents
- Users sign in through Entra External ID. MCP clients get a delegated token for that user, so they get the same permissions as the user.
- **Only verified emails count.** Permissions are looked up by the `email` claim, and that claim must be verified. Otherwise someone could sign up as `alice@…` and pick up Alice's pending shares.
- Scopes: `todos.read`, `todos.write`, `todos.share`. Delete and share tools are marked destructive, audit-logged with the client id, and rate-limited per client.
- Shared content from other users is marked as untrusted in MCP tool results (prompt-injection defense).

## 6. Sharing model (Unix/Linux permission model)

### 6.1 Permission bits

Each grant is a set of independent bits on one todo for one email, just like `rwx` on a file:

| Bit | Allows | Unix analogy |
|---|---|---|
| `read` | see the todo's content | `r` |
| `write` | change the todo's content | `w` (works without `r`: write-only is allowed; it shows in lists as `{ id, permissions }` only) |
| `share` | give *other* emails a subset of your own bits | (no direct analogy; the closest is a capability you can delegate) |
| `admin:share_creator` | change or revoke **anyone's** grants on this todo | owner of the file (`chmod`) |

- The **creator** of a todo is its owner. The owner holds `read`, `write`, `share` and `admin:share_creator`.
- **Delete** follows Unix: you remove a file through *its directory*, and the todo "lives" in its owner's list, so **only the owner can delete it**. In v1 grantees can't remove their own access ("leave" is future work).
- `admin:share_creator` **can't be granted through the API or MCP.** Giving it to anyone else is a manual database change by a developer (the "root" in this analogy).

### 6.2 Rules (a pure domain function, property-tested)
- A user may give a set `Q` to an email only if `share ∈ P_grantor`, `Q ⊆ P_grantor` (excluding `admin:share_creator`) and `Q ≠ ∅`.
  - Example: holding `{share, read}` lets you give `{read}`, `{share}` or `{share, read}`, never `{write}`.
- Grants only ever **add** bits. A `share` holder can't remove or lower anyone's bits; only an `admin:share_creator` holder can.
- **No cascade.** Revoking someone's bits doesn't touch the grants they made earlier.
- Effective bits = union of all active grants to that email.
- Property tests prove that no sequence of grants ever gives anyone more than its grantor held.
- Storage: `todo_grant(todo_id, grantee_email, can_read, can_write, can_share, granted_by, created_at, revoked_at)`; owner/admin is a separate `todo_admin(todo_id, user_id)` table.

### 6.3 No user enumeration
- Granting to an email always returns the **same response** (`204`, same body, same timing) whether the email belongs to a user or not.
- Grants are stored by normalized email and activate on that email's first verified sign-in.
- There is no endpoint or MCP tool that lists or searches users.
- Only `admin:share_creator` holders can see the grant list of a todo, and it shows only emails someone already typed in.
- Share endpoints are rate-limited per user and per client, so emails can't be probed in bulk.

## 7. Privileged access: Entra P2 + PIM (D23)

**Background.** PIM (Privileged Identity Management, part of Entra ID P2) replaces *standing* privileged access with *eligible* access. You activate a role for a limited time, with MFA and a justification; every activation is audit-logged and expires automatically. P2 includes P1, so we also get Conditional Access. Licenses are per user (roughly USD 9/user/month, to verify); only infra developers need one.

**Design**: every rule below is defined in Pulumi (F#) in the platform repo, generated from `access/people.yaml`.

| Access | Type | Who | Activation rules |
|---|---|---|---|
| Read (Azure Reader, AKS read RBAC, DB read-only, Grafana `platform` org) | Standing | `infra-developers` | Covered by the Conditional Access rules below (MFA) |
| **Break-glass write** (`infra-break-glass` group → Contributor on the app resource groups, AKS RBAC Cluster Admin, DB admin role) | **PIM for Groups, eligible only** | `infra-developers` | Max 2 h, MFA, written justification, notification to all platform leads. **Approval by another platform lead once there are ≥2 leads** (Entra doesn't allow approving your own request, so with one lead it's MFA + justification + alert). |
| Entra admin roles (Global Admin, Privileged Role Admin, Application Admin) | **PIM eligible, no permanent assignments** | Platform leads | Max 1 h, phishing-resistant MFA, justification |
| Emergency access | 2 cloud-only accounts, FIDO2, stored offline | You | Excluded from Conditional Access; any sign-in alerts all leads |

**Conditional Access** (replaces security defaults; the emergency accounts are excluded from every policy):
- CA01: MFA for everyone in the workforce tenant;
- CA02: block legacy authentication;
- CA03: phishing-resistant MFA for Azure management, for members (not guests: the owner Microsoft account can't register a passkey in the tenant and would lose billing; guests have no other Azure access, and CA01 still requires MFA);
- CA04: sign-in risk policies (Identity Protection, a P2 feature) for infra developers;
- CA05: a fresh phishing-resistant sign-in for **every** PIM activation. An authentication context with sign-in frequency "every time", which the PIM policies of Global Admin, `platform-write` and `platform-admin` require. Without it, PIM accepts the MFA from an hours-old sign-in (D39);
- CA06: Azure portal, CLI and ARM for infra developers: sign in again every 4 h, no persistent browser session (D39);
- CA07: every other app for infra and app developers: sign in again every 12 h (D39).
- CA08: infra developers never stay signed in, on all apps (Entra only allows that control on policies for all apps, so it can't be part of CA06) (D39);
- CA09: guests and external users (such as the owner Microsoft account) sign in again every 12 h (D39).

**Alerts** (D40): PIM emails every activation and every new eligible or active assignment of a privileged role to the platform alert list. Until Phase 5 that is the only alert; Phase 5 adds the log-based ones.

**Why this matters.** Normal writes still go only through approved PR → CI (D18). PIM covers the moments when CI or ArgoCD itself is broken, and it makes even those moments time-boxed, justified and visible.

**Limits to verify in Phase 3**:
- PIM and Conditional Access apply to the **workforce tenant**. App users in the External ID tenant are separate, and that tenant's admin roles are kept to as few people as possible; I'll check which PIM features External ID supports.
- Buying the license is a manual bootstrap step (§4).
- Pulumi support for PIM resources (`azuread` PIM-for-Groups eligibility/policy, `azure-native` role management policies) will be checked in a short spike. Anything Pulumi can't manage gets a documented script that runs in CI.

## 8. Deferred components (add when the trigger happens)

| Component | Add when |
|---|---|
| **Valkey** | We need shared state across pods: distributed rate limiting (e.g. the share endpoint across ≥2 pods), a permission cache, or MCP session state. MCP starts in stateless mode. |
| **Service mesh (Linkerd)** | We get real service-to-service calls between our own pods, or a compliance need for encryption inside the cluster. Accepted until then: the hops ingress → backend and app → OTel Collector are plaintext inside the cluster, but restricted by network policies. |
| **Keycloak** | Entra External ID can't do something we need. The most likely case is MCP clients that require Dynamic Client Registration, which Entra doesn't support; Keycloak would then broker in front of External ID. |

## 9. Cost and network security (estimate, verify in Phase 3)

### 9.1 Network: the database is never reachable from the internet
- PostgreSQL Flexible Server with **VNet integration** (delegated subnet, *private access*). It has **no public endpoint at all**; this isn't a firewall rule that could be opened later. It is resolved through a private DNS zone.
- **NSG on the DB subnet**: inbound 5432 only from the AKS node subnet; everything else denied.
- **Pod level (Cilium network policies)**: default-deny egress in every namespace. Only pods labeled `app=todo-backend` (rendered from `database: todo` in `platform.yaml`) may reach the DB's private IP on 5432. Frontend, observability and ArgoCD pods can't.

  *Why both layers*: with Azure CNI overlay, pod traffic leaves with the node's IP, so the NSG alone can't tell backend pods from other pods. The Cilium policy does that part. A dedicated node pool for the backend would let the NSG do it too, but it costs an extra node; I'll note it as an option.
- **Identity**: the DB accepts only identities on its allowlist (§9.2), so even reachable traffic has to authenticate.
- Same pattern for Key Vault and ACR (private endpoints or firewall + trusted services, depending on SKU cost; decided in Phase 3). Grafana/ArgoCD/Kubernetes API access is via Entra SSO.

### 9.2 Database user security (option B chosen, D24)

| Option | How | Pros / cons |
|---|---|---|
| A. Password user | One app user, password in Key Vault, CSI-mounted | Simple; a password exists and must be rotated |
| **B. Entra-only, split identities (chosen)** | Password auth disabled. Workload identities: `todo-migrator` (DDL on schema `todo`, used only by the migration Job), `todo-app` (`SELECT/INSERT/UPDATE/DELETE` on `todo` tables only), `infra-readers` group (`SELECT` only). Short-lived tokens. | No secrets anywhere, least privilege, every connection is attributable to an identity. Locally we use password users with the same three roles, so the role model is still tested. |
| C. B + column-level grants + `pgaudit` | Same as B, plus audit logging of all statements by human/agent identities | Strongest; `pgaudit` adds log volume. A good "future work" item. |

### 9.3 Monthly estimate (one `dev` environment)

| Item | Choice | ~USD/month |
|---|---|---|
| AKS control plane | Free tier | 0 |
| Nodes | 2 × ARM `Standard_D2ps_v6` (2 vCPU / 8 GB, D56; price to verify in Phase 3) | ~120 |
| PostgreSQL Flexible | Burstable `B1ms`, 32 GB, private access | ~15–20 |
| Ingress | Standard LB (comes with AKS) + Envoy Gateway in-cluster + cert-manager/Let's Encrypt | ~20 |
| Logs/traces storage | Loki + Tempo on Azure Blob, short retention | ~5–10 |
| ACR | Basic | ~5 |
| Key Vault, External ID (free ≤ 50k MAU), VNet, private DNS | | ~1–3 |
| Entra ID P2 | 1 license (you); +1 per extra infra developer | ~9 |
| **Total** | | **~175–190** |

**With `prod` (D38)**, the cluster, Postgres, ingress, storage and network costs repeat, so the total is about **USD 350/month** if both ran all the time. ACR, Entra and P2 licences are shared.

**On demand (D45)**, they don't: idle cost is the persistent part (state, ACR, Key Vault, telemetry storage, two static IPs, P2) at roughly **USD 30/month**, plus about USD 6 per environment-day actually running.

Savings levers: stop AKS + Postgres between demos (both environments). Trade-off kept: no managed WAF (Application Gateway WAF costs more than the rest combined). Instead we use rate limiting, strict routing, security headers and app-level parsing.

## 10. Phases (in order; each ends green in CI with no broken windows)

### Phase 0: Ways of working + bootstrap
- Done: `AGENTS.md`, `CLAUDE.md`, `.prompts/`, `PLAN.md`.
- `git init` both repos (`reference/` in `.gitignore`), push to `Ulberg-Org/todo` and `Ulberg-Org/platform`.
- `.editorconfig`, Fantomas, Prettier/ESLint (all enforced in CI only, no git hooks), Claude Code prompt-capture hooks, gitleaks + Dependabot.
- Azure bootstrap checklist + script (§4). Pulumi "bootstrap" stack (F#): state storage, CI identity, `access/people.yaml` → Entra groups, PIM eligibility + policies, Conditional Access, GitHub rulesets, CODEOWNERS.
- **Done when** both repos are protected by rules defined in code, and the CI identity can run `pulumi preview`.
- ✅ **Phase 0 done 2026-11-09.** Status: bootstrap done; `platform/stacks/access` merged and applied (PR #2) (groups, PIM for Groups, Reader for `platform-read`, rulesets, environments, CODEOWNERS check), preview runs clean. Steps after that:
  1. ✅ **Conditional Access on** (done 2026-11-09, platform PRs #9–#12). CA01–CA09 enforced; security defaults off. PIM activation of Global Admin, `platform-write` and `platform-admin` requires authentication context `c1` (CA05: fresh phishing-resistant sign-in), set by `pim-policy-rules.sh` because the `azuread` provider can't switch a group policy from MFA to a context (Entra refuses both at once). Every rule is read back on apply.
  2. ✅ **Remove standing admin access** (done 2026-11-09, platform PRs #3–#8). Global Admin is PIM-only (1 h, MFA, justification; no permanent active assignment) and was tested; the admin account has Reader day to day; the owner Microsoft account keeps only billing (D35); subscription Owner is held only by the two emergency accounts (D36); every activation and new assignment emails the alert list (D40). `check_standing_access.sh` fails the `pulumi preview` check (every PR, main and nightly) if standing Global Admin, Privileged Role Administrator, Owner or User Access Administrator appears outside PIM and the emergency accounts.

### Phase 0b: Agent identities (Claude acts as itself, on behalf of a developer)
- ✅ **Azure** (D41, platform PRs #13–#15): agent account per developer, read roles only, P2 by group, smoke test + nightly check.
- ✅ **Tenant lockdown** (D42, platform PRs #16–#18): members can't create groups (security or Microsoft 365), apps or tenants, consent to apps or invite guests; the nightly check verifies the settings and fails on any group or app made outside Pulumi. Lesson: Entra keeps a session's rights from when it was signed in, so test a permission change with a fresh sign-in.
- **GitHub organization** (D43), in this order, so CI never loses access:
  1. You create the organization (Free).
  2. Platform PR: the `access` stack targets the org: org settings (2FA required, base permission none, members can't create repos, fine-grained tokens need approval and expire, classic tokens off), teams from `people.yaml` (leads, infra developers, app developers) with repo access per team, `CODEOWNERS` naming teams, environment reviewers as teams. Federated credentials for the new OIDC subjects (org ID) are added *next to* the old ones.
  3. You transfer `platform` and `todo` to the org (rulesets, environments and secrets move with them; old URLs redirect), then right after the three GitHub Apps (`ulberg-platform-admin`, `-reader`, `-pr-bot`): they are private, so they can only be installed on their owner, and you install them on the org. CI's GitHub-token steps fail for those few minutes. An apply verifies everything; then the old federated credentials are removed.
  4. ✅ Org, teams, memberships, org defaults, team `CODEOWNERS` (platform PRs #19–#21).
  5. ✅ The org-owned **Claude app** (D52, platform PRs #22–#23): created from a manifest, private key imported into Key Vault as a non-exportable key that only `platform-agents` may sign with; `scripts/claude-github-token.sh` (Key Vault signs the JWT → 1 h installation token → a separate `gh` profile) and the hook's GitHub part (`gh` and `git push` only with Claude's profile). Test: Claude opens a PR as `ulberg-claude[bot]`; you approve and merge.
  6. ✅ **Leads control platform and app** (platform PRs #25–#28): remove the D34 bypass, so `main` changes only through PRs approved by the repo's `<repo>-leads` team.
- ✅ **Agent sign-in reviewed** (platform PR "Authentication Administrator via PIM"): routine renewals need only the agent's passkey; onboarding or a lost passkey needs a Temporary Access Pass, issued with **Authentication Administrator** (PIM, 1 h, fresh passkey, alert), never Global Admin. Nobody holds it standing (nightly check). AGENTS.md §4 says so.
- **Done when** Claude reads Azure and opens GitHub PRs only with its own, attributed, read-only/PR-only identity, and every agent permission is defined in code and checked nightly.

### Phase 1: Walking skeleton backend + local tier 1 + CI
- ✅ **Done 2026-11-19** (todo #7, #9, #11; platform #30–#33). The first image `todo/backend:55fb0bd` is in ACR, locked, signed and with its SBOM attestation.
- **First, the `shared` stack and ACR** (D53, D54): stack order file, `up`/teardown workflows, ACR, todo's push identity.
- **Layout:** the reference app's structure inside `backend/`: `Todo.slnx`, `src/Core/domain/` (pure domain), `src/Database/` (roles now, migrations later), `src/TodoBackend/` (`Program.fs`, `Telemetry.fs`, `api/`, `storage/`, later `service/` and `mcp/`), `test/TodoBackend.Test/`. Later `frontend/`; `platform.yaml` at the root.
- **Backend:** F# Oxpecker (.NET 11, D59): `/health/live`, `/health/ready` (also checks the database as the app role), `/version` (commit SHA, build time, image tag).
- **Telemetry:** OpenTelemetry traces/metrics/logs over OTLP; JSON logs carrying `trace_id`; `ProblemDetails` errors that include `trace_id`; no PII.
- **Database:** Postgres 18 with the three roles (§9.2) from day one, no tables yet.
- **Local tier 1** (§11): `make up` / `make down`, documented in README and AGENTS.md. Two instances behind Envoy (D57). `mcp-grafana` and a read-only Postgres MCP server for Claude.
- **Container:** chiseled, non-root, read-only file system, arm64 (D56).
- **App CI** on ARM runners: format → build (warnings as errors) → tests (incl. Testcontainers) → `platform.yaml` schema check → image → Trivy → SBOM; on `main` also push, sign and attest (D54, D55). Both new jobs are required checks.
- **Deferred:** the mock OIDC server (to Phase 6, when the first protected endpoint appears).
- **Done when** CI is green, and one request through Envoy shows locally as trace + log + metric including the database hop, readable by Claude through `mcp-grafana`.

### Phase 2: Local platform (kind) and the renderer
- **First a thin end-to-end slice**, then extend it into the cloud (Phase 3+):
  - the platform CLI (D63) creates a kind cluster with Kubernetes 1.37, Cilium, Envoy Gateway, ArgoCD and the telemetry stack (Helm, same charts as Azure);
  - the renderer (D60, D62) turns todo's `platform.yaml` into the backend's Kubernetes YAML and gateway route; ArgoCD runs it as a plugin and deploys from the local checkout (D67, D68);
  - two backend pods with hot reload (D61), Postgres outside the cluster;
  - full resource metrics (D64).
- **Then:** `make test-platform` (D66, done: 12 checks, and a cluster-wide baseline rule found by them), Dependabot cooldown and hash pins in both repos (D65), and Phase 1's compose files deleted (done).
- **Done: infrastructure signals into the same stream (D71),** each readable by the developer and by Claude (platform #47; the image cache stays deferred):
  - the gateway's own spans and access logs (with the trace ID), so a trace starts at the entrance;
  - every pod's logs and the Kubernetes events (scheduling failures, OOM kills, failed probes, image pulls) in Loki;
  - network decisions from Cilium's Hubble: denied flows in Loki, drop and flow metrics in Prometheus, and alerts on a rise in denials or above thresholds. Cloud parity (Hubble flows in AKS need ACNS, D70) decided in Phase 3;
  - Postgres metrics and query statistics (D64): the `todo_monitor` role (`pg_monitor`), `pg_stat_statements`; in Azure, Azure Monitor metrics and Query Store;
  - dashboards and alert rules as code (D71): per service request rate, errors and latency; per pod CPU and memory against requests and limits; Argo CD sync health; alerts on error rate, restarts, OOM kills, out-of-sync applications and network denials;
  - audit logs in Loki: the Kubernetes API's (who changed what in the cluster), Argo CD's and Grafana's;
  - read-only Kubernetes access for infra developers' Claude (a Kubernetes MCP server with a view-only role); an app developer's Claude keeps only the telemetry (AGENTS.md §3);
  - the local image cache (D72), to make `make up` faster (deferred: after the hash pins of D65).
- **Done (D73, the part that needs no authentication):** health and version on the management port, which only the probes reach; no gateway route until the public API has endpoints.
- **Done when** `make up` works from a fresh cluster, a request through the gateway (http://localhost:9875) shows in Grafana (http://localhost:9876) as trace + log + metric including the database step, every pod's CPU and memory are visible, and `make test-platform` passes. *Amended 2026-11-21:* while the public API has no endpoints (D73), the gateway hop and the backend → database hop are proven separately: a gateway span whose parent is the request's `traceparent`, and backend traces with the database step, logs and metrics. **Done 2026-11-21.**

### Phase 2.5: A sandbox for Claude (D76–D79)
- `flake.nix` in todo and in the platform repo (D79); the sandbox images built from them.
- A Lima VM per profile (D77, D81), confined by `srt`: the repo picks the profile, `access/people.yaml` allows it. A host-side launcher starts it, mints Claude's tokens and copies them in (D78). First a spike: `make up` and `make test-platform` in the VM, and the confinement holds on the developer's macOS.
- Network deny by default with the allowlist (D78), enforced on the host (D81); none of the developer's ports. Docker and the cluster inside the VM.
- **TODO (prod, Phase 6b):** the database's operations as written in the platform repo's `docs/database-operations.md`: geo-redundant backups and long-term ones outside the server's life, restore drills, zone-redundant HA, read replicas and multi-region when needed, PgBouncer, maintenance window, alerts.
- **TODO:** Postgres 19, locally and in Azure together (D56), once Azure Database for PostgreSQL offers it (it offers up to 18 on 2026-11-21).
- **TODO:** `dotnet test` exits 0 without running anything (Expecto has no test adapter; CI and the README use `dotnet run --project backend/test/TodoBackend.Test`). Add the adapter (`YoloDev.Expecto.TestSdk`, after the supply-chain check) or make `dotnet test` fail loudly. Found by Claude in the sandbox's end-to-end test.
- **TODO (every quarter, or on each Docker Sandboxes release note):** check whether Docker Sandboxes now fits (its kernel runs Cilium: policy routing, `NET_CLS_BPF`; test: `ip rule` works and `make up` passes inside). If so, move back: it adds credentials on the way out and filters by host name, which our setup gives up (D81).
- **Done when** Claude completes one task end to end inside the sandbox (code, `make up`, tests, reading its effect through its MCPs, a PR) in each profile, and from inside these fail, even as root: reading the developer's home folder or `~/.ssh`, `curl` to a host not on the allowlist or to the developer's machine or LAN, pushing with the developer's GitHub account, and asking for the platform profile without being in `infra-developers`. **Done 2026-11-21:** the app profile's task is todo #28 (README; `make up`, tests, the Grafana and Postgres MCPs, a verified PR), the platform profile's is platform #56 (a memory limit found and checked through the Kubernetes MCP), and `sandbox/test` passes 10/10.

### Phase 3: Azure foundation for dev (Pulumi F#, applied only by CI after approval; D82–D90)
- `shared` (persistent, Sweden Central, D83): ACR, a static IP and DNS label per environment, telemetry storage, the budget alert (D89).
- dev, on demand (D45): a `start` workflow (approval) runs `pulumi up`; a `destroy` workflow every 15 minutes removes anything started more than 4 hours ago. Its own resource group; CI's apply identity gets what it needs scoped to it and `shared`.
- Network (D86): VNet `10.20.0.0/16`, node subnet `/23`, database subnet `10.20.2.0/24` (D70), NSGs, private DNS for Postgres.
- AKS (D84) and Postgres with its roles (D85). Access, all through Entra groups: `platform-read` and the agents AKS RBAC Reader, `platform-write` (PIM) AKS RBAC Cluster Admin, app developers nothing in the cluster; the database roles follow the groups (D75). Every developer's Claude agent mirrors its developer, read-only (D88).
- The sandbox's read-only Azure token (D88).
- The database's security (D93): `pgaudit` and connection logging, Microsoft Defender for open-source relational databases.
- Ownership (D94): CODEOWNERS gives tooling and infrastructure paths to `platform-leads` in app repos; app leads and platform engineers may contribute to the platform repo.
- Tests per D90.
- **Done when** everything exists, `start` and `destroy` work, and the access tests show: app developers can change nothing; developers and every Claude can read but not write; the database isn't reachable from outside the VNet; its roles map to the Entra groups.

### Phase 4: GitOps + cluster baseline on Azure
- `db-proxy` and `platform db <env>` (D87).
- The app's database migrations in Azure (D92): a migrator workload identity mapped to `<db>_migrator`, and a migration Job Argo CD runs before each deployment.
- ArgoCD installed by Pulumi with the same root Application as locally (D67), the renderer image published for its plugin (D68), Entra SSO, admin login disabled; Argo CD's GitHub access; release image tags per environment in `apps/<app>.yaml`; the `platform.yaml` PR flow (§2.1 steps 1–3), signed images + admission policy.
- The release-PR flow (§2.1 step 4): the pr-bot opens it, the policy check runs, and the path-based `CODEOWNERS` (app leads own `apps/<app>/prod/`) is generated and checked by the `access` stack. Until prod exists (Phase 6b), nothing syncs `apps/<app>/prod/`.
- Backend ≥2 replicas, HPA, PDB, topology spread; default-deny network policies (identical to tier 2).
- **Done when** an app-repo merge reaches the cluster with no human touching the cluster.

### Phase 5: Observability platform on Azure
- OTel Collector → Loki / Prometheus / Tempo (Blob) → Grafana (Entra SSO, two orgs, tenants).
- Infra signals: the same as locally (Phase 2 TODOs), from Azure's sources where they differ: kube-state-metrics, node metrics, AKS audit logs, Postgres metrics/logs and Query Store, Envoy access logs, Argo CD, network flows (if ACNS).
- Dashboards + alerts as code; `mcp-grafana` setup documented for both groups.
- Entra sign-in and audit logs → alerts (D40): any emergency-account sign-in, role or group-membership changes not made by the apply identity, changes to PIM policies or Conditional Access.
- The database's monitoring and alerts (D93): its metrics, logs (slow queries, pgaudit, connections) and Defender's findings in Grafana, with the alerts in the platform repo's `docs/database-operations.md`.
- **Done when** each group's Claude sees exactly its column of §5.1, proven by tests.

### Phase 6: Edge + application security
- TLS, HSTS, CSP, security headers, strict CORS, rate limiting.
- External ID JWT validation (issuer, audience, scopes, **verified email**), email → user → permissions (`bindPermissionsByBearer` pattern), deny by default.
- Users table, migrations Job.
- **APIs by audience (D73):** `platform.yaml` declares an app's APIs (`public`, `internal`, `external`, later `private`) instead of free routes. The renderer gives each its route (hostname), port, gateway token check and network rule. The app gets one endpoint group, port and auth middleware per API, plus one OpenAPI document per API generated at build time; tests check that every endpoint answers only on its own API. `/version` moves to the internal API.
- **External API keys (D73):** API users (username and password, exchanged for short-lived tokens) owned by a customer and acting only on that customer's data, like the reference app's delegates.
- **Status page (D74):** choose the service, then synthetic checks of the public and external APIs and the alerts feed it.
- **Done when** the security suite (authz matrix, CORS, headers, anonymous/wrong-scope rejection) passes locally and against dev.

### Phase 6b: `prod` environment + releases (D37, D38)
- ~~First: remove the solo-dev exception (D34)~~: done in Phase 0b.
- A `prod` Pulumi stack from the same code: its own AKS cluster, Postgres, VNet and Key Vault. A `prod` GitHub environment whose reviewers are the platform leads, and an apply identity federated to it.
- ArgoCD syncs `apps/<app>/prod/` into the prod cluster. Post-deploy smoke + access tests run against both environments.
- **Done when** an app change goes PR → app-lead approval → dev (automatic) → release PR → app-lead approval → prod, with no human touching either cluster, and an app developer who isn't a lead can't release.

### Phase 7: Frontend shell
- Vite + React + TS, TanStack Router (CSR) + Query, shadcn/ui, MSAL.
- `openapi-typescript` + `openapi-fetch` with auth + `traceparent` middleware, from the internal API's OpenAPI document (D73); each API's document is committed and checked for drift in CI.
- Browser telemetry (OTel web / Faro: page loads, errors, web vitals) joined to the backend traces, in the same Grafana (D71).
- Chrome DevTools MCP for Claude, so it can drive and inspect the UI end to end locally.
- **Done when** login works and a "who am I" page shows one trace browser → API → DB.

### Phase 8: Agent shell (MCP)
- `/mcp` per audience (D73): a customer's agent through the external API, our own agents through the internal one.
- Stateless `/mcp`, protected-resource metadata, pre-registered client(s), scopes, audit log, per-client rate limit.
- REST/MCP parity check in CI; first tool `whoami`.
- **Done when** the feature template (domain → DBO → use case → REST → MCP → FE hook → tests → dashboard panel) is documented in `AGENTS.md`.

### Phase 9: Todo features
- Create, list, get, edit, delete, each through the template.
- Continuous profiling (Pyroscope): CPU and memory profiles per service, linked from slow spans, in the same Grafana (D71).
- **Done when** all five operations work for a human (FE) and an agent (MCP) under the same permissions, with tests and a dashboard panel for each.

### Phase 10: Sharing
- An audit trail of every permission decision and change (who, what, on whose behalf: a person or their Claude), in Loki and queryable by both (D71).
- Grant, admin change/revoke, grant list for admins, pending grants, no-enumeration tests (same response + timing), property tests, untrusted-content marking in MCP.
- **Done when** the property tests (no escalation), the no-enumeration tests (same response and timing) and the authz matrix all pass, and sharing works the same from the FE and from MCP.

## 11. Local development

| Tier | For | What runs | Reliable for |
|---|---|---|---|
| **1: inner loop** | App devs, daily | `docker compose`: Postgres (with the same 3 roles as §9.2), `grafana/otel-lgtm`, Envoy as load balancer in front of **two** backend instances (`dotnet watch`, D57), `mcp-grafana` + read-only Postgres MCP for Claude (D58); from Phase 6 a **mock OIDC server** issuing External-ID-shaped tokens (incl. `email_verified`). Frontend Vite | All app code, auth/permissions/sharing, telemetry, MCP (Claude Code connects to local `/mcp`) |
| **2: local platform** | Infra devs, plus app devs checking a `platform.yaml` change | `kind` + Cilium + Envoy Gateway + ArgoCD + LGTM, the same charts/values as Azure | Rendered manifests, network policies, Pod Security, HPA/PDB, ArgoCD sync, ingress/CORS/headers, Grafana org/tenant split |
| **3: cloud-only** | CI | Pulumi unit tests with mocks; `pulumi preview` against real dev | Azure resources, Entra, NSGs, private DNS/endpoints, workload identity |

**How reliable is tier 2?** Kubernetes-level behavior (network policy enforcement with Cilium, RBAC, ArgoCD, Envoy routing, the observability stack) behaves the same as on AKS, because we run the same components and the same versions. What it can't reproduce:
- Azure networking (NSGs, private DNS, Standard LB);
- workload identity → Entra tokens (locally we use password users / mock OIDC);
- Postgres Flexible specifics.

Those are covered by tier 3 plus a post-deploy smoke + access test suite against dev. So you can do almost all work locally before touching the real environment; only Azure/Entra resource changes need CI.

Tests: unit + property (domain), Testcontainers (storage), in-process API tests, Playwright smoke on tier 1/2, access-matrix tests on tier 2 and dev.

## 12. Patterns from `reference-infra/`

| Reference pattern | What we do with it |
|---|---|
| `azure_ad_fas_group`: per team/env groups `read`, `write`, `admin`, **nested** (admin ∈ write ∈ read) | **Adopt.** `people.yaml` renders `<scope>-read/-write/-admin` Entra groups with the same nesting, so every consumer (Azure RBAC, AKS, Grafana, DB, PIM) maps to only three levels. `write`/`admin` are only reachable through PIM (§7). To verify in Phase 3: nested group claims in AKS tokens. |
| `generate-rbac.py`: RoleBindings generated per namespace × level from group names | **Adopt**, inside the F# `platform.yaml` renderer. No hand-written RBAC. |
| `fas_application/ci-cd`: GitHub OIDC trust scoped to a repo, and to `main` for prod | **Adopt, tighter**: scoped to a GitHub *Environment* with required reviewers (§2.1). |
| `fas_application/service_profile`: one cloud identity per service, bound to its K8s service account, with least-privilege policies derived from declared needs (queues, buckets, secrets) | **Adopt.** It's the same idea as `platform.yaml`: declared needs → workload identity + exactly those role assignments. |
| `.fas/` folder in app repos with raw Kustomize base/overlays + image tag | **Replaced** by the higher-level `platform.yaml`. Raw manifests in app repos would give app devs control over pod specs, which we don't want. |
| External Secrets Operator + per-app `SecretStore` bound to the app's identity | **Adopt** with Azure Key Vault + workload identity. There will be few secrets (DB is Entra-only), but the pattern is in place. |
| cert-manager + ClusterIssuer, external-dns, custom error pages | **Adopt** cert-manager now; external-dns when we add a custom domain. |
| Linkerd service mesh | **Deferred** (D32, §8). |
| Teleport for K8s/DB/SSH access | **Not adopted** (D33): Entra SSO needs the paid Enterprise edition. Entra RBAC + `kubelogin` + the `db-access` proxy cover it. |
| Dependabot reporting / `VULNERABILITYOWNERS` | **Adopt** a light version: Dependabot + GitHub security alerts, with Trivy results in CI. |
| Flux (Lab) | **Not adopted**: we use ArgoCD as agreed. |
| Jenkins, Fluent Bit → Splunk, Skogul, SOC logger | **Not adopted**: GitHub Actions + the OTel/Loki stack cover these. |

## 13. Future work

- **Portability milestone (D49), near the end.** One interactive bootstrap wizard replaces today's checklist, `bootstrap.sh` and `grant-graph-permission.sh`: it does everything that can be automated, stops at each manual step with the account and the exact URL and checks it was done, removes its own standing roles at the end, and can be rerun from any point. Then prove it: a from-scratch run in an empty trial tenant (Azure + P2 trials), with the run log published.
- **MCP connectors for Claude.ai, Claude Desktop and ChatGPT** (D46): needs Dynamic Client Registration, so Keycloak in front of External ID (§8).

- **GitHub Enterprise Managed Users** (upgrade path for D43): GitHub accounts created and removed from Entra (SSO + SCIM), so people and their agents get GitHub access from `people.yaml` with no separate sign-up, and Conditional Access covers GitHub too. About USD 21/user/month; worth it once there is a real team.
- Postgres row-level security as defense in depth (D16).
- `pgaudit` + column-level grants (§9.2 option C).
- "Leave a share": a grantee drops their own access (D27).
- Custom domain.
- Managed WAF, or Coraza in Envoy.
- A2A Agent Card, if the app ever contains its own agent.
- ~~Solo-developer exception (D34)~~: done 2026-11-19, see D34 and D52.
