#!/usr/bin/env bash
# ============================================================
#  WoLLM example script — llama-server for Mistral 7B
#  Copy to scripts/mistral-7b.sh, chmod +x, adjust paths.
# ============================================================

MODEL_PATH="$HOME/models/mistral-7b-instruct-v0.2.Q4_K_M.gguf"
LLAMA_SERVER="$HOME/llama.cpp/llama-server"

"$LLAMA_SERVER" \
    --model "$MODEL_PATH" \
    --port 8081 \
    --host 127.0.0.1 \
    --n-gpu-layers 99 \
    --ctx-size 4096
