# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

This is a native Windows GUI for PowerShell SecretManagement and SecretStore: C# on .NET 11, with the UI in **Microsoft.UI.Reactor**,
an experimental MVU-style framework over WinUI 3. The app is unpackaged and self-contained. `legacy/` holds the earlier
PowerShell/WPF version; it still works and has its own README and tests. The user chose this stack explicitly (Reactor on .NET 11,
unpackaged, legacy kept side by side).

## Commands

```powershell
dotnet build src/SecretsManager.App -p:Platform=x64          # -p:Platform is REQUIRED (Windows App SDK needs an architecture)
dotnet run --project src/SecretsManager.App -p:Platform=x64
dotnet test --project tests/SecretsManager.Core.Tests        # MTP runner (global.json); a few seconds, ~60 tests
dotnet test --project tests/SecretsManager.Core.Tests -- --filter-method "*Locked*"   # subset
dotnet test --project tests/SecretsManager.Core.Tests -- --explicit only              # real SecretStore round trip
pwsh -NoProfile -File tests/Invoke-AppUiTest.ps1 [-ExePath publish\SecretsManager.exe]  # UI Automation E2E (build first)
dotnet publish src/SecretsManager.App -c Release -p:Platform=x64 -r win-x64 --self-contained -o publish
pwsh -NoProfile -File tools/Install-SecretsManager.ps1       # publish + install to %LOCALAPPDATA%\Programs\SecretsManager + Start menu shortcut
pwsh -NoProfile -File tools/New-AppIcon.ps1                  # redraw src/SecretsManager.App/Assets/app.ico
```

