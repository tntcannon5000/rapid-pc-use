# Real-world benchmark results

## Pass one — 2026-08-21

These measurements compare the frozen `938d574` main executable with the pass-one working tree on the same three task definitions, 900p capture, low reasoning, fast service tier, one fresh driver/provider process per cell, and a 120-second task limit. One pass-one infrastructure-failure cell was replaced by an isolated rerun with the same model and benchmark settings.

The raw JSON artifacts remain locally ignored because they contain account-derived aggregate Amazon counts. This checked-in summary contains only statuses, bounded timings, model/action counts, and the verifier classification.

| Scenario | Model | Before status / TTC | Pass-one status / TTC | First decision before → after | Output fill before → after | Actions before → after |
|---|---|---:|---:|---:|---:|---:|
| Amazon | Luna | blocked / 75.929 s | blocked / 42.224 s | 4.136 → 4.323 s | 1.798 → 1.602 s | 15 → 7 |
| Amazon | Terra | completed, wrong count / 65.049 s | confirmation / 110.528 s | 3.396 → 2.942 s | 1.598 → 1.390 s | 18 → 39 |
| Amazon | Sol | completed, wrong count / 93.122 s | limit / 120.079 s | 5.004 → 4.601 s | 2.406 → 1.891 s | 26 → 25 |
| Discord | Luna | blocked / 4.954 s | limit / 120.113 s | 3.772 → 4.563 s | 0.923 → 1.775 s | 0 → 26 |
| Discord | Terra | limit / 120.096 s | limit / 120.077 s | 4.112 → 3.505 s | 1.458 → 1.432 s | 29 → 15 |
| Discord | Sol | limit / 120.106 s | limit / 120.079 s | 4.542 → 5.571 s | 2.489 → 2.365 s | 32 → 16 |
| YouTube | Luna | blocked / 5.327 s | blocked / 6.318 s | 4.257 → 4.931 s | 0.850 → 1.120 s | 0 → 0 |
| YouTube | Terra | limit / 120.104 s | model-attested cleanup / 92.006 s | 3.319 → 3.267 s | 1.619 → 1.483 s | 34 → 25 |
| YouTube | Sol | limit / 120.082 s | limit / 120.088 s | 4.628 → 3.963 s | 2.227 → 1.841 s | 26 → 33 |

Verified outcomes were **0/9 before and 0/9 after**. Pass one produced one provisional result: Terra reported YouTube state restoration and closed Chrome in 92.006 seconds. That is not an independently verified success; the inner model's marker and application closure cannot prove exact Like/playlist restoration. The current runner therefore labels Discord/YouTube results provisional and forbids multi-cell mutation schedules until an account-level verifier exists.

The clearest performance result is negative but useful: native capture, action dispatch, and the 80 ms pointer floor are not the real-task bottleneck. Pass-one first-decision latency remained roughly 2.9–5.6 seconds per model turn, output fill roughly 1.1–2.4 seconds, and difficult cells used up to 24 turns. The next pass should primarily remove model barriers and unnecessary visual navigation while preserving Terra/Sol-level judgment, rather than weakening capture quality or pointer safety.

## Pass two — local working tree, not pushed

Pass two adds trusted fast start: the outer Codex layer can provide a bounded one-turn execution brief and a strictly validated HTTP(S) or exact `discord:` launch. The driver launches while visible control is active, discovers only allowlisted browser/Discord windows, makes a bounded foreground attempt, captures the resulting active window, and clears the brief after the first inner-model response. This removes app-launch and known-route discovery turns without changing the configured model, reasoning level, service tier, or 900p capture.

An initial matrix was invalidated because an elevated Task Manager window remained foreground; those rows are excluded. The final implementation now fails that condition before invoking the inner model. Its live guard probe stopped in 1.339 seconds with zero model turns, actions, or tokens. After the obstruction was manually closed, the unchanged Amazon matrix was run three times per model with fresh driver processes and ephemeral model threads.

