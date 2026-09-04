# Rapid PC Use: Architecture, Benchmarks, and Knowledgebase Plan

## Executive direction

The primary metric should be **time to verified task completion**, including recovery and cleanup. Actions per minute is useful as a local diagnostic, but it is not the product metric.

The target architecture is:

```text
User intent
    ↓
Outer Codex planner ←→ PC knowledgebase
    ↓ execution brief
Hybrid executor
    ├─ direct launch / terminal / URI
    └─ inner visual PC loop
           observe → stable action batch → observe → small decision → …
                    ↘ yield to outer only when genuinely needed
    ↓
Independent verification
    ↓
Learning proposal → reviewed/validated knowledge update
```

The outer model should own intent, authority, knowledge, and durable learning. The inner model should own fast visual control and local dependency decisions.

Pass-one implementation now includes the 80 ms native pacing invariant, dependency-frontier prompt, distinct `pc_continue` handoff, dedicated remote-content-change authority, a bounded atomic knowledge store, skill retrieval/update guidance, and the three real-world benchmark adapters requested below.

The local pass-two working tree adds a strictly allowlisted direct-launch coordinator and a one-turn trusted execution brief. The current route-learning stage adds driver-local semantic retrieval over facts and structured runbooks plus exact stored local launches, fixed direct-process commands, and fixed loopback app-interface calls selected by opaque IDs. Retrieval, dispatch, readiness, and capture are measured separately. The inner model cannot supply executable paths, commands, URLs, bodies, arguments, environment, or stdin; cannot execute a runbook step snapshot it did not retrieve during the current run; and cannot write durable knowledge.

## 1. Action pacing and batching

### Current behavior

Rapid PC Use currently supports:

- `wait` actions of 0–10,000 ms anywhere in a batch.
- `type.interval_ms` for per-character typing delays.
- `drag.duration_ms` for pointer movement duration.
- A native 80 ms minimum between every pointer button-up and the next activation, including clicks, multi-clicks, drags, and explicit mouse-down actions.

The inter-click floor is enforced in the native input layer, so separate model actions and clicks inside one action batch share the same invariant.

The relevant implementation is in [DesktopController.cs](../src/RapidPcUse/DesktopController.cs), [InputController.cs](../src/RapidPcUse/InputController.cs), and [OpenAiDecisionSchema.cs](../src/RapidPcUse/Agent/Providers/OpenAiDecisionSchema.cs).

### Implemented mechanical floor

The executor-level invariant is:

> At least 80 ms must elapse from the previous pointer button-up to the next pointer button-down.

This should apply to consecutive clicks, double-clicks, click counts, and mouse-down activations. It should be enforced by the native input layer rather than relying on the model to emit `wait` objects.

The model should not need to spend output tokens describing sixteen identical 80 ms waits. The executor can measure elapsed time with a monotonic clock and sleep only for the remaining interval.

### Mechanical delay is not state-transition handling

An 80 ms floor protects against debouncing and event loss, but it does not make a changing UI safe. The model must end a batch and recapture whenever an action can change the meaning or position of a later target.

Examples that should normally create a batch boundary:

- Country selection changes a state/province list.
- A checkbox reveals additional fields.
- Discord search selects a conversation and changes the main panel.
- A YouTube result opens a watch page.
- Delete opens a confirmation modal.
- A username submission triggers an availability result.
- Selecting a file closes a picker and returns to another application.

Examples that can remain in one batch when the screen is stable:

- Typing several known form fields through Tab traversal.
- A series of known keyboard shortcuts.
- Clicking independent, static controls.
- A fixed sequence of targets whose geometry and meaning cannot change.

The prompt should prioritize correctness and verified completion over raw action count:

1. Preserve correctness and authority.
2. Minimize verified time to completion.
3. Batch the longest sequence whose targets and meanings are invariant.
4. Stop at the first dependency frontier and recapture.
5. Use fixed waits only when the next action does not depend on inspecting the result.

## 2. Inner loop and outer Codex interaction

### What already exists

An inner `act` decision currently follows this path:

```text
inner model decision
→ execute action batch
→ capture a fresh active-window observation
→ invoke the inner model again
```

The outer Codex conversation does not receive control after every action batch. It only receives a terminal result such as completion, confirmation, blocked, failure, or a limit result. This behavior is implemented in [PcAgentLoop.cs](../src/RapidPcUse/Agent/PcAgentLoop.cs).

Therefore, dependent visual decisions already remain inside the PC-use loop in cases such as:

- Inspecting a dynamically populated second form field.
- Waiting for Discord search results and selecting one.
- Handling a Save As dialog.
- Opening a YouTube video and grounding the Like button.
- Responding to a harmless modal.
- Scrolling and inspecting newly visible content.

### Cooperative handoff

The missing state is a structured handoff to the outer planner. `blocked` and confirmation are not appropriate for ordinary capability changes.

The inner schema now exposes a bounded `handoff` decision with fields such as:

```json
{
  "decision": "handoff",
  "handoff_reason": "need_knowledge|need_terminal|need_filesystem|semantic_ambiguity|unsupported_capability",
  "handoff_request": "short bounded description",
  "memory": "compact resumable state"
}
```

