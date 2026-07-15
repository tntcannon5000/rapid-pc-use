# Release checklist

1. Update `CHANGELOG.md`, `src\RapidPcUse\BuildInfo.cs`, the project version fields, and the plugin manifest to the same version.
2. Run `powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1` on the interactive Windows desktop.
3. Confirm the working tree is clean and review the release commit.
4. Tag the commit as `v<version>`.
5. Create a GitHub **pre-release** and upload:
   - `dist\rapid-pc-use-win-x64.zip`
   - `dist\rapid-pc-use-win-x64.zip.sha256`
6. Keep the unsigned-binary warning in the release notes and enable GitHub private vulnerability reporting.

Do not publish if the active-control smoke check, package checksum verification, version consistency check, or clean-build validation fails.
