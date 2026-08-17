# 04 — Model and thinking-level switch per session

**What to build:** the end-to-end behaviour this ticket makes work, from the user's perspective — not a layer-by-layer implementation list.

Each session has pickers for the active model and the thinking level, backed by the RPC model/thinking commands (set/cycle and available lists). Choosing a model or a thinking level takes effect on that session and the UI shows the current selection. You can trade off speed, cost, and reasoning without leaving the browser.

**Blocked by:** 01 — Tracer bullet: live session in browser

**Status:** ready-for-agent

- [ ] Available models and thinking levels are listed per session
- [ ] Selecting a model or thinking level issues the RPC change and the UI reflects it
- [ ] Selection is per-session (changing one session doesn't affect another)
