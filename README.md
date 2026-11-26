# PowerShell Secrets

A native Windows desktop app for
[Microsoft.PowerShell.SecretManagement](https://github.com/PowerShell/SecretManagement) and
[Microsoft.PowerShell.SecretStore](https://github.com/PowerShell/SecretStore), written in C# with
[Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor). Reactor is a declarative C# framework over WinUI 3.

> Reactor is an **experimental preview** from Microsoft (pinned here to `0.1.0-preview.18`). Expect API changes when upgrading.
> The earlier PowerShell/WPF version still works and lives in [`legacy/`](legacy/README.md).

## Install

Download `PowerShellSecrets-<version>-x64.msi` from [Releases](https://github.com/amsdias/secrets-manager/releases) and run it.
It installs for your account only (no admin rights) into `%LOCALAPPDATA%\Programs\SecretsManager`, adds **PowerShell Secrets** to
the Start menu, puts `secrets` (the command line, below) on your user PATH, and can be removed from Settings > Apps. Uninstalling keeps your settings, vaults and secrets.

Nothing else is needed: the app is self-contained (no .NET install) and ships Microsoft.PowerShell.SecretManagement and
Microsoft.PowerShell.SecretStore (see [third_party](third_party/README.md)). If you have those modules installed, yours are used.
To use `Get-Secret` directly in your own PowerShell you still need them installed there:
`Install-PSResource Microsoft.PowerShell.SecretManagement, Microsoft.PowerShell.SecretStore -Scope CurrentUser`.

The installer isn't code-signed yet, so Windows SmartScreen may warn about an unknown publisher ("More info" > "Run anyway").

### From source

```powershell
pwsh -File tools/Install-SecretsManager.ps1             # one-file build, install to %LOCALAPPDATA%\Programs\SecretsManager, Start menu shortcut
pwsh -File tools/Install-SecretsManager.ps1 -FrameworkDependent  # needs the .NET 11 Desktop Runtime instead (bigger: no compression)
pwsh -File tools/Install-SecretsManager.ps1 -Desktop    # also a desktop shortcut
pwsh -File tools/Install-SecretsManager.ps1 -Uninstall  # remove the app and its shortcuts (vaults, secrets and settings stay)
```

Run it again to update (close the app first). No admin rights are needed. Nothing else needs installing: `SecretsManager.exe` is a
self-contained, compressed single file (about 90 MB) that unpacks itself to `%TEMP%\.net\SecretsManager` the first time each
version starts, and `secrets.exe` is a small native exe. The install folder is added to your user PATH, so `secrets get <name>` works in new terminals.

## Releases

Pushing a tag `v1.2.3` makes CI build version 1.2.3 (app, CLI and MSI) and publish a GitHub Release with them. Every other build
uploads the same files as a workflow artifact.

## Build and run

```powershell
dotnet build src/SecretsManager.App -p:Platform=x64
dotnet run --project src/SecretsManager.App -p:Platform=x64

# Self-contained, unpackaged: copy the folder anywhere and run publish\SecretsManager.exe
dotnet publish src/SecretsManager.App -c Release -p:Platform=x64 -r win-x64 --self-contained -o publish
```

## Features

- **Vaults**: add (SecretStore, a Windows-account vault, or any extension module), remove from the list (secrets are kept, so adding
  it again shows them; a Windows-account vault can also delete them), set default, unlock (only a vault that is locked). There is
  one SecretStore per Windows user: every SecretStore vault shows the same secrets.
- **Secrets**: search (name and metadata; `key=value` matches one metadata key), view (masked until revealed), copy, create, edit,
  rename, copy to another vault, delete. The three most used metadata keys get their own columns.
- **Export / import**: save a whole vault to a password-encrypted `.smsecrets` file, and import it into any vault. On import you choose
  whether secrets that already exist are skipped or overwritten.
- **Lock**: **Lock** (Ctrl+L) closes open secrets, clears a copied value from the clipboard and locks SecretStore. The app also locks
  itself after a chosen time without keyboard or mouse activity (status bar; default 10 minutes, saved in
  `%LOCALAPPDATA%\SecretsManager\settings.json`).
- **All secret types**: String, SecureString, PSCredential, Hashtable, ByteArray (load from / save to file), plus metadata.
- **Password generator** (24 random characters, ambiguous characters left out).
- **SecretStore settings**: authentication, password timeout, interaction, change password, reset (asks twice, and you must type `RESET`).
  The first run offers to create a SecretStore vault.
- **Locked vaults**: any operation on a locked vault asks for the password in a dialog and retries. A wrong password asks again.

- **Settings** (Ctrl+,): color theme (Windows light/dark, or Catppuccin Latte, Frappé, Macchiato, Mocha), auto-lock time,
  how long copied values stay on the clipboard, and minimize to the notification area (tray icon: click to show, right-click for
  Show / Lock now / Exit). The window's size and position and the last selected vault are remembered.

Keyboard: **Ctrl+N** new secret, **Ctrl+,** settings, **F5** refresh, **Ctrl+F** search, **Ctrl+E** edit, **Ctrl+L** lock, **F1** list of shortcuts.
In the secret list: **Enter** view, **F2** edit, **Del** delete, **Ctrl+C** copy. **Esc** closes a side panel.

## Dev keys, prod keys, and scripts (or Claude Code)

Where a secret lives decides whether reading it asks for approval:

| Vault type (Add vault…) | Reading it | Good for |
| --- | --- | --- |
| **Windows account** | never asks; encrypted for your Windows account (DPAPI), so anything running as you can read it | development keys |
| **SecretStore with a password**, *Unlock with Windows Hello* on (Store settings) | asks for Windows Hello (PIN, face, fingerprint) every time, in the app and from the command line | production keys |

With Hello unlock on, the store password is saved only sealed by a Windows Hello key in the TPM: nothing can read the store
without a Hello check. Move a key between vaults with **Copy to vault…**.

From a script, a terminal or Claude Code, use `secrets` (`secrets.exe`, installed next to the app and on your PATH; open a new
terminal after installing):

```powershell
secrets get api-key                     # prints the value; exit 0 found, 1 not found, 2 cancelled/locked, 3 error
secrets get db-login --field username   # PSCredential: password by default, or the user name
secrets get api-key --vault Dev         # without --vault, password-free vaults are searched before SecretStore
```

Use a value inside the command instead of printing it, so it doesn't end up in logs or in a chat transcript:
`$env:API_KEY = secrets get api-key; ./deploy.ps1` (PowerShell) or `API_KEY="$(secrets get api-key)" ./deploy.sh` (bash).

### Telling Claude Code

Claude Code doesn't discover `secrets` by itself. Add this to `~/.claude/CLAUDE.md` (all projects) or a project's CLAUDE.md:

```markdown
## Secrets (API keys, tokens, passwords)

Read them with the `secrets` command (PowerShell Secrets) instead of asking me to paste them or creating files:
- PowerShell: `$env:API_KEY = secrets get <name>; <command>`, bash: `API_KEY="$(secrets get <name>)" <command>`.
- Never print, echo or log a value, and never write one to a file or commit it. Pass it straight to the command that needs it.
- Exit codes: 1 not found (ask me for the name), 2 I declined Windows Hello or the store is locked (don't retry; ask), 3 error.
- `--vault <name>` picks a vault; `--field username` gives a credential's user name.
``` Windows-account vaults also work with plain `Get-Secret`.

## Security notes

- Copies are **cleared from the clipboard after 30 s**, and on exit, if the clipboard still holds them. They are also kept out of
  Windows clipboard history (Win+V) and cloud clipboard sync.
- PowerShell runs **inside the app process**. SecretStore's unlock therefore lasts for the session, as it would in a PowerShell window.
  The hosted runspace uses ExecutionPolicy `Bypass` for itself only; it runs no scripts other than the app's built-in ones.
- While you view or edit a secret, its value is held as a .NET string. Neither WinUI text controls nor modern .NET support `SecureString`
  end to end. Values become `SecureString` again when they are handed to the SecretManagement cmdlets.
- **Metadata is not encrypted** in the vault. Keep secrets out of it. (In export files, everything is encrypted, names and metadata too.)
- **Export files** use AES-256-GCM with a key derived from your password (PBKDF2-SHA256, 600,000 iterations). Anyone with the file and
  the password can read every secret in it, so choose a strong password and store the file carefully.
- **Locking SecretStore** uses SecretStore's internal reset of its unlocked state (it has no public lock command). If a future SecretStore
  version changes that, the app says so, and the store still locks itself after its password timeout.
- Prefer SecretStore `Interaction = None` (the app creates stores that way). With `Prompt`, SecretStore can't prompt inside the app, so
  the app asks for the password itself.

## Tests

```powershell
dotnet test --project tests/SecretsManager.Core.Tests                     # ~60 tests, a few seconds
dotnet test --project tests/SecretsManager.Core.Tests -- --explicit only  # real SecretStore round trip (see below)
pwsh -NoProfile -File tests/Invoke-AppUiTest.ps1                         # drives the built app with UI Automation (build first)
```

- **Normal tests** never touch your SecretStore. Integration tests use `tests/TestVault`, a small file-based extension vault, under
  temporary vault names that are removed afterwards.
- **The explicit test** uses the real SecretStore. It refuses to run if a store already exists for your account, and deletes the one it creates.
