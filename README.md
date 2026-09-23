# Velo

Velo is a minimal local task orchestrator for Codex, published as a single .NET 10 Native AOT executable.

## Build

```powershell
dotnet publish src/Velo/velo.csproj -c Release -r win-x64
```

The native executable is written below `src/Velo/bin/Release/net10.0/win-x64/publish`.

Velo requires `codex` to be available on `PATH`.

## Tests

```powershell
dotnet test Velo.slnx
pwsh -File tests/NativeAotSmoke.ps1
```

The Native AOT smoke test publishes `win-x64` and directly runs the resulting `velo.exe`.
The integration tests use an isolated temporary `VELO_HOME` and a fake Codex executable.
It does not run the installed Codex CLI or access the user's Velo task directory. The
process-level test currently requires Windows.

## Commands

```text
velo add [--worktree] [--unsafe] [--] <prompt>
velo list [--state <state>]
velo show <id>
velo cancel <id>
velo retry <id>
velo remove <id>
velo logs [<id>] [--tail]
velo start [--concurrency <n>] [--timeout <timespan>]
velo stop
velo status
```

`velo add` stores the current directory as the task workspace. The worker runs Codex
in that directory, whether or not it is a Git repository.

By default, Velo runs Codex in the `workspace-write` sandbox with automatic approval
review. Use `--unsafe` only for trusted tasks that must bypass Codex approvals and the
sandbox. The selected execution policy is stored with the task and shown by
`velo show <id>`.

`velo add --worktree` creates a task-specific Git branch named `velo/<id>` from the
current `HEAD` and checks it out below `~/.velo/workspaces/<id>`. If the command is
run from a repository subdirectory, Codex runs from the matching subdirectory in the
new worktree. Uncommitted changes in the source checkout are not copied.

Worktrees are retained when tasks finish, fail, or are cancelled. Velo does not
commit, merge, push, or remove them automatically.

Tasks targeting different directories inside the same Git worktree run serially to
avoid concurrent changes to one checkout. Separate Git worktrees may run in parallel,
subject to the worker concurrency limit. Non-Git workspaces are locked by directory.

`log` is an alias for `logs`. Without a task ID, the command reads the Velo worker log.
With an ID, it reads that task's log. `--tail` prints the last 20 lines and follows new
content until Ctrl+C.

`velo list --state <state>` filters tasks by one of `todo`, `running`, `done`, `failed`,
or `cancelled`. Without `--state`, it continues to list all tasks. `velo status` shows
both the worker state and task counts for all five states.

`velo remove <id>` explicitly removes a terminal task and its log. Ordinary task
workspaces are never removed. For a managed worktree, Velo refuses removal while the
worktree has uncommitted or untracked changes. A clean managed worktree is removed,
while its `velo/<id>` branch is retained. Velo does not automatically expire tasks and
does not provide forced removal.

Examples:

```powershell
velo add "Implement the requested change and verify it"
velo add --worktree "Implement an isolated change"
velo add --worktree --unsafe "Run a fully trusted task without sandboxing"
velo start --concurrency 2 --timeout 00:30:00
velo list --state failed
velo remove <id>
velo status
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

A controlled stop terminates each Codex process tree and returns interrupted `running`
tasks to `todo`. They start again from the beginning the next time the worker runs.
Tasks left in `running` after a crash are also recovered to `todo` when the worker
starts. Velo does not resume Codex sessions or partial task progress.

Recovery of orphan processes after a crash or forced termination is intentionally out
of scope.
