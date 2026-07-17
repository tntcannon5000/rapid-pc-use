# Rapid PC Use agent installation

Use this procedure when installing Rapid PC Use for a user through Codex or another automated development environment.

## Procedure

1. Clone the official repository if it is not already the current checkout:

   ```powershell
   git clone https://github.com/tntcannon5000/rapid-pc-use.git
   cd rapid-pc-use
   ```

2. From the repository root, run the single installation entry point:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\agent_install\install.ps1
   ```

3. If the script succeeds, tell the user to restart Codex or ChatGPT desktop. Do not try to use the newly installed plugin in the current task because the desktop process must reload its plugin cache.

## Installer behavior

The script performs the complete installation. It:

- Requires 64-bit Windows.
- Uses the repository-pinned .NET SDK, downloading it into `.tools\dotnet` if it is not already available.
- Verifies the downloaded Microsoft SDK archive against the SHA-512 pinned in the repository.
- Builds a self-contained driver from the current checkout.
- Installs and enables the personal Codex plugin with prompted native-tool approval.
- Verifies that the build, personal-plugin, and Codex-cache executable hashes are identical.

Do not manually download a binary, install a system-wide SDK, edit Codex configuration, or use a GitHub Release. If the script fails, report the exact error and do not improvise manual installation steps.