Run the UI test after any change in `src/SecretsManager.App`. The unit tests do not exercise any rendering code.
**The user runs the installed copy** (`%LOCALAPPDATA%\Programs\SecretsManager`, Start menu "PowerShell Secrets"; earlier `publish\`).
After a change, reinstall with `tools/Install-SecretsManager.ps1` (it refuses while that copy is running, so the user must close it) and
run the UI test with `-ExePath` on the installed exe. Verifying only the Debug build once led to reporting a fix the user couldn't see.

## Architecture

- **`src/SecretsManager.Core`** (net11.0) has no UI dependency.
  - `PowerShellHost`: one in-process runspace. Calls are serialized by a semaphore and run on a background thread. Errors become
    `SecretsOperationException`, or `VaultLockedException` when the message matches SecretStore's "password required" texts.
    SecretStore's unlock state lasts as long as the process, which is why PowerShell is hosted rather than spawned.
  - `SecretsService : ISecretsService`: every SecretManagement/SecretStore call, as small PowerShell scripts. Values are bound
    through `param()`, never spliced into script text.
  - `LockedRetry`: catch `VaultLockedException`, ask for the password, unlock, retry. A wrong password asks again; cancelling throws
    `OperationCanceledException`.
  - `App/AppStore` + `App/AppState`: the whole application as an immutable state record plus async actions (all flows,
    confirmations, the clipboard countdown). **Prompts are state**: `AskAsync` sets `State.Prompt` and awaits a
    TaskCompletionSource, and the UI answers with `CompletePrompt`. This is why every flow is unit-testable with `FakeSecretsService`
    and `PromptScript`.
- **`src/SecretsManager.App`** (WinUI 3 / Reactor) only renders `AppStore.State` (subscribed with `UseExternalStore`) and calls
  AppStore methods. Simple questions use `PromptDialog`, a ContentDialog keyed by prompt Id that reports its answer from `OnClosed`.
  Forms (editor, viewer, register vault, store settings) are in-window overlay panels, **not** ContentDialogs: only one ContentDialog can
  be open per XamlRoot, and a locked vault must be able to prompt on top of a form.
- **`tests/TestVault`** is a minimal file-backed SecretManagement extension vault, shared by the C# tests and `legacy/`.
  `VaultParameters.Path` is a Clixml file; the optional `Password` simulates a locked vault, with unlock state kept in a `.unlocked`
  marker file.

## Pitfalls already hit (do not reintroduce)

- **PowerShell scripts:** `param()` must be the first statement, so nothing may be prepended to a script.
  `$ErrorActionPreference='Stop'` is set through `InitialSessionState`, and modules autoload.
- **`Write-Output -NoEnumerate $x`** wraps a scalar in a `List<object>` in PowerShell 7. Use the comma operator (`,(Get-Secret …)`),
  which also keeps a `byte[]` as one object.
- **`Get-Secret -AsPlainText`** returns SecureString secrets as `[string]`, so the stored kind must come from `SecretInfo.Type`.
- **Reactor `ListView<T>`** has no controlled selection. The selected index is pushed with `.Set(lv => lv.SelectedIndex = …)`;
  setting the same name again is a no-op in AppStore, so this doesn't loop.
- **Lambda `_` parameters:** in `ListView<T>(…, (x, _) => …)`, `_` is a real `int` parameter. An inner `_ = task` then assigns to it
  (CS0029), so name the parameter.
- **CsWinRT1032:** pass explicit arrays, not collection expressions, to WinRT option types (`FilePickerOptions` etc.).
- **`.FontFamily()` on a TextBox** also changes its header font. Fluent `Theme.CardBackground` is translucent, so overlays need
  `Theme.SolidBackground`.
- **Reactor analyzers** (`REACTOR_A11Y_003`, `REACTOR_HOOKS_011`, `REACTOR_GRID_001`) catch real issues; keep the build free of them.
- **Reactor API reference:** read the files shipped in the NuGet package,
  `%USERPROFILE%\.nuget\packages\microsoft.ui.reactor\<version>\agentkit\`. `reactor.api.txt` lists every signature,
  `skills/*.md` are guides, and `skills/recipes/*.cs` are worked examples. The public docs lag the package.
- **UI tests:** after a UI Automation `SetValue`, WinUI raises `TextChanged` asynchronously, so pause before clicking. TestVault reads
  can race the app's writes, so retry them.
- **Self-contained publish moves `System.Management.Automation.dll` into the publish root**, which makes it the hosted
  PowerShell's `$PSHOME`. The built-in modules (Management, Utility, …) must then be in `publish\Modules`, which the
  `CopyPowerShellBuiltInModulesToPublishRoot` target in the App csproj handles. Without them, `Register-SecretVault` and
  `Write-Verbose` fail, but **only when the app is started from Explorer**.
- **The UI test launches the app with Explorer's real, captured environment**, where `PSModulePath` contains a literal
  `%ProgramFiles%`. Don't simplify this: launching from pwsh, using `Start-Process -UseNewEnvironment`, or even prepending a
  folder to `PSModulePath` all hid the missing-modules bug.
- **Registering a SecretStore vault writes `localstore\storeconfig` without creating the store.** `AppState.IsStoreMissing`
  makes the app offer to create the store instead of asking to unlock one that doesn't exist.
- **Real-store tests share SecretStore's process-wide static state**, so `RealSecretStoreTests` is a single test that runs its
  scenarios in a fixed order. Deleting the store folder mid-process breaks SecretStore's own password timeout afterwards (the app
  never deletes it). To run them on a machine that has a store: back up `localstore` and `secretvaultregistry`, remove
  `localstore`, run, then restore and compare file hashes. Only do that with the user's consent.
- **Locking SecretStore:** there is no Lock cmdlet. `LockStoreAsync` calls the internal static `LocalSecretStore.Reset()` through
  reflection on every loaded SecretStore assembly. The unlocked state is process-wide, not per runspace. The real-store tests cover this.
- **Auto-lock** runs on `TimeProvider` (5-second checks). The UI reports activity with `NotifyActivity()` from the root's
  `OnPreviewKeyDown`/`OnPointerMoved`. Locking closes the viewer and editor, so unsaved edits are dropped after the idle time.
- **Reactor hooks** like `UseElementFocus` are extension methods in `Microsoft.UI.Reactor.Hooks`: call them as `this.UseElementFocus()`.
- **`Reset-SecretStore` without a password (Interaction None) writes only `storeconfig`.** The `storefile` appears on first access,
  so `ResetStoreAsync` calls `Get-SecretStoreConfiguration` afterwards. Without that, `IsSecretStoreInitialized` stayed false and
  the app kept offering to create the store.
- **Screenshots:** capture **only the app window** (`PrintWindow` on its handle). Never capture the screen; it can expose the user's
  other windows.

## Safety

The tests must never use the user's real SecretStore. It is one per-user store under
`%LOCALAPPDATA%\Microsoft\PowerShell\secretmanagement\localstore`, and resetting it destroys their secrets. The only exception is
the explicit `RealSecretStoreTests`, which skips if that folder exists and deletes only a store it created itself.
