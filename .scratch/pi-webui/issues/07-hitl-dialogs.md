# 07 — HITL dialogs

**What to build:** the end-to-end behaviour this ticket makes work, from the user's perspective — not a layer-by-layer implementation list.

When the running agent asks something that needs the human — pi's `select`/`confirm`/`input`/`editor`/`notify` requests (e.g. during grilling, `/ask`, or a confirm-yes/no) — the browser surfaces it as a modal dialog instead of dead-ending. Answering the dialog sends the response back over RPC, so agent-driven questions resolve in the web UI.

**Blocked by:** 01 — Tracer bullet: live session in browser

**Status:** done

- [ ] `select`/`confirm` requests render as modals and answers are sent back
- [ ] `input`/`editor` requests accept freeform text and return it
- [ ] `notify` requests surface a notice
- [ ] A modal shown for one session doesn't break another session's stream

## Testing note (code-review finding #4 — frontend UI)

The spec's Testing Decisions call for the frontend to be exercised through the WS boundary with a "fake event stream". Today `web/` has **zero** automated tests of the HITL modal UI, and adding one is not tractable with the current tooling/scope: the render logic is embedded in the single `setup()` closure in `web/src/main.ts` with heavy DOM + WebSocket side effects, and the frontend has no test runner or DOM shim installed (no jsdom/vitest/jest — only `typescript`). Standing one up would mean installing a runner, writing a DOM shim, and re-architecting `setup()` for testability — beyond a minimal, correct change and a throwaway harness.

Accepted decision: the HITL modal UI (select/confirm/input/editor/notify render + answer production) is exercised **only via the WS boundary manually** (selecting each dialog method in a live session and confirming the answer round-trips). The modal logic is kept deliberately thin. The backend WS boundary itself IS covered by `backend/tests/WsBridgeTests.cs` (`HITL_dialog_and_notify_events_relay_to_attached_client`, `Hitl_response_*` round-trips).
