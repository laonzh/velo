# Velo

Velo is a deliberately small local Codex work queue. It has no daemon, web UI,
database, event stream, or worker lifecycle manager.

## Build and test

```powershell
dotnet publish src/Velo/Velo.csproj -c Release -r win-x64
dotnet test Velo.slnx
```

Velo invokes `codex` from `PATH`. Integration tests use an isolated `VELO_HOME`
and a fake Codex executable.

## Commands

```text
velo add [--worktree] [--] <prompt>
velo list [pending|running|succeeded|failed]
velo run
velo retry <id>
velo logs <id>
```

`add` uses the current directory as the workspace. `--worktree` creates a Git
worktree at `~/.velo/workspaces/<id>`, on branch `velo/<id>`, from the current
repository `HEAD`; Codex always runs at that worktree root. Worktrees are retained.

`run` is a foreground queue drain. Its default concurrency is 2; set
`VELO_CONCURRENCY` to a positive integer to change it. Work items that resolve to
the same Git checkout are serialized, while distinct worktrees and directories
can occupy separate concurrency slots.

Only one `run` orchestrator is active for a given `VELO_HOME`; start another after
the first exits. Press Ctrl+C to stop `run`. Velo terminates its Codex process
trees and returns interrupted work to `pending`. A later run starts it again from
the beginning; partial Codex sessions are not resumed.

Work runs with the Codex `workspace-write` sandbox. There is intentionally no
unsafe-execution switch in the simplified surface.

## Storage

```text
~/.velo/
  work/*.json
  logs/*.log
  workspaces/<id>/
  run.lock
  queue.lock
```

Each work item is one JSON file. Its `state` field is `pending`, `running`,
`succeeded`, or `failed`; state is not represented by directories. `retry`
changes only failed work back to `pending`.

`run.lock` is an internal tombstone used to prevent a second `run` orchestrator
for the same `VELO_HOME`. It is retained after `run` exits; the operating system
releases the file handle when the process exits. Likewise, `queue.lock`
serializes short read-modify-write operations on work files and is not held while
Codex runs.

Old `work/<state>/` layouts are ignored and are not migrated. Set `VELO_HOME` to
use another root.