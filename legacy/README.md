# PowerShell Secrets

A Windows desktop GUI (WPF, pure PowerShell) for
[Microsoft.PowerShell.SecretManagement](https://github.com/PowerShell/SecretManagement) and
[Microsoft.PowerShell.SecretStore](https://github.com/PowerShell/SecretStore).

## Requirements

- Windows, PowerShell 7+ (recommended) or Windows PowerShell 5.1
- The modules, installed for the PowerShell edition you launch with:

  ```powershell
  Install-PSResource Microsoft.PowerShell.SecretManagement, Microsoft.PowerShell.SecretStore -Scope CurrentUser
  ```

## Run

Double-click **`SecretsGui.cmd`**. It starts pwsh (or powershell.exe) hidden, in STA and non-interactive mode.

Or from a terminal:

```powershell
pwsh -NoProfile -STA -NonInteractive -File .\SecretsGui.ps1
```

On first start with no vaults, the app offers to register a SecretStore vault and create the store, with a
password or without one.

## Features

- **Vaults**: list, register (any extension module, with vault parameters), unregister, set default, unlock.
- **Secrets**: list with search (name and metadata), view (masked until revealed), copy, create, edit, rename,
  copy to another vault, delete.
- **All secret types**: String, SecureString, PSCredential, Hashtable, ByteArray (load from / save to file), plus metadata.
- **Password generator** (24 random characters, ambiguous characters left out).
- **SecretStore settings**: authentication (Password/None), password timeout, interaction, change password,
  reset store (asks twice, and you must type `RESET`).
- **Locked vaults**: any operation on a locked vault opens a password prompt and retries.

### Keyboard

| Key | Action |
|---|---|
| Ctrl+N | New secret |
| Enter / double-click | View |
| Ctrl+C (in list) | Copy value (the password, for credentials) |
| F2 | Edit |
| Del | Delete |
| Ctrl+F / Esc | Search / clear search |
| F5 | Refresh |

## Security notes

- Copied values are **cleared from the clipboard after 30 s** if the clipboard still holds them. They are also
  marked so they are kept out of Windows clipboard history (Win+V) and cloud clipboard sync.
- Set the SecretStore **Interaction to `None`** (the app creates stores that way). With `Prompt`, SecretStore tries
  to ask for the password on the console; the app copes because it runs non-interactively, but `None` is cleaner.
- **Metadata is not encrypted**; keep secrets out of it.
- While you view or edit a secret, its plain text lives in WPF controls (WPF `TextBox` cannot hold a SecureString).

## Limitations

- Hashtable values and metadata values are edited and saved as strings.
- ByteArray secrets loaded from a file are limited to 1 MB.
- Rename and copy are implemented as copy + remove, because SecretManagement has no rename.

## Tests

```powershell
Invoke-Pester ./tests -Output Detailed          # core logic + integration against a file-backed test vault
pwsh -NoProfile -File ./SecretsGui.ps1 -SmokeTest   # every window's XAML loads and handlers wire up
pwsh -NoProfile -File ./tests/Invoke-UiSmoke.ps1    # drives the real GUI with UI Automation
```

The tests register temporary vaults that use `tests/TestVault` and unregister them afterwards. **They never touch the real SecretStore.**
