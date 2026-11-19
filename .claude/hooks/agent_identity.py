#!/usr/bin/env python3
"""PreToolUse hook for Bash: Claude uses its own identities, never the developer's (D41, D52).

- Azure: `az` runs only when Claude Code was started with the agent's profile (~/.azure-agent).
- GitHub: `gh` and `git push` run only when Claude Code was started with Claude's `gh` profile
  (~/.config/gh-claude, the `ulberg-claude` app's token from scripts/claude-github-token.sh in the platform
  repo) and with git set to push over HTTPS through that profile.
- Claude may not choose an identity itself: a command that names one of these settings is blocked.

It is a guardrail against mistakes, not a security boundary: the real limits are the agent account's read
roles and the app's permissions (contents + pull requests). See AGENTS.md.
"""
import json
import os
import re
import sys

HOME = os.path.expanduser("~")
AZ_PROFILE = os.path.realpath(os.path.join(HOME, ".azure-agent"))
GH_PROFILE = os.path.realpath(os.path.join(HOME, ".config", "gh-claude"))
# Split, so this file never trips the hook when Claude edits or greps it.
AZ_VAR, GH_VAR, GIT_VARS = "AZURE_" + "CONFIG_DIR", "GH_" + "CONFIG_DIR", "GIT_" + "CONFIG_"
HTTPS_REWRITE = ("url.https://github.com/.insteadOf", "git@github.com:")

SETUP = (
    "Set it up once (AGENTS.md, 'Claude's identities'): sign the agent in with "
    f"`{AZ_VAR}=~/.azure-agent az login --tenant <tenant>` in your own terminal, put the env block from "
    "AGENTS.md into ~/.claude/settings.json, and restart Claude Code."
)

# A command word: at the start, after a separator, pipe or subshell, or behind `sudo`/`env`/..., optionally
# after variable assignments (`FOO=bar az ...`).
PREFIX = r"(?:^|[;&|(`]|\$\(|\b(?:sudo|env|exec|xargs|command)\s)\s*(?:\w+=\S*\s+)*"
AZ = re.compile(PREFIX + r"az(?:\s|$)", re.MULTILINE)
GH = re.compile(PREFIX + r"gh(?:\s|$)", re.MULTILINE)
GIT_PUSH = re.compile(PREFIX + r"git\s(?:[^;&|\n]*\s)?push(?:\s|$)", re.MULTILINE)
CHOOSES_IDENTITY = re.compile(
    re.escape(AZ_VAR) + "|" + re.escape(GH_VAR) + "|" + re.escape(GIT_VARS) + r"|\b(?:GH|GITHUB)_TOKEN="
)


def block(reason: str) -> None:
    print(reason, file=sys.stderr)
    sys.exit(2)


def uses(var: str, profile: str) -> bool:
    current = os.environ.get(var, "")
    return bool(current) and os.path.realpath(os.path.expanduser(current)) == profile


def pushes_over_https() -> bool:
    count = os.environ.get(GIT_VARS + "COUNT", "0")
    n = int(count) if count.isdigit() else 0
    pairs = {(os.environ.get(f"{GIT_VARS}KEY_{i}"), os.environ.get(f"{GIT_VARS}VALUE_{i}")) for i in range(n)}
    return HTTPS_REWRITE in pairs


command = json.load(sys.stdin).get("tool_input", {}).get("command", "")
if CHOOSES_IDENTITY.search(command):
    block("Claude must not choose its own Azure or GitHub identity. " + SETUP)
if AZ.search(command) and not uses(AZ_VAR, AZ_PROFILE):
    block("`az` runs only with Claude's agent profile, never your own login. " + SETUP)
if GH.search(command) and not uses(GH_VAR, GH_PROFILE):
    block("`gh` runs only with Claude's own GitHub profile (the ulberg-claude app), never your login. " + SETUP)
if GIT_PUSH.search(command) and not (uses(GH_VAR, GH_PROFILE) and pushes_over_https()):
    block("`git push` runs only as Claude's GitHub app, over HTTPS through Claude's gh profile. " + SETUP)
