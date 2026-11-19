#!/usr/bin/env python3
"""Tests for agent_identity.py. Run: python3 .claude/hooks/test_agent_identity.py"""

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HOOK = Path(__file__).with_name("agent_identity.py")
# Split, so this file never trips the hook when Claude edits it.
AZ_VAR, GH_VAR, GIT_VARS = "AZURE_" + "CONFIG_DIR", "GH_" + "CONFIG_DIR", "GIT_" + "CONFIG_"


def run_hook(command: str, home: Path, env_vars: dict[str, str]) -> int:
    env = {k: v for k, v in os.environ.items() if not k.startswith((AZ_VAR, GH_VAR, GIT_VARS))}
    env["HOME"] = str(home)
    env.update(env_vars)
    event = json.dumps({"tool_input": {"command": command}})
    return subprocess.run([sys.executable, str(HOOK)], input=event, text=True, env=env, capture_output=True).returncode


class AgentIdentityTest(unittest.TestCase):
    def setUp(self):
        self.home = Path(tempfile.mkdtemp())
        self.az = {AZ_VAR: str(self.home / ".azure-agent")}
        self.gh = {GH_VAR: str(self.home / ".config" / "gh-claude")}
        self.https = {
            GIT_VARS + "COUNT": "1",
            GIT_VARS + "KEY_0": "url.https://github.com/.insteadOf",
            GIT_VARS + "VALUE_0": "git@github.com:",
        }
        self.claude = {**self.az, **self.gh, **self.https}

    def assertBlocked(self, command, env=None):
        self.assertEqual(run_hook(command, self.home, env or {}), 2, command)

    def assertAllowed(self, command, env=None):
        self.assertEqual(run_hook(command, self.home, env or {}), 0, command)

    def test_az_without_the_agent_profile_is_blocked(self):
        self.assertBlocked("az account show")
        self.assertBlocked("az account show", {AZ_VAR: str(self.home / ".azure")})

    def test_az_with_the_agent_profile_runs(self):
        self.assertAllowed("az account show", self.az)
        self.assertAllowed("az account show", {AZ_VAR: "~/.azure-agent"})

    def test_az_is_found_anywhere_in_a_command(self):
        for command in ["echo hi && az rest --url x", "ls | az group list", "x=$(az account show)", "FOO=bar az group list"]:
            self.assertBlocked(command)

    def test_gh_runs_only_with_claudes_profile(self):
        self.assertBlocked("gh pr create --fill")
        self.assertBlocked("gh pr create --fill", {GH_VAR: str(self.home / ".config" / "gh")})
        self.assertBlocked("cd x && gh api user")
        self.assertAllowed("gh pr create --fill", self.gh)

    def test_git_push_runs_only_as_the_app_over_https(self):
        self.assertBlocked("git push -u origin x")
        self.assertBlocked("git push -u origin x", self.gh)  # Claude's profile, but git would use SSH (you)
        self.assertBlocked("git commit -m x && git -C repo push", self.gh)
        self.assertAllowed("git push -u origin x", self.claude)

    def test_choosing_an_identity_is_blocked_even_for_claude(self):
        for command in [
            f"{AZ_VAR}=~/.azure az account show",
            f"export {AZ_VAR}=/tmp/x",
            f"{GH_VAR}=~/.config/gh gh pr list",
            f"{GIT_VARS}COUNT=0 git push",
            "GH_TOKEN=abc gh pr list",
            "GITHUB_TOKEN=abc gh api user",
        ]:
            self.assertBlocked(command, self.claude)

    def test_other_commands_pass(self):
        for command in ["git status", "git log --oneline", "git commit -m 'push it'", "ls azure-stuff; grep az file",
                        "cat bootstrap/az.md", "lazy run", "echo ghost", "pushd x"]:
            self.assertAllowed(command)


if __name__ == "__main__":
    unittest.main()
