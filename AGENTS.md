# AGENTS.md

Instructions for every agent (and human) working in this repository. Read this file and `PLAN.md` before doing anything.

## 1. Prompt log (`.prompts/`) — mandatory

All prompts are stored in the `.prompts/` folder. It contains **everything ever written by anyone in this repo**: every user prompt and every final agent reply, from every person and every session. It is the project's long-term context and must be consulted for history before making decisions.

Rules:

- Store **only** user prompts and final replies. Never store chain-of-thought, reasoning, intermediate tool output or tool calls.
- One `.txt` file per message, never edited after it is written (append-only history).
- Path: `.prompts/<YYYY-MM-DD>/<HHMMSS>Z_<author>_<role>.txt`
  - time is UTC, `<author>` is the human who drove the session (lowercase handle), `<role>` is `user` or `assistant`.
- Each file starts with a header, a blank line, then the message verbatim:

  ```
  author: <handle>
  role: user | assistant
  timestamp: <ISO 8601 UTC>
  session: <session id or "unknown">
  ```

- **Claude Code writes the log automatically** with the hooks in `.claude/settings.json` (`.claude/hooks/prompt_log.py`). Don't also write it by hand in a Claude Code session, or the turn gets logged twice.
- Other agents and tools without these hooks must write the files themselves: the user prompt file when they receive a prompt, the assistant file when they give the final reply. If a turn is skipped, add it later instead of leaving a gap.
- CI checks the format (`.github/scripts/check_prompt_log.py`).
- Never put secrets, tokens or credentials in a prompt file. If a prompt contains one, redact it as `[REDACTED]` and tell the user.

## 2. Infrastructure- and AI-first

When developing in this project everything is infrastructure and AI-first. That means that before developing any features we always ensure the proper infrastructure is in place, an example is that we start the whole project by setting up CI/CD, access controls (both in infra and in application) before doing anything else. This includes things like tracing, telemetry, and logs. Also setting up A2A integrations should be a first-class citizen, meaning that we always implement it as an integrated part of each feature, taking into consideration if there are any security concerns by giving agents access to any given functionality. We also want to keep the application clean, so we always fix a problem before moving on to the next task. No broken-windows.

In practice, a change is not done until:

- It builds, is formatted, and all tests pass in CI.
- It is authorized (deny by default) both in infrastructure and in the application.
- It emits traces, metrics and structured logs that let an agent debug it end to end (frontend → backend → database → infrastructure). The trace context (W3C `traceparent`) travels with every hop, including messages and queued work: whoever picks the work up continues the same trace as a real span, never just copies an ID into the logs.
- It is reachable by agents through the agent integration layer (see `PLAN.md`), with the security implications written down in the PR.
- No known warnings, failing checks or TODO-hacks are left behind. If you find a broken window, fix it (or stop and raise it) before continuing.

## 3. Working agreement

