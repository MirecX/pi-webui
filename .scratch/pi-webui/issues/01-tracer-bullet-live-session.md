# 01 — Tracer bullet: live session in browser

**What to build:** the end-to-end behaviour this ticket makes work, from the user's perspective — not a layer-by-layer implementation list.

Open the web UI and see a live pi coding-agent session: the server (running detached as the container user in /workspace) boots, spawns one `pi --mode rpc` child, and streams the agent's messages, thinking, tool calls, and bash output to the browser in real time. A send box lets you prompt the agent and watch the reply stream back. This is the vertical spine that proves the whole architecture (C# server → RPC client → WS → TypeScript frontend) works end to end, and it includes the project scaffold, config loading, static hosting, and the minimal reactive binding.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] `pi` RPC child spawns with the correct working directory and container user, and its JSONL framing is read/written correctly
- [ ] Live events (agent start/end, message streaming, tool calls, bash output) reach the browser over WebSocket
- [ ] Frontend renders a live, updating transcript from those events
- [ ] A prompt composed in the web UI reaches the agent and its reply streams back
- [ ] Server starts detached (survives no SSH session) with sane default config
