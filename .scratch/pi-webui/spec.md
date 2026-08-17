## Status: implemented (tickets #01–#08 done; deltas + follow-ups below)
## Parent: (conversation → design, see DESIGN.md)

# pi-webui — Web UI for a pi coding-agent instance

## Problem Statement

As a developer running pi in a headless box (e.g. a yolobox container), I currently only
reach the coding agent through SSH/terminal. I want to see the agent's live session —
messages, thinking, tool calls, bash output — and interact with it (prompt, interrupt,
switch model, answer its HITL questions) from a web browser, without SSH and without
needing a terminal on my client. A web UI must reflect everything the TUI shows and let
me drive the same agent, minus a handful of terminal-only behaviors that don't map to a
web anyway.

## Solution

A `pi-webui` service: a detached C# / ASP.NET Core server that spawns and owns one
`pi --mode rpc` child process per session (running as the container user in /workspace),
translates pi's JSONL RPC event stream to WebSocket push, and exposes compose/send,
steer, abort, model & thinking switching, and HITL dialogs through a TypeScript web
frontend. Sessions are on-demand and user-managed (init/recycle with history preserved),
token-authenticated, and the service is installed into the container on demand rather
than baked into the box image.

## User Stories

1. As a developer, I want to open the web UI and see all live messages of a pi session (streaming text, thinking, tool calls, bash output), so that I can watch the agent work from a browser.
2. As a developer, I want to type a prompt and send it, so that I can drive the agent without SSH.
3. As a developer, I want to steer the agent mid-run and send follow-ups, so that I can redirect or queue work without waiting for the turn to finish.
4. As a developer, I want an abort/interrupt button, so that I can stop a runaway turn immediately.
5. As a developer, I want to switch the active model and the thinking level per session, so that I can trade off speed, cost, and reasoning.
6. As a developer, I want to initialize a new session on demand, so that I control when an agent starts rather than it spawning silently.
7. As a developer, I want to recycle a session (stop the process but keep its history), so that I can free resources without losing work.
8. As a developer, I want to delete a session permanently, so that I can clean up completed or discarded work.
9. As a developer, I want multiple named sessions running concurrently, each browser tab attached to one, so that I can parallelize independent agent tasks.
10. As a developer, I want a session auto-titled from its first message (via the model), so that I can pick the right session from a list at a glance.
11. As a developer, I want a session browser that can resume, fork, and clone stored sessions, so that I can continue or branch previous work.
12. As a developer, I want pi's select/confirm/input/editor HITL requests surfaced as browser dialogs, so that agent-driven questions (grilling, /ask, confirmations) actually resolve in the web UI.
13. As a developer, I want live streaming in the UI as events arrive, so that the web mirrors the TUI's real-time feel rather than polling.
14. As a developer, I want compaction and auto-retry controls visible from the web, so that I can manage long sessions.
15. As a developer, I want the service token-authenticated via a config file token, so that only I can control the agent over the network.
16. As a developer, I want the external port to be opt-in and mapped off the SSH port, so that the default box stays safe and the web UI is reachable only when I expose it.

## Implementation Decisions

- **Server-side agent control, not attachment.** The server owns dedicated `pi --mode rpc` child processes; it does not attach to an external interactive TUI session. Session management is the server's responsibility.
- **C# / ASP.NET Core backend.** REST + WebSocket, static file hosting, with a JSONL-framing RPC client to pi. The RPC protocol is versioned/stable and re-implemented in C# (Node reference clients exist).
- **One child per session.** Each session maps 1:1 to a `pi --mode rpc` child (yolo user, /workspace cwd). Server multiplexes N children; each browser tab attaches to one session stream.
- **Session lifecycle is explicit and user-driven.** init (new or resume) / recycle (kill child, keep history) / delete (remove file). Distilled as three distinct actions.
- **Multi-session, named.** Named, concurrent sessions; server fans each child's event stream only to tabs attached to that session.
- **Full RPC feature set minus TUI-exclusive.** Includes prompt/steer/follow-up/abort, streaming mirror, model & thinking switch, fork/clone/resume, compaction/retry, HITL dialogs, state/export. Excludes job-control suspend and the live interactive-command-terminal handoff.
- **Auth via config-file token.** Token in the service config file (auto-generated on first run, printed once), required on every HTTP request and WS handshake. External port exposure is opt-in.
- **Ports.** Internal 8456; external = SSH host port + 10000, off by default.
- **Auto-title.** Generated once per new session from the first user message via a tiny non-blocking completion at the box's default model endpoint; fallback to truncated first message.
- **Frontend is web-native, not a terminal emulator.** TypeScript + a light reactive layer maps RPC events to DOM; no xterm.js in V1.
- **Packaging.** Own repo, installed on demand into the container (clone into the extensions dir), never forced into the box image. The box image is merely made "ready" (optional port mapping, no entrypoint changes).
- **Config schema.** Service config persists as `{ token, port, external }`, container-agnostic, matching the pi-subagents/pi-searxng config.json convention (`defaultModel` deferred).

## Testing Decisions

