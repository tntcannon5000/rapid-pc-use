---
name: rapid-pc-use
description: Control the visible Windows desktop at native speed with real mouse and keyboard input. Use whenever the user asks Codex or ChatGPT desktop to operate their PC, open or use any app, navigate a website through its GUI, click, drag, scroll, type, fill forms, message someone, configure a cloud console, play a simple game, supervise visual progress, interact with another agent, or complete any task a human could perform with the screen, mouse, and keyboard. Also use when the user invokes $rapid-pc-use explicitly. Prefer this driver over built-in Computer Use for full-desktop work.
---

# Rapid PC Use

Operate the foreground Windows desktop directly. Keep the action loop tight and perform the work in the main task unless the user explicitly asks for delegation.

## Operating loop

1. Form a high-level route before acting. Use digital-world knowledge aggressively: prefer known direct URLs, app launch shortcuts, keyboard shortcuts, and product configurators over slow exploratory navigation.
2. State that route in one short commentary update, then call `pc_observe` with `begin_control=true`.
3. Read every returned display image and its manifest. Treat each display independently. All `pc_act` coordinates are monitor-local normalized integers: `(0,0)` is top-left and `(1000,1000)` is bottom-right, regardless of image or native resolution.
4. Maintain an evolving low-level next-action plan. Call `pc_act` with the latest `frame_id`; it executes an ordered batch and normally returns the next screenshots in the same call.
5. Batch only actions whose outcome and focus are deterministic, such as click field -> type text -> press Tab. After navigation, opening a menu, submitting, loading, animation, or any uncertain state change, inspect the returned image before choosing the next action.
6. Call `pc_stop` as soon as the requested PC work is complete or cannot continue.

## Speed and judgment

- Act instead of narrating. Keep commentary to brief route changes, material ambiguity, or blockers.
- Make sensible educated guesses from context and on-screen conventions. Do not ask about harmless reversible choices. Ask only when a missing choice materially changes an irreversible, costly, privacy-sensitive, or externally consequential result.
- Prefer keyboard shortcuts and direct address-bar navigation when they reduce visual steps. Use mouse input when spatial interaction matters.
- Use `type` for literal text; it emits real high-speed keystrokes and never uses the clipboard. Use `key` for chords such as `CTRL+L`, `ALT+TAB`, or `ENTER`.
- Use teleported clicks for ordinary targets. Use `move`, `drag`, `mouse_down`, `mouse_up`, or `relative_move` when pointer motion or hold state matters.
- Use a short `wait` action when the UI is predictably busy. For supervision lasting longer than 60 seconds, bounded PowerShell `Start-Sleep` intervals are allowed only as a timer while the visual driver remains healthy; never use the shell to operate or inspect the target app. Re-observe afterward.
- If a target is ambiguous or tiny, move to the center of the visible hit area and verify the result. Never reuse coordinates from an older `frame_id` after display topology changes.

## Driver failure discipline

Treat `RAPID_PC_USE_FAILURE`, a closed connection, an unavailable server, a missing tool, or a transport error as terminal for the current PC task.

1. Make no more PC-use calls. Do not call `pc_stop`; an ordinary reported failure already releases control internally.
2. Do not touch the target through PowerShell, an app CLI, browser automation, built-in Computer Use, or any workaround. Do not infer that an unconfirmed action succeeded.
3. Inspect only the running driver log with exactly one bounded read-only PowerShell command. For a reported failure ID, run `Get-Content -LiteralPath "$env:LOCALAPPDATA\RapidPcUse\rapid-pc-use.log" -Tail 120 | Select-String -SimpleMatch "<failure-id>"`. If the transport died without an ID, read only the last 20 lines instead.
4. Do not run process checks, source searches, web searches, system diagnostics, retries, or repairs. Do not turn the failure into a broad investigation.
5. Tell the user in 1-3 plain sentences what failed and which requested result remains unconfirmed. Avoid tables, timing ledgers, stack traces, and speculative root-cause essays.

If the user later asks what happened, use the existing correlated log entry as the diagnosis. Do not expand into source-level debugging unless the user explicitly asks to repair the driver code.

## Human takeover

The user can press the physical Escape key at any moment. If any tool returns `USER_TAKEOVER` or `The user is now operating the PC`:

1. Make no further PC-use or shell automation calls in this turn.
2. Do not restart control.
3. End the turn immediately with a brief acknowledgement that control was returned.

The driver automatically releases every key and mouse button it pressed and hides its control cue.

## Boundaries

Windows prevents normal-integrity software from controlling UAC secure desktop, the lock/login screen, Ctrl+Alt+Delete, protected video, some anti-cheat games, and elevated apps. Report the exact boundary if encountered; never claim a click succeeded when the screen did not confirm it.
