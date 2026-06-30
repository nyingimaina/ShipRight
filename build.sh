#!/bin/bash
# build.sh — Builds ShipRight as a self-contained Linux binary and deploys it
# to DEPLOY_DIR alongside run.sh. Safe to run multiple times (re-entrant).
#
# Usage:
#   bash build.sh                        # deploy to default: ~/shipright-app
#   bash build.sh /some/other/dir        # deploy to a custom directory
#
# What it does:
#   1. Finds the ShipRight source relative to this script (works from any CWD)
#   2. Builds the Next.js frontend (SSG export)
#   3. Copies the frontend output into back-end/ShipRight/wwwroot/
#   4. Publishes the .NET backend as linux-x64 self-contained single binary
#   5. Copies the binary + run.sh to DEPLOY_DIR
#   6. Makes run.sh + binary executable
#
# Re-entrancy: each step is idempotent. Existing files in DEPLOY_DIR are
# overwritten. Running while ShipRight is already running is safe — the old
# process keeps the old binary; restart with run.sh to pick up the new one.

set -euo pipefail

# ── Colours for stdout ────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
log()  { echo -e "${CYAN}[build]${NC} $*"; }
ok()   { echo -e "${GREEN}[build]${NC} ✓ $*"; }
warn() { echo -e "${YELLOW}[build]${NC} ⚠ $*"; }
fail() { echo -e "${RED}[build]${NC} ✗ $*"; exit 1; }

# ── Locate script directory (works with symlinks) ─────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BACKEND_DIR="$SCRIPT_DIR/back-end/ShipRight.Server"
FRONTEND_DIR="$SCRIPT_DIR/front-end"
DEPLOY_DIR="${1:-"$HOME/shipright-app"}"

log "ShipRight build starting"
log "  Source:  $SCRIPT_DIR"
log "  Deploy:  $DEPLOY_DIR"
echo ""

# ── Preflight checks ──────────────────────────────────────────────────────────
log "Checking prerequisites…"

command -v dotnet >/dev/null 2>&1 || fail "dotnet not found. Install .NET 8 SDK."
command -v node   >/dev/null 2>&1 || fail "node not found. Install Node.js 20+."
command -v npm    >/dev/null 2>&1 || fail "npm not found."

DOTNET_VER=$(dotnet --version)
NODE_VER=$(node --version)
log "  dotnet $DOTNET_VER"
log "  node   $NODE_VER"

[[ -d "$BACKEND_DIR"  ]] || fail "Backend directory not found: $BACKEND_DIR"
[[ -d "$FRONTEND_DIR" ]] || fail "Frontend directory not found: $FRONTEND_DIR"
ok "Prerequisites satisfied"
echo ""

# ── Step 1: Frontend build ─────────────────────────────────────────────────────
log "Step 1/4 — Building Next.js frontend…"
cd "$FRONTEND_DIR"

if [[ ! -d "node_modules" ]]; then
    log "  node_modules missing — running npm install…"
    npm install --silent
fi

npm run build
ok "Frontend built → $FRONTEND_DIR/out"
echo ""

# ── Step 2: Copy frontend output to wwwroot ────────────────────────────────────
log "Step 2/4 — Copying frontend to wwwroot…"
WWWROOT="$BACKEND_DIR/wwwroot"
mkdir -p "$WWWROOT"

# Remove old static files (keep .gitkeep if present)
find "$WWWROOT" -mindepth 1 -not -name '.gitkeep' -delete 2>/dev/null || true

cp -r "$FRONTEND_DIR/out/." "$WWWROOT/"
ok "Frontend deployed to wwwroot ($(find "$WWWROOT" -type f | wc -l | tr -d ' ') files)"
echo ""

# ── Step 3: Publish .NET backend ──────────────────────────────────────────────
log "Step 3/4 — Publishing ShipRight backend (linux-x64 self-contained)…"
cd "$BACKEND_DIR"

PUBLISH_DIR="$BACKEND_DIR/publish"
rm -rf "$PUBLISH_DIR"

dotnet publish \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -o "$PUBLISH_DIR" \
    --nologo

BINARY="$PUBLISH_DIR/ShipRight.Server"
[[ -f "$BINARY" ]] || fail "Published binary not found at $PUBLISH_DIR/ShipRight.Server"
ok "Backend published → $BINARY ($(du -sh "$BINARY" | cut -f1))"
echo ""

# ── Step 4: Deploy to target directory ────────────────────────────────────────
log "Step 4/4 — Deploying to $DEPLOY_DIR…"
mkdir -p "$DEPLOY_DIR"

cp "$BINARY"         "$DEPLOY_DIR/ShipRight.Server"
cp "$BACKEND_DIR/run.sh" "$DEPLOY_DIR/run.sh"

chmod +x "$DEPLOY_DIR/ShipRight.Server"
chmod +x "$DEPLOY_DIR/run.sh"

ok "Deployed:"
log "  $DEPLOY_DIR/ShipRight.Server"
log "  $DEPLOY_DIR/run.sh"
echo ""

# ── Done ──────────────────────────────────────────────────────────────────────
echo -e "${GREEN}════════════════════════════════════════${NC}"
echo -e "${GREEN}  ShipRight build complete${NC}"
echo -e "${GREEN}════════════════════════════════════════${NC}"
echo ""
echo "  To start ShipRight:"
echo ""
echo -e "    ${CYAN}bash $DEPLOY_DIR/run.sh${NC}"
echo ""

if lsof -ti:5200 >/dev/null 2>&1; then
    warn "Port 5200 is already in use — ShipRight may already be running."
    warn "Stop the existing process before running run.sh to pick up the new build."
fi
