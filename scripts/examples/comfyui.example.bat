@echo off
REM ============================================================
REM  WoLLM example script — ComfyUI
REM  Copy to scripts/comfyui-sdxl.bat and adjust paths.
REM ============================================================

set COMFYUI_PATH=C:\ComfyUI
set PYTHON=%COMFYUI_PATH%\venv\Scripts\python.exe

cd /d "%COMFYUI_PATH%"
"%PYTHON%" main.py --port 8188 --listen 127.0.0.1
