#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG_FILE="$HOME/.shipright/logs/shipright.log"
mkdir -p "$(dirname "$LOG_FILE")"

# Idempotency check: refuse to start a second instance
if lsof -ti:5200 > /dev/null 2>&1; then
    echo "ShipRight is already running on port 5200."
    echo "Stop the existing process before starting a new one."
    exit 1
fi

# Start backend
nohup "$SCRIPT_DIR/ShipRight" > "$LOG_FILE" 2>&1 &
BACKEND_PID=$!
echo "ShipRight started (PID $BACKEND_PID)"

# Wait for backend to be ready (max 10 seconds)
READY=0
for i in $(seq 1 20); do
    if curl -sf http://localhost:5200/api/health > /dev/null 2>&1; then
        echo "Backend ready."
        READY=1
        break
    fi
    sleep 0.5
done

if [ $READY -eq 0 ]; then
    echo "ERROR: Backend did not become ready within 10 seconds. Check $LOG_FILE"
    exit 1
fi

# Open browser (WSL only; omit on EC2)
wslview http://localhost:5200 2>/dev/null || true

# Tail the log so the developer sees backend output
tail -f "$LOG_FILE"
