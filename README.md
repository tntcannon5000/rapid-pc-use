# Rapid PC Use

Rapid PC Use is a clean-room, speed-first Windows computer-use driver for Codex and ChatGPT desktop. It gives the model the real Windows cursor and keyboard, one image per display, batched native actions, and physical-Escape takeover.

This repository is a **public beta**. The capture backend is still GDI-based. Use it only on a desktop where you can safely take over with the physical Escape key, and install only signed release artifacts whose checksum and provenance verify.

## Requirements

- Windows 11 x64, or another x64 Windows release still supported by Microsoft and .NET 10.
- A current Codex or ChatGPT desktop build with local plugin support.
- PowerShell 5.1 or later for the install and verification scripts.

Do not install an unsigned beta executable. Verify the release archive against its published SHA-256, signature, SBOM, and build provenance before running it.

## Install

From PowerShell in this directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
```

The installer uses the bundled self-contained `win-x64` host, verifies that the copied executable hash is exact, installs the `rapid-pc-use` personal plugin, and configures its native tools to prompt for approval. Restart the ChatGPT desktop app and start a new task. Then ask naturally, for example:

- "Open Discord and message Alex that the deploy is finished."
- "Go to the AWS console and configure this infrastructure."
- "Watch this export and tell me when it finishes."
- "Use my PC to complete this form."

Press the physical **Escape** key at any time to cancel the current driver action, release held input, hide the overlay, and return control to yourself.

For a trusted, dedicated test machine only, `-EnableFastMode` is an explicit opt-in that disables per-call tool prompts. The default and recommended mode is prompted approval.

## Hot path

1. `pc_observe` captures every display as an independent JPEG and returns a `frame_id`.
2. `pc_act` runs up to 32 deterministic native actions and returns the post-action screenshots in the same tool result.
3. `pc_stop` releases ownership and all driver-held input.

Coordinates are monitor-local normalized integers from `0..1000`, so model-side image resizing, mixed DPI, and negative virtual-desktop origins do not change click mapping. Display-topology changes invalidate old frames; frames also expire after 30 seconds and are consumed by one action batch.

The capture backend is isolated behind the host boundary and uses parallel GDI capture plus WIC JPEG encoding. On the development machine, a warm 2560x1600 capture completes in roughly 65 ms; the intended next optimization is a drop-in DXGI Desktop Duplication backend with presentation-aware settling.

## Safety and failure behavior

- The native Win32 overlay is click-through and topmost. Windows is asked to exclude it from screenshots; a privacy-safe warning is recorded if that OS capability is unavailable.
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

The build requires the exact .NET 10.0.110 SDK pinned in `global.json`. If it is not installed system-wide, the script downloads the official SDK ZIP into `.tools\dotnet` only after verifying Microsoft's pinned SHA-512. End users receive a self-contained executable and do not need .NET installed.

The self-contained plugin redistributes .NET and WPF runtime components. Their license and third-party notices are included as `DOTNET-LICENSE.txt` and `DOTNET-THIRD-PARTY-NOTICES.txt` inside the plugin directory and are refreshed by every build.

Run the bounded active-control smoke check with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\smoke.ps1
```

Before a release, run the complete build, formatting, metadata, smoke, package, and checksum gate with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

Structured JSONL diagnostics are written continuously to `%LOCALAPPDATA%\RapidPcUse\rapid-pc-use.log`. Entries include session and operation IDs, tool timing, capture metrics, bounded safe action metadata, exception types, and numeric native error codes. Messages, stack traces, arbitrary exception data, screenshots, typed content, and literal key values are omitted. The log rotates at 4 MB with three retained archives.

Create the offline distributable with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1
```

Packaging rebuilds by default and writes both `dist\rapid-pc-use-win-x64.zip` and its `.sha256` companion. See `RELEASING.md` for the short release checklist.
