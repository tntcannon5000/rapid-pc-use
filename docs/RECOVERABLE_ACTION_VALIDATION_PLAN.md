# Recoverable PC Action Validation Plan

Status: approved by the implementation request on 2026-08-12

## Problem

`pc_act` validates the complete action batch before native input, but the MCP boundary currently treats those pre-execution validation errors like driver or Windows failures. An out-of-range value therefore releases control and ends the PC task even though no native action was attempted.

The model-facing contract now accepts computer-use scroll deltas from `-10000..10000` and converts approximately 100 delta units to one bounded Windows wheel notch. Invalid values still need to be cheaply correctable.

## Intended behavior

1. Preserve all-or-nothing validation: validate every action before checking/consuming the frame or executing input.
2. Represent request-validation failures with a dedicated exception carrying privacy-safe correction data:
   - error code;
   - one-based action index when applicable;
   - action type;
   - field name;
   - supplied numeric value when safe;
   - allowed minimum and maximum when applicable.
3. At the low-level MCP boundary, return `PC_ACTION_REJECTED` as a successful tool result with `structuredContent`, because it is a recoverable request correction rather than a driver failure.
4. Execute no action, retain the unconsumed `frame_id`, and keep desktop control active so the caller can retry immediately while the frame remains valid.
5. Log a warning with numeric metadata only. Do not allocate a failure ID or use the terminal `RAPID_PC_USE_FAILURE` path.
6. Keep only unrecoverable transport, control-session, and Windows security-boundary failures terminal.
7. Use shared constants in both the public MCP schema and the inner-agent schema so advertised and enforced limits cannot drift.
8. Tell both the low-level skill and inner model to use familiar screen-space scroll deltas followed by observation.

## Scroll normalization decision

Normalize scroll deltas rather than treating them as native wheel ticks. For example, `591` becomes six notches. This preserves the model's intended magnitude without clamping it to an excessive 100-notch native action.

## Tests

- An out-of-range scroll throws the dedicated validation exception with field, supplied value, range, action index, and action type.
- A later invalid action rejects the whole batch before any execution.
- The MCP response contains `PC_ACTION_REJECTED`, `isError: false`, correction metadata, and retry guidance.
- The MCP validation-rejection path does not call `Stop`.
- The internal and public schemas both use the shared scroll limits.
- Existing terminal failure, takeover, security, and agent tests remain green.

## Acceptance criteria

- The IKEA-style `scroll_y: 591` request executes as six bounded Windows wheel notches.
- The same `frame_id` remains eligible for an immediate corrected retry, subject only to the existing 30-second expiry and display-topology checks.
- No typed text, literal keys, screenshots, or task content is added to diagnostics.
- `RAPID_PC_USE_FAILURE` behavior is unchanged for real driver failures.
