# WoLLM

**WoLLM** is a lightweight AI model orchestrator for Windows and Linux. It manages the lifecycle of local LLMs and image generation models via a simple REST API Ã¢â‚¬â€ loading models on demand, unloading them when idle, and optionally shutting down the system after a period of inactivity.

Built with **.NET 10** and **ASP.NET Core**, it runs as a background service (Windows Task Scheduler / Linux systemd) and acts as a transparent proxy coordinator between your client applications and the underlying model servers.

---

## Features

- **On-demand model switching** Ã¢â‚¬â€ load any configured model with a single HTTP call; the previous model is automatically killed first
- **Idle watchdog** Ã¢â‚¬â€ automatically unloads the active model after a configurable period of inactivity
- **Auto system shutdown** Ã¢â‚¬â€ optionally shuts down the machine after the idle timeout (useful for unattended inference rigs)
- **Health-check polling** Ã¢â‚¬â€ waits for the model server to become healthy before reporting success, with a configurable timeout
- **Process supervision** Ã¢â‚¬â€ detects unexpected model exits, keeps the desired model under supervision, and restarts it automatically with restart telemetry exposed by the API
- **Optional API key auth** Ã¢â‚¬â€ protect the API with a shared secret; leave empty for open/local access
- **Cross-platform** Ã¢â‚¬â€ full support for Windows (Task Scheduler, `.bat` scripts) and Linux (systemd, `.sh` scripts)
- **GPU/CUDA friendly** Ã¢â‚¬â€ launches scripts via shell so conda environments, CUDA drivers, and venvs are inherited correctly
- **Zero dependencies at runtime** Ã¢â‚¬â€ single self-contained executable, no .NET installation required on the target machine

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) *(build only Ã¢â‚¬â€ the published binary is self-contained)*
- A supported model backend, e.g.:
  - [llama-server](https://github.com/ggerganov/llama.cpp) for GGUF LLMs
  - [ComfyUI](https://github.com/comfyanonymous/ComfyUI) for image generation

---

## Installation

Download the archive for your platform from the latest [GitHub Release](https://github.com/mauropriori/WoLLM/releases), extract it to the target directory, then run the install script from that extracted folder.

Suggested asset names:

- `wollm-<version>-win-x64.zip`
- `wollm-<version>-linux-x64.tar.gz`
- `wollm-<version>-win-x64.zip.sha256`
- `wollm-<version>-linux-x64.tar.gz.sha256`

Download only from the official GitHub Releases page for this repository. Avoid repackaged mirrors or third-party archives so you can verify the published checksums and keep a single trusted distribution channel.

### Windows Ã¢â‚¬â€ Task Scheduler

```powershell
# Optional but recommended: verify the checksum first
$expected = (Get-Content .\wollm-<version>-win-x64.zip.sha256).Split()[0].ToLowerInvariant()
$actual = (Get-FileHash .\wollm-<version>-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
$actual -eq $expected

# Extract the release archive, open an elevated PowerShell in the extracted folder, then:
.\install-windows-release.ps1
```

`install-windows-release.ps1` copies the release files into `C:\Program Files\WoLLM`, tries to remove the Internet download mark from the extracted files, and registers a machine-level Task Scheduler entry that launches WoLLM at system boot. The task runs as `LocalSystem`.

Use local absolute paths for models and scripts on Windows. `LocalSystem` does not see user-mapped network drives or per-user profile state.

### Windows SmartScreen

Because `wollm.exe` is not yet Authenticode-signed, Microsoft Defender SmartScreen may show `Editore sconosciuto` the first time you run it on some machines. This is expected for unsigned binaries and does not by itself indicate malware.

To proceed safely:

1. Download only from the official GitHub Release page.
2. Verify the SHA256 checksum published next to the archive.
3. Extract the archive and run `install-windows-release.ps1` from an elevated PowerShell session.
4. If SmartScreen appears, use `Altre info` and then `Esegui comunque` only after confirming you are using the official release asset.

Code signing support is already prepared in the release workflow and can be enabled later by adding these GitHub secrets:

- `WOLLM_WINDOWS_SIGNING_CERT_BASE64`
- `WOLLM_WINDOWS_SIGNING_CERT_PASSWORD`
- `WOLLM_WINDOWS_SIGNING_CERT_THUMBPRINT` or `WOLLM_WINDOWS_SIGNING_CERT_SUBJECT`

### Linux Ã¢â‚¬â€ systemd user service

```bash
# Optional but recommended: verify the checksum first
sha256sum -c wollm-<version>-linux-x64.tar.gz.sha256

# Extract the release archive, open a shell in that folder, then:
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
  "loadModelOnStartup": "",
  "idleTimeoutMinutes": 5,
  "shutdownOnIdle": false,
  "unloadOnIdle": true,
  "healthCheckTimeoutSeconds": 120,
  "models": [
    {
      "name": "mistral-7b",
      "type": "llama",
      "port": 8081,
      "scriptPath": "scripts/mistral-7b.bat",
      "activityDetectionMode": "llama_slots",
      "activityPollSeconds": 5,
      "activityGraceSeconds": 30
    },
    {
      "name": "sdxl",
      "type": "comfyui",
      "port": 8188,
      "scriptPath": "scripts/comfyui-sdxl.bat",
      "activityDetectionMode": "comfy_queue",
      "activityPollSeconds": 5,
      "activityGraceSeconds": 120
    }
  ]
}
```

| Field | Default | Description |
|---|---|---|
| `port` | `8080` | Port WoLLM listens on |
| `apiKey` | `""` | API key required on every request (empty = public access) |
| `loadModelOnStartup` | `""` | Optional model name to load automatically when WoLLM starts |
| `idleTimeoutMinutes` | `5` | Minutes of inactivity before idle actions are triggered |
| `shutdownOnIdle` | `false` | If `true`, WoLLM shuts down the machine when the idle timeout expires |
| `unloadOnIdle` | `true` | If `true`, WoLLM unloads the active model when the idle timeout expires |
| `healthCheckTimeoutSeconds` | `120` | Max seconds to wait for a model to become healthy |
| `models[].name` | Ã¢â‚¬â€ | Unique model identifier used in API calls |
| `models[].type` | Ã¢â‚¬â€ | `llama` or `comfyui` |
| `models[].port` | Ã¢â‚¬â€ | Port the model server will bind to |
| `models[].scriptPath` | Ã¢â‚¬â€ | Path to the launch script (`.bat` on Windows, `.sh` on Linux) |

---

## Security Ã¢â‚¬â€ API Key

Additional per-model activity detection settings:

- `activityDetectionMode`: `llama_slots`, `comfy_queue`, or `none`; controls how WoLLM infers real backend usage
- `activityPollSeconds`: poll interval in seconds for backend activity detection
- `activityGraceSeconds`: conservative grace window applied after detected backend activity

If `loadModelOnStartup` is set, it must match one of the configured `models[].name` values. WoLLM will try to load that model automatically during startup.

By default `apiKey` is empty and the server is publicly accessible (suitable for trusted local networks).

To restrict access, generate a key and set it in `wollm.json`:

```json
"apiKey": "your-secret-key-here"
```

Every request must then include the key in the `X-Api-Key` header. Requests with a missing or wrong key receive `401 Unauthorized`:

```json
{ "error": "Unauthorized: invalid or missing API key." }
```

**Generating a key** Ã¢â‚¬â€ use any online generator, for example:
- https://www.uuidgenerator.net/
- https://generate-random.org/api-key-generator

Copy the generated value into `wollm.json` on the server, and pass the same value as the `X-Api-Key` header in your client requests.

---

## Model Scripts

Each model needs a launch script that starts its server on the configured port. See [`scripts/examples/`](scripts/examples/) for templates.

**Windows Ã¢â‚¬â€ LLM (llama-server)**
```bat
@echo off
set MODEL_PATH=C:\Models\mistral-7b-instruct-v0.2.Q4_K_M.gguf
set LLAMA_SERVER=C:\llama.cpp\llama-server.exe

"%LLAMA_SERVER%" --model "%MODEL_PATH%" --port 8081 --host 127.0.0.1 --n-gpu-layers 99 --ctx-size 4096
```

**Windows Ã¢â‚¬â€ Image generation (ComfyUI)**
```bat
@echo off
set COMFYUI_PATH=C:\ComfyUI
set PYTHON=%COMFYUI_PATH%\venv\Scripts\python.exe

cd /d "%COMFYUI_PATH%"
"%PYTHON%" main.py --port 8188 --listen 127.0.0.1
```

The scripts are executed via the system shell, so environment variables, conda/venv activation, and GPU driver state are fully inherited. WoLLM also captures each launched model process `stdout` and `stderr` into dedicated files under `logs/processes/<model-name>/` without requiring changes to the script commands themselves.

---

## API Reference

Base URL: `http://localhost:8080` (or whatever `port` is configured)

| Method | Endpoint | Description | Resets idle timer |
|---|---|---|---|
| `GET` | `/health` | Service health + verified model load state | No |
| `GET` | `/status` | Full status: verified model load state, supervisor/activity monitor state, idle timer, WoL boot state, and system stats | No |
| `GET` | `/models` | List all configured models | No |
| `GET` | `/logs` | In-memory log entries captured during the current WoLLM startup | No |
| `POST` | `/set?idleTimeoutMinutes=5&shutdown_on_idle=true\|false&unload_on_idle=true\|false` | Override idle settings for the current WoLLM runtime | Yes |
| `POST` | `/load/{modelName}` | Load a model (unloads the previous one first) | Yes |
| `POST` | `/unload` | Unload the currently active model | Yes |
| `POST` | `/shutdown?forceShutdown=true\|false` | Shut down the machine. Allowed automatically after WoL boot or when `shutdown_on_idle=true`; otherwise requires `forceShutdown=true` | No |

### `GET /health` response

Returns a lightweight service health snapshot plus the most recent model load result:

```json
{
  "status": "ok",
  "currentModel": "mistral-7b",
  "desiredModel": "mistral-7b",
  "loadStatus": "loaded"
}
```

### `GET /status` response

Returns the current orchestration state plus basic machine telemetry:

```json
{
  "currentModel": "mistral-7b",
  "desiredModel": "mistral-7b",
  "loadStatus": "loaded",
  "shutdownOnIdle": true,
  "unloadOnIdle": true,
  "idleTimeoutMinutes": 5,
  "idleSeconds": 42,
  "wolBoot": true,
  "supervisor": {
    "state": "running",
    "desiredModel": "mistral-7b",
    "processId": 12345,
    "processStartedAtUtc": "2026-04-20T12:30:03Z",
    "restartCount": 1,
    "consecutiveRestartFailures": 0,
    "lastUnexpectedExitAtUtc": "2026-04-20T12:30:00Z",
    "lastExitCode": 137,
    "lastRestartAttemptAtUtc": "2026-04-20T12:30:03Z",
    "lastRestartSucceededAtUtc": "2026-04-20T12:30:18Z",
    "lastRestartFailureAtUtc": null,
    "lastRestartFailure": null,
    "nextRestartAttemptAtUtc": null
  },
  "activityMonitor": {
    "mode": "llama_slots",
    "state": "active",
    "lastActivityAtUtc": "2026-04-20T12:30:45Z",
    "lastCheckAtUtc": "2026-04-20T12:30:47Z",
    "lastSuccessfulCheckAtUtc": "2026-04-20T12:30:47Z",
    "lastError": null,
    "consecutiveErrors": 0,
    "activityEvidence": "llama slot 0 processing"
  },
  "system": {
    "cpus": [],
    "ramUsedMb": 4096,
    "ramTotalMb": 32768,
    "gpus": []
  }
}
```

`/health` intentionally stays lightweight. Detailed supervisor and backend activity information is exposed only by `/status`.

`currentModel` is set only after the target model passes its health check. If the managed process crashes later, `currentModel` becomes `null` as soon as WoLLM observes the exit, while `desiredModel` stays set so the supervisor can restart it automatically.

Possible `loadStatus` values:
- `none`: no load has been attempted since WoLLM started
- `loading`: a load is currently in progress
- `loaded`: the last load completed successfully and the model is available
- `failed`: the last load attempt did not complete successfully

`supervisor.state` values:
- `idle`: no model is currently supervised
- `starting`: a manual `/load` is in progress
- `running`: the desired model is healthy and supervised
- `restarting`: WoLLM is attempting an automatic restart after an unexpected exit
- `failed`: the supervised model is currently down; WoLLM will retry with backoff and expose the last failure metadata in `supervisor.*`

`activityMonitor.state` values:
- `idle`: no backend is currently monitored
- `monitoring`: WoLLM is polling the backend but has not seen recent activity
- `active`: WoLLM has observed backend work or is inside the configured grace window
- `degraded`: activity polling is failing; WoLLM keeps retrying and reports the last error
- `unsupported`: the configured activity endpoint is not available on the backend

### `POST /shutdown` behavior

- If the machine was booted via Wake-on-LAN, shutdown is allowed.
- If `shutdown_on_idle=true` is currently active, shutdown is allowed.
- Otherwise the request is rejected unless `forceShutdown=true` is provided.

Rejected shutdown requests return `400 Bad Request` with:

```json
{
  "error": "Shutdown requires forceShutdown=true (system was not booted via WoL and shutdown_on_idle is not set)."
}
```

### `GET /logs` response

Returns the in-memory log buffer for the current WoLLM process only. These entries are reset on every restart and are separate from the persisted files in `logs/`.

```json
{
  "startup": "current",
  "entries": [
    {
      "timestamp": "2026-04-10T12:34:56.0000000Z",
      "level": "Information",
      "message": "Config loaded: 2 model(s), port 8080, idle timeout 5 min.",
      "exception": null
    }
  ]
}
```

### Example calls

Without API key (public server):
```bash
curl http://localhost:8080/status
curl http://localhost:8080/logs
curl -X POST http://localhost:8080/load/mistral-7b
curl -X POST "http://localhost:8080/set?idleTimeoutMinutes=10&shutdown_on_idle=true&unload_on_idle=true"
curl -X POST http://localhost:8080/unload
curl -X POST http://localhost:8080/shutdown
curl -X POST "http://localhost:8080/shutdown?forceShutdown=true"
```

With API key (protected server):
```bash
curl -H "X-Api-Key: your-secret-key-here" http://localhost:8080/status
curl -H "X-Api-Key: your-secret-key-here" http://localhost:8080/logs
curl -H "X-Api-Key: your-secret-key-here" -X POST http://localhost:8080/load/mistral-7b
curl -H "X-Api-Key: your-secret-key-here" -X POST "http://localhost:8080/set?idleTimeoutMinutes=10&shutdown_on_idle=true&unload_on_idle=true"
curl -H "X-Api-Key: your-secret-key-here" -X POST http://localhost:8080/unload
curl -H "X-Api-Key: your-secret-key-here" -X POST http://localhost:8080/shutdown
curl -H "X-Api-Key: your-secret-key-here" -X POST "http://localhost:8080/shutdown?forceShutdown=true"
```

---

## Architecture

```text
+--------------------------------------------------------+
|                         WoLLM                          |
|                                                        |
|  +------------+      +----------------------+          |
|  |  HTTP API  |----->|     IdleWatchdog     |          |
|  | (Kestrel)  |      |     (background)     |          |
|  +-----+------+      +----------+-----------+          |
|        |                        ^                      |
|        v                        | reset idle           |
|  +-----------------------------------------------+     |
|  |               ModelOrchestrator               |     |
|  |   desired model + healthy process state       |     |
|  +-------------+------------------+--------------+     |
|                |                  |                    |
|      supervises|                  |launches            |
|                v                  v                    |
|      +----------------+   +----------------------+     |
|      | ModelSupervisor|   |   ProcessLauncher    |     |
|      | (background)   |   |  (Windows / Linux)   |     |
|      +----------------+   +----------------------+     |
|                ^                                       |
|                | health / exit checks                  |
|                |                                       |
|      +------------------------------------------+      |
|      |       BackendActivityMonitor             |      |
|      |   polls /slots, /queue, /history         |----->|
|      |   infers real backend usage              |      |
|      +------------------------------------------+      |
+--------------------------------------------------------+
          |                                   |
          v                                   v
     llama-server                         ComfyUI
     :8081                                :8188
```

- **ModelOrchestrator** — thread-safe lifecycle state holder; tracks desired model, healthy model, and managed process state
- **ModelSupervisor** — background service that detects unexpected exits and restarts the supervised backend with backoff
- **BackendActivityMonitor** — background service that polls backend runtime endpoints such as `/slots`, `/queue`, and `/history` to infer real usage and extend the idle timer conservatively
- **IdleWatchdog** — background service that tracks the last activity timestamp and triggers unload (and optionally system shutdown) after the configured timeout
- **ProcessLauncher** — cross-platform process spawner; preserves shell environment for GPU/CUDA/venv access

---
## Build from Source

Prebuilt release assets are the recommended installation path for end users. Build from source only if you want to modify WoLLM or produce custom binaries.

```bash
# Clone the repo
git clone https://github.com/mauropriori/wollm.git
cd wollm

# Build (requires .NET 10 SDK)
dotnet build src/WoLLM/WoLLM.csproj

# Publish self-contained executable
dotnet publish src/WoLLM/WoLLM.csproj -c Release -r win-x64  -o dist/win-x64
dotnet publish src/WoLLM/WoLLM.csproj -c Release -r linux-x64 -o dist/linux-x64
```

The output is a single self-contained binary (`wollm.exe` / `wollm`) with no external .NET dependency.

## Releases and Versioning

- Application version metadata is defined in `Directory.Build.props`.
- GitHub Releases must use tags in the format `vX.Y.Z` (for example `v0.2.1`).
- The release workflow runs when a GitHub Release is published.
- The workflow validates that the tag version matches the project `Version`.
- If the versions differ, the workflow fails and no release assets are uploaded.
- Windows signing is optional and activates automatically when the signing secrets are configured in GitHub Actions.
- Successful releases upload:
  - `wollm-<version>-win-x64.zip`
  - `wollm-<version>-win-x64.zip.sha256`
  - `wollm-<version>-linux-x64.tar.gz`
  - `wollm-<version>-linux-x64.tar.gz.sha256`

---

## Backend Activity Detection

WoLLM can infer real backend usage and extend the idle timer without acting as an HTTP proxy.

- `activityDetectionMode`: `llama_slots`, `comfy_queue`, or `none`
- `activityPollSeconds`: poll interval in seconds for backend activity detection
- `activityGraceSeconds`: conservative grace window after detected backend activity

For `llama-server`, WoLLM polls `/slots` and keeps the model alive while slots are processing or task counters are advancing.

For `ComfyUI`, WoLLM polls `/queue` and, when available, `/history` to detect queued or recently completed work. The logic is conservative and prefers preventing premature unload/shutdown over aggressively expiring the idle timer.

`GET /status` now includes an `activityMonitor` object with:

- `mode`
- `state`
- `lastActivityAtUtc`
- `lastCheckAtUtc`
- `lastSuccessfulCheckAtUtc`
- `lastError`
- `consecutiveErrors`
- `activityEvidence`

---

## Logs

Application logs are written to `logs/wollm-<date>.log` with daily rotation and 7-day retention, using [Serilog](https://serilog.net/). Console output is also enabled.

For each model launch, WoLLM also captures child-process output into dedicated files under `logs/processes/<model-name>/`, with separate `stdout` and `stderr` files per launch. File names include a UTC timestamp and PID so restart attempts and crashes can be correlated without modifying the underlying `.bat` or `.sh` scripts.

---

## License

See [LICENSE](LICENSE) for details.
