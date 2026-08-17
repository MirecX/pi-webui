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

```bash
# in the container (yolo user)
git clone https://github.com/MirecX/pi-webui ~/.pi/agent/extensions/pi-webui
cd ~/.pi/agent/extensions/pi-webui
make install      # builds C# backend + TS frontend
make start        # starts the server (token written to config.json, printed once)
```

Open `http://<box-ip>:<HOST_PORT+10000>` (e.g. `32223`) and use the token.

## Layout

```
backend/     C# / ASP.NET Core server (RPC client, session manager, WS, auth)
web/         TypeScript frontend (streaming renderer)
DESIGN.md    architecture + decisions
```

## License

MIT
