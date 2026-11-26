<#
    End-to-end UI test for the C# app (src/SecretsManager.App). Builds nothing: run
    "dotnet build src/SecretsManager.App -p:Platform=x64" first. Drives the real window with UI
    Automation against two temporary TestVault vaults (one password-protected) that it registers and
    removes itself. Never touches the real SecretStore. Only the app's own window is ever captured.

    Run: pwsh -NoProfile -File tests/Invoke-AppUiTest.ps1 [-Configuration Debug] [-ExePath publish\SecretsManager.exe]
#>
param([string]$Configuration = 'Debug', [string]$ExePath, [string]$ScreenshotDir, [string]$CliPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Import-Module Microsoft.PowerShell.SecretManagement

$exe = if ($ExePath) { $ExePath } else { Join-Path $PSScriptRoot "..\src\SecretsManager.App\bin\x64\$Configuration\net11.0-windows10.0.22621.0\SecretsManager.exe" }
$cli = if ($CliPath) { $CliPath } elseif ($ExePath) { Join-Path (Split-Path $ExePath) 'SecretsManager.Cli.exe' } else {
    Join-Path $PSScriptRoot "..\src\SecretsManager.Cli\bin\$Configuration\net11.0\SecretsManager.Cli.exe" }
if (-not (Test-Path $cli)) { throw "Build the CLI first: dotnet build src/SecretsManager.Cli ($cli not found)" }
if (-not (Test-Path $exe)) { throw "Build the app first: dotnet build src/SecretsManager.App -p:Platform=x64 ($exe not found)" }

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$suffix = [guid]::NewGuid().ToString('N').Substring(0, 6)
$plain = "SgUi_$suffix"; $locked = "SgUiLocked_$suffix"
$tmp = [IO.Path]::GetTempPath()
$vaultModule = Join-Path $PSScriptRoot 'TestVault\TestVault.psd1'
$passed = 0

function Cond($prop, $value) { [System.Windows.Automation.PropertyCondition]::new($prop, $value) }
function ById($id) { $script:main.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)) }
function ByName($name, $type) {
    $c = Cond $AE::NameProperty $name
    if ($type) { $c = [System.Windows.Automation.AndCondition]::new($c, (Cond $AE::ControlTypeProperty $type)) }
    $script:main.FindFirst($TS::Descendants, $c)
}
function Wait-For([scriptblock]$Probe, [string]$What, [int]$Seconds = 15) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $r = & $Probe
        if ($r) { return $r }
        $err = if ($script:main) { ById 'ErrorBar' }
        if ($err) { throw "App showed an error while waiting for $($What): $($err.Current.Name) $((ById 'StatusText').Current.Name)" }
        Start-Sleep -Milliseconds 200
    }
    $status = if ($script:main) { (ById 'StatusText').Current.Name }
    throw "Timed out waiting for $What (status: $status)"
}
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Set-Val($el, $v) {
    $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v)
    Start-Sleep -Milliseconds 300   # WinUI raises TextChanged asynchronously after a UIA SetValue
}
function Select-Row($name, $listId) {
    $text = Wait-For { $list = ById $listId; if ($list) { $list.FindFirst($TS::Descendants, (Cond $AE::NameProperty $name)) } } "row '$name'"
    $item = $text
    while ($item -and $item.Current.ControlType -ne $CT::ListItem) { $item = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($item) }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
# TestVault keeps secrets in a Clixml file the app rewrites; a read can race a write, so retry.
function Get-Info($vault, $name) { try { Get-SecretInfo -Vault $vault -Name $name } catch { $null } }
function Get-Value($vault, $name) { try { Get-Secret -Vault $vault -Name $name -AsPlainText } catch { $null } }
function Pass($m) { $script:passed++; Write-Host "PASS $m" }
function Save-Shot($name) {
    if (-not $ScreenshotDir) { return }
    Add-Type -AssemblyName System.Drawing
    if (-not ('SgUi.Native' -as [type])) {
        Add-Type -Namespace SgUi -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool PrintWindow(System.IntPtr h, System.IntPtr dc, uint f);'
    }
    $r = $script:main.Current.BoundingRectangle; $bmp = [Drawing.Bitmap]::new([int]$r.Width, [int]$r.Height); $g = [Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc(); [void][SgUi.Native]::PrintWindow([IntPtr]$script:main.Current.NativeWindowHandle, $dc, 2); $g.ReleaseHdc($dc)
    New-Item -ItemType Directory -Force -Path $ScreenshotDir | Out-Null
    $bmp.Save((Join-Path $ScreenshotDir "$name.png")); $g.Dispose(); $bmp.Dispose()
}
function Select-ComboItem($comboId, $itemName) {
    $combo = Wait-For { ById $comboId } "combo $comboId"
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $item = Wait-For { $combo.FindFirst($TS::Descendants, (Cond $AE::NameProperty $itemName)) } "item '$itemName'"
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    try { $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
}

$proc = $null
try {
    Register-SecretVault -Name $plain -ModuleName $vaultModule -VaultParameters @{ Path = "$tmp$plain.clixml" }
    Register-SecretVault -Name $locked -ModuleName $vaultModule -VaultParameters @{ Path = "$tmp$locked.clixml"; Password = 'pw123' }
    Set-SecretVaultDefault -Name $plain
    Set-Secret -Name existing -Secret 'hello' -Vault $plain
    Unlock-SecretVault -Name $locked -Password (ConvertTo-SecureString 'pw123' -AsPlainText -Force)
    Set-Secret -Name lockedsecret -Secret 'x' -Vault $locked
    Remove-Item "$tmp$locked.clixml.unlocked"   # lock it again

    # Put tests\ on the app's module path so the Register form can resolve the TestVault module by NAME
    # (name resolution uses built-in cmdlets such as Resolve-Path, which broke once in the published app).
    # Launch with EXPLORER'S REAL ENVIRONMENT, not the one inherited from this pwsh session: double-clicking the exe
    # gives it Explorer's environment, where PSModulePath holds a literal, unexpanded %ProgramFiles%. Only that
    # environment reproduced a missing-built-in-modules bug in the published app. It is captured by having
    # Explorer run a one-line .cmd. Do NOT modify it: even prepending a folder to PSModulePath hides the bug.
    $envDir = Join-Path ([IO.Path]::GetTempPath()) "sg-env-$suffix"
    New-Item -ItemType Directory $envDir | Out-Null
    $envFile = Join-Path $envDir 'env.txt'
    Set-Content (Join-Path $envDir 'dump.cmd') "@set > `"$envFile`""
    Start-Process explorer.exe -ArgumentList ('"{0}"' -f (Join-Path $envDir 'dump.cmd'))
    [void](Wait-For { (Test-Path $envFile) -and (Get-Item $envFile).Length -gt 0 } "Explorer's environment" 15)
    Start-Sleep -Milliseconds 300
    $psi = [System.Diagnostics.ProcessStartInfo]::new($exe)
    # Test-only switches: a private settings file (so the user's theme and last vault are untouched) and a file path
    # that export/import use instead of a file dialog.
    $settingsFile = Join-Path $tmp "sg-settings-$suffix.json"
    $exportFile = Join-Path $tmp "sg-export-$suffix.smsecrets"
    foreach ($a in '--settings-file', $settingsFile, '--ui-test-file', $exportFile) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = Split-Path $exe
    $psi.Environment.Clear()
    foreach ($line in Get-Content $envFile) {
        $i = $line.IndexOf('=')
        if ($i -gt 0) { $psi.Environment[$line.Substring(0, $i)] = $line.Substring($i + 1) }
    }
    Remove-Item $envDir -Recurse -Force
    $proc = [System.Diagnostics.Process]::Start($psi)
    $script:main = Wait-For { $AE::RootElement.FindFirst($TS::Children, [System.Windows.Automation.AndCondition]::new(
        (Cond $AE::NameProperty 'PowerShell Secrets'), (Cond $AE::ProcessIdProperty $proc.Id))) } 'main window' 40
    Pass 'main window opened'

    [void](Wait-For { ByName 'existing' } 'existing secret listed' 30)
    Pass 'default vault selected and listed'

    # --- new secret through the editor panel
    Invoke-El (ById 'NewSecretButton')
    $nameBox = Wait-For { ById 'NameBox' } 'editor'
    Set-Val $nameBox 'uitest1'
    Invoke-El (ById 'GenerateButton')
    Set-Val (ById 'MetadataBox') 'env=ui'
    Invoke-El (ById 'SaveButton')
    [void](Wait-For { -not (ById 'NameBox') } 'editor to close')
    $info = Wait-For { Get-Info $plain uitest1 } 'uitest1 in the vault'
    if ([string]$info.Type -ne 'SecureString' -or $info.Metadata['env'] -ne 'ui') { throw 'uitest1 was not saved correctly' }
    if ((Wait-For { Get-Value $plain uitest1 } 'uitest1 value').Length -ne 24) { throw 'generated password has the wrong length' }
    Pass 'new SecureString secret saved with generated password and metadata'
    [void](Wait-For { (ByName 'env') -and (ByName 'ui') } 'metadata column "env" with value "ui"')
    Pass 'metadata key shown as its own column'

    # --- taken name: editor stays open with the vault's error
    Invoke-El (ById 'NewSecretButton')
    Set-Val (Wait-For { ById 'NameBox' } 'editor') 'existing'
    Invoke-El (ById 'GenerateButton')
    Invoke-El (ById 'SaveButton')
    $err = Wait-For { $e = ById 'PanelError'; if ($e) { $e.Current.Name } } 'duplicate-name error'
    Pass "duplicate name rejected in the editor: $err"
    Invoke-El (ById 'CancelButton')
    if ((Wait-For { Get-Value $plain existing } 'existing value') -ne 'hello') { throw 'existing secret was overwritten' }
    Pass 'existing secret untouched'

    # --- view with reveal
    Select-Row 'existing' 'SecretList'
    Invoke-El (Wait-For { $b = ById 'ViewSecretButton'; if ($b.Current.IsEnabled) { $b } } 'View enabled')
    $value = Wait-For { ById 'ViewerValue' } 'viewer'
    if ($value.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'hello') { throw 'value shown before reveal' }
    $check = ById 'RevealValueCheck'
    $check.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    [void](Wait-For { (ById 'ViewerValue').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'hello' } 'revealed value')
    Invoke-El (ById 'CloseViewerButton')
    Pass 'viewer masks until revealed'

    # --- delete with confirmation dialog
    Select-Row 'uitest1' 'SecretList'
    Invoke-El (Wait-For { $b = ById 'DeleteSecretButton'; if ($b.Current.IsEnabled) { $b } } 'Delete enabled')
    Invoke-El (Wait-For { ByName 'Delete' $CT::Button | Where-Object { $_.Current.AutomationId -ne 'DeleteSecretButton' } } 'delete confirmation')
    [void](Wait-For { $all = try { @(Get-SecretInfo -Vault $plain) } catch { $null }; $all -and -not ($all.Name -contains 'uitest1') } 'deletion')
    Pass 'secret deleted after confirmation'

    # --- locked vault: wrong password re-prompts, right one unlocks
    Select-Row $locked 'VaultList'
    Set-Val (Wait-For { ById 'PromptPassword' } 'unlock prompt') 'wrong'
    Invoke-El (Wait-For { ByName 'Unlock' $CT::Button } 'Unlock button')
    [void](Wait-For { ByName "The password was not accepted. Enter the password for '$locked'." } 're-prompt')
    Pass 'wrong password re-prompts'
    Set-Val (Wait-For { ById 'PromptPassword' } 'second prompt') 'pw123'
    Invoke-El (Wait-For { ByName 'Unlock' $CT::Button } 'Unlock button')
    [void](Wait-For { ByName 'lockedsecret' } 'locked vault secrets')
    Pass 'locked vault unlocked through the dialog and listed'

    # --- register a vault through the form. Register-SecretVault calls built-in cmdlets (Resolve-Path); this step
    #     failed in the published app when PowerShell's built-in modules weren't next to System.Management.Automation.dll.
    $byName = "SgUiByName_$suffix"
    Invoke-El (ById 'RegisterVaultButton')
    [void](Wait-For { ById 'VaultNameBox' } 'register form')
    Select-ComboItem 'VaultKindCombo' 'Other extension module'
    Set-Val (Wait-For { ById 'VaultNameBox' } 'register form') $byName
    Set-Val (Wait-For { ById 'VaultModuleBox' } 'module box') $vaultModule
    Set-Val (ById 'VaultParametersBox') "Path=$tmp$byName.clixml"
    $default = ById 'VaultDefaultCheck'
    if ($default.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') {
        $default.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    }
    Invoke-El (ById 'VaultSaveButton')
    [void](Wait-For {
        $formError = ById 'PanelError'
        if ($formError) { throw "Register form showed an error: $($formError.Current.Name)" }
        -not (ById 'VaultNameBox')
    } 'register form to close')
    $registered = Get-SecretVault -Name $byName -ErrorAction SilentlyContinue
    if (-not $registered) { throw "vault registered through the form not found" }
    Pass 'vault registered through the Register form'

    # --- export the plain vault to an encrypted file, then import it into the new (empty) vault
    Select-Row $plain 'VaultList'
    [void](Wait-For { ByName 'existing' } 'plain vault listed')
    Invoke-El (Wait-For { $b = ById 'ExportButton'; if ($b -and $b.Current.IsEnabled) { $b } } 'Export enabled')
    Set-Val (Wait-For { ById 'PromptPassword' } 'export password prompt') 'exp#123'
    Set-Val (ById 'PromptConfirm') 'exp#123'
    Invoke-El (Wait-For { ByName 'Export' $CT::Button } 'Export button in the dialog')
    [void](Wait-For { (Test-Path $exportFile) -and (ById 'StatusText').Current.Name -like 'Exported*' } 'export file')
    if ((Get-Content $exportFile -Raw) -match 'hello|existing') { throw 'the export file contains plain text' }
    Pass 'vault exported to an encrypted file'
    Select-Row $byName 'VaultList'
    Invoke-El (Wait-For { $b = ById 'ImportButton'; if ($b -and $b.Current.IsEnabled) { $b } } 'Import enabled')
    Set-Val (Wait-For { ById 'PromptPassword' } 'import password prompt') 'exp#123'
    Invoke-El (Wait-For { ByName 'Open' $CT::Button } 'Open button in the dialog')
    [void](Wait-For { (Get-Value $byName existing) -eq 'hello' } 'imported secret in the new vault')
    Pass 'export file imported into another vault'

    # --- a Windows-account vault (dev keys): created through the form, readable by plain Get-Secret and by "get"
    $dev = "SgUiDev_$suffix"
    $devFile = Join-Path $env:LOCALAPPDATA "SecretsManager\vaults\$dev.clixml"
    Invoke-El (ById 'RegisterVaultButton')
    [void](Wait-For { ById 'VaultNameBox' } 'register form')
    Select-ComboItem 'VaultKindCombo' 'Windows account (never asks)'
    Set-Val (ById 'VaultNameBox') $dev
    $default = ById 'VaultDefaultCheck'
    if ($default.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') {
        $default.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    }
    Invoke-El (ById 'VaultSaveButton')
    [void](Wait-For { -not (ById 'VaultNameBox') } 'register form to close')
    Select-Row $dev 'VaultList'
    Invoke-El (Wait-For { $b = ById 'NewSecretButton'; if ($b.Current.IsEnabled) { $b } } 'New secret enabled')
    Set-Val (Wait-For { ById 'NameBox' } 'editor') 'devkey'
    Select-ComboItem 'TypeCombo' 'String'
    Set-Val (Wait-For { ById 'ValueBox' } 'value box') 'dev-value-123'
    Invoke-El (ById 'SaveButton')
    [void](Wait-For { -not (ById 'NameBox') } 'editor to close')
    if ((Wait-For { Get-Value $dev devkey } 'devkey via Get-Secret') -ne 'dev-value-123') { throw 'Get-Secret returned the wrong dev value' }
    if ((Get-Content $devFile -Raw) -match 'dev-value-123') { throw 'the Windows-account vault file contains plain text' }
    Pass 'Windows-account vault: created in the app, encrypted on disk, readable by Get-Secret'
    if ((ById 'UnlockButton').Current.IsEnabled) { throw 'Unlock is enabled on a vault that has no password' }
    Pass 'Unlock is disabled on a vault without a password'
    # SecretsManager.Cli.exe (console) relays to the exe under test: PowerShell doesn't capture a GUI exe's output.
    $env:SECRETSMANAGER_APP = (Resolve-Path $exe).Path
    $cliOut = & $cli get devkey --vault $dev
    if ($LASTEXITCODE -ne 0 -or $cliOut -ne 'dev-value-123') { throw "get returned '$cliOut' (exit $LASTEXITCODE)" }
    $null = & $cli get "missing-$suffix" 2>$null
    if ($LASTEXITCODE -ne 1) { throw "get of a missing secret exited $LASTEXITCODE, expected 1" }
    Remove-Item Env:SECRETSMANAGER_APP
    Pass 'SecretsManager.Cli get prints a dev key without asking; exit 1 when missing'

    # --- copy starts the clipboard countdown
    Select-Row $locked 'VaultList'
    Select-Row 'lockedsecret' 'SecretList'
    Invoke-El (Wait-For { $b = ById 'CopySecretButton'; if ($b.Current.IsEnabled) { $b } } 'Copy enabled')
    [void](Wait-For { ById 'ClipboardText' } 'clipboard countdown' 5)
    Pass 'copy started the clipboard auto-clear countdown'

    # --- Lock now: drops the copied value and reports it
    Invoke-El (ById 'LockButton')
    [void](Wait-For { -not (ById 'ClipboardText') -and (ById 'StatusText').Current.Name -match 'Locked|cleared the clipboard' } 'lock to clear the clipboard countdown')
    Pass 'Lock cleared the clipboard countdown'

    Save-Shot 'app-final'

    # --- themes: every choice applies and is saved to the (test) settings file
    Invoke-El (ById 'SettingsButton')
    [void](Wait-For { ById 'ThemeCombo' } 'settings panel')
    foreach ($t in @(@('Catppuccin Latte', 'Latte'), @('Catppuccin Frappé', 'Frappe'), @('Catppuccin Macchiato', 'Macchiato'),
                     @('Catppuccin Mocha', 'Mocha'), @('Windows (light or dark)', 'System'))) {
        Select-ComboItem 'ThemeCombo' $t[0]
        [void](Wait-For { (Test-Path $settingsFile) -and (Get-Content $settingsFile -Raw | ConvertFrom-Json).Theme -eq $t[1] } "theme $($t[1]) saved")
        Start-Sleep -Milliseconds 400
        Save-Shot "theme-$($t[1])"
    }
    Pass 'every theme applies and is saved'
    Invoke-El (ById 'SettingsCloseButton')

    $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $proc.WaitForExit(15000)) { throw 'app did not exit after closing its window' }
    Pass "app exited (code $($proc.ExitCode))"
    "ALL UI CHECKS PASSED ($passed)"
}
finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill() }
    foreach ($v in $plain, $locked, "SgUiByName_$suffix", "SgUiDev_$suffix") { Unregister-SecretVault -Name $v -ErrorAction SilentlyContinue }
    Remove-Item (Join-Path $env:LOCALAPPDATA "SecretsManager\vaults\SgUiDev_$suffix.clixml") -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $tmp "sg-settings-$suffix.json"), (Join-Path $tmp "sg-export-$suffix.smsecrets") -ErrorAction SilentlyContinue
    Remove-Item "$tmp$plain.clixml*", "$tmp$locked.clixml*", "$($tmp)SgUiByName_$($suffix).clixml*" -ErrorAction SilentlyContinue
}
