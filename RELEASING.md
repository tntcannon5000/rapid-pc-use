# Release checklist

Rapid PC Use releases are public beta pre-releases. The repository rejects unsigned commits on `main`, and the release workflow rejects an unsigned or unverified tag.

1. Update `CHANGELOG.md`, `src\RapidPcUse\BuildInfo.cs`, project version fields, application manifest, and plugin manifest to the same version.
2. Run the complete local gate on an interactive Windows desktop:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
   ```

3. Review the clean release commit and confirm CI, CodeQL, and dependency review pass through a pull request.
4. Configure the repository Actions secrets `RAPID_PC_USE_SIGNING_CERT_BASE64` and `RAPID_PC_USE_SIGNING_CERT_PASSWORD` with a trusted Windows code-signing certificate. Never create a release without them.
5. Create and push an annotated, cryptographically signed tag named `v<version>`.
6. The tag workflow must verify the tag, rebuild and retest from source, Authenticode-sign and timestamp the executable, verify that signature, repackage it, attest the archive, and publish a GitHub pre-release containing:

   - `rapid-pc-use-win-x64.zip`
   - `rapid-pc-use-win-x64.zip.sha256`
   - `rapid-pc-use.spdx.json`
   - GitHub build-provenance attestation

7. Download the published archive on a clean Windows account, verify the checksum, provenance, and embedded executable signature, install that exact archive, restart Codex, and compare repository, release, installed-plugin, and active-cache executable hashes.

Do not publish if any gate fails, the tag is unverified, signing credentials are absent, the Authenticode timestamp is invalid, or the installed and active hashes differ from the signed release artifact.
