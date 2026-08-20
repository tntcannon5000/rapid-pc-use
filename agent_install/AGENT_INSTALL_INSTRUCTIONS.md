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

## Update an existing installation

1. Locate the existing Rapid PC Use checkout and confirm it points to the official repository:

   ```powershell
   git remote get-url origin
   ```

   The result must be `https://github.com/tntcannon5000/rapid-pc-use.git` or the equivalent official SSH URL.

2. Check for local work before updating:

   ```powershell
   git status --short
   ```

   If this produces any output, do not discard, reset, or overwrite those changes. Report them to the user, or clone the official repository into a new directory and continue there.

3. Update a clean checkout using a fast-forward only:

   ```powershell
   git fetch origin
   git switch main
   git pull --ff-only origin main
   ```

4. Run the same installation entry point:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\agent_install\install.ps1
   ```

   The installer rebuilds from the updated source, atomically replaces the personal plugin, creates a uniquely versioned Codex cache, and verifies the new executable at every copy boundary.

5. If the update succeeds, explicitly tell the user: **"Rapid PC Use was updated successfully. Restart Codex or ChatGPT desktop before using it."** Do not test the updated plugin in the current task.

Never use `git reset --hard`, delete an existing checkout, reuse an old executable, or edit Codex configuration manually as part of an update.

## Installer behavior

The script performs the complete installation. It:

- Requires 64-bit Windows.
- Uses the repository-pinned .NET SDK, downloading it into `.tools\dotnet` if it is not already available.
- Verifies the downloaded Microsoft SDK archive against the SHA-512 pinned in the repository.
- Builds a self-contained driver from the current checkout.
- Installs and enables the personal Codex plugin with prompted native-tool approval.
- Verifies that the build, personal-plugin, and Codex-cache executable hashes are identical.

Do not manually download a binary, install a system-wide SDK, edit Codex configuration, or use a GitHub Release. If the script fails, report the exact error and do not improvise manual installation steps.
