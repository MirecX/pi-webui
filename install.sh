#!/usr/bin/env sh
# pi-webui installer — clone, build, configure and start the pi-webui web service on demand.
#
#   curl -fsSL https://raw.githubusercontent.com/MirecX/pi-webui/master/install.sh | sh
#   (pi.dev/install.sh is a future short alias)
#
# Works in a yolobox container or any box with pi + .NET SDK + Node. Installs like
# pi-subagents (a repo under ~/.pi/agent/extensions/) but is NOT part of the image.
#
# Env overrides:
#   PI_WEBUI_REPO     git URL to clone (default https://github.com/MirecX/pi-webui)
#   PI_WEBUI_REF      branch/tag to use (default master)
#   PI_WEBUI_DIR      install dir  (default ~/.pi/agent/extensions/pi-webui)
#   PI_WEBUI_PORT     server port  (default 8456)
#   PI_WEBUI_EXTERNAL set to 1 to bind 0.0.0.0 (needs a forwarded/opt-in port); default localhost
set -eu

REPO="${PI_WEBUI_REPO:-https://github.com/MirecX/pi-webui}"
REF="${PI_WEBUI_REF:-master}"
HOME_DIR="${HOME:-/home/\u}"
TARGET="${PI_WEBUI_DIR:-$HOME_DIR/.pi/agent/extensions/pi-webui}"
PORT="${PI_WEBUI_PORT:-8456}"
EXTERNAL="${PI_WEBUI_EXTERNAL:-0}"

log()  { printf '\033[1;34m[pi-webui]\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31m[pi-webui] ERROR:\033[0m %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" = "0" ] && log "running as root; the server will run as root (config under $TARGET)"

# --- preflight ---
command -v git    >/dev/null 2>&1 || die "git not found"
command -v dotnet >/dev/null 2>&1 || die ".NET SDK not found (run inside a yolobox image or install .NET)"
command -v node   >/dev/null 2>&1 || die "node not found"
command -v npm    >/dev/null 2>&1 || die "npm not found"

# --- clone / update (idempotent) ---
if [ ! -d "$TARGET/.git" ]; then
  mkdir -p "$(dirname "$TARGET")"
  log "cloning $REPO (ref=$REF) -> $TARGET"
  git clone --branch "$REF" --depth 1 "$REPO" "$TARGET" 2>/dev/null || die "clone failed: $REPO"
else
  log "updating existing install at $TARGET"
  git -C "$TARGET" fetch --depth 1 origin "$REF" 2>/dev/null || true
  git -C "$TARGET" checkout -q "$REF" 2>/dev/null || true
  git -C "$TARGET" pull -q --ff-only 2>/dev/null || true
fi
cd "$TARGET"

# --- build frontend -> web/dist ---
log "building web frontend (web/dist)..."
[ -f web/package-lock.json ] && (cd web && npm ci --no-audit --no-fund >/dev/null 2>&1) || true
(cd web && npm install --no-audit --no-fund >/dev/null && npm run build >/dev/null) || die "web build failed"

# --- build backend (published binary; cwd stays = repo root so web/dist is found) ---
log "building backend (dotnet publish)..."
mkdir -p .run
dotnet publish backend/PiWebui.csproj -c Release -o .run/bin >/dev/null 2>&1 || die "dotnet publish failed"
[ -x .run/bin/pi-webui ] || die "publish produced no pi-webui binary"

# --- config ---
CONFIG="$TARGET/config.json"
if [ ! -f "$CONFIG" ]; then
  TOKEN="$(node -e 'process.stdout.write(require("crypto").randomBytes(32).toString("hex"))')"
  EX="false"; [ "$EXTERNAL" = "1" ] && EX="true"
  printf '{\n  "token": "%s",\n  "port": %s,\n  "external": %s\n}\n' "$TOKEN" "$PORT" "$EX" > "$CONFIG"
  chmod 600 "$CONFIG"
  log "wrote config $CONFIG"
else
  TOKEN="$(node -e 'const fs=require("fs"),d=JSON.parse(fs.readFileSync(process.argv[1]));process.stdout.write(d.token||"")' "$CONFIG" 2>/dev/null || true)"
  [ -n "$TOKEN" ] || die "config exists but has no token: $CONFIG"
  log "reusing existing config $CONFIG"
fi

# --- start (detached) ---
log "starting server on $([ "$EXTERNAL" = "1" ] && echo "0.0.0.0" || echo "127.0.0.1"):$PORT ..."
if [ -f .run/pi-webui.pid ]; then
  oldpid="$(cat .run/pi-webui.pid 2>/dev/null || true)"
  if [ -n "$oldpid" ] && kill -0 "$oldpid" 2>/dev/null; then
    kill "$oldpid" 2>/dev/null || true
    sleep 1
  fi
fi
# cwd = repo root so Frontend.ResolveWebroot finds ./web/dist
"./.run/bin/pi-webui" > ".run/pi-webui.log" 2>&1 &
PID=$!
echo "$PID" > .run/pi-webui.pid
sleep 2
kill -0 "$PID" 2>/dev/null || die "server failed to start — see $TARGET/.run/pi-webui.log"

HOST="localhost"
[ "$EXTERNAL" = "1" ] && HOST="<your-box-ip>"
log "pi-webui installed and running (pid $PID)"
log "  token:       $TOKEN"
log "  first open:  http://$HOST:$PORT/?token=$TOKEN   (then a cookie keeps you logged in)"
log "  ls sessions: $HOME_DIR/.pi/agent/sessions"
log "  stop:        kill \$(cat $TARGET/.run/pi-webui.pid)"
log "  logs:        $TARGET/.run/pi-webui.log"
