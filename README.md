# Velo

A minimal local task runner for Codex, with a CLI and a small web UI. Tasks run in isolated Git worktrees; review logs and diffs before approving a merge.

**Under active development. Experimental and not suitable for production use.**

## Quick Start

Requires `velo` on your `PATH`, Git, and an authenticated Codex CLI. Start the server:

```text
velo serve
```

In another terminal, use a Git repository with at least one commit:

```text
velo add "Fix login" -d path/to/repo -m "Fix SSO login failures"
velo logs 1 -f
velo diff 1
velo approve 1
```

Replace `1` with the returned task ID. Use `velo web` for the web UI or `velo --help` for commands.

Data and optional `config.json` live in `~/.velo` (override with `VELO_HOME`).