- A good test here verifies behavior over the public seam, not the internals of the renderer. Test the server's observable I/O, not DOM details.
- **Seam 1 — the RPC client.** The session manager talks to a thin PiRpcClient interface. Test that it frames commands and parses events correctly against a scripted fake pi (event types, streaming deltas, framing edge cases). This is the highest-value seam and the hardest to get right.
- **Seam 2 — the WebSocket API boundary.** Backed by the fake pi, assert that the WS layer relays RPC events to connected clients and turns client commands into the right RPC calls (prompt, abort, set_model, etc.), including HITL request/response.
- Lifecycle (init/recycle/delete) tested at the session-manager seam against the fake pi.
- Auth tested at the HTTP/WS boundary (no token → 401; wrong token rejected; valid token accepted).
- Frontend tested through the WS boundary with a fake event stream; prior art: standard component/streaming tests; keep renderer logic thin.
- **Gap (tracked):** no frontend automated test harness exists yet (`web/` has only `typescript`; render logic is embedded in `setup()`). Backend WS-seam tests cover the wiring; the frontend UI (modals, transcript, content-block rendering, history replay) is validated manually. Adding a jsdom/headless harness is a follow-up (see Development Log).

## Out of Scope

- **Live interactive-command-terminal handoff** (typing into a running interactive process via xterm.js + PTY). Deferred to V2.
- `app.suspend` / OS job control (N/A in a headless server model; recycle covers it).
- Embedding pi-webui into the yolobox image by default (on-demand install only).
- True terminal input semantics (IME hardware cursor, image-as-ANSI-art).
- Multi-user identity/roles (single operator token in V1).

## Development Log (deltas decided during implementation of #01–#08)

Implemented and committed on `master` (see git history). Items reflecting decisions that
changed or were clarified vs. the original plan above:

1. **History replay on (re)attach.** The original plan said "live-stream, events not
   replayed to late attach." The user wanted a page refresh / tab switch to keep the
   transcript. The webui now fetches `get_messages` on every connect and redraws the past
   transcript (block-aware), then live events continue. *(Change from the initial decision.)*
2. **Cross-TUI/web continuity (deliberate — rely on it).** Sessions are real pi session
   files in the standard store; the same session can be resumed in the TUI
   (`cd <cwd> && pi` → `/resume`, or `pi --session <id>`). One loop owns a session at a
   time — recycle in the web, then resume in the TUI. Full history + same model carry over.
3. **Content-block rendering.** Messages render as typed blocks: `text`, `>>> <tool>` rows
   with a pretty-printed payload, `tool_result` output folded into its tool row (grouped
   by `toolCallId`), images as `[image]`. Tool calls are deduped between `tool_use` and
   `tool_execution_start`. *(Fixes an early `[object Object]` bug.)*
4. **Live stats.** The session panel (state/stats/structure) refreshes on `agent_settled`
   and every 5s while a session is active — not just on connect.
5. **Auto-retry caveat.** pi's `get_state` exposes only `autoCompactionEnabled`, NOT
   `autoRetryEnabled`. The auto-retry checkbox is an optimistic PER-SESSION frontend value
   (persisted in the session map, restored on switch/reconnect), defaulting to off.
6. **Auto-title.** Implemented per design: non-blocking, no-tools completion at the box's
   default model endpoint (resolved from `~/.pi/agent/models.json` "work" provider; no
   hardcoded localhost fallback); falls back to a truncated first message + timestamp on
   failure; titles persist across restarts via a sidecar `titles.json`.
7. **Fork vs clone semantics.** `fork` is an in-place branch within the same session file
   (surfaces the forking message). `clone` creates a NEW session file, which is discovered
   (via `get_state`) and registered as a listable/resumable **stored** session.
8. **Auth first-load UX.** The gate 401s without a token. First visit must be
   `http://host:port/?token=<token>`; the frontend then sets a same-origin cookie for the
   WS + session. A plain `/` (no token) is a 401 by design.
9. **Ports on this host.** Server binds `0.0.0.0:8456` when `"external": true`; local dev
   has no compose mapping (just `8456`). `HOST_PORT+10000` mapping applies only inside the
   docker-compose box.
10. **Config schema (as implemented).** `{ token, port, external }` (plus optional
    `PIE_*` env hooks used in dev/testing); `defaultModel` deferred.

### Known follow-ups / gaps to pick up next
- **Frontend test harness** — add a jsdom/headless runner; the UI (modals, transcript,
  content-block rendering, history replay, pickers) is currently untested by automation.
- **Export persistence** — `export_html` path is held in memory only; after a server
  restart `GET /api/sessions/{name}/export` 404s until re-exported. Persist the mapping
  (e.g. alongside the history/export file).
- **Bash tool display (polish)** — show just the `command` (`$ cd … && make`) rather than
  the full JSON payload.
- **Installer/launcher for the box** — `make install`/`make start` exist; a hardened
  on-demand installer + container README was not yet polished.
- **Multi-operator auth (V2).**

## Further Notes

- The web session can execute arbitrary commands in /workspace; docs carry the same credential warnings as yolobox — treat it like a root shell on the box.
- The pi RPC protocol is the contract; pin/adhere to its documented framing (JSONL, LF-only delimiters) to avoid client bugs.
- V2 candidates: xterm.js live-terminal handoff, per-session provider pickers, multi-operator auth.
