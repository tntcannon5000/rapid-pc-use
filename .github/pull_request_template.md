## Summary

Describe the user-visible or architectural change and why it is needed.

## Verification

- [ ] `powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1` passed
- [ ] New behavior has deterministic regression coverage
- [ ] Performance claims include independently verified outcomes and before/after measurements
- [ ] Documentation and `CHANGELOG.md` are updated where applicable

## Safety and privacy

- [ ] Physical-Escape takeover and Windows integrity boundaries are preserved
- [ ] Action validation and effect-authority checks are not weakened
- [ ] Tests, telemetry, and examples contain no credentials, private screenshots, message contents, or unredacted logs
- [ ] GUI automation is used only where structured tools cannot complete and verify the task without meaningful tradeoffs
