# Contributing to Rapid PC Use

Rapid PC Use is a Windows-native computer-control driver where performance and safety are both part of the public contract. Focused bug reports, benchmark improvements, security hardening, documentation fixes, and well-measured performance changes are welcome.

## Before opening an issue

- Use the bug form for reproducible defects and the feature form for proposals.
- Search existing issues first.
- Do not post credentials, screenshots containing private information, message contents, or unredacted Rapid PC Use logs.
- Report suspected vulnerabilities through GitHub's private **Report a vulnerability** flow as described in [`SECURITY.md`](./SECURITY.md).

## Development setup

Development requires Windows x64 and PowerShell 5.1 or later. The repository pins its .NET SDK in `global.json`; the build script downloads and verifies that exact SDK into `.tools` when necessary.

```powershell
git clone https://github.com/tntcannon5000/rapid-pc-use.git
cd rapid-pc-use
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

The complete verification command builds self-contained artifacts, runs strict analyzers and formatting checks, executes security and agent regression tests, validates benchmark helpers and plugin routing, and performs a bounded active desktop smoke check. Use `-SkipSmoke` only in CI or another environment that cannot provide an interactive Windows desktop.

## Pull requests

- Keep changes focused and explain the behavior being changed.
- Include deterministic regression coverage for behavior or security changes.
- For performance claims, provide the workload, repetitions, capture tier, model configuration, before/after distributions, and independent success verification. Do not use model-reported completion or raw action count as proof of useful throughput.
- Preserve the physical-Escape takeover, normal-integrity Windows boundaries, atomic action validation, privacy-filtered telemetry, and authority checks.
- Update `CHANGELOG.md` when behavior visible to users or integrators changes.
- Run `scripts\verify.ps1` and include its result in the pull request.

By contributing, you agree that your contribution is licensed under this repository's MIT License.
