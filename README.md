# Rapid PC Use

Rapid PC Use is a clean-room, speed-first Windows computer-use driver for Codex and ChatGPT desktop. It gives the model the real Windows cursor and keyboard, one image per display, batched native actions, and physical-Escape takeover.

## Install

From PowerShell in this directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

The installer uses the bundled self-contained `win-x64` host, installs the `rapid-pc-use` personal plugin, enables only its MCP tools for automatic approval, and adds a marked global trigger block. Restart the ChatGPT desktop app and start a new task. Then ask naturally, for example:

- "Open Discord and message Alex that the deploy is finished."
- "Go to the AWS console and configure this infrastructure."
- "Watch this export and tell me when it finishes."
- "Use my PC to complete this form."

Press the physical **Escape** key at any time to cancel the current driver action, release held input, hide the overlay, and return control to yourself.

## Hot path

1. `pc_observe` captures every display as an independent JPEG and returns a `frame_id`.
2. `pc_act` runs up to 32 deterministic native actions and returns the post-action screenshots in the same tool result.
3. `pc_stop` releases ownership and all driver-held input.

Coordinates are monitor-local normalized integers from `0..1000`, so model-side image resizing, mixed DPI, and negative virtual-desktop origins do not change click mapping. Display-topology changes invalidate old frames.

The capture backend is isolated behind the host boundary and uses parallel GDI capture plus WIC JPEG encoding. On the development machine, a warm 2560x1600 capture completes in roughly 65 ms; the intended next optimization is a drop-in DXGI Desktop Duplication backend with presentation-aware settling.

## Safety and failure behavior

- The native Win32 overlay is click-through, topmost, and excluded from screenshots.
- A `WH_KEYBOARD_LL` hook reacts only to physical Escape, not driver-injected Escape.
- Input uses `SetCursorPos` and `SendInput`; typed text is emitted as Unicode keystrokes, never clipboard paste.
- A current-user ownership lease prevents two driver processes from controlling the same desktop.
- Any driver or transport failure is terminal for the current PC task. The driver releases control, records a correlation ID, and directs the model to perform one bounded read-only log lookup before giving a 1-3 sentence explanation. Retries, hidden fallbacks, and broad investigations are forbidden.
- Windows blocks normal-integrity automation of UAC secure desktop, lock/login screens, Ctrl+Alt+Delete, protected video, elevated apps, and some anti-cheat games.

ChatGPT Work in the desktop app can use the local plugin. Hosted ChatGPT Work on the web cannot directly launch a local stdio driver; that surface would need an authenticated local-daemon/remote-broker design and would add latency.

## Development

Build and republish the plugin binary with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

The build downloads the official .NET 9 SDK into `.tools\dotnet` only when no SDK is already available. End users receive a self-contained executable and do not need .NET installed.

The self-contained plugin redistributes .NET and WPF runtime components. Their license and third-party notices are included as `DOTNET-LICENSE.txt` and `DOTNET-THIRD-PARTY-NOTICES.txt` inside the plugin directory and are refreshed by every build.

Run the bounded active-control smoke check with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\smoke.ps1
```

Structured JSONL diagnostics are written continuously to `%LOCALAPPDATA%\RapidPcUse\rapid-pc-use.log`. Entries include session and operation IDs, tool timing, capture metrics, safe action metadata, native Windows error details, and full exception chains. The log rotates at 4 MB with three retained archives and never records screenshots, typed content, or literal key values.

Create the offline distributable with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1
```
