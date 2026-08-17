# 08 — Compaction, auto-retry, state, and export

**What to build:** the end-to-end behaviour this ticket makes work, from the user's perspective — not a layer-by-layer implementation list.

Advanced session management from the web: run compaction on a session, toggle auto-compaction and auto-retry, view session state/stats/tree, and download an exported HTML transcript — all driven by the corresponding RPC commands and surfaced in the UI.

**Blocked by:** 01 — Tracer bullet: live session in browser

**Status:** ready-for-agent

- [ ] Compaction can be triggered on a session, with auto-compaction toggles
- [ ] Auto-retry on/off is controllable
- [ ] Session state, stats, and structure are visible in the UI
- [ ] A session can be exported as an HTML transcript
