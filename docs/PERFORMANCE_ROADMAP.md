# Rapid PC Use performance roadmap

## Objective

Reach the useful interaction rate of a muscle-memory-optimized human while keeping task success as the denominator. The model should become an interrupt and recovery mechanism for known workflows, not the clock source for every individual input.

Provisional targets, to be replaced by measurements from the same Windows fixtures:

| Workload | Successful actions per minute | Supporting target |
| --- | ---: | --- |
| Unfamiliar visual UI | 90–120 | At least 3 useful actions per model barrier |
| Learned/repeated UI | 180–240 | At least 6 useful actions per model barrier |
| Local action-to-observation checkpoint | — | p50 under 100 ms; p95 under 150 ms |

An action is successful only when the independently verified fixture state advances. Counting JSON action objects is not a performance result: typing a string, clicking three times, and moving a pointer have different work and value.

## Current critical path

For each visual decision, the current loop performs:

1. Foreground-window GDI capture at a 900-pixel short-edge cap, with full-desktop fallback when a usable foreground window is unavailable.
2. A Luna decision through a prepared first ephemeral Codex thread or the stateless Responses API.
3. The longest deterministic native action program justified by the current stable screen.
4. Immediate post-action capture followed by 10/20/40/80/160 ms backoffs until a 32×18 fingerprint changes meaningfully, rather than on focus/caret noise.
5. An optional bounded completion guard: native child-window text first, then UI Automation fallback. A match ends the run without another model barrier.
6. Background privacy-safe telemetry with per-turn capture, provider, policy, routing, input, settle, guard, and token stages.

The dominant multiplier is the number of model barriers. If local checkpoint work is 100 ms and useful action success is 90%, approximate throughput is:

| Model barrier | 1 action/decision | 3 actions/decision | 6 actions/decision |
| ---: | ---: | ---: | ---: |
| 0.8 s | 60 APM | 180 APM | 360 APM |
| 1.5 s | 34 APM | 101 APM | 203 APM |
| 2.5 s | 21 APM | 62 APM | 125 APM |

This is why micro-optimizing `SendInput` cannot deliver the target by itself.

## Baseline observations

Measured on the development machine with two displays (2560×1440 and 1920×1080), 900p JPEG output:

- A warm parallel full capture was approximately 27–33 ms.
- Codex app-server startup, initialization, and local account validation took 116.7 ms. It is now started in the background without making a model request.
- A tested GDI 32×18 settle probe still took 25.9 ms because it required another desktop readback. That prototype was rejected: cheap change detection must reuse a resident Desktop Duplication frame rather than issue another GDI capture.

Two deterministic Windows fixtures now provide independently verified useful-work counts:

- `form-tab-v1`: enter five exact values through keyboard navigation and submit; six useful actions.
- `click-ladder-v1`: click 16 fixed targets in a non-spatial order; 16 useful actions, with incorrect clicks counted separately.

The local, no-model ceiling below used two displays (2560×1440 and 1920×1080), one excluded warm-up, fresh fixture resets, and a 35 ms requested settle. These results establish where optimization cannot pay; they are not model-agent results.

| Fixture / capture tier | Runs | Success | Successful useful APM | Completion p50 / p95 | Native dispatch p50 | Post-action capture p50 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Form / 900p | 10 | 100% | 1,894.5 | 188.6 / 204.1 ms | 9.3 ms | 39 ms |
| Form / 720p | 10 | 100% | 2,069.0 | 167.7 / 191.8 ms | 9.2 ms | 34 ms |
| Click ladder / 900p | 5 | 100% | 3,440.1 | 281.8 / 291.2 ms | 45.1 ms | 41 ms |
| Click ladder / 720p | 5 | 100% | 3,625.4 | 261.1 / 287.3 ms | 45.0 ms | 34 ms |

The click runs include a fresh, batched UI Automation tree lookup to resolve all target rectangles. Its median cost was about 53 ms at 900p; a learned coordinate trace can avoid most of it, while a guarded program may deliberately retain it.

A later active-window slice materially changed the local ceiling. At 900p and 100% fixture success, active-window capture improved form useful APM from 2,173.17 to 3,207.85 (+47.6%) and click-ladder useful APM from 4,212.35 to 5,446.83 (+29.3%) relative to full-desktop capture. Form initial/post capture fell from 46/38 ms to 20/12 ms; click-ladder post capture fell from 34 ms to 12 ms.

