# Security policy

## Supported versions

Rapid PC Use is currently a public beta. Security fixes are provided on the newest `main` revision.

## Reporting a vulnerability

Use GitHub's private **Report a vulnerability** flow in the repository's Security tab. Do not place exploit details, secrets, or sensitive screenshots in a public issue. Public issues are appropriate for non-sensitive bugs and hardening suggestions.

Include the Rapid PC Use version, Windows version, reproduction steps, expected impact, and whether the physical Escape takeover still worked. Logs from `%LOCALAPPDATA%\RapidPcUse\rapid-pc-use.log` are useful, but review them before sharing.

## Security boundary

Rapid PC Use intentionally sends real mouse and keyboard input to the interactive Windows session. It cannot cross the Windows secure desktop, lock or login screens, Ctrl+Alt+Delete, integrity-level boundaries into elevated applications, or application-specific input protections.

Public-beta executables are not Authenticode-signed. The supported installer builds from the checked-out source with a checksum-pinned Microsoft SDK, verifies the copied executable at each installation boundary, and relies on protected CI and signed Git history for repository integrity. Install only from the official repository or another source revision you have independently reviewed.
