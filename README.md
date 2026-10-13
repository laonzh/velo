# Velo

Velo is a deliberately small local Codex work queue. It has no database, event
stream, or worker manager. `start --port` adds a loopback JSON API and a browser
dashboard to the running queue, and `start -d` only re-launches Velo itself in the
background.

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
velo start [--once] [-d] [--port N]
velo stop
velo retry <id>
velo cancel <id>
velo edit <id> [--] <prompt>
velo delete <id>
velo logs <id>
velo diff <id>
velo accept <id>
velo discard <id>
```

`add` uses the current directory as the workspace. `--worktree` creates a Git
worktree at `~/.velo/workspaces/<id>`, on branch `velo/<id>`, from the current
repository `HEAD`; Codex always runs at that worktree root. `delete` keeps the
worktree; `discard` removes it (see below).

`diff <id>` shows what a succeeded worktree changed against its base commit. It
stages everything in a temporary index, so the worktree's own index is untouched
and untracked files are included; the patch is capped at 512 KiB.

`accept <id>` applies those changes to the repository the worktree came from, as
uncommitted changes; nothing is committed. The patch is checked with `git apply
--check` first, and nothing is written unless all of it applies. The check uses the
whole patch, not the capped preview. A conflict, such as a file that already exists
in the repository, is refused with the reason and changes nothing. Binary files are
not supported: their patch cannot be applied without a full index, so accepting it
fails the same way. The worktree and branch stay after accepting, so you can still
`diff` them; each item can be accepted once.

`discard <id>` removes a succeeded worktree and its branch `velo/<id>`, including any
uncommitted or committed changes on that branch. Changes that were not accepted are
lost with it. The work record and its log stay, marked `discarded`, and a discarded
item cannot be reviewed, accepted, or discarded again. Only worktree work that
succeeded can be reviewed or discarded.

`start` runs the queue in the foreground until Ctrl+C; while idle it keeps
waiting for new work. On Unix, closing its terminal (SIGHUP) stops it the same way.
`start --once` drains the queue and exits. `start -d` starts the same watch loop in
the background and returns once it is running; its output goes to
`~/.velo/start.log`; a background start first moves a log over 1 MiB to
`start.log.1`. On Unix a background start ignores SIGHUP, so it keeps running after
its terminal closes, and so does the Codex it starts.

`velo stop` ends the running start, foreground or background, as Ctrl+C does: Codex is
stopped, running work returns to `pending`, and the command waits up to 30 seconds for
the start to exit. It writes the start's PID to `~/.velo/stop.request`, which the start
polls; a request naming any other process is ignored. If a start does not stop in
time, `Stop-Process -Id` with the PID in `~/.velo/start.pid` is the last resort; the
Codex job object still ends Codex with it.

`start.pid` is trusted only while `run.lock` is held. Codex runs in a Windows job
object that is closed together with velo, so a forced kill also stops Codex. Interrupted
work returns to `pending` at the next start. Outside Windows this guarantee does not
exist yet.

`start --port N` also serves the [HTTP API](#http-api) and the [dashboard](#dashboard)
on `127.0.0.1:N`. `--port 0` picks a free port and prints the address it bound.
`--once` cannot be combined with `-d` or `--port`.

Default concurrency is 2; set `VELO_CONCURRENCY` to a positive integer to change it.
Work items that resolve to the same Git checkout are serialized, while distinct
worktrees and directories can occupy separate concurrency slots.

Only one `start` orchestrator is active for a given `VELO_HOME`; a second start
reports the running PID and exits. Press Ctrl+C, close its terminal on Unix, or run `velo stop`, to stop a foreground
`start`. Velo terminates its Codex process trees and returns interrupted work to `pending`. A
later start runs it again from the beginning; partial Codex sessions are not
resumed.

Work runs with `codex exec --approve-for-me`, which Codex documents as using the
`workspace-write` sandbox. There is intentionally no
unsafe-execution switch in the simplified surface.

## Storage

```text
~/.velo/
  work/*.json
  work/*.review.lock
  logs/*.log
  workspaces/<id>/
  run.lock
  queue.lock
  start.pid
  stop.request
  start.log
  start.token
```

Each work item is one JSON file. Its `state` field is `pending`, `running`,
`succeeded`, or `failed`; state is not represented by directories. `retry`
changes only failed work back to `pending`.

`cancel` fails a pending item immediately with `Cancelled by user.`. For a running
item it records a request; the orchestrator stops the Codex process tree and marks
the item failed with the same message. `edit` applies only to pending work. `delete`
removes work that is not running, along with its log; a worktree it used is kept.
Each item also records its `attempts`, `startedAt`, `finishedAt`, and `exitCode`,
and worktree work also records its `repository`, `baseCommit`, `discarded`, and
`accepted` fields.

`run.lock` is an internal tombstone used to prevent a second `start` orchestrator
for the same `VELO_HOME`. It is retained after `start` exits; the operating system
releases the file handle when the process exits. Likewise, `queue.lock`
serializes short read-modify-write operations on work files and is not held while
Codex runs. Per-work `.review.lock` files serialize diff, accept, discard, and
delete across CLI and HTTP processes.

Old `work/<state>/` layouts are ignored and are not migrated. Set `VELO_HOME` to
use another root.

## HTTP API

`start --port N` serves JSON on `127.0.0.1:N` for as long as that start runs. The
server accepts only `127.0.0.1` or `localhost` in the `Host` header. Each start writes
a new random token to `start.token`, and every `/api` request must send it in the
`X-Velo-Token` header. The token file is removed when the server stops cleanly.

| Method | Path | Result |
| --- | --- | --- |
| GET | `/api/v1/state` | Counts, running work, and pending work |
| GET | `/api/v1/work?state=<state>` | Work summaries, optionally filtered |
| POST | `/api/v1/work` | `201` with `work_id`; body `{prompt, workspace, worktree}` |
| GET | `/api/v1/{id}` | Full detail, including `logs.path` |
| PATCH | `/api/v1/{id}` | Replace the prompt of pending work; `{prompt}` |
| DELETE | `/api/v1/{id}` | `204`; running work is refused |
| GET | `/api/v1/{id}/log?offset=N` | Complete lines from `offset`; resume at `next_offset` |
| POST | `/api/v1/{id}/cancel` | `result` is `cancelled` or `cancel_requested` |
| POST | `/api/v1/{id}/retry` | Failed work returns to `pending` |
| GET | `/api/v1/{id}/diff` | Changes of succeeded worktree work: `{work_id, base, stat, patch, truncated}` |
| POST | `/api/v1/{id}/accept` | Applies the changes to the repository; `{work_id, accepted: true}` |
| POST | `/api/v1/{id}/discard` | Removes the worktree and branch; `{work_id, discarded: true}` |
| POST | `/api/v1/refresh` | `202`; the idle watch loop polls now |

`workspace` must be an absolute path. Errors use one envelope,
`{"error":{"code":"...","message":"..."}}`. The codes are `unauthorized` (401),
`forbidden_host` (403), `invalid_request` (400), `work_not_found` and `not_found`
(404), `method_not_allowed` (405), `work_running`, `work_not_pending`,
`work_not_cancellable`, `work_not_failed`, `work_not_reviewable`,
`work_already_accepted`, `accept_conflict`, and `worktree_missing` (409), and
`git_failed` (500) when git cannot be started, or a Git command fails during a diff,
accept, or discard.

The field names follow Symphony's `SPEC.md` §13.7 in snake_case, with these
differences: `issue_identifier` is `work_id`; there are no `codex_totals`,
`rate_limits`, or `retry` fields, because Velo keeps no token accounting and does
not retry automatically; and `attempts` is a plain integer. Work summaries also carry
`last_error`, the message that stopped a failed or cancelled item, and `reviewable`,
`discarded`, and `accepted`. `reviewable` is true for succeeded worktree work that has
not been discarded, so only those items accept `diff`, `accept`, and `discard`.

## Dashboard

`start --port N` serves a single page at `http://127.0.0.1:N/`. Open it in a browser
on the same machine. The token is embedded in the page, so no sign-in is needed. Other
websites cannot read the page: it sends no CORS headers, and the `Host` check rejects
other host names.

The page shows four sections (running, pending, failed, succeeded) and the counts in
its header, and it refreshes every two seconds. From it you can:

- add work with a prompt, an absolute workspace path, and an optional Git worktree;
- edit pending work, cancel pending or running work, and retry failed work;
- delete work that is not running, after a confirmation prompt;
- show a succeeded worktree's diff; apply its changes to the repository after a
  confirmation prompt; or discard the worktree and its branch after a confirmation
  prompt. Accepted work is marked 已接受 and discarded work 已丢弃;
- show or hide a work item's log, which keeps following the log while it is open;
- ask the idle watch loop to poll now (the refresh button).

The page is served with a strict `Content-Security-Policy` (`script-src 'self'`, so
no inline scripts) and `Cache-Control: no-store`. Its HTML, JavaScript, and CSS are
embedded in the executable, so the AOT build needs no extra files. The interface text
is in Chinese.