The first active-window capture still paid a one-time WPF interop tax. A cold stage trace measured 136 ms capture wall time: 90.2 ms materialization, 7.5 ms blit, 5.9 ms fingerprint/resize, and 7.9 ms JPEG. Warming the same pipeline on a locally cleared 1×1 GDI surface at process startup reduced the first real capture to 15 ms, with materialization at 0.5 ms. The deterministic cold-process form path improved from 286 ms to 158 ms.

The settle policy was then remeasured over ten 900p active-window form runs. Zero requested settle and 35 ms fixed settle were both 10/10 successful and had effectively equal independently observed fixture completion (96.2 versus 93.9 ms p50), while driver action response fell from roughly 54–66 ms to 15–21 ms with immediate capture. Exact-byte detection was too sensitive to caret noise, so the production path now requires a small visual-difference threshold and uses short exponential repaint backoffs.

### First live Luna profile

The first live `gpt-5.6-luna`/low/fast form run used 900p active-window capture and completed correctly:

| Metric | Result |
| --- | ---: |
| Model turns / action objects | 2 / 11 |
| Elapsed / successful useful APM | 11.65 s / 30.9 |
| Model decision time | 11.42 s (98.0%) |
| First action-program turn | 8.22 s |
| Redundant finish-verification turn | 3.19 s |
| Input / settle / post capture | 10.5 / 64.1 / 16.0 ms |

All 11 actions were already present in the first decision. This made the redundant terminal model barrier, not native input or capture, the highest-leverage target.

The controller now requests maximal deterministic programs and can attach an exact visible completion guard. A matching native window-text guard costs 1.4–1.6 ms, compared with 110–112 ms for the original UI Automation walk. UI Automation remains the bounded fallback.

Exploratory before/after live form results (one run each; useful work independently verified) are:

| Slice | Success | Turns / actions | Elapsed | Successful useful APM |
| --- | ---: | ---: | ---: | ---: |
| Original active-window loop | 100% | 2 / 11 | 11.65 s | 30.9 |
| Maximal batch + completion guard | 100% | 1 / 11 | 7.38 s | 48.79 |
| Capture/thread prewarm + adaptive settle | 100% | 1 / 11 | 6.79 s | 53.0 |

The last run's critical path was 6.60 s model decision, 0 ms thread setup, 14 ms initial capture, 9.5 ms input, 51.5 ms adaptive settle (including captures/backoff), 10 ms final capture, and 1.6 ms native guard. Relative to the original live slice, successful APM improved 71.5% and elapsed time fell 41.7%. These are architectural validation runs, not release claims; model latency variance requires at least 20 repetitions.

These are directional local measurements, not release claims. Model-route comparisons still require repeated fixture runs and percentile reporting.

## Phase 0: trustworthy measurements and free latency

Status: in progress.

- [x] Send the configured `service_tier` on direct Responses requests.
- [x] Move normal telemetry serialization, mutex acquisition, and file writes off the control-loop thread using a bounded background writer.
- [x] Preserve synchronous error logging where immediate failure diagnostics matter.
- [x] Report parallel capture wall time separately from summed worker time.
- [x] Prewarm the Codex app-server/account path in the background.
- [x] Avoid thread-pool dispatch for the common single-display capture case.
- [x] Make zero-delay typing the default and submit bounded native input batches instead of one syscall per UTF-16 code unit.
- [x] Exercise a settled no-op action in the active-control smoke test.
- [x] Add deterministic fixture reset/verification contracts and concrete click-ladder/form fixtures to the benchmark harness.
- [x] Add a no-model actuator/capture ceiling benchmark against those fixtures.
- [x] Add an interactive skilled-human timing path against those exact fixtures.
- [ ] Record skilled-human baselines on those exact fixtures.
- [x] Support seeded, balanced repetitions with p50/p95, independently verified success rate, successful APM, model barriers, and actions per barrier.
- [x] Join every benchmark row to flushed provider, capture, execution, settle, token, image, no-progress, and recovery telemetry.
- [x] Add explicit parse/policy and fixture reset/verification wall timings to close the remaining stage gaps.
- [x] Capture the foreground window for `pc_run`, preserving an explicit/full-desktop fallback path.
- [x] Prewarm the WPF/GDI-to-JPEG pipeline without capturing the desktop.
- [x] Pre-create the first empty ephemeral Codex decision thread during provider warmup.
- [x] Profile every loop iteration through preparation, provider-local stages, model stream milestones, parsing, policy, action, settle, capture, guard, and routing.
- [x] Add bounded exact-text completion guards with a native fast path and UI Automation fallback.
- [x] Replace the fixed high-level settle with thresholded adaptive repaint backoff.

