# WoLLM

**WoLLM** is a lightweight AI model orchestrator for Windows and Linux. It manages the lifecycle of local LLMs and image generation models via a simple REST API — loading models on demand, unloading them when idle, and optionally shutting down the system after a period of inactivity.

Built with **.NET 10** and **ASP.NET Core**, it runs as a background service (Windows Task Scheduler / Linux systemd) and acts as a transparent proxy coordinator between your client applications and the underlying model servers.

---

## Features

- **On-demand model switching** — load any configured model with a single HTTP call; the previous model is automatically killed first
- **Idle watchdog** — automatically unloads the active model after a configurable period of inactivity
- **Auto system shutdown** — optionally shuts down the machine after the idle timeout (useful for unattended inference rigs)
- **Health-check polling** — waits for the model server to become healthy before reporting success, with a configurable timeout
- **Optional API key auth** — protect the API with a shared secret; leave empty for open/local access
- **Cross-platform** — full support for Windows (Task Scheduler, `.bat` scripts) and Linux (systemd, `.sh` scripts)
- **GPU/CUDA friendly** — launches scripts via shell so conda environments, CUDA drivers, and venvs are inherited correctly
- **Zero dependencies at runtime** — single self-contained executable, no .NET installation required on the target machine

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) *(build only — the published binary is self-contained)*
- A supported model backend, e.g.:
  - [llama-server](https://github.com/ggerganov/llama.cpp) for GGUF LLMs
  - [ComfyUI](https://github.com/comfyanonymous/ComfyUI) for image generation

---

## Installation

### Windows — Task Scheduler

```powershell
# Run once as the user that owns the GPU/models
.\install-windows.ps1
```

This registers a Task Scheduler entry that launches WoLLM at user logon (Limited run level, so GPU drivers and conda environments are available).

### Linux — systemd user service

```bash
chmod +x install-linux.sh
./install-linux.sh
```

This installs a `systemd --user` service with automatic restart on failure.

---

## Configuration

Copy `wollm.example.json` to `wollm.json` in the same directory as the executable and edit it:

```json
{
  "port": 8080,
  "apiKey": "",
  "idleTimeoutMinutes": 5,
  "healthCheckTimeoutSeconds": 120,
  "models": [
    {
      "name": "mistral-7b",
      "type": "llama",
      "port": 8081,
      "script": {
        "win":  "scripts/mistral-7b.bat",
        "unix": "scripts/mistral-7b.sh"
      }
    },
    {
      "name": "sdxl",
      "type": "comfyui",
      "port": 8188,
      "script": {
        "win":  "scripts/comfyui-sdxl.bat",
        "unix": "scripts/comfyui-sdxl.sh"
      }
    }
  ]
}
```

| Field | Default | Description |
|---|---|---|
| `port` | `8080` | Port WoLLM listens on |
| `apiKey` | `""` | API key required on every request (empty = public access) |
| `idleTimeoutMinutes` | `5` | Minutes of inactivity before auto-unload |
| `healthCheckTimeoutSeconds` | `120` | Max seconds to wait for a model to become healthy |
| `models[].name` | — | Unique model identifier used in API calls |
| `models[].type` | — | `llama` or `comfyui` |
| `models[].port` | — | Port the model server will bind to |
| `models[].script.win` | — | Path to the Windows `.bat` launch script |
| `models[].script.unix` | — | Path to the Linux/macOS `.sh` launch script |

---

## Security — API Key

By default `apiKey` is empty and the server is publicly accessible (suitable for trusted local networks).

To restrict access, generate a key and set it in `wollm.json`:

```json
"apiKey": "your-secret-key-here"
```

Every request must then include the key in the `X-Api-Key` header. Requests with a missing or wrong key receive `401 Unauthorized`:

```json
{ "error": "Unauthorized: invalid or missing API key." }
```

**Generating a key** — use any online generator, for example:
- https://www.uuidgenerator.net/
- https://generate-random.org/api-key-generator

Copy the generated value into `wollm.json` on the server, and pass the same value as the `X-Api-Key` header in your client requests.

---

## Model Scripts

Each model needs a launch script that starts its server on the configured port. See [`scripts/examples/`](scripts/examples/) for templates.

**Windows — LLM (llama-server)**
```bat
@echo off
set MODEL_PATH=C:\Models\mistral-7b-instruct-v0.2.Q4_K_M.gguf
set LLAMA_SERVER=C:\llama.cpp\llama-server.exe

"%LLAMA_SERVER%" --model "%MODEL_PATH%" --port 8081 --host 127.0.0.1 --n-gpu-layers 99 --ctx-size 4096
```

**Windows — Image generation (ComfyUI)**
```bat
@echo off
set COMFYUI_PATH=C:\ComfyUI
set PYTHON=%COMFYUI_PATH%\venv\Scripts\python.exe

cd /d "%COMFYUI_PATH%"
"%PYTHON%" main.py --port 8188 --listen 127.0.0.1
```

The scripts are executed via the system shell, so environment variables, conda/venv activation, and GPU driver state are fully inherited.

---

## API Reference

Base URL: `http://localhost:8080` (or whatever `port` is configured)

| Method | Endpoint | Description | Resets idle timer |
|---|---|---|---|
| `GET` | `/health` | Service health + active model name | No |
| `GET` | `/status` | Full status: model, idle time, shutdown flag | No |
| `GET` | `/models` | List all configured models | No |
| `POST` | `/session/start?shutdown_on_idle=true\|false` | Start a session, enable/disable auto-shutdown | Yes |
| `POST` | `/switch/{modelName}` | Load a model (unloads the previous one first) | Yes |
| `POST` | `/unload` | Unload the currently active model | Yes |

### Example calls

Without API key (public server):
```bash
curl http://localhost:8080/status
curl -X POST http://localhost:8080/switch/mistral-7b
curl -X POST "http://localhost:8080/session/start?shutdown_on_idle=true"
curl -X POST http://localhost:8080/unload
```

With API key (protected server):
```bash
curl -H "X-Api-Key: your-secret-key-here" http://localhost:8080/status
curl -H "X-Api-Key: your-secret-key-here" -X POST http://localhost:8080/switch/mistral-7b
curl -H "X-Api-Key: your-secret-key-here" -X POST "http://localhost:8080/session/start?shutdown_on_idle=true"
curl -H "X-Api-Key: your-secret-key-here" -X POST http://localhost:8080/unload
```

---

## Architecture

```
┌─────────────────────────────────────────────┐
│                  WoLLM                      │
│                                             │
│  ┌──────────────┐   ┌──────────────────┐   │
│  │  HTTP API    │   │  IdleWatchdog    │   │
│  │  (Kestrel)   │──▶│  (background)    │   │
│  └──────┬───────┘   └────────┬─────────┘   │
│         │                    │             │
│         ▼                    ▼             │
│  ┌──────────────────────────────────────┐  │
│  │         ModelOrchestrator            │  │
│  │  (SwitchAsync / UnloadAsync)         │  │
│  └──────────────────┬───────────────────┘  │
│                     │                      │
│         ┌───────────▼──────────┐           │
│         │   ProcessLauncher    │           │
│         │ (Windows / Linux)    │           │
│         └──────────────────────┘           │
└─────────────────────────────────────────────┘
         │                   │
         ▼                   ▼
  llama-server           ComfyUI
  :8081                  :8188
```

- **ModelOrchestrator** — thread-safe model lifecycle manager; polls the model's health endpoint until it's ready
- **IdleWatchdog** — background service that tracks the last activity timestamp and triggers unload (and optionally system shutdown) after the configured timeout
- **ProcessLauncher** — cross-platform process spawner; preserves shell environment for GPU/CUDA/venv access

---

## Build from Source

```bash
# Clone the repo
git clone https://github.com/your-org/wollm.git
cd wollm

# Build (requires .NET 10 SDK)
dotnet build src/WoLLM/WoLLM.csproj

# Publish self-contained executable
dotnet publish src/WoLLM/WoLLM.csproj -c Release -r win-x64  -o dist/win-x64
dotnet publish src/WoLLM/WoLLM.csproj -c Release -r linux-x64 -o dist/linux-x64
```

The output is a single self-contained binary (`wollm.exe` / `wollm`) with no external .NET dependency.

---

## Logs

Logs are written to `logs/wollm-<date>.log` with daily rotation and 7-day retention, using [Serilog](https://serilog.net/). Console output is also enabled.

---

## License

See [LICENSE](LICENSE) for details.
