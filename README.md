# pi-webui

A web UI that mirrors a running pi coding-agent session and lets you interact with it
from the browser — no SSH, no terminal.

- C# / ASP.NET Core server (spawns & owns `pi --mode rpc` children)
- TypeScript + light reactive frontend
- Multi-session, token-auth, on-demand lifecycle

> ⚠️ The web session can run arbitrary commands in `/workspace`. Treat it like a root
> shell on the box. Do not place credentials inside the container.

See **[DESIGN.md](DESIGN.md)** for the full architecture and decision record.

## Quick start (installed on demand)

**One-liner (in a yolobox container or any box with pi + .NET SDK + Node):**

```bash
curl -fsSL https://pi.dev/install.sh | sh
```

[install.sh](install.sh) clones the repo, builds frontend + backend, writes
`~/.pi/agent/extensions/pi-webui/config.json` (auto-generating a token), starts the server
detached, and prints the token + `http://…/?token=…` URL. Idempotent (re-running updates,
rebuilds, restarts). Env overrides: `PI_WEBUI_REPO`, `PI_WEBUI_REF`, `PI_WEBUI_DIR`,
`PI_WEBUI_PORT` (default 8456), `PI_WEBUI_EXTERNAL=1` (bind `0.0.0.0` for a forwarded port).

Manual (same as the script):

```bash
# in the container (yolo user)
git clone https://github.com/MirecX/pi-webui ~/.pi/agent/extensions/pi-webui
cd ~/.pi/agent/extensions/pi-webui
make build      # builds C# backend + TS frontend
make run        # runs the server (token written to config.json, printed once)
```

The server loads `~/.pi/agent/extensions/pi-webui/config.json` (fallback to repo
`./config.json`), auto-generating + printing a token on first run (auth is
enforced in ticket #02). Open `http://<box-ip>:<PORT>/?token=<token>` — first load
needs the token (it sets a same-origin cookie afterwards). External reachability
= SSH host port + 10000, opt-in — DESIGN.md §6; see `docker-compose.example.yml` for
the container port mapping, which only takes effect when `external: true` is set in
the service config).

## Development

```bash
make build-backend   # dotnet build (Release)
make build-frontend  # npm install + tsc build into web/dist
make test            # dotnet test (fake-pi seams, no real pi needed)
```

Tests exercise the two seams against a scripted fake `pi` process
(`backend/tests/fixtures/fake-pi.mjs`) plus the in-process fake client:
RPC-client framing/parsing and the WebSocket relay, so no real `pi` child is
required. The tracer bullet (ticket #01) manages one default session; later
tickets add auth, abort/steer, models, multi-session, and HITL dialogs.

## Layout

```
backend/     C# / ASP.NET Core server (config, RPC client, session mgr, WS, static)
backend/tests  xUnit tests + fake-pi fixture
web/         TypeScript frontend (streaming renderer, src/ -> dist/)
Makefile     build/run/test launcher
DESIGN.md    architecture + decisions
```

## License

MIT