The handoff releases desktop control, returns a short-lived resume token, and preserves bounded text state without screenshots. Its request is untrusted inner-model data influenced by the screen, never an executable instruction. Outer Codex must independently derive commands and paths from the original user task and trusted PC knowledge, prefer read-only checks, and perform effects only under authority already present in the original request. It then returns only a bounded factual result through `pc_continue`. Confirmation remains isolated in `pc_resume`.

The inner loop should hand off only when it genuinely needs information or capability outside visible desktop control. A changing screen alone is not a reason to return to outer Codex.

## 3. Terra investigation

The recent benchmark showed that Terra is potentially attractive:

- On successful single-turn click-ladder runs, Terra had the lowest complete-decision p50 at 12.696 seconds.
- Terra had lower raw latency than Luna when its first plan was correct.
- Terra was less reliable on the long ladder: 7/10 one-turn completions, one complete failure.
- Sol was slower to first output but completed all ten ladders correctly in one turn.

The practical conclusion is that Terra may become the best model if visual grounding and first-turn reliability improve without adding extra turns.

Current privacy-preserving telemetry intentionally excludes screenshots and model responses, so it cannot yet distinguish:

- Misreading the displayed sequence.
- Incorrect coordinate mapping.
- UI transition assumptions.
- Action schema mistakes.
- Premature completion.
- Poor recovery after an incorrect action.

For dedicated fixtures and test accounts, add an opt-in benchmark trace mode containing screenshots, structured decisions, action coordinates, and independent verifier results. Keep it local-only, ignored by Git, and disabled for ordinary desktop use.

Prompt experiments should test:

- Dependency-frontier wording.
- Explicit target transcription/mapping before a long program.
- A stronger distinction between stable and contingent actions.
- Compact task decomposition without inflating the fixed prompt.
- Whether a target-count or plan-consistency field improves reliability enough to justify its output cost.

## 4. Three realistic reversible benchmarks

Each benchmark should use a dedicated test account or disposable data, a unique per-run nonce, and an independent verifier. The task should start and finish in the same externally meaningful state.

### 4.1 Discord message roundtrip

Task:

> Send `BENCH-{nonce}` to the Discord test account, verify it arrived, remove it, and fully exit Discord.

Start state:

- Discord is fully stopped.
- The nonce is absent from the test conversation.

End state:

- The nonce is absent again.
- All Discord processes are stopped.

Verification should check the message state through the test account or bot and process state separately. Closing the window is insufficient if Discord remains in the tray.

### 4.2 YouTube investigation and restoration

Task:

> Find and play the designated video, like it, check whether it belongs to any playlists, restore its original account state, and close Chrome.

Start state:

- Chrome window closed.
- The test account has a known like and playlist state for the designated video.
- Watch history is paused or can be explicitly cleaned up.

End state:

- Original like and playlist state restored.
- Chrome window closed.

This tests search, result selection, page transitions, controls, playlist reasoning, state restoration, and browser cleanup.

### 4.3 Amazon order counting

Task:

> Count individual Amazon items ordered since the configured July date, with a returned-item bonus, then close Chrome without changing account state.

Start state:

- Chrome is closed.
- The expected item count is configured locally and ignored by Git.

End state:

- No Amazon state changed.
- Chrome is closed.
- The observed count matches the local expected value; returned count is scored separately.

This tests authenticated navigation, pagination/time filtering, order-card interpretation, quantities, return-state recognition, and read-only cleanup.

## 5. Benchmark scoring

APM should remain a diagnostic, but the primary score should be:

- Verified completion rate.
- Time to verified completion.
- Time to restored end state.
- Inner model turns.
- Outer handoffs.
- Terminal/direct-launch operations.
- UI actions and incorrect actions.
- TTFT and output-fill time per model turn.
- Recovery and rollback count.
- User interventions.
- Residual-state failures.

A task that sends a Discord message quickly but fails to remove it is a failure, regardless of action throughput.

The current Amazon result is independently scored against the locally configured known answer. Discord and YouTube completion markers plus app closure are only model-attested cleanup, so they are recorded as provisional. Until an independent account-level verifier exists, the live runner stops after either mutating scenario rather than allowing another model to inherit uncertain message, Like, or playlist state.

## 6. PC knowledgebase

Hermes Agent makes a useful distinction: persistent memory stores compact factual knowledge, while skills store reusable procedures and are loaded on demand. Its architecture also separates stable, contextual, and volatile prompt material to preserve caching.

References:

- [Hermes skills documentation](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/guides/work-with-skills.md)
- [Hermes architecture documentation](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/developer-guide/architecture.md)

Rapid PC Use should adopt the concepts without injecting a large knowledgebase into Luna, Terra, or Sol.

### Machine facts

Compact attributable facts:

- Installed applications and reliable launch routes.
- “Jeremy” mapped to a particular Discord identity.
- “Bot laptop” mapped to a machine, Parsec host, or SSH alias.
- Project path and service identity.
- Preferred browser and profile.
- Whether an application remains in the tray after closing.
- Known direct URLs and URI handlers.

