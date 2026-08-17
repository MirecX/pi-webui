.PHONY: build build-backend build-frontend test run start install clean

# pi-webui build + launcher (ticket #01 tracer bullet)
# Run from the repo root.
#
# Runtime artifacts live in .run/ (server logs + PID file). The token is echoed
# only on first-run generation (see Config.Load), not on every startup.

build-backend:
	cd backend && dotnet build -c Release

build-frontend:
	cd web && npm install && npm run build

build: build-backend build-frontend

test:
	cd backend && dotnet test tests/PiWebui.Tests.csproj

# Build once, then run the server FOREGROUND (tied to the current shell).
# Config (token/port) is auto-created on first run under
# ~/.pi/agent/extensions/pi-webui/config.json (fallback ./config.json).
run: build
	cd backend && dotnet run -c Release --no-build

# Build once, then launch the server DETACHED (nohup) so it survives without an
# SSH session. Logs go to .run/server.log and the process id to .run/server.pid.
start: build
	@mkdir -p .run
	cd backend && nohup dotnet run -c Release --no-build > ../.run/server.log 2>&1 &
	@echo $$! > .run/server.pid
	@echo "[pi-webui] server started detached (pid $$(cat .run/server.pid)); logs: .run/server.log"

# Install the frontend artifacts into the backend's served wwwroot (optional;
# the server also resolves ../web/dist automatically when run from backend/).
install: build

clean:
	cd backend && dotnet clean
	rm -rf web/dist node_modules 2>/dev/null || true
