# Changelog

All notable changes to Rapid PC Use are recorded here.

## 0.1.2 - 2026-07-15

First public beta candidate.

- Publish a single self-contained Windows x64 executable with WPF native libraries extracted at runtime.
- Build in an isolated staging directory and atomically replace the bundled executable, preventing stale files from masking broken releases.
- Pin the release toolchain to .NET 10.0.109 LTS and align executable, server, and plugin versions.
- Add active-control smoke checks, release verification, deterministic package contents, and a SHA-256 companion file.
- Include the .NET redistribution license and third-party notices.
- Document the unsigned-beta, Windows-support, and physical-Escape safety boundaries.
