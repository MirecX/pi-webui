# 06 — Session browser: resume, fork, clone, and auto-title

**What to build:** the end-to-end behaviour this ticket makes work, from the user's perspective — not a layer-by-layer implementation list.

A session list lets you browse stored sessions and resume, fork, and clone them (switch_session/fork/clone), so recycled or past work can be continued or branched. New sessions are auto-titled from their first user message via a short, non-blocking completion at the default model endpoint (falling back to a truncated first message + timestamp), so the list is scannable at a glance.

**Blocked by:** 05 — Multi-session lifecycle

**Status:** ready-for-agent

- [ ] Stored sessions are listable and openable/resumable
- [ ] A session can be forked from or cloned, creating a new branch
- [ ] A new session is auto-titled from its first message, with a safe fallback
- [ ] Auto-title never delays the agent's first turn
