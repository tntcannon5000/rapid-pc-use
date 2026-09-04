# Release checklist

Rapid PC Use is distributed as source and installed through the checksum-verifying local build in `agent_install\install.ps1`. Do not attach an unsigned executable to a GitHub release or direct users around the supported installer.

1. Choose the version and update it consistently in the plugin manifest, project properties, `BuildInfo.cs`, and the Windows application manifest.
2. Move the relevant `CHANGELOG.md` entries from **Unreleased** into a dated version section.
3. Run the full interactive verification gate on Windows:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
   ```

4. Install from the candidate checkout with `agent_install\install.ps1`, restart Codex, and verify that the Rapid PC Use skill and `pc_run` tools are advertised in a fresh task.
5. Open a pull request into `main`. Wait for CI, CodeQL, and dependency review, resolve every conversation, and preserve signed linear history.
6. Merge the pull request, create a signed `v<version>` tag on the resulting `main` commit, and push the tag.
7. Create a GitHub prerelease while the project remains a public beta. Generate notes from the tagged comparison, but state that installation builds from source and that binaries are unsigned.
8. Clone the tag into a clean directory and run the supported installer once more. Record the verified executable SHA-256 in the release notes for auditability, not as an alternate installation path.

If any verification step fails, do not publish or retag the candidate. Fix it through a new commit and repeat the checks.
