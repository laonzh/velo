# Velo

Velo is a minimal local task orchestrator for Codex, published as a single .NET 10 Native AOT executable.

## Build

```powershell
dotnet publish -c Release -r win-x64
```

The native executable is written below `bin/Release/net10.0/win-x64/publish`.

Velo requires `codex` to be available on `PATH`.

## Commands

```text
velo add [--worktree] <prompt>
velo list
velo show <id>
velo cancel <id>
velo retry <id>
velo logs [<id>] [--tail]
velo start [--concurrency <n>] [--timeout <timespan>]
velo stop
velo status
```

`velo add` stores the current directory as the task workspace. The worker runs Codex in that directory.

`velo add --worktree` creates a task-specific Git branch named `velo/<id>` from the
current `HEAD` and checks it out below `~/.velo/workspaces/<id>`. If the command is
run from a repository subdirectory, Codex runs from the matching subdirectory in the
new worktree. Uncommitted changes in the source checkout are not copied.

Worktrees are retained when tasks finish, fail, or are cancelled. Velo does not
commit, merge, push, or remove them automatically.

`log` is an alias for `logs`. Without a task ID, the command reads the Velo worker log.
With an ID, it reads that task's log. `--tail` prints the last 20 lines and follows new
content until Ctrl+C.

Examples:

```powershell
velo add "Implement the requested change and verify it"
velo add --worktree "Implement an isolated change"
velo start --concurrency 2 --timeout 00:30:00
velo list
velo logs --tail
velo logs <id> --tail
velo stop
```

## Storage

State is represented by directories under `~/.velo`:

```text
~/.velo/
  tasks/
    todo/
    running/
    done/
    failed/
    cancelled/
  logs/
  workspaces/
  velo.log
  velo.pid
  stop.flag
```

Set `VELO_HOME` to use another location.

When the worker starts, tasks left in `running` are moved back to `todo` and started again from the beginning. Velo does not resume Codex sessions or partial task progress.

A controlled stop terminates each Codex process tree. Recovery of orphan processes after a crash or forced termination is intentionally out of scope.
