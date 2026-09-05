# Rapid PC Use agent installation

Use this procedure when installing or updating Rapid PC Use through Codex or another automated development environment. It is the authoritative route for a request consisting of the repository URL plus language such as **"install this for me."**

## Authority and boundaries

- The explicit install request authorizes cloning or updating the official repository and running the supported installer on the current Windows machine.
- The request does not authorize `-EnableFastMode`. Use prompted native-tool approval unless the user separately and explicitly asks for fast mode.
- Do not install merely because the user shared or asked about the repository URL.
- Use direct shell commands for cloning and installation. Do not invoke computer-use tooling to type these commands into a visible terminal.
- Do not discard local work, replace a dirty checkout, manually edit Codex configuration, download an unsigned release executable, or improvise an alternate installation path.

## URL-only request

When the user provides `https://github.com/tntcannon5000/rapid-pc-use` and asks to install it:

1. Confirm the machine is 64-bit Windows and that `git` is available. If either condition is false, stop and report the unsupported prerequisite.
2. If no checkout is already in scope, clone the official `main` branch into a new directory:

   ```powershell
   git clone --branch main --single-branch https://github.com/tntcannon5000/rapid-pc-use.git
   cd rapid-pc-use
   ```

   Never overwrite or recursively delete an existing directory to make this command succeed. If the default destination exists, inspect it using the update procedure below or choose a new non-existing directory.
3. Verify that the checkout is the official repository, is on `main`, and has no local changes:

   ```powershell
   git remote get-url origin
   git branch --show-current
   git status --short
   ```

   The remote must be `https://github.com/tntcannon5000/rapid-pc-use.git` or the equivalent official SSH URL, the branch must be `main`, and `git status --short` must produce no output. If any check fails, stop rather than changing or deleting the checkout.
4. Read `README.md`, `SECURITY.md`, and the repository-root `AGENTS.md`, then run the single installation entry point from the repository root:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\agent_install\install.ps1
   ```

5. Treat the installation as successful only when the command exits successfully and reports that installation and hash verification completed. Preserve the reported installed version and SHA-256 for the user-facing result.
6. Tell the user: **"Rapid PC Use was installed successfully. Fully restart Codex or ChatGPT desktop before using it."** Do not try to use the newly installed plugin in the current task because the desktop process must reload its plugin cache.

## Installation from an existing clean checkout

If the official `main` checkout is already current and clean, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\agent_install\install.ps1
```

Apply the same success criteria and restart requirement as the URL-only procedure.

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
- Registers the plugin marketplace and MCP server, and installs bounded global routing guidance that selects Rapid PC Use only for genuinely visual Windows work.
- Creates timestamped backups before changing existing Codex configuration or global guidance.
- Verifies that the build, personal-plugin, and Codex-cache executable hashes are identical.

Do not manually download a binary, install a system-wide SDK, edit Codex configuration, or use a GitHub Release. If the script fails, report the exact error and do not improvise manual installation steps.
