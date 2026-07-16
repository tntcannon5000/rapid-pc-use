# Security policy

## Supported versions

Rapid PC Use is currently a public beta. Security fixes are provided only for the newest published beta release.

## Reporting a vulnerability

Use GitHub's private **Report a vulnerability** flow in the repository's Security tab. Do not place exploit details, secrets, or sensitive screenshots in a public issue. Public issues are appropriate for non-sensitive bugs and hardening suggestions.

Include the Rapid PC Use version, Windows version, reproduction steps, expected impact, and whether the physical Escape takeover still worked. Logs from `%LOCALAPPDATA%\RapidPcUse\rapid-pc-use.log` are useful, but review them before sharing.

## Security boundary

Rapid PC Use intentionally sends real mouse and keyboard input to the interactive Windows session. It cannot cross the Windows secure desktop, lock or login screens, Ctrl+Alt+Delete, integrity-level boundaries into elevated applications, or application-specific input protections. Official release executables must carry a valid Authenticode signature and be traceable to a signed Git tag and published build provenance.