| Model | Pass-one result | Pass-two verified | Observed item/return counts | Pass-two TTC min / median / max | Turns | Actions |
|---|---|---:|---|---:|---|---|
| Luna | blocked / 42.224 s | 0/3 | —, —, — | 5.513 / 5.699 / 6.036 s | 1, 1, 1 | 0, 0, 0 |
| Terra | confirmation / 110.528 s | 0/3 | 16/1, 9/1, 12/1 | 43.211 / 45.894 / 46.627 s | 7, 7, 7 | 6, 6, 6 |
| Sol | limit / 120.079 s | **3/3** | 15/1, 15/1, 15/1 | 48.162 / 49.230 / 88.801 s | 13, 7, 7 | 18, 6, 6 |

Sol is the only model that completed the task correctly and did so on every run, including the returned-item bonus and closed-browser end state. Terra was slightly faster but consistently asserted completion with a wrong count. Luna consistently blocked after one turn. This is direct evidence that raw APM/TTC without independent correctness rewards the wrong model.

Fast-start launch averaged 0.297–0.301 seconds and activated Chrome on every valid row. Median total model-decision time was 45.217 seconds of Sol's 49.230-second median TTC; median native action execution was 3.169 seconds. Average first-decision latency remained 4.212 seconds and output fill 2.248 seconds for Sol. The successful path therefore still spends roughly 92% of wall time inside model turns: trusted routing removes startup barriers and reduced the successful Sol path from a 120-second limit to a 49.230-second median, but the next large gain must reduce the number and latency of correctly grounded model decisions rather than weaken capture quality or native pacing.

## Route-learning stage — local working tree, not pushed

The next local stage adds driver-native bounded retrieval, exact immutable runbook-step snapshots, fixed local launches, fixed direct-process commands with stored arguments and bounded output, fixed loopback application interfaces, driver-enforced fresh finish verification, and privacy-safe per-step reliability/latency learning. The inner Luna controller receives descriptions and opaque IDs only; it cannot author a command, path, argument, URL, body, environment, or stdin. Effectful steps consume action budget and one-shot authority before dispatch and remain at most once after an ambiguous response.

The repeatable Warframe route was seeded from the user-provided procedure and independently discovered local entry points. Its initial visual-only combined task failed at the action limit after **186.7 seconds**. The first structured route completed the scanner reset/start portion and reached the expected ZenTimings UAC handoff in **31.057 seconds**, four Luna turns, and three action objects; the exact local status independently showed running at a 7-second interval. That combined route was not rerun after the scanner reached its intended state because resetting it again or dispatching an elevated target would change the live machine merely for measurement.

The first optimized non-mutating status probe asked only whether the Warframe Market scraper was running and at what interval. It completed in **13.806 seconds**, two Luna turns, and one exact read-only runbook action. After the final authority, process-containment, execution-fingerprint, and terminal-learning timing hardening, the current build repeated that probe in **13.139 seconds active / 13.169 seconds outer wall**, again with two Luna turns and one GET. Independent API inspection agreed: running, 7.0-second interval, no error. The prior marker-only design for the same read-only check required **23.071 seconds**, four turns, and two redundant GETs, so activated-route finish verification removed two model barriers and one app call: a **43.0% final TTC reduction**.

| Final read-only layer | Time |
|---|---:|
| Initial active-window capture | 42.0 ms |
| Driver-local retrieval | 23.215 ms |
| Exact read-only verifier | 36.012 ms |
| Terminal route learning | 25.802 ms |
| Total model decisions | 12,797.420 ms |
| Mean first-decision delta | 4,714.135 ms |
| Mean output fill | 1,684.575 ms |
| Active TTC | 13,139 ms |

Model work is now **97.4%** of active wall time; retrieval plus exact verification is under 60 ms, while durable privacy-safe learning adds 25.8 ms. The optimized architecture therefore moves known machine routes, CLI checks, and app APIs off the visual path, but its remaining repeat-task floor is still two Luna decisions. The next performance target is a safely driver-verified one-turn completion path for exact read-only outcomes, plus richer route-level planning that preserves Sol/Terra judgment for genuinely ambiguous screens.
