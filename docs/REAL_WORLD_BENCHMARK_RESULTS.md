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
