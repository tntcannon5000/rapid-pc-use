# Rapid PC Use repository instructions

These instructions apply to the entire repository.

## Agent installation requests

When a user supplies `https://github.com/tntcannon5000/rapid-pc-use` and explicitly asks to install or update it, read [`agent_install/AGENT_INSTALL_INSTRUCTIONS.md`](./agent_install/AGENT_INSTALL_INSTRUCTIONS.md) completely before acting and follow that procedure exactly.

The install request authorizes cloning or updating the official repository and running its supported installer on the current Windows machine. It does not authorize `-EnableFastMode`, disabling approval prompts, discarding local changes, downloading an unsigned release binary, or manually rewriting Codex configuration. Use direct shell execution for installation; do not use GUI automation to type commands into a terminal.

After a successful install, tell the user that Codex or ChatGPT desktop must be fully restarted. Do not attempt to use the newly installed plugin in the same task because the running app cannot reload its plugin cache.
