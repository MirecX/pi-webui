# 02 — Auth token gate and opt-in external port

**What to build:** the end-to-end behaviour this ticket makes work, from the user's perspective — not a layer-by-layer implementation list.

The network service is safe by default: every HTTP request and WebSocket handshake requires the token from the service config (requests without or with a wrong token are rejected with 401/403). The token is auto-generated on first run and printed once. The server binds localhost by default and only exposes the external port (SSH host port + 10000) when the config explicitly opts in. No unauthenticated browser can control the agent.

**Blocked by:** 01 — Tracer bullet: live session in browser

**Status:** done

- [ ] Config file holds token + port; token auto-generated and printed on first run
- [ ] Every HTTP request and WS handshake requires the token; invalid/missing rejected
- [ ] Server binds localhost by default; external port binds only when opted-in
- [ ] Rotating the token via the config file is effective on restart