## Medium-horizon priority order

The measurements change the optimization order. Native input is already one to two orders of magnitude faster than the target useful-work rate, so input micro-optimizations are accepted only when they also improve an end-to-end fixture percentile. Work proceeds in this dependency order:

1. Run release-quality (20+ repetition) Luna/low/fast baselines on both fixtures. Keep the 900p cap and current model intelligence fixed while separating model variance from local changes.
2. Increase useful actions per barrier. Evolve exact terminal guards into bounded programs with intermediate UIA/OCR/state guards so stable workflows do not return to Luna between deterministic steps.
3. Reduce model time-to-first-decision without changing model intelligence: shorten schema/prompt output, measure direct Responses/WebSocket transport, and evaluate safe incremental action parsing only when a complete validated prefix cannot be invalidated by later risk fields.
4. Replace GDI polling with resident DXGI frames, dirty rectangles, and presentation-aware stable-frame detection. Encode only when a model turn actually needs pixels.
5. Cache repeatedly successful guarded programs by application/window/state signature. The model becomes the acquisition, exception, and recovery path; learned execution becomes the normal clock source.

The promotion gate for every step is unchanged: higher successful useful APM with no material success-rate loss. A faster model response that causes extra recovery turns is a regression.

## Phase 1: reduce model-route latency

Expected horizon: 2–4 weeks.

- Benchmark GPT-5.6 Luna at `none` and `low`; use `none` only where task success does not regress.
- [x] Pre-create the first Codex decision thread without task text or screenshots.
- Prepare the next empty decision thread concurrently with native action execution on multi-turn runs, using a connection design that safely multiplexes app-server messages.
- Replace per-turn temporary directories with bounded reusable frame slots or a direct image transport.
- Minimize repeated instructions and decision-schema bytes, one controlled removal at a time.
- [x] Default high-level runs to the active window at a 900p maximum; retain explicit full-desktop observation and automatic fallback.
- Benchmark the persistent Responses WebSocket route for long runs while retaining strict per-turn image/state bounds.

## Phase 2: resident capture and event-driven settling

Expected horizon: 4–7 weeks.

- Replace per-observation GDI allocation/readback with DXGI Desktop Duplication and persistent surfaces.
- Consume frame arrival and dirty rectangles instead of sleeping and recapturing blindly.
- Compute fingerprints and visual deltas from the resident frame before JPEG encoding.
- Require a short stable-frame window after change so animations do not trigger premature model turns.
- Encode only the active window or changed region when sufficient; retain a full-frame escalation path.
- Measure capture-to-JPEG and action-to-stable-frame p50/p95 independently.

The rejected low-resolution GDI probe is a guardrail for this phase: a smaller output is not a cheap probe if it forces the same desktop readback.

## Phase 3: guarded action programs

Expected horizon: 6–10 weeks.

- Extend decisions from flat batches to bounded programs with checkpoints and explicit guards.
- Add local UI Automation and OCR observations for element identity, enabled state, text, and focus.
- Route actions through the fastest reliable actuator: UIA, browser protocol where explicitly available, keyboard navigation, then coordinate input.
- Execute locally while guards pass; interrupt the model only on divergence, ambiguity, or a required approval.
- Verify every completed program against fixture state rather than trusting action dispatch success.

This phase is the main path from roughly three actions per barrier to six or more.

## Phase 4: learned muscle memory

Expected horizon: 9–16 weeks.

- Store bounded successful traces keyed by application, window identity, task shape, and coarse UI signature.
- Generalize repeated traces into parameterized local skills with guard conditions and rollback/recovery points.
- Route known states to cached skills, ambiguous states to the fast visual model, and recovery to a stronger model only when necessary.
- Track cache/skill hit rate, guard-failure rate, recovery cost, and successful APM separately.
- Promote a trace only after repeated success across fresh fixture resets.

## Benchmark acceptance rule

A change is a performance win only if it improves successful task throughput or a critical-path percentile without materially lowering task success. Report:

- Fixture and reset method.
- Successful runs / total runs.
- Successful APM and weighted useful-work rate.
- Model barriers and useful actions per barrier.
- End-to-end p50/p95 and stage p50/p95.
- Recovery and guard-failure counts.
- Model, reasoning effort, service tier, capture tier, display count, and commit.

OpenAI configuration experiments should follow the current official guidance for [Fast mode](https://developers.openai.com/api/docs/guides/fast-mode) and [GPT-5.6 model selection/reasoning](https://developers.openai.com/api/docs/guides/latest-model), but local fixture results decide the default.
