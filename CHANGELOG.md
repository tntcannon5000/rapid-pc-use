# Changelog

## Unreleased

## 0.2.0 - 2026-09-04

- Enable precise implicit skill routing for genuinely visual Windows tasks while keeping shell, API, connector, filesystem, and structured-browser work on their faster native paths.
- Allow hybrid workflows to use deterministic inspection and launch steps before handing only the visual remainder to Rapid PC Use.
- Add driver-local semantic retrieval, structured trusted runbooks, exact local process launches, fixed direct-process commands with stored arguments and bounded output, fixed loopback app-interface steps with declared effect authority, driver-enforced read-only finish verifiers, and route profiling inside the fast PC loop.
- Learn privacy-safe per-step reliability and latency after terminal runs and use it to rank equally relevant trusted routes without persisting task text, output, paths, or arguments.
- Bind learned performance to an execution fingerprint, bound terminal learning lock waits, and isolate fixed commands with closed stdin, a minimal non-secret environment, and kill-on-close descendant containment.
- Add native 80 ms pointer pacing, dependency-frontier action batching, and pacing telemetry.
- Add resumable, authority-preserving outer assistance through `pc_continue`, with explicit untrusted-handoff boundaries.
- Add dedicated remote-content-change policy without weakening independently inferred local-deletion risk.
- Add a bounded, atomic, cross-process-locked local PC knowledge store and retrieval/update tools.
- Add privacy-filtered Discord, YouTube, and Amazon real-world benchmark adapters with model-isolated runs, terminal takeover semantics, honest provisional verification, and residual-state aborts.
- Replace the fixed maximum-edge screenshot resize with aspect-aware 720/900 short-edge tiers, including canonical 16:9, 16:10, portrait, and ultrawide mappings without upscaling small displays.
- Add per-action, settle, capture-stage, MCP serialization, and response-to-request loop telemetry plus a local session profiler.
- Add privacy-safe cumulative screenshot context estimates and exact-repeat counts while explicitly separating unavailable provider cache metrics.
- Add an in-process PC agent loop exposed as `pc_run`/`pc_resume`, using the saved Codex ChatGPT session by default while preserving the existing border, approval surface, physical-Escape takeover, and low-level fallback tools.
- Normalize out-of-range optional `pc_run` budgets to configured ceilings instead of terminating the workflow, and record the non-sensitive requested numeric budgets for diagnosis.
- Raise the default internal PC-loop ceiling from 24 to 48 model turns for longer visual workflows.
- Keep one persistent Codex app-server process off the per-action path, but use a fresh ephemeral thread and immediately deleted current-frame files for every decision so screenshots never accumulate in later model context.
- Add strict decision schemas, capability and confirmation policy, no-progress termination, provider timing/token telemetry, and deterministic no-network agent tests.
- Make pre-execution action validation recoverable: invalid batches now return structured `PC_ACTION_REJECTED` correction data without consuming the frame or releasing control, while preserving atomic validation and terminal handling for real driver failures.
- Align public and inner action schemas with computer-use model conventions, normalize scroll deltas to bounded Windows wheel input, and preflight displays, keys, buttons, and coordinates before consuming a frame.
- Keep stale frames, transient provider/protocol faults, partial native execution, and repeated no-progress states inside a bounded recovery loop; add adaptive change-aware capture, cursor-free perceptual progress fingerprints, and compact one-sentence agent memory.

All notable changes to Rapid PC Use are recorded here.

## 0.1.3 - 2026-07-16

Security-hardening beta.

- Update the self-contained runtime to .NET 10.0.10 using SDK 10.0.110.
- Require explicit plugin invocation and per-tool approval by default.
- Verify the official SDK archive against a pinned Microsoft SHA-512 before extraction.
- Add cancellation, resource bounds, privacy redaction, frame freshness, security tests, and protected CI controls.
- Document the unsigned public-beta boundary and retain signed Git history without requiring paid Authenticode infrastructure.
- Provide a one-command, agent-facing source installer that builds with the pinned SDK and verifies every installed copy.
- Ground references such as "this file" in the visible desktop before searching off-screen or asking the user.

## 0.1.2 - 2026-07-15

First public beta candidate.

- Publish a single self-contained Windows x64 executable with WPF native libraries extracted at runtime.
- Build in an isolated staging directory and atomically replace the bundled executable, preventing stale files from masking broken releases.
- Pin the release toolchain to .NET 10.0.109 LTS and align executable, server, and plugin versions.
- Add active-control smoke checks, release verification, deterministic package contents, and a SHA-256 companion file.
- Include the .NET redistribution license and third-party notices.
- Document the unsigned-beta, Windows-support, and physical-Escape safety boundaries.
