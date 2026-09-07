# Rapid PC Use

[![CI](https://github.com/tntcannon5000/rapid-pc-use/actions/workflows/ci.yml/badge.svg)](https://github.com/tntcannon5000/rapid-pc-use/actions/workflows/ci.yml)
[![CodeQL](https://github.com/tntcannon5000/rapid-pc-use/actions/workflows/codeql.yml/badge.svg)](https://github.com/tntcannon5000/rapid-pc-use/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](./LICENSE)

Rapid PC Use is a clean-room, speed-first Windows computer-use driver for Codex and ChatGPT desktop. It gives the model the real Windows cursor and keyboard, one image per display, batched native actions, and physical-Escape takeover.

This repository is a **public beta**. The capture backend is still GDI-based and the Windows executable is not Authenticode-signed. Use it only on a desktop where you can safely take over with the physical Escape key.

## Requirements

- Windows 11 x64, or another x64 Windows release still supported by Microsoft and .NET 10.
- A current Codex or ChatGPT desktop build with local plugin support.
- PowerShell 5.1 or later for the install and verification scripts.

## Install

The supported agent-install experience is intentionally simple. Give a coding agent this repository URL and say **"Install this for me"**:

```text
https://github.com/tntcannon5000/rapid-pc-use

Install this for me.
```

The repository-root [`AGENTS.md`](./AGENTS.md) routes that explicit request to the complete [`agent_install/AGENT_INSTALL_INSTRUCTIONS.md`](./agent_install/AGENT_INSTALL_INSTRUCTIONS.md) procedure. A compatible agent will clone or safely update the official `main` branch, run the single supported installer, verify the installed copies, and tell you to restart Codex or ChatGPT desktop. Merely sharing the URL without asking to install it does not authorize installation.

For manual installation, run the same entry point from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\agent_install\install.ps1
```

The locally built public-beta executable is not Authenticode-signed, so install only from a checkout you trust. Restart Codex or ChatGPT desktop after installation. Then ask naturally, for example:

- "Open Discord and message Alex that the deploy is finished."
- "Go to the AWS console and configure this infrastructure."
- "Watch this export and tell me when it finishes."
- "Use my PC to complete this form."

Press the physical **Escape** key at any time to cancel the current driver action, release held input, hide the overlay, and return control to yourself.

For a trusted, dedicated test machine only, `-EnableFastMode` is an explicit opt-in that disables per-call tool prompts. The default and recommended mode is prompted approval.

## When Rapid PC Use runs

The Codex skill is available for implicit selection, but it is deliberately scoped to work that requires visible Windows GUI state. Codex should continue to use direct shell commands, filesystem tools, APIs, connectors, and structured browser automation when those routes can complete and verify the task without meaningful tradeoffs. In particular, Rapid PC Use should not be invoked merely to type commands into an open terminal.

Hybrid workflows are encouraged: a trusted direct command can inspect state or launch an application, then Rapid PC Use can take over only for the visual portion. When visual desktop control is actually required, the installed guidance prefers Rapid PC Use over built-in Computer Use.

## Hot path

The active performance contract, measured baselines, and medium-horizon architecture plan live in [`docs/PERFORMANCE_ROADMAP.md`](./docs/PERFORMANCE_ROADMAP.md).

When an inner model provider is configured, Codex starts visible-PC work with one `pc_run` call. It can include a compact first-turn execution brief and a strictly validated direct HTTP(S) or `discord:` launch. The driver automatically retrieves matching trusted local facts and structured runbooks before turn one; the inner controller can issue a later semantic query without returning to outer Codex. It can then select an opaque step ID for an exact stored local process launch, a fixed direct `.exe` invocation with fixed arguments and bounded output, or a fixed loopback app-interface call. It never supplies an executable path, command, URL, request body, argument, environment variable, or stdin. Rapid PC Use owns the screenshot → model → native action loop and returns a compact completion, blocker, limit, or bounded outer-assistance result. Trusted runbook steps execute directly inside that loop without a second operation-specific approval protocol.

Pointer activations have a native, cancellation-aware 80 ms button-up-to-button-down floor. The model still chooses longer waits at UI dependency frontiers, and every action batch is followed by a fresh capture before the next inner decision.

The outer Codex planner can retrieve and maintain a compact local machine profile through `pc_knowledge_search`/`pc_knowledge_update` and ordered routes through `pc_runbook_search`/`pc_runbook_update`. Facts are stored at `%LOCALAPPDATA%\RapidPcUse\knowledge-v1.json`; runbooks at `%LOCALAPPDATA%\RapidPcUse\runbooks-v1.json`. A runbook can mark an exact read-only loopback GET as `required_before_finish`. The driver arms that gate only for an explicitly selected route or the single strongest exact multiword task match, rejects completion until the verifier succeeds, and re-arms it after later consequential native or runbook mutations. Fixed local commands must instead be selected explicitly so process-launch authority is always enforced. This prevents stale pixels or a stale earlier verifier from becoming a false report without letting unrelated fuzzy matches block the task. On terminal run completion the driver automatically persists only privacy-safe per-step attempts, success/failure/uncertain outcome, and timings; equally relevant routes are then ranked by measured reliability and successful latency. Semantic facts and executable procedures remain outer-curated after independent verification so hostile screen content cannot teach itself a trusted command. The inner visual model can retrieve both stores but cannot write either. Knowledge and runbooks are navigation context, never authority, and must not contain secrets, message contents, screenshots, or untrusted page instructions.

This does not change the user-facing control experience: the same border is visible for the full active run, the same client approval applies to the high-level native-control call, and physical **Escape** still releases input, hides the border, and returns immediately to the main Codex model. Completed or paused runs release control themselves.

The compatible diagnostic/fallback path remains:

1. `pc_observe` captures every display as independent JPEGs by default and returns a `frame_id`; pass `capture_scope: "active_window"` for the foreground window only.
2. `pc_act` runs up to 32 deterministic native actions and returns the post-action screenshots in the same tool result.
3. `pc_stop` releases ownership and all driver-held input.

Coordinates are monitor-local normalized integers from `0..1000`, so model-side image resizing, mixed DPI, and negative virtual-desktop origins do not change click mapping. Display-topology changes invalidate old frames; frames also expire after 30 seconds and are consumed by one action batch.

Action batches are validated completely before a frame is consumed or any native input executes, including display IDs, keys, buttons, coordinates, and resource bounds. A malformed batch returns `PC_ACTION_REJECTED` without releasing control. Scroll inputs use the computer-use model convention: approximately 100 delta units become one bounded Windows wheel notch, so an input such as `591` is safely normalized to six notches. A stale frame is refreshed automatically, while a partially interrupted native batch returns the completed prefix and a fresh screenshot so work can continue from visible state.

The capture backend is isolated behind the host boundary and uses parallel GDI capture plus WIC JPEG encoding. High-level `pc_run` captures the foreground window, with automatic full-desktop fallback when no usable foreground window exists. The configured tier is a maximum short edge, not an upscaling target: the default 900 tier keeps smaller native windows at native resolution and reduces larger surfaces proportionally. Set `RAPID_PC_CAPTURE_TIER` to `720`, `900`, or `native` before starting the driver. A locally cleared 1×1 surface warms WPF imaging at process startup without capturing desktop content; measured first active-window capture fell from 136 ms to 15 ms. The intended next backend is DXGI Desktop Duplication with presentation-aware settling.

## Safety and failure behavior

- The native Win32 overlay is click-through and topmost. Windows is asked to exclude it from screenshots; a privacy-safe warning is recorded if that OS capability is unavailable.
- A `WH_KEYBOARD_LL` hook reacts only to physical Escape, not driver-injected Escape.
- Input uses `SetCursorPos` and `SendInput`; typed text is emitted as Unicode keystrokes, never clipboard paste.
- Absolute pointer moves are read back before any button event, and ignored relative motion is detected whenever the cursor has room to move. Cursor clipping, rewriting, or suppression interrupts the batch instead of silently clicking the wrong pixel; pointer-stage diagnostics remain specific across moves, clicks, drags, button holds, and scrolling.
- Native input is validated at the action that uses it rather than through a speculative pointer gate at control acquisition, so observation and keyboard-only work remain available. The executor enforces at least 80 ms between clicks and 5 ms between typed UTF-16 code units.
- A current-user ownership lease prevents two driver processes from controlling the same desktop.
- Recoverable request, frame, provider, progress, and partial-action conditions stay inside the visual loop. Only user takeover, an unavailable Windows security boundary, exhausted bounded recovery, or a broken driver transport ends control.
- Pre-execution `PC_ACTION_REJECTED` responses are recoverable request corrections, not driver failures. They execute nothing and do not release control.
- Windows blocks normal-integrity automation of UAC secure desktop, lock/login screens, Ctrl+Alt+Delete, protected video, elevated apps, and some anti-cheat games.

ChatGPT Work in the desktop app can use the local plugin. Hosted ChatGPT Work on the web cannot directly launch a local stdio driver; that surface would need an authenticated local-daemon/remote-broker design and would add latency.

## Internal model provider

The high-level loop is advertised automatically and uses `gpt-5.6-sol`, medium reasoning, and Codex fast mode by default. A persistent Codex app-server child is warmed in the background and reuses the user's saved ChatGPT sign-in; Codex owns and refreshes the session, while Rapid PC Use never reads, copies, or receives OAuth credentials. The optional direct OpenAI provider remains available for explicit Platform API-key configurations.

Every visual decision uses a fresh ephemeral Codex thread containing the stable controller instructions and schema, a maximum 2 KB structured working state, the last three bounded action outcomes, and only the current screenshot for each captured surface. The first empty thread is prepared during background startup without task text or screenshots. Screenshot files are deleted after the turn and the persistent app-server process is recycled after a bounded number of turns. Previous screenshots therefore never accumulate in the next model decision's context. Paused sessions retain no screenshot.

The inner controller is instructed to emit the longest deterministic program supported by the stable screen. If a terminal batch has a known exact visible success string, it can attach a completion guard and finish locally without a second model turn. Guard checks scan bounded native window text first and use UI Automation only as fallback; an unmatched or inaccessible guard returns to the normal screenshot/model loop.

Configuration is read once when the driver starts:

| Variable | Default |
| --- | --- |
| `RAPID_PC_AGENT_ENABLED` | Automatic for the Codex-session provider; set `0` to force the low-level route. |
| `RAPID_PC_AGENT_PROVIDER` | `codex` (`openai` remains available for explicit API-key use) |
| `RAPID_PC_AGENT_MODEL` | `gpt-5.6-sol` |
| `RAPID_PC_AGENT_REASONING` | `medium` |
| `RAPID_PC_AGENT_SERVICE_TIER` | `fast` |
| `RAPID_PC_AGENT_MAX_TURNS` | `48` |
| `RAPID_PC_AGENT_MAX_ACTIONS` | `96` |
| `RAPID_PC_AGENT_MAX_DURATION_MS` | `120000` |
| `RAPID_PC_AGENT_NO_PROGRESS_LIMIT` | `3` |
| `RAPID_PC_AGENT_IMAGE_DETAIL` | `original` after local 720p/900p downscaling |
| `RAPID_PC_AGENT_CODEX_PATH` | Automatic: newest Codex desktop runtime, then `codex.exe` on `PATH` |

Invalid or unavailable agent configuration disables only `pc_run`/`pc_continue`; it never prevents the native driver, knowledge tools, or low-level tools from starting. Transient provider and malformed-response failures are retried up to two times against the same configured endpoint before any native action executes. The driver never silently fails over to another provider.

## Real-world benchmark

The opt-in benchmark defines Discord DM roundtrip/cleanup, YouTube state restoration, and Amazon order counting cells in randomized model order. It starts a new driver process and ephemeral provider thread for every cell, records prompt-to-result TTC plus TTFT/output fill/capture/action/pacing layers, and omits account content and screenshots from its artifact. Amazon's aggregate observed item/return counts are retained only in the locally ignored result.

```powershell
.\scripts\real-world-benchmark.ps1 -RunLive
```

The default command runs the read-only Amazon matrix across Luna, Terra, and Sol. Copy `benchmark-config/example.json` to the ignored `benchmark-config/local.json` and use only dedicated test accounts. The current Discord and YouTube checks are model-attested, not independent account-state verification, so each mutation invocation accepts exactly one scenario/model/repetition, requires `-AllowAccountMutations`, records completion as provisional, and then stops. Closing or killing an app establishes only the window/process start state; it does not prove that a message, Like, or playlist mutation was restored. A complete multi-model mutation matrix requires an independent account-level verifier.

The privacy-filtered pass-one measurements and their limitations are recorded in [`docs/REAL_WORLD_BENCHMARK_RESULTS.md`](./docs/REAL_WORLD_BENCHMARK_RESULTS.md).

Optional `pc_run` budgets are hints for shorter runs. If an outer agent supplies an integer below or above the configured range, the driver normalizes it to the nearest supported bound and continues; a harmless budget mismatch cannot terminate the visible workflow. Effective budgets are recorded in the agent-run telemetry, while requested numeric budgets are recorded without task or process-name content for diagnosis.

## Development

Build and republish the plugin binary with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

The build requires the exact .NET 10.0.110 SDK pinned in `global.json`. If it is not installed system-wide, the script downloads the official SDK ZIP into `.tools\dotnet` only after verifying Microsoft's pinned SHA-512. The resulting executable is self-contained.

The self-contained plugin redistributes .NET and WPF runtime components. Their license and third-party notices are included as `DOTNET-LICENSE.txt` and `DOTNET-THIRD-PARTY-NOTICES.txt` inside the plugin directory and are refreshed by every build.

Run the bounded active-control smoke check with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\smoke.ps1
```

Before committing, run the complete build, formatting, metadata, security-test, and smoke gate with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

See [`CONTRIBUTING.md`](./CONTRIBUTING.md) for change guidelines and [`docs/RELEASING.md`](./docs/RELEASING.md) for the maintainer release checklist.

Structured JSONL diagnostics are written continuously to `%LOCALAPPDATA%\RapidPcUse\rapid-pc-use.log`. Entries include session and operation IDs; response-to-request loop gaps; request and response sizes; per-action timings; settle timing; capture, resize, and JPEG stage timing; encoded dimensions and bytes; conservative cumulative image-patch context estimates; exception types; and numeric native error codes. High-level runs additionally report provider/model identifiers, image staging, connection acquisition, thread setup, payload construction, response headers/first event/first decision delta/decision completion, parse, policy, action, adaptive settle, capture, completion guard, knowledge retrieval, runbook dispatch/readiness, terminal route-learning persistence, decision routing, token/cache counters, progress signals, and aggregate run timing. Failed model attempts identify the bounded provider stage, a safe reason code, retry decision, latency, exception type, and HRESULT. `scripts/profile.ps1` projects local retrieval, runbook execution, and terminal route learning as separate totals. Messages, task text, model prose, state text, retrieved content, runbook keys/targets/results, stack traces, arbitrary exception data, exception messages, screenshots, image hashes, typed content, literal key values, window titles, credentials, and provider response bodies are omitted. The log rotates at 4 MB with three retained archives.

Profile the latest driver session with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\profile.ps1
```

Use `-SessionId <id>` for an older session or `-Json` for machine-readable output. Pass `-CodexRolloutPath <rollout.jsonl>` to join numeric `token_count` events and report active-input growth, cached input, and uncached input without emitting prompt or screenshot content. The reported response-to-request gap is the exact client/model/orchestration interval visible to the driver: it begins after the previous MCP response is flushed and ends when the next request is read. Cumulative screenshot counts and 32-pixel patch estimates are deliberately labeled as upper bounds because the MCP server itself cannot observe whether its client retained, compacted, or pruned earlier tool results.

When the session contains high-level runs, the profiler automatically selects the latest one. Use `-AgentRunId <run-id>` to select another; this resolves the owning driver session even if another process wrote a newer log entry. The agent section reports actions per second, actions per model turn, display topology, outer MCP calls avoided, capture stages, provider-local stages, model stream milestones, parse/policy/routing time, native action/adaptive-settle time, completion-guard results, token/cache counters, image bytes, visual-progress signals, and bounded recovery categories.

`scripts/benchmark-matrix.ps1` runs a seeded, repetition-balanced Luna/Terra/Sol × none/low × 720p/900p live matrix through the saved Codex ChatGPT session. It requires the explicit `-RunLive` switch, waits for background provider warm-up, gracefully flushes each process's telemetry, validates the camel-case `pc_run` result contract against that telemetry, and writes raw rows plus p50/p95 summaries to an ignored `benchmark-results` artifact. Use at least 20 repetitions for release comparisons.

For publishable successful-APM results, pass `-FixtureScript` with a reviewed PowerShell fixture adapter. The script is called before and after every run with `-Phase Reset` or `-Phase Verify` plus `-FixtureId`, `-RunNumber`, `-Repetition`, `-Model`, `-Reasoning`, and `-CaptureTier`. Reset must restore a deterministic state. Verify must emit only a bounded result such as:

```json
{"success":true,"usefulActions":7}
```

A failed verification also supplies a bounded identifier, for example `{"success":false,"usefulActions":2,"failureCategory":"wrong_state"}`. Without this adapter the harness deliberately leaves success rate and successful APM null; model-reported completion and JSON action-object throughput are not treated as proof of useful work. Task text, screenshots, typed content, model output, and raw fixture output are excluded from benchmark artifacts.

The repository includes `click-ladder-v1` and `form-tab-v1` as visible, deterministic Windows fixtures. Measure the no-model actuator/capture ceiling with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\actuator-baseline.ps1 -RunLocal -FixtureId form-tab-v1 -Repetitions 20 -WarmupRuns 1 -CaptureTier 900 -CaptureScope active_window
```

This path performs real keyboard/mouse input, verifies terminal fixture state independently, separates initial capture, action preparation, native dispatch, settle, and post-action capture, and writes raw rows plus percentiles to ignored `benchmark-results` JSON. It makes no model request.

Record the skilled-human comparison on the identical fixture with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\human-baseline.ps1 -RunHuman -FixtureId form-tab-v1 -Repetitions 5
```

Run the click-ladder fixture separately rather than averaging unlike workloads. Human timing starts on the first accepted fixture input, so process launch and window activation do not inflate the muscle-memory rate.
