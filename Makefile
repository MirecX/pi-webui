.PHONY: build build-backend build-frontend test run start clean

# pi-webui build + launcher (ticket #01 tracer bullet)
# Run from the repo root.

build-backend:
	cd backend && dotnet build -c Release

build-frontend:
	cd web && npm install && npm run build

build: build-backend build-frontend

test:
	cd backend && dotnet test tests/PiWebui.Tests.csproj

# Build once, then run the server. Config (token/port) is auto-created on first
# run under ~/.pi/agent/extensions/pi-webui/config.json (fallback ./config.json).
run:
	cd backend && dotnet run -c Release --no-build

start: build run

# Install the frontend artifacts into the backend's served wwwroot (optional;
# the server also resolves ../web/dist automatically when run from backend/).
install: build

clean:
	cd backend && dotnet clean
	rm -rf web/dist node_modules 2>/dev/null || true
