# 07 — HITL dialogs

**What to build:** the end-to-end behaviour this ticket makes work, from the user's perspective — not a layer-by-layer implementation list.

When the running agent asks something that needs the human — pi's `select`/`confirm`/`input`/`editor`/`notify` requests (e.g. during grilling, `/ask`, or a confirm-yes/no) — the browser surfaces it as a modal dialog instead of dead-ending. Answering the dialog sends the response back over RPC, so agent-driven questions resolve in the web UI.

**Blocked by:** 01 — Tracer bullet: live session in browser

**Status:** ready-for-agent

- [ ] `select`/`confirm` requests render as modals and answers are sent back
- [ ] `input`/`editor` requests accept freeform text and return it
- [ ] `notify` requests surface a notice
- [ ] A modal shown for one session doesn't break another session's stream
