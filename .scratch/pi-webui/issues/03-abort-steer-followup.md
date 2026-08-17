# 03 — Abort, steer, and follow-up controls

**What to build:** the end-to-end behaviour this ticket makes work, from the user's perspective — not a layer-by-layer implementation list.

From the web UI you can interrupt a running turn, and queue steering or follow-up messages while the agent is busy. An abort button stops the current turn immediately; steer/follow-up inputs queue a redirect delivered at the right point in the run (steer pre-delivers before the next LLM call, follow-up after the agent settles). The UI reflects queued vs. executing state.

**Blocked by:** 01 — Tracer bullet: live session in browser

**Status:** ready-for-agent

- [ ] Abort interrupts an in-flight turn and the UI reflects "stopped"
- [ ] Steering a message during a run is queued and delivered before the next LLM call
- [ ] Follow-up is queued and delivered after the agent settles
- [ ] UI distinguishes running/queued/idle states
