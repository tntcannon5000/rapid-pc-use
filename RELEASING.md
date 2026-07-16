# Release checklist

Rapid PC Use releases are unsigned public-beta pre-releases. The repository rejects unsigned commits on `main`, and the release workflow rejects an unsigned or unverified tag.

1. Update `CHANGELOG.md`, `src\RapidPcUse\BuildInfo.cs`, project version fields, application manifest, and plugin manifest to the same version.
2. Run the complete local gate on an interactive Windows desktop:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
   ```

3. Review the clean release commit and confirm CI, CodeQL, and dependency review pass through a pull request.
4. Create and push an annotated, cryptographically signed tag named `v<version>`.
5. The tag workflow must verify the tag, rebuild and retest from source, attest the archive, and publish a GitHub pre-release containing:

   - `rapid-pc-use-win-x64.zip`
   - `rapid-pc-use-win-x64.zip.sha256`
   - `rapid-pc-use.spdx.json`
   - GitHub build-provenance attestation
   - A prominent warning that the executable is not Authenticode-signed

6. Download the published archive on a clean Windows account, verify the checksum and provenance, install that exact archive, restart Codex, and compare repository, release, installed-plugin, and active-cache executable hashes.

Do not publish if any gate fails, the tag is unverified, the checksum or provenance is missing, or the installed and active hashes differ from the release artifact.
