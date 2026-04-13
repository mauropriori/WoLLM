#!/usr/bin/env bash
# ============================================================
#  WoLLM — Linux install script
#  Installs WoLLM as a systemd user service (no root needed).
#  The service starts when the user logs in and inherits the
#  user's environment (PATH, CUDA, conda, virtualenvs).
# ============================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WOLLM_BIN="$SCRIPT_DIR/wollm"
SERVICE_DIR="$HOME/.config/systemd/user"
SERVICE_FILE="$SERVICE_DIR/wollm.service"

if [[ ! -f "$WOLLM_BIN" ]]; then
    echo "ERROR: wollm binary not found at: $WOLLM_BIN" >&2
    exit 1
fi

chmod +x "$WOLLM_BIN"
mkdir -p "$SERVICE_DIR"

cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=WoLLM AI Process Manager
After=network.target

[Service]
Type=simple
ExecStart=$WOLLM_BIN
WorkingDirectory=$SCRIPT_DIR
Restart=on-failure
RestartSec=5s
Environment=HOME=$HOME
Environment=PATH=$PATH

[Install]
WantedBy=default.target
EOF

systemctl --user daemon-reload
systemctl --user enable wollm.service
systemctl --user start  wollm.service

echo ""
echo "WoLLM user service installed and started."
echo "  Status:  systemctl --user status wollm"
echo "  Logs:    journalctl --user -u wollm -f"
echo "  Stop:    systemctl --user stop wollm"
echo "  Remove:  systemctl --user disable wollm && rm $SERVICE_FILE"
echo ""
echo "NOTE: To auto-start WoLLM at boot (without login), enable linger:"
echo "  loginctl enable-linger $USER"
