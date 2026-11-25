<#
    End-to-end UI check: launches SecretsGui.ps1 and drives it with UI Automation against two
    temporary TestVault vaults (one password-protected). Registers/unregisters them itself.
    Run: pwsh -NoProfile -File tests/Invoke-UiSmoke.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Import-Module Microsoft.PowerShell.SecretManagement

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$root = $AE::RootElement
$suffix = [guid]::NewGuid().ToString('N').Substring(0, 6)
$plainVault = "SgUi_$suffix"
$lockedVault = "SgUiLocked_$suffix"
$tmp = [IO.Path]::GetTempPath()
$vaultModule = Join-Path $PSScriptRoot '..\..\tests\TestVault\TestVault.psd1'
$results = [System.Collections.Generic.List[string]]::new()

function Find-Window([string]$Name) {
    $c = [System.Windows.Automation.PropertyCondition]::new($AE::NameProperty, $Name)
    $w = $root.FindFirst($TS::Children, $c)
    if ($w) { return $w }
    # dialogs owned by the main window can appear under it in the UIA tree
    if ($script:main) {
        $isWindow = [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $CT::Window)
        return $script:main.FindFirst($TS::Descendants, [System.Windows.Automation.AndCondition]::new($c, $isWindow))
    }
}
function Get-Texts($el) {
    $all = $el.FindAll($TS::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $CT::Text))
    foreach ($t in $all) { $t.Current.Name }
}
function Wait-Until([scriptblock]$Condition, [int]$TimeoutSec = 15, [string]$What = 'condition') {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $r = & $Condition
        if ($r) { return $r }
        $err = Find-Window 'Error'
        if ($err) { throw "GUI error dialog while waiting for $($What): $((Get-Texts $err) -join ' | ')" }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for $What"
}
function Find-ById($parent, [string]$Id) {
    $parent.FindFirst($TS::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::AutomationIdProperty, $Id))
}
function Find-ByName($parent, [string]$Name) {
    $parent.FindFirst($TS::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::NameProperty, $Name))
}
function Find-Button($parent, [string]$Name) {
    $c = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new($AE::NameProperty, $Name),
        [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $CT::Button))
    $parent.FindFirst($TS::Descendants, $c)
}
Add-Type -Namespace SgUi -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern System.IntPtr SendMessage(System.IntPtr h, int msg, System.IntPtr w, System.IntPtr l);'
function Invoke-Win32Button($dialog, [int]$ControlId) {
    # Win32 MessageBox buttons surface as Panes without InvokePattern; click via BM_CLICK.
    $btn = Find-ById $dialog ([string]$ControlId)
    if (-not $btn) { throw "button $ControlId not found" }
    [void][SgUi.Native]::SendMessage([IntPtr]$btn.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
}
function Get-Ancestor($el, $type) {
    while ($el -and $el.Current.ControlType -ne $type) { $el = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($el) }
    $el
}
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Set-Val($el, $v) { $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function Select-El($el) { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Pass($m) { $results.Add("PASS $m"); Write-Host "PASS $m" }

$proc = $null
try {
    Register-SecretVault -Name $plainVault -ModuleName $vaultModule -VaultParameters @{ Path = "$tmp$plainVault.clixml" }
    Register-SecretVault -Name $lockedVault -ModuleName $vaultModule -VaultParameters @{ Path = "$tmp$lockedVault.clixml"; Password = 'pw123' }
    Set-SecretVaultDefault -Name $plainVault
    Set-Secret -Name existing -Secret 'hello' -Vault $plainVault
    # seed the locked vault: unlock, write, then lock it again by removing the marker file
    Unlock-SecretVault -Name $lockedVault -Password (ConvertTo-SecureString 'pw123' -AsPlainText -Force)
    Set-Secret -Name lockedsecret -Secret 'x' -Vault $lockedVault
    Remove-Item "$tmp$lockedVault.clixml.unlocked"

    $gui = Join-Path $PSScriptRoot '..\SecretsGui.ps1'
    $proc = Start-Process pwsh -ArgumentList '-NoProfile', '-STA', '-NonInteractive', '-File', ('"{0}"' -f $gui) -PassThru
    $script:main = Wait-Until { Find-Window 'PowerShell Secrets' } 30 'main window'
    Pass 'main window opened'

    $grid = Find-ById $main 'SecretGrid'
    [void](Wait-Until { Find-ByName $grid 'existing' } 15 'existing secret in grid')
    Pass 'default vault secrets listed'

    # --- add a secret through the editor
    Invoke-El (Find-ById $main 'AddSecretButton')
    $ed = Wait-Until { Find-Window "New secret - $plainVault" } 10 'editor'
    Set-Val (Find-ById $ed 'NameBox') 'uitest1'
    Invoke-El (Find-ById $ed 'SecureGenerateButton')
    Set-Val (Find-ById $ed 'MetadataBox') 'env=ui'
    Invoke-El (Find-ById $ed 'SaveButton')
    [void](Wait-Until { -not (Find-Window "New secret - $plainVault") } 10 'editor to close')
    $info = Get-SecretInfo -Vault $plainVault -Name uitest1
    if (-not $info -or [string]$info.Type -ne 'SecureString' -or $info.Metadata['env'] -ne 'ui') { throw 'uitest1 was not saved correctly' }
    if ((Get-Secret -Name uitest1 -Vault $plainVault -AsPlainText).Length -ne 24) { throw 'generated password has wrong length' }
    Pass 'new secret saved (SecureString, generated 24 chars, metadata)'

    # --- name collision keeps the editor open with an error
    Invoke-El (Find-ById $main 'AddSecretButton')
    $ed = Wait-Until { Find-Window "New secret - $plainVault" } 10 'editor'
    Set-Val (Find-ById $ed 'NameBox') 'existing'
    Invoke-El (Find-ById $ed 'SecureGenerateButton')
    Invoke-El (Find-ById $ed 'SaveButton')
    $errText = Wait-Until { $t = Find-ById $ed 'ErrorText'; if ($t -and $t.Current.Name) { $t.Current.Name } } 10 'duplicate error'
    Pass "duplicate name rejected in-dialog: $errText"
    Invoke-El (Find-ById $ed 'CancelButton')
    if ((Get-Secret -Name existing -Vault $plainVault -AsPlainText) -ne 'hello') { throw 'existing secret was overwritten' }
    Pass 'existing secret untouched'

    # --- delete with confirmation
    $row = Get-Ancestor (Wait-Until { Find-ByName $grid 'uitest1' } 10 'uitest1 row') $CT::DataItem
    Select-El $row
    Invoke-El (Find-ById $main 'DeleteSecretButton')
    $confirm = Wait-Until { Find-Window 'Delete secret' } 10 'delete confirmation'
    Invoke-Win32Button $confirm 6   # IDYES
    [void](Wait-Until { -not (Get-SecretInfo -Vault $plainVault -Name uitest1) } 10 'deletion')
    Pass 'secret deleted after confirmation'

    # --- locked vault: selecting it prompts for the password via the GUI, wrong then right
    $vaultList = Find-ById $main 'VaultList'
    $item = Get-Ancestor (Wait-Until { Find-ByName $vaultList $lockedVault } 10 'locked vault item') $CT::ListItem
    Select-El $item
    $pw = Wait-Until { Find-Window 'Unlock vault' } 10 'unlock prompt'
    Set-Val (Find-ById $pw 'PasswordBox1') 'wrong'
    Invoke-El (Find-ById $pw 'OkButton')
    Start-Sleep -Milliseconds 800
    $pw = Wait-Until { Find-Window 'Unlock vault' } 10 're-prompt after wrong password'
    $msg = (Get-Texts $pw) -join ' '
    if ($msg -notmatch 'not accepted') { throw "expected re-prompt message, got: $msg" }
    Pass 'wrong password re-prompts'
    Set-Val (Find-ById $pw 'PasswordBox1') 'pw123'
    Invoke-El (Find-ById $pw 'OkButton')
    [void](Wait-Until { Find-ByName $grid 'lockedsecret' } 10 'locked vault secrets')
    Pass 'locked vault unlocked through GUI prompt and listed'

    # --- clipboard copy with auto-clear countdown
    $row = Get-Ancestor (Find-ByName $grid 'lockedsecret') $CT::DataItem
    Select-El $row
    Invoke-El (Find-ById $main 'CopySecretButton')
    [void](Wait-Until { (Find-ById $main 'ClipboardText').Current.Name -match 'clears in' } 5 'clipboard countdown')
    Pass 'copy started clipboard auto-clear countdown'

    $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $proc.WaitForExit(10000)) { throw 'GUI did not exit after closing the main window' }
    Pass "GUI exited (code $($proc.ExitCode))"
    "ALL UI CHECKS PASSED ($($results.Count))"
}
finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill() }
    foreach ($v in $plainVault, $lockedVault) { Unregister-SecretVault -Name $v -ErrorAction SilentlyContinue }
    Remove-Item "$tmp$plainVault.clixml*", "$tmp$lockedVault.clixml*" -ErrorAction SilentlyContinue
}
