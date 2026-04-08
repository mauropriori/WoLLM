#!/usr/bin/env bash
# ============================================================
#  WoLLM example script — ComfyUI
#  Copy to scripts/comfyui-sdxl.sh, chmod +x, adjust paths.
# ============================================================

COMFYUI_PATH="$HOME/ComfyUI"

cd "$COMFYUI_PATH"
source venv/bin/activate
python main.py --port 8188 --listen 127.0.0.1