Knowledge v1 stores a source, confidence, creation/update timestamps, and revision. Runbooks v1 separately store semantic search terms, concise ordered guidance, exact trusted launch, fixed direct-process, or loopback app-interface steps, source/confidence metadata, timestamps, and revision. The driver automatically records only per-step attempt disposition and timing after terminal completion, never task text or output, and uses those measurements to rank equally relevant routes by reliability and successful latency. Machine/profile scope, expiry and stale-route suppression, sensitivity classification, and higher-order route optimization remain future design goals.

### Entities and relationships

```text
Jeremy → reachable_via → Discord
bot-laptop → hosts → discord-control-bot
discord-control-bot → located_at → project path
discord-control-bot → verified_by → health endpoint
bot-laptop → preferred_route → SSH
bot-laptop → fallback_route → Parsec
```

Credentials should never be stored in this database. Store references to Windows Credential Manager, SSH configuration, or another secret store instead.

### Procedural runbooks

Runbooks are versioned and human-readable. The first implementation supports guidance; exact `.exe`, `.cmd`, `.bat`, and `.lnk` visible launch steps; fixed direct `.exe` commands with bounded stored arguments, runtime, and output; and fixed `127.0.0.1`/`localhost` HTTP operations for a known local application. The inner model never writes the target or arguments. GET and effect-free commands are read-only. Effectful commands and POSTs must declare an independently enforced authority, and an ambiguous response or exit is never retried; a separate read-only step must verify state. Only an exact loopback GET may be marked `required_before_finish`, in which case the driver rejects premature model completion until that exact verifier succeeds. Fixed commands remain explicit model-selected steps so process-launch authority cannot be bypassed by automatic finish verification:

```text
restart-discord-bot
  preferred: SSH → restart service → health check
  fallback: Parsec → terminal → project launcher
  verification: new process generation + health response
  cleanup: disconnect session
  known failures: stale environment, port already occupied
```

### Performance memory

For each route step, the implemented privacy-safe learner stores attempt, success, failure, uncertain-effect, total successful time, last time, and timestamp. It updates only after the desktop run reaches a terminal result, preserves compatible metrics across semantic revisions, and treats persistence failure as a non-terminal optimization failure. For future higher-order planning, also derive:

- Success count.
- Median and p95 completion time.
- Last successful app/version.
- Required confirmations.
- Common failure states.
- Whether terminal, URI, keyboard, or visual navigation was fastest.

The current tie-breaker already learns which equally relevant trusted route is more reliable and faster. The richer layer will let the planner compare unlike route shapes, such as direct Chrome launch versus Start navigation or SSH versus Parsec, without injecting raw task or screen content into durable memory.

### Durable-write boundary

The inner visual model must not directly write durable knowledge. On-screen content is untrusted and could poison the machine profile.

The safe learning path is:

1. Inner loop emits bounded candidate facts or procedure changes.
2. Independent verification establishes what actually happened.
3. Outer Codex reviews and deduplicates candidates.
4. Sensitive mappings or executable procedures may require user approval.
5. A curator writes a versioned update.
6. Failed runs lower confidence or add a known failure; they do not silently overwrite a working route.

The full knowledgebase is never injected into the inner model. The driver automatically prefetches the original task; outer Codex can also add a one-turn execution brief. When a later named entity or sub-workflow is discovered, the inner model can issue one bounded semantic query inside the driver. It receives at most a small text projection containing facts, runbook guidance, and opaque executable step IDs; targets and payloads are omitted. Any selected operation must match the exact immutable `(runbook key, step ID, stored step)` snapshot projected during that run; a mutable store re-read cannot change the validated dispatch. Launches still pass explicit process-launch scope or one-shot confirmation; local mutations separately pass their declared effect boundary. Effectful app steps consume action budget and one-shot authority before dispatch and are at most once even when the response is lost. A target marked as requiring elevation hands off before Windows secure desktop rather than waiting on UAC. Required finish verifiers are armed only for an explicitly selected route or the single strongest exact multiword task match. They remain driver state, not model discretion, and a later consequential native or runbook mutation re-arms them so completion always depends on a fresh exact read.

## 7. Recommended implementation order

1. Enforce the 80 ms inter-click floor and add timing tests.
2. Rewrite batching guidance around dependency frontiers.
3. Add opt-in benchmark trace capture.
4. Implement generalized inner handoff and resumable `pc_continue`.
5. Build the three scenario adapters and bounded verifiers.
6. Benchmark Luna, Terra, and Sol on realistic scenarios.
7. Tune Terra using captured failure traces.
8. Build knowledgebase v1 facts and structured runbooks. **Implemented locally.**
9. Add safe direct-launch and exact trusted local runbook execution. **Implemented locally for URI launch, exact process launch, fixed direct `.exe` commands with stored arguments and bounded output, and fixed loopback app interfaces; arbitrary model-authored shell remains intentionally unsupported.**
10. Add route performance memory, expiry/staleness policy, and independent verification-backed learning. **Per-step disposition/timing learning and same-relevance route ranking are implemented locally; semantic route promotion remains outer-curated, while expiry and richer cross-route planning remain.**
11. Select the default model using verified task-completion time and reliability.
