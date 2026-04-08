@echo off
REM ============================================================
REM  WoLLM example script — llama-server for Mistral 7B
REM  Copy to scripts/mistral-7b.bat and adjust paths.
REM ============================================================

set MODEL_PATH=C:\Models\mistral-7b-instruct-v0.2.Q4_K_M.gguf
set LLAMA_SERVER=C:\llama.cpp\llama-server.exe

"%LLAMA_SERVER%" ^
    --model "%MODEL_PATH%" ^
    --port 8081 ^
    --host 127.0.0.1 ^
    --n-gpu-layers 99 ^
    --ctx-size 4096