- `PLAN.md` is the source of truth for order of work. Do not start a step that is not agreed in `PLAN.md`. Propose changes to the plan instead of silently deviating.
- Ask before making unusual or hard-to-reverse choices. The project owner does not want to be surprised by awkward choices.
- Access is defined as code in the platform repo (`access/people.yaml`). Only infra developers (and their agents, with their own read-only identity, §4) may work on infrastructure, and even they only change it through approved PRs applied by CI. Every developer's agent mirrors its developer's reach, read-only (D88): an app developer's agent uses telemetry and the database read-only, never Azure or Kubernetes beyond that; an infra developer's agent also reads Azure and the cluster. No agent ever writes.
- `reference/` is a read-only example of the architectural patterns (Oxpecker, parse-don't-validate, DTO → domain → DBO, permissions resolved from the JWT email). `reference-infra/` is a read-only example of infrastructure patterns. Copy patterns, not code or business logic. Both folders contain third-party company material: they are git-ignored, must never be committed or pushed, and must never be quoted in `.prompts/`.
- **F# code follows [`docs/fsharp-style.md`](docs/fsharp-style.md)**, in every file: input → domain → action → report, no mutation, no `null`, parse-don't-validate. CI enforces what it can.
- No git pre-commit hooks. Every check (format, lint, tests, secret scanning) runs in CI.

## 4. Claude's identities (D41, D52, D91)

Claude never uses your logins. It has its own identity in Azure and in GitHub, and `.claude/hooks/agent_identity.py` stops it from using yours by mistake (it's a guardrail; the real limits are the identities' rights).

**Azure:** every developer's Claude has its own Entra account, `<name>-agent@<tenant>`, displayed as "Claude agent of <name> (<GitHub handle>)", with you as its manager and employee type `Agent`: everyone in the platform repo's `access/people.yaml` developer groups gets one (D91). You bring your own Claude subscription; Claude's rights come only from this account and the GitHub app below, never from whose subscription it runs on. An infra developer's agent is in the `platform-agents` group:

- **Azure (subscription):** Reader, Monitoring Reader, Log Analytics Reader, Cost Management Reader. **Entra:** Security Reader (sign-in and audit logs, Conditional Access). Nothing that writes, and no data-plane access to the Pulumi state or Key Vault secrets. The one exception is D52: it may *sign* with the Claude GitHub App's key.
- Never PIM-eligible, so your activations never give Claude more rights. The nightly standing-access check covers it.
- The same Conditional Access as you: a passkey, a fresh sign-in every 4 h for Azure tools and every 12 h otherwise.
- Its sign-ins and actions are logged under its own name, so "you did X" and "your Claude did X" stay distinguishable.

An app developer's agent is in `app-agents`: the database read-only and telemetry, like you; nothing in Azure or the cluster; a fresh sign-in every 12 h; and the same signing with the Claude GitHub App's key (D52), so it can open PRs. The rest below applies to both.

**GitHub:** Claude acts as the org's `ulberg-claude` app (`ulberg-claude[bot]`): contents, pull requests and workflow files (write), and Actions (read: run logs and previews, so it can see why a check failed or what an apply will change). It pushes branches and opens PRs; it can't approve (you're the code owner), re-run, cancel or approve workflow runs or deployments, or change settings, rulesets, environments or secrets. Its private key is a Key Vault key that only the agents' groups (`platform-agents`, `app-agents`) may sign with, and never leaves Key Vault. `scripts/claude-github-token.sh` (platform repo) has Key Vault sign the app's JWT and stores a 1 h installation token in Claude's own `gh` profile; Claude runs it itself whenever the token expires. To publish a branch, Claude runs `scripts/claude-push.sh` (platform repo) instead of `git push`: the rulesets require verified signatures, and the app can't sign git commits, so the script makes one commit through GitHub's API (signed by GitHub, author `ulberg-claude[bot]`, you as `Co-authored-by`). Claude may merge a PR once you've approved it and checks pass.

The hook blocks `az` without the agent's profile, `gh` and `git push` without Claude's `gh` profile and HTTPS pushes, and any command that names `AZURE_CONFIG_DIR`, `GH_CONFIG_DIR`, `GIT_CONFIG_*`, `GH_TOKEN=` or `GITHUB_TOKEN=` (Claude must not choose its own identity).

One-time setup, after the apply has created the agent account (direct links are in the PR that adds you to `access/people.yaml`):

1. Its licence comes by group: an infra developer's agent gets **Entra ID P2** through `platform-agents`; an app developer's, like app developers themselves, needs none.
2. Create a **Temporary Access Pass** for it. This needs **Authentication Administrator**, never Global Admin: activate it in PIM (<https://portal.azure.com/#view/Microsoft_Azure_PIMCommon/ActivationMenuBlade/~/aadmigratedroles>, 1 h, your passkey), then open the agent account in Entra (*Users* → the agent → *Authentication methods* → *Add authentication method* → *Temporary Access Pass*). The same applies when the agent's passkey is lost.
3. In a private browser window, sign in as the agent at <https://aka.ms/mysecurityinfo> with the pass and **add a passkey**.
4. In your own terminal (not through Claude): `AZURE_CONFIG_DIR=~/.azure-agent az login --tenant <tenant-id>`, and sign in as the agent with its passkey.
5. Add this to `~/.claude/settings.json`, with `<home>` replaced by your home directory written out (e.g. `/Users/you`; neither `~` nor `$HOME` is expanded there), and restart Claude Code:

   ```json
   "env": {
     "AZURE_CONFIG_DIR": "<home>/.azure-agent",
     "GH_CONFIG_DIR": "<home>/.config/gh-claude",
     "GIT_CONFIG_COUNT": "3",
     "GIT_CONFIG_KEY_0": "url.https://github.com/.insteadOf", "GIT_CONFIG_VALUE_0": "git@github.com:",
     "GIT_CONFIG_KEY_1": "credential.https://github.com.helper", "GIT_CONFIG_VALUE_1": "",
     "GIT_CONFIG_KEY_2": "credential.https://github.com.helper", "GIT_CONFIG_VALUE_2": "!gh auth git-credential",
     "GIT_COMMITTER_NAME": "ulberg-claude[bot]",
     "GIT_COMMITTER_EMAIL": "358816865+ulberg-claude[bot]@users.noreply.github.com"
   }
   ```

When Claude reports that the agent's session expired, repeat step 4 with the passkey (never the pass: a session started with a pass ends when the pass expires). That needs no admin role. The GitHub token renews itself through Claude.

Tests: `.claude/hooks/test_agent_identity.py` (CI, both repos); `.github/scripts/check_agent_access.sh` (platform) compares each agent's real rights with the above on every PR, on main and nightly; `scripts/agent-smoke-test.sh` (platform) is the manual test Claude runs as the agent (reads work, writes are refused).

## 5. Local development

`make up` runs the app as it runs in the cloud (D61, D67): a local Kubernetes cluster where Argo CD deploys the platform and the app from the checkouts, two backend instances on different nodes behind the gateway (http://localhost:9875), Postgres 18 outside the cluster with the three roles, and telemetry through one collector into Grafana (http://localhost:9876). Always test through the gateway, never an instance directly. Code changes reload by themselves; after changing `platform.yaml`, run `make sync`. See README.md.

Claude reads the local system through two MCP servers in `.mcp.json`, both read-only and both as itself: `grafana` (logs, traces and metrics of the app, Postgres and every pod, as Grafana's `claude` Viewer service account, whose token `make up` writes to `.local/grafana-claude-token`; after a fresh `make up`, reconnect the MCP servers with `/mcp` so they read the new token) and `postgres` (queries as `todo_readonly`, including EXPLAIN ANALYZE and per-query statistics; the database enforces read-only: SELECT-only privileges, read-only transactions, 30 s per statement). People sign in to Grafana as `admin`. Everything the cluster emits is there: every pod's logs, Kubernetes events, the gateway's spans and access logs, the Kubernetes API's audit log, dropped network connections (Hubble), Postgres's metrics and logs, with dashboards and alert rules as code (D71). Infra developers' Claude also reads the cluster itself through the platform repo's read-only Kubernetes MCP server (AGENTS.md §3: never an app developer's). Debug from a request's trace ID, which every error response contains.

## 6. How we work (every agent and person; the same in both repos)

- **Grill, then build.** Every phase starts with a grilling round: questions with a recommended answer each, until nothing is assumed. The decisions go into `PLAN.md` before any code.
- **Look up best practice first.** Before using a technology we haven't used, search for its current best practice and real end-to-end examples, and say in the PR which practice we chose.
- **Supply chain.** A dependency needs 1M+ weekly downloads or an official source (the project's own org). Otherwise read its source at the pinned version first, and record the review in the platform repo's `docs/dependency-reviews.md`. Everything is pinned by content (D65).
- **Least privilege, zero trust.** Every identity, pod, tool and sandbox gets only what it needs: deny by default, verify rather than assume.
- **Least footprint.** Choose and configure for the smallest memory, storage and compute: the tools we download and run, local development, the cloud, and the code (simple and fast; no needless allocations; stack over heap where it matters).
- **Be lazy: simple over strict, and less code.** Choose the simplest solution that is safe enough, and write as little as you can: less code is less to maintain. Often it means moving the problem to where one shared solution serves everyone, rather than solving it in each place: from a feature into a core utility or reusable service, from a BFF into the core system, from the core into an expert system, from the app into the platform or the infrastructure (auth, for example). A short rule that survives change beats a long exact one that breaks when something outside us moves. For example, allow a trusted vendor's domains (`*.anthropic.com`) rather than each host it happens to use today. When the strictest version would be large or brittle, take the simpler one and write down the risk it accepts.
- **Text in files, not inline.** Anything longer than a few lines (configuration, policies, manifests, SQL, prompts, PR bodies) lives in its own file that the code reads or templates, not in a heredoc or a string inside a script.
- **Red, green, refactor.** A failing test (or check) first, then make it pass, then a pass that makes the new code shorter without making it harder to read, before every PR.
- **Fast feedback.** Check each fix the cheapest way that proves it (a unit test, `helm template`, `make sync`); run the slow path (a fresh `make up`) once per increment, in the background, one at a time. Timeouts are short and a failure prints its cause at once.
- **Find the real cause.** Don't blame slow propagation, caches or flakiness until the evidence rules out everything else.
- **One branch per piece of work.** Follow-up fixes go on the open PR's branch; no PRs stacked on unmerged ones.
- **Merging.** Only after the code owner's approval and with every check green, read from the workflow run itself (a check list can show a finished job as pending). A new tool developers will use (a UI, a `make` command) gets tried locally by them first.
- **Manual steps.** If tools can do a decided step, do it. When a person has to act, name the account and give the direct URL of the exact page.
