#Requires -Version 5.1
<#
.SYNOPSIS
    WPF desktop GUI for Microsoft.PowerShell.SecretManagement and Microsoft.PowerShell.SecretStore.

.DESCRIPTION
    Browse vaults, create / view / edit / rename / copy / delete secrets, and manage the
    SecretStore configuration (authentication, timeout, password, reset). Vault passwords are
    requested through GUI dialogs, so the SecretStore should use Interaction = None.

.PARAMETER SmokeTest
    Load every window and wire every handler without showing the GUI, then exit.
    Exit code 0 means all XAML parsed and every named control was found.

.EXAMPLE
    pwsh -STA -File .\SecretsGui.ps1
#>
[CmdletBinding()]
param([switch]$SmokeTest)

$ErrorActionPreference = 'Stop'

# WPF needs a single-threaded apartment. pwsh 7 and Windows PowerShell default to STA, but -MTA hosts exist.
if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne [System.Threading.ApartmentState]::STA) {
    $argList = @('-NoProfile', '-STA', '-File', $PSCommandPath)
    if ($SmokeTest) { $argList += '-SmokeTest' }
    & (Get-Process -Id $PID).Path @argList
    exit $LASTEXITCODE
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

$script:XamlDir = Join-Path $PSScriptRoot 'src\Xaml'
$script:ClipboardClearSeconds = 30
$script:AppTitle = 'PowerShell Secrets'
$script:Mask = [string]::new([char]0x2022, 10)   # bullet characters (kept out of the source for 5.1 encoding safety)

#region Module loading ---------------------------------------------------------------

try { Import-Module Microsoft.PowerShell.SecretManagement -ErrorAction Stop }
catch {
    $msg = "The Microsoft.PowerShell.SecretManagement module is not installed for this PowerShell ($($PSVersionTable.PSVersion)).`n`n" +
           "Install it with:`n  Install-PSResource Microsoft.PowerShell.SecretManagement, Microsoft.PowerShell.SecretStore -Scope CurrentUser"
    if ($SmokeTest) { throw $msg }
    [void][System.Windows.MessageBox]::Show($msg, $script:AppTitle, 'OK', 'Error')
    exit 1
}
$script:HasSecretStore = [bool](Import-Module Microsoft.PowerShell.SecretStore -PassThru -ErrorAction SilentlyContinue)
Import-Module (Join-Path $PSScriptRoot 'src\SecretsGui.Core.psm1') -Force

#endregion

#region WPF plumbing -----------------------------------------------------------------

# Shared styles live in Application resources so every window picks them up.
if (-not [System.Windows.Application]::Current) {
    $app = [System.Windows.Application]::new()
    $app.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown
}
$themeXaml = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $script:XamlDir 'Theme.xaml')
[System.Windows.Application]::Current.Resources.MergedDictionaries.Clear()
[System.Windows.Application]::Current.Resources.MergedDictionaries.Add([Windows.Markup.XamlReader]::Parse($themeXaml))

function New-SgWindow {
    <# Loads src/Xaml/<Name>.xaml; returns { Window; UI = hashtable of every x:Name'd element }. #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [System.Windows.Window]$Owner
    )
    $xaml = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $script:XamlDir "$Name.xaml")
    $window = [Windows.Markup.XamlReader]::Parse($xaml)
    $ui = @{}
    foreach ($m in [regex]::Matches($xaml, 'x:Name="([^"]+)"')) {
        $n = $m.Groups[1].Value
        $element = $window.FindName($n)
        if ($null -eq $element) { throw "Control '$n' was not found in $Name.xaml." }
        $ui[$n] = $element
    }
    if ($Owner -and $Owner.IsVisible) { $window.Owner = $Owner }
    else { $window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::CenterScreen }
    [pscustomobject]@{ Window = $window; UI = $ui }
}

function Get-OwnerWindow {
    $app = [System.Windows.Application]::Current
    $active = @($app.Windows) | Where-Object { $_.IsActive } | Select-Object -First 1
    if ($active) { return $active }
    if ($script:Main -and $script:Main.Window.IsVisible) { return $script:Main.Window }
    $null
}

function Show-Message {
    param(
        [Parameter(Mandatory)][string]$Text,
        [string]$Title = $script:AppTitle,
        [System.Windows.MessageBoxButton]$Buttons = 'OK',
        [System.Windows.MessageBoxImage]$Icon = 'Information'
    )
    $owner = Get-OwnerWindow
    if ($owner) { [System.Windows.MessageBox]::Show($owner, $Text, $Title, $Buttons, $Icon) }
    else { [System.Windows.MessageBox]::Show($Text, $Title, $Buttons, $Icon) }
}

function Get-ErrorText($ErrorObject) {
    $ex = $ErrorObject
    if ($ex -is [System.Management.Automation.ErrorRecord]) { $ex = $ex.Exception }
    while ($ex -is [System.Management.Automation.MethodInvocationException] -and $ex.InnerException) { $ex = $ex.InnerException }
    if ($ex -is [Exception]) { return $ex.Message }
    [string]$ErrorObject
}

function Test-Cancelled($ErrorObject) {
    $ex = $ErrorObject
    if ($ex -is [System.Management.Automation.ErrorRecord]) { $ex = $ex.Exception }
    while ($ex) {
        if ($ex -is [System.OperationCanceledException]) { return $true }
        $ex = $ex.InnerException
    }
    $false
}

function Set-Status([string]$Text) {
    if ($script:MainUI) { $script:MainUI.StatusText.Text = $Text }
}

function Show-DialogError($UI, $ErrorObject) {
    $UI.ErrorText.Text = Get-ErrorText $ErrorObject
    $UI.ErrorText.Visibility = 'Visible'
}

function Invoke-UiAction {
    <# Runs a GUI action: wait cursor, cancellations become a status message, other errors a message box. #>
    param([Parameter(Mandatory)][scriptblock]$Action)
    $owner = Get-OwnerWindow
    if ($owner) { $owner.Cursor = [System.Windows.Input.Cursors]::Wait }
    try { & $Action }
    catch {
        if (Test-Cancelled $_) { Set-Status 'Cancelled.' }
        else {
            $text = Get-ErrorText $_
            Set-Status "Error: $text"
            [void](Show-Message -Text $text -Title 'Error' -Icon Error)
        }
    }
    finally { if ($owner) { $owner.Cursor = $null } }
}

#endregion

#region Vault access with GUI password prompts ----------------------------------------

$script:PasswordProvider = {
    param($VaultName, $Attempt)
    $message = if ($Attempt -gt 1) { "The password was not accepted. Enter the password for vault '$VaultName'." }
               else { "Vault '$VaultName' is locked. Enter its password to unlock it." }
    $answer = Show-PasswordDialog -Title 'Unlock vault' -Message $message
    if ($answer) { $answer.Password } else { $null }
}

function Invoke-Vault {
    <# Runs a vault operation; if the vault is locked, asks for the password and retries. #>
    param([Parameter(Mandatory)][string]$VaultName, [Parameter(Mandatory)][scriptblock]$Operation)
    Invoke-SgVaultOperation -VaultName $VaultName -Operation $Operation -PasswordProvider $script:PasswordProvider
}

function Invoke-Store {
    <# Same as Invoke-Vault, for SecretStore configuration cmdlets (unlocked with Unlock-SecretStore). #>
    param([Parameter(Mandatory)][scriptblock]$Operation)
    Invoke-SgVaultOperation -VaultName 'SecretStore' -Operation $Operation -PasswordProvider $script:PasswordProvider `
        -UnlockAction { param($Password) Unlock-SgSecretStore -Password $Password }
}

#endregion

#region Clipboard (auto-clearing, excluded from clipboard history) --------------------

$script:ClipboardValue = $null
$script:ClipboardDeadline = [datetime]::MinValue
$script:ClipTimer = [System.Windows.Threading.DispatcherTimer]::new()
$script:ClipTimer.Interval = [TimeSpan]::FromSeconds(1)
$script:ClipTimer.Add_Tick({
    $left = [int][Math]::Ceiling(($script:ClipboardDeadline - (Get-Date)).TotalSeconds)
    if ($left -le 0) { Clear-SecretClipboard; Set-Status 'Clipboard cleared.' }
    elseif ($script:MainUI) { $script:MainUI.ClipboardText.Text = "Clipboard clears in $($left)s" }
})

function New-DwordStream([int]$Value) { [System.IO.MemoryStream]::new([BitConverter]::GetBytes($Value)) }

function Set-SecretClipboard {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text, [string]$What = 'Secret')
    if ($Text -eq '') { throw 'There is nothing to copy.' }
    $data = [System.Windows.DataObject]::new()
    $data.SetText($Text)
    # Ask Windows not to keep this in clipboard history (Win+V) or sync it to the cloud clipboard.
    $data.SetData('ExcludeClipboardContentFromMonitorProcessing', (New-DwordStream 1))
    $data.SetData('CanIncludeInClipboardHistory', (New-DwordStream 0))
    $data.SetData('CanUploadToCloudClipboard', (New-DwordStream 0))
    for ($i = 1; ; $i++) {
        try { [System.Windows.Clipboard]::SetDataObject($data, $true); break }
        catch { if ($i -ge 5) { throw }; Start-Sleep -Milliseconds 60 }   # clipboard can be briefly locked by other apps
    }
    $script:ClipboardValue = $Text
    $script:ClipboardDeadline = (Get-Date).AddSeconds($script:ClipboardClearSeconds)
    $script:ClipTimer.Start()
    if ($script:MainUI) { $script:MainUI.ClipboardText.Text = "Clipboard clears in $($script:ClipboardClearSeconds)s" }
    Set-Status "$What copied to the clipboard."
}

function Clear-SecretClipboard {
    $script:ClipTimer.Stop()
    try {
        # Only clear if the clipboard still holds what we put there.
        if ($script:ClipboardValue -and [System.Windows.Clipboard]::ContainsText() -and
            [System.Windows.Clipboard]::GetText() -ceq $script:ClipboardValue) {
            [System.Windows.Clipboard]::Clear()
        }
    }
    catch { Write-Verbose "Clipboard clear failed: $_" }
    $script:ClipboardValue = $null
    if ($script:MainUI) { $script:MainUI.ClipboardText.Text = '' }
}

#endregion

#region Dialogs ----------------------------------------------------------------------
# Note: dialog event handlers run while ShowDialog() is on the stack inside the dialog
# function, so they can read that function's local variables. Results are passed back
# through a $state hashtable because assigning a plain variable inside a handler would
# only create a handler-local copy.

function Show-PasswordDialog {
    <# Returns @{ Password; Current; Option } (SecureStrings) or $null when cancelled. #>
    param(
        [string]$Title = 'Password',
        [string]$Message = '',
        [string]$PasswordLabel = 'Password',
        [switch]$AskCurrent,
        [switch]$Confirm,
        [string]$OptionText
    )
    $d = New-SgWindow PasswordDialog -Owner (Get-OwnerWindow)
    $ui = $d.UI
    $state = @{ Result = $null }
    $d.Window.Title = $Title
    $ui.MessageText.Text = $Message
    $ui.PasswordLabel.Text = $PasswordLabel
    if ($AskCurrent) { $ui.CurrentPanel.Visibility = 'Visible' }
    if ($Confirm) { $ui.ConfirmPanel.Visibility = 'Visible' }
    if ($OptionText) {
        $ui.OptionText.Text = $OptionText
        $ui.OptionCheck.Visibility = 'Visible'
        $ui.OptionCheck.Add_Click({
            $enabled = -not [bool]$ui.OptionCheck.IsChecked
            $ui.NewPanel.IsEnabled = $enabled
            $ui.ConfirmPanel.IsEnabled = $enabled
        })
    }
    $ui.OkButton.Add_Click({
        $useOption = [bool]$ui.OptionCheck.IsChecked
        if ($AskCurrent -and $ui.CurrentBox.SecurePassword.Length -eq 0) { Show-DialogError $ui 'Enter the current password.'; return }
        if (-not $useOption) {
            if ($ui.PasswordBox1.SecurePassword.Length -eq 0) { Show-DialogError $ui 'Enter a password.'; return }
            if ($Confirm -and $ui.PasswordBox1.Password -cne $ui.PasswordBox2.Password) { Show-DialogError $ui 'The passwords do not match.'; return }
        }
        $state.Result = [pscustomobject]@{
            Password = $ui.PasswordBox1.SecurePassword.Copy()
            Current  = $ui.CurrentBox.SecurePassword.Copy()
            Option   = $useOption
        }
        $d.Window.DialogResult = $true
    })
    $d.Window.Add_ContentRendered({
        if ($AskCurrent) { [void]$ui.CurrentBox.Focus() } else { [void]$ui.PasswordBox1.Focus() }
    })
    [void]$d.Window.ShowDialog()
    $state.Result
}

function Show-InputDialog {
    <# Free-text input, or a pick list when -Choices is given. Returns the value or $null. #>
    param([string]$Title = 'Input', [string]$Message = '', [string]$Text = '', [string[]]$Choices)
    $d = New-SgWindow InputDialog -Owner (Get-OwnerWindow)
    $ui = $d.UI
    $state = @{ Result = $null }
    $d.Window.Title = $Title
    $ui.MessageText.Text = $Message
    if ($Choices) {
        $ui.InputBox.Visibility = 'Collapsed'
        $ui.InputCombo.Visibility = 'Visible'
        $ui.InputCombo.ItemsSource = $Choices
        $ui.InputCombo.SelectedIndex = 0
    }
    else { $ui.InputBox.Text = $Text }
    $ui.OkButton.Add_Click({
        $value = if ($Choices) { [string]$ui.InputCombo.SelectedItem } else { $ui.InputBox.Text.Trim() }
        if (-not $value) { Show-DialogError $ui 'Enter a value.'; return }
        $state.Result = $value
        $d.Window.DialogResult = $true
    })
    $d.Window.Add_ContentRendered({
        if ($Choices) { [void]$ui.InputCombo.Focus() } else { $ui.InputBox.SelectAll(); [void]$ui.InputBox.Focus() }
    })
    [void]$d.Window.ShowDialog()
    $state.Result
}

function Show-SecretEditor {
    <#
        New secret (no -Name) or edit an existing one. The save happens inside the dialog so
        errors (name taken, locked vault, bad input) keep it open. Returns the saved name or $null.
    #>
    param(
        [Parameter(Mandatory)][string]$VaultName,
        [string]$Name,
        [string]$Type,
        [object]$Value,
        [System.Collections.IDictionary]$Metadata
    )
    $isEdit = [bool]$Name
    $d = New-SgWindow SecretEditor -Owner (Get-OwnerWindow)
    $ui = $d.UI
    $state = @{ Saved = $null }
    $d.Window.Title = if ($isEdit) { "Edit secret - $VaultName" } else { "New secret - $VaultName" }

    $types = @('String', 'SecureString', 'PSCredential', 'Hashtable', 'ByteArray')
    $panels = @{ String = $ui.StringBox; SecureString = $ui.SecurePanel; PSCredential = $ui.CredPanel; Hashtable = $ui.HashPanel; ByteArray = $ui.BytesPanel }
    $showPanel = {
        foreach ($k in $panels.Keys) {
            $panels[$k].Visibility = if ($k -eq [string]$ui.TypeCombo.SelectedItem) { 'Visible' } else { 'Collapsed' }
        }
    }
    $ui.TypeCombo.ItemsSource = $types
    $ui.TypeCombo.Add_SelectionChanged({ & $showPanel })

    # Prefill. Get-Secret -AsPlainText returns SecureString secrets as [string], so the
    # stored type comes from SecretInfo ($Type), not from the value.
    if (-not $Type -or $types -notcontains $Type) { $Type = if ($isEdit) { Get-SgSecretTypeName $Value } else { 'SecureString' } }
    if ($isEdit) {
        $ui.NameBox.Text = $Name
        switch ($Type) {
            'String' { $ui.StringBox.Text = ConvertTo-SgDisplayText $Value }
            'SecureString' { $ui.SecureBox.Password = ConvertTo-SgDisplayText $Value }
            'PSCredential' {
                $ui.UserBox.Text = $Value.UserName
                $ui.CredBox.Password = ConvertFrom-SgSecureString $Value.Password
            }
            'Hashtable' { $ui.HashBox.Text = ConvertTo-SgKeyValueText -Dictionary $Value -Reveal }
            'ByteArray' {
                $ui.BytesBox.Text = [Convert]::ToBase64String([byte[]]$Value, [Base64FormattingOptions]::InsertLineBreaks)
                $ui.BytesInfo.Text = "$(([byte[]]$Value).Length) bytes"
            }
        }
        $ui.MetadataBox.Text = ConvertTo-SgKeyValueText -Dictionary $Metadata
    }
    $originalMetadata = $ui.MetadataBox.Text.Trim()
    $ui.TypeCombo.SelectedItem = $Type
    & $showPanel

    # Show / hide plain text for the two password fields.
    $ui.SecureShowCheck.Add_Click({
        if ($ui.SecureShowCheck.IsChecked) {
            $ui.SecureReveal.Text = $ui.SecureBox.Password
            $ui.SecureBox.Visibility = 'Collapsed'; $ui.SecureReveal.Visibility = 'Visible'
        }
        else {
            $ui.SecureBox.Password = $ui.SecureReveal.Text; $ui.SecureReveal.Text = ''
            $ui.SecureReveal.Visibility = 'Collapsed'; $ui.SecureBox.Visibility = 'Visible'
        }
    })
    $ui.CredShowCheck.Add_Click({
        if ($ui.CredShowCheck.IsChecked) {
            $ui.CredReveal.Text = $ui.CredBox.Password
            $ui.CredBox.Visibility = 'Collapsed'; $ui.CredReveal.Visibility = 'Visible'
        }
        else {
            $ui.CredBox.Password = $ui.CredReveal.Text; $ui.CredReveal.Text = ''
            $ui.CredReveal.Visibility = 'Collapsed'; $ui.CredBox.Visibility = 'Visible'
        }
    })
    $ui.SecureGenerateButton.Add_Click({
        $pw = New-SgPassword
        $ui.SecureBox.Password = $pw; if ($ui.SecureShowCheck.IsChecked) { $ui.SecureReveal.Text = $pw }
    })
    $ui.CredGenerateButton.Add_Click({
        $pw = New-SgPassword
        $ui.CredBox.Password = $pw; if ($ui.CredShowCheck.IsChecked) { $ui.CredReveal.Text = $pw }
    })
    $ui.LoadFileButton.Add_Click({
        try {
            $dlg = [Microsoft.Win32.OpenFileDialog]::new()
            $dlg.Title = 'Load file as secret'
            if (-not $dlg.ShowDialog($d.Window)) { return }
            $bytes = [System.IO.File]::ReadAllBytes($dlg.FileName)
            if ($bytes.Length -gt 1MB) { throw "The file is $([Math]::Round($bytes.Length / 1MB, 1)) MB; the limit is 1 MB." }
            $ui.BytesBox.Text = [Convert]::ToBase64String($bytes, [Base64FormattingOptions]::InsertLineBreaks)
            $ui.BytesInfo.Text = "$($bytes.Length) bytes from $([System.IO.Path]::GetFileName($dlg.FileName))"
            if (-not $ui.NameBox.Text) { $ui.NameBox.Text = [System.IO.Path]::GetFileNameWithoutExtension($dlg.FileName) }
        }
        catch { Show-DialogError $ui $_ }
    })

    $ui.SaveButton.Add_Click({
        try {
            $ui.ErrorText.Visibility = 'Collapsed'
            $newName = $ui.NameBox.Text.Trim()
            if (-not $newName) { throw 'Enter a name for the secret.' }
            $t = [string]$ui.TypeCombo.SelectedItem
            $secure = $null; $text = $null
            switch ($t) {
                'String' { $text = $ui.StringBox.Text }
                'SecureString' {
                    $secure = if ($ui.SecureShowCheck.IsChecked) { ConvertTo-SgSecureString $ui.SecureReveal.Text } else { $ui.SecureBox.SecurePassword.Copy() }
                }
                'PSCredential' {
                    $secure = if ($ui.CredShowCheck.IsChecked) { ConvertTo-SgSecureString $ui.CredReveal.Text } else { $ui.CredBox.SecurePassword.Copy() }
                }
                'Hashtable' { $text = $ui.HashBox.Text }
                'ByteArray' { $text = $ui.BytesBox.Text }
            }
            $newValue = ConvertTo-SgSecretValue -Type $t -Text $text -SecureText $secure -UserName $ui.UserBox.Text.Trim()
            $newMetadata = ConvertFrom-SgKeyValueText -Text $ui.MetadataBox.Text
            $metadataCleared = $isEdit -and $originalMetadata -and $newMetadata.Count -eq 0

            Invoke-Vault $VaultName {
                if (-not $isEdit) {
                    Set-SgSecret -Name $newName -Vault $VaultName -Value $newValue -Metadata $newMetadata -NoClobber
                }
                elseif ($newName -ceq $Name) {
                    Set-SgSecret -Name $newName -Vault $VaultName -Value $newValue -Metadata $newMetadata
                    if ($metadataCleared) { Set-SgSecretMetadata -Name $newName -Vault $VaultName -Metadata @{} }
                }
                else {
                    # Renamed while editing: write the new name first (refusing to overwrite), then drop the old one.
                    Set-SgSecret -Name $newName -Vault $VaultName -Value $newValue -Metadata $newMetadata -NoClobber
                    Remove-Secret -Name $Name -Vault $VaultName -ErrorAction Stop
                }
            }
            $state.Saved = $newName
            $d.Window.DialogResult = $true
        }
        catch {
            if (-not (Test-Cancelled $_)) { Show-DialogError $ui $_ }
        }
    })
    $d.Window.Add_ContentRendered({ if ($isEdit) { [void]$ui.TypeCombo.Focus() } else { [void]$ui.NameBox.Focus() } })
    [void]$d.Window.ShowDialog()
    $state.Saved
}

function Show-SecretViewer {
    param([Parameter(Mandatory)]$Secret, [Parameter(Mandatory)][string]$VaultName, [Parameter(Mandatory)][AllowNull()][object]$Value)
    $d = New-SgWindow SecretViewer -Owner (Get-OwnerWindow)
    $ui = $d.UI
    $isCred = $Value -is [pscredential]
    $isBytes = $Value -is [byte[]]
    $display = ConvertTo-SgDisplayText $Value
    $masked = if ($isCred) { "UserName: $($Value.UserName)`r`nPassword: $script:Mask" } else { $script:Mask }

    $d.Window.Title = "$($Secret.Name) - $VaultName"
    $ui.NameText.Text = $Secret.Name
    $ui.InfoText.Text = "$($Secret.Type) in vault '$VaultName'"
    $meta = ConvertTo-SgKeyValueText -Dictionary $Secret.Metadata
    if ($meta) { $ui.MetaText.Text = $meta } else { $ui.MetaText.Visibility = 'Collapsed' }
    $ui.ValueBox.Text = $masked
    if ($isCred) { $ui.CopyUserButton.Visibility = 'Visible'; $ui.CopyButton.Content = 'Copy password' }
    if ($isBytes) { $ui.SaveFileButton.Visibility = 'Visible'; $ui.CopyButton.Content = 'Copy Base64' }

    $ui.RevealCheck.Add_Click({ $ui.ValueBox.Text = if ($ui.RevealCheck.IsChecked) { $display } else { $masked } })
    $ui.CopyButton.Add_Click({ Invoke-UiAction { Set-SecretClipboard -Text (Get-SgCopyText $Value) -What "'$($Secret.Name)'" } })
    $ui.CopyUserButton.Add_Click({ Invoke-UiAction { [System.Windows.Clipboard]::SetText($Value.UserName); Set-Status 'User name copied.' } })
    $ui.SaveFileButton.Add_Click({
        Invoke-UiAction {
            $dlg = [Microsoft.Win32.SaveFileDialog]::new()
            $dlg.FileName = $Secret.Name
            if ($dlg.ShowDialog($d.Window)) {
                [System.IO.File]::WriteAllBytes($dlg.FileName, [byte[]]$Value)
                Set-Status "Saved $(([byte[]]$Value).Length) bytes to $($dlg.FileName)."
            }
        }
    })
    $ui.CloseButton.Add_Click({ $d.Window.Close() })
    [void]$d.Window.ShowDialog()
    $ui.ValueBox.Text = ''
}

function Show-VaultDialog {
    <# Registers a vault. Returns the new vault name or $null. #>
    $d = New-SgWindow VaultDialog -Owner (Get-OwnerWindow)
    $ui = $d.UI
    $state = @{ Name = $null }
    $existing = @($script:Vaults)

    $modules = [System.Collections.Generic.List[string]]::new()
    if ($script:HasSecretStore) { $modules.Add('Microsoft.PowerShell.SecretStore') }
    foreach ($v in $existing) { if (-not $modules.Contains($v.ModuleName)) { $modules.Add($v.ModuleName) } }
    $ui.ModuleCombo.ItemsSource = $modules
    $ui.ModuleCombo.Text = if ($script:HasSecretStore) { 'Microsoft.PowerShell.SecretStore' } else { '' }
    $ui.ModuleHint.Text = 'Microsoft.PowerShell.SecretStore keeps secrets encrypted locally for your account. ' +
                          'Any other installed SecretManagement extension module (e.g. Az.KeyVault, SecretManagement.KeePass) also works; enter its name or path.'
    $ui.NameBox.Text = if (-not ($existing | Where-Object Name -EQ 'SecretStore')) { 'SecretStore' } else { '' }
    $ui.DefaultCheck.IsChecked = -not ($existing | Where-Object IsDefault)

    $ui.OkButton.Add_Click({
        try {
            $ui.ErrorText.Visibility = 'Collapsed'
            $name = $ui.NameBox.Text.Trim()
            $module = $ui.ModuleCombo.Text.Trim()
            if (-not $name) { throw 'Enter a vault name.' }
            if (-not $module) { throw 'Enter the extension module name.' }
            Register-SgVault -Name $name -ModuleName $module -Description $ui.DescriptionBox.Text.Trim() `
                -VaultParametersText $ui.ParametersBox.Text -DefaultVault:([bool]$ui.DefaultCheck.IsChecked) -Confirm:$false
            $state.Name = $name
            $d.Window.DialogResult = $true
        }
        catch { Show-DialogError $ui $_ }
    })
    $d.Window.Add_ContentRendered({ $ui.NameBox.SelectAll(); [void]$ui.NameBox.Focus() })
    [void]$d.Window.ShowDialog()
    $state.Name
}

function Invoke-InitializeStore {
    <# Creates the user's SecretStore (Interaction = None). Returns $true when created. #>
    $answer = Show-PasswordDialog -Title 'Create SecretStore' -Confirm `
        -Message "The SecretStore for your account has not been created yet. Choose a password to protect it; you will be asked for it whenever the store has locked itself again (after the password timeout)." `
        -OptionText "Don't use a password (protected only by your Windows account)"
    if (-not $answer) { Set-Status 'SecretStore was not created.'; return $false }
    if ($answer.Option) { Initialize-SgSecretStore -Authentication None -Confirm:$false }
    else { Initialize-SgSecretStore -Authentication Password -Password $answer.Password -Confirm:$false }
    Set-Status 'SecretStore created.'
    $true
}

function Invoke-ResetStore {
    <# DESTRUCTIVE. Double confirmation, then Reset-SecretStore. Returns $true when reset. #>
    $text = "Resetting the SecretStore permanently deletes ALL secrets stored in it, for every vault that uses Microsoft.PowerShell.SecretStore.`n`nContinue?"
    if ((Show-Message -Text $text -Title 'Reset SecretStore' -Buttons YesNo -Icon Warning) -ne 'Yes') { return $false }
    $typed = Show-InputDialog -Title 'Confirm reset' -Message 'Type RESET (in capitals) to delete every secret in the SecretStore.'
    if ($typed -cne 'RESET') { Set-Status 'Reset cancelled.'; return $false }
    $answer = Show-PasswordDialog -Title 'New SecretStore password' -Confirm -Message 'Choose a password for the new, empty store.' `
        -OptionText "Don't use a password (protected only by your Windows account)"
    if (-not $answer) { Set-Status 'Reset cancelled.'; return $false }
    if ($answer.Option) { Reset-SgSecretStore -Authentication None -Confirm:$false }
    else { Reset-SgSecretStore -Authentication Password -Password $answer.Password -Confirm:$false }
    Set-Status 'SecretStore was reset.'
    $true
}

function Show-StoreSettings {
    if (-not $script:HasSecretStore) {
        [void](Show-Message -Text 'The Microsoft.PowerShell.SecretStore module is not installed.`n`nInstall-PSResource Microsoft.PowerShell.SecretStore -Scope CurrentUser' -Icon Warning)
        return
    }
    if (-not (Test-SgSecretStoreInitialized)) {
        if ((Show-Message -Text 'No SecretStore exists for your account yet. Create it now?' -Buttons YesNo -Icon Question) -eq 'Yes') {
            [void](Invoke-InitializeStore)
        }
        return
    }
    $config = Invoke-Store { Get-SgSecretStoreConfiguration }
    $d = New-SgWindow StoreSettings -Owner (Get-OwnerWindow)
    $ui = $d.UI
    $state = @{ Changed = $false }
    $ui.PathText.Text = Get-SgSecretStorePath
    $ui.AuthCombo.ItemsSource = @('Password', 'None')
    $ui.AuthCombo.SelectedItem = $config.Authentication
    $ui.TimeoutBox.Text = [string]$config.PasswordTimeout
    $ui.InteractionCombo.ItemsSource = @('None', 'Prompt')
    $ui.InteractionCombo.SelectedItem = $config.Interaction
    $ui.ChangePasswordButton.IsEnabled = $config.Authentication -eq 'Password'
    $updateHint = {
        $ui.InteractionHint.Text = if ([string]$ui.InteractionCombo.SelectedItem -eq 'Prompt') {
            'Prompt: SecretStore asks for the password on the console. This GUI cannot answer console prompts; choose None.'
        } else { 'None: SecretStore never prompts; this GUI asks for the password when the store is locked (recommended).' }
    }
    & $updateHint
    $ui.InteractionCombo.Add_SelectionChanged({ & $updateHint })

    $ui.SaveButton.Add_Click({
        try {
            $ui.ErrorText.Visibility = 'Collapsed'
            $auth = [string]$ui.AuthCombo.SelectedItem
            $interaction = [string]$ui.InteractionCombo.SelectedItem
            $timeout = 0
            if (-not [int]::TryParse($ui.TimeoutBox.Text.Trim(), [ref]$timeout) -or $timeout -lt 1) { throw 'The password timeout must be a whole number of seconds (1 or more).' }
            $password = $null
            if ($config.Authentication -eq 'None' -and $auth -eq 'Password') {
                $answer = Show-PasswordDialog -Title 'Set SecretStore password' -Message 'Choose a password for the SecretStore.' -Confirm
                if (-not $answer) { return }
                $password = $answer.Password
            }
            elseif ($config.Authentication -eq 'Password' -and $auth -eq 'None') {
                $answer = Show-PasswordDialog -Title 'Remove SecretStore password' -Message 'Enter the current SecretStore password to turn off password protection.'
                if (-not $answer) { return }
                $password = $answer.Password
            }
            Invoke-Store { Set-SgSecretStoreConfiguration -Authentication $auth -PasswordTimeout $timeout -Interaction $interaction -Password $password }
            $state.Changed = $true
            Set-Status 'SecretStore settings saved.'
            $d.Window.DialogResult = $true
        }
        catch { if (-not (Test-Cancelled $_)) { Show-DialogError $ui $_ } }
    })
    $ui.ChangePasswordButton.Add_Click({
        try {
            $answer = Show-PasswordDialog -Title 'Change SecretStore password' -AskCurrent -Confirm -PasswordLabel 'New password'
            if (-not $answer) { return }
            Set-SgSecretStorePassword -CurrentPassword $answer.Current -NewPassword $answer.Password
            Set-Status 'SecretStore password changed.'
            [void](Show-Message -Text 'The SecretStore password was changed.')
        }
        catch { Show-DialogError $ui $_ }
    })
    $ui.ResetButton.Add_Click({
        try {
            if (Invoke-ResetStore) { $state.Changed = $true; $d.Window.DialogResult = $true }
        }
        catch { Show-DialogError $ui $_ }
    })
    [void]$d.Window.ShowDialog()
    $state.Changed
}

#endregion

#region Main window ------------------------------------------------------------------

$script:Vaults = @()
$script:AllSecrets = @()
$script:LockedVault = $null
$script:SuppressVaultSelection = $false

function Get-SelectedVault { $script:MainUI.VaultList.SelectedItem }
function Get-SelectedSecret { $script:MainUI.SecretGrid.SelectedItem }

function Update-CommandState {
    $ui = $script:MainUI
    $vault = Get-SelectedVault
    $secret = Get-SelectedSecret
    $hasVault = $null -ne $vault
    $hasSecret = $null -ne $secret
    $ui.RemoveVaultButton.IsEnabled = $hasVault
    $ui.DefaultVaultButton.IsEnabled = $hasVault -and -not $vault.IsDefault
    $ui.UnlockButton.IsEnabled = $hasVault
    $ui.StoreSettingsButton.IsEnabled = $script:HasSecretStore
    $ui.AddSecretButton.IsEnabled = $hasVault
    $ui.RefreshButton.IsEnabled = $true
    foreach ($b in 'ViewSecretButton', 'CopySecretButton', 'EditSecretButton', 'RenameSecretButton', 'DeleteSecretButton') {
        $ui[$b].IsEnabled = $hasSecret
    }
    $ui.CopyToVaultButton.IsEnabled = $hasSecret -and @($script:Vaults).Count -gt 1
}

function Update-VaultList {
    param([string]$SelectName)
    $ui = $script:MainUI
    if (-not $SelectName -and $ui.VaultList.SelectedItem) { $SelectName = $ui.VaultList.SelectedItem.Name }
    $script:Vaults = @(Get-SgVault)
    $script:SuppressVaultSelection = $true
    try { $ui.VaultList.ItemsSource = $script:Vaults }
    finally { $script:SuppressVaultSelection = $false }

    $pick = $script:Vaults | Where-Object Name -EQ $SelectName | Select-Object -First 1
    if (-not $pick) { $pick = $script:Vaults | Where-Object IsDefault | Select-Object -First 1 }
    if (-not $pick) { $pick = $script:Vaults | Select-Object -First 1 }
    if ($pick) { $ui.VaultList.SelectedItem = $pick }   # fires SelectionChanged -> Update-SecretList
    else { Update-SecretList }
}

function Update-SecretList {
    param([string]$SelectName)
    $ui = $script:MainUI
    $vault = Get-SelectedVault
    $script:AllSecrets = @()
    $script:LockedVault = $null
    if (-not $vault) {
        $ui.VaultTitle.Text = 'No vault selected'
        $ui.VaultSubtitle.Text = ''
    }
    else {
        $vn = $vault.Name
        $ui.VaultTitle.Text = $vn
        $sub = $vault.ModuleName
        if ($vault.Description) { $sub = "$sub - $($vault.Description)" }
        $ui.VaultSubtitle.Text = $sub
        try {
            $script:AllSecrets = @(Invoke-Vault $vn { Get-SgSecretList -Vault $vn })
            Set-Status "$($script:AllSecrets.Count) secret(s) in '$vn'."
        }
        catch {
            if ((Test-Cancelled $_) -or (Test-SgLockedError $_)) {
                $script:LockedVault = $vn
                Set-Status "Vault '$vn' is locked."
            }
            else { Update-SecretView; throw }
        }
    }
    Update-SecretView -SelectName $SelectName
}

function Update-SecretView {
    param([string]$SelectName)
    $ui = $script:MainUI
    $filter = $ui.SearchBox.Text.Trim()
    $items = $script:AllSecrets
    if ($filter) {
        $items = @($items | Where-Object {
            $_.Name.IndexOf($filter, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            ($_.MetadataText -and $_.MetadataText.IndexOf($filter, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        })
    }
    $ui.SecretGrid.ItemsSource = $items
    if ($SelectName) {
        $match = $items | Where-Object Name -EQ $SelectName | Select-Object -First 1
        if ($match) { $ui.SecretGrid.SelectedItem = $match; $ui.SecretGrid.ScrollIntoView($match) }
    }

    $empty = ''
    if (-not @($script:Vaults).Count) {
        $empty = if ($script:HasSecretStore) { 'No vaults are registered yet.`nClick "Register..." to create a SecretStore vault.' }
                 else { 'No vaults are registered and Microsoft.PowerShell.SecretStore is not installed.`nInstall an extension vault module, then click "Register...".' }
    }
    elseif (-not (Get-SelectedVault)) { $empty = 'Select a vault.' }
    elseif ($script:LockedVault) { $empty = "This vault is locked.`nClick ""Unlock..."" or press F5 to enter its password." }
    elseif (-not $script:AllSecrets.Count) { $empty = 'This vault has no secrets yet.`nClick "New secret" (Ctrl+N) to add one.' }
    elseif (-not @($items).Count) { $empty = "No secrets match '$filter'." }
    $ui.EmptyText.Text = $empty
    $ui.EmptyText.Visibility = if ($empty) { 'Visible' } else { 'Collapsed' }
    Update-CommandState
}

function Invoke-NewSecret {
    $vault = Get-SelectedVault
    if (-not $vault) { return }
    $saved = Show-SecretEditor -VaultName $vault.Name
    if ($saved) { Update-SecretList -SelectName $saved; Set-Status "Saved '$saved'." }
}

function Invoke-EditSecret {
    $vault = Get-SelectedVault; $secret = Get-SelectedSecret
    if (-not $vault -or -not $secret) { return }
    $vn = $vault.Name; $sn = $secret.Name
    $value = Invoke-Vault $vn { Get-SgSecretValue -Name $sn -Vault $vn }
    $saved = Show-SecretEditor -VaultName $vn -Name $sn -Type $secret.Type -Value $value -Metadata $secret.Metadata
    if ($saved) { Update-SecretList -SelectName $saved; Set-Status "Saved '$saved'." }
}

function Invoke-ViewSecret {
    $vault = Get-SelectedVault; $secret = Get-SelectedSecret
    if (-not $vault -or -not $secret) { return }
    $vn = $vault.Name; $sn = $secret.Name
    $value = Invoke-Vault $vn { Get-SgSecretValue -Name $sn -Vault $vn }
    Show-SecretViewer -Secret $secret -VaultName $vn -Value $value
}

function Invoke-CopySecret {
    $vault = Get-SelectedVault; $secret = Get-SelectedSecret
    if (-not $vault -or -not $secret) { return }
    $vn = $vault.Name; $sn = $secret.Name
    $value = Invoke-Vault $vn { Get-SgSecretValue -Name $sn -Vault $vn }
    $what = if ($value -is [pscredential]) { "Password of '$sn'" } else { "'$sn'" }
    Set-SecretClipboard -Text (Get-SgCopyText $value) -What $what
}

function Invoke-RenameSecret {
    $vault = Get-SelectedVault; $secret = Get-SelectedSecret
    if (-not $vault -or -not $secret) { return }
    $vn = $vault.Name; $sn = $secret.Name
    $newName = Show-InputDialog -Title 'Rename secret' -Message "New name for '$sn':" -Text $sn
    if (-not $newName -or $newName -ceq $sn) { return }
    Invoke-Vault $vn { Rename-SgSecret -Name $sn -NewName $newName -Vault $vn }
    Update-SecretList -SelectName $newName
    Set-Status "Renamed '$sn' to '$newName'."
}

function Invoke-CopySecretToVault {
    $vault = Get-SelectedVault; $secret = Get-SelectedSecret
    if (-not $vault -or -not $secret) { return }
    $vn = $vault.Name; $sn = $secret.Name
    $targets = @($script:Vaults | Where-Object Name -NE $vn | ForEach-Object Name)
    if (-not $targets) { return }
    $target = Show-InputDialog -Title 'Copy secret to vault' -Message "Copy '$sn' (value and metadata) to vault:" -Choices $targets
    if (-not $target) { return }
    [void](Invoke-Vault $vn { Get-SgSecretValue -Name $sn -Vault $vn })   # unlock the source first so prompts name the right vault
    Invoke-Vault $target { Copy-SgSecret -Name $sn -SourceVault $vn -TargetVault $target }
    Set-Status "Copied '$sn' to '$target'."
}

function Invoke-DeleteSecret {
    $vault = Get-SelectedVault; $secret = Get-SelectedSecret
    if (-not $vault -or -not $secret) { return }
    $vn = $vault.Name; $sn = $secret.Name
    $answer = Show-Message -Text "Delete secret '$sn' from vault '$vn'?`n`nThis cannot be undone." -Title 'Delete secret' -Buttons YesNo -Icon Warning
    if ($answer -ne 'Yes') { return }
    Invoke-Vault $vn { Remove-SgSecret -Name $sn -Vault $vn -Confirm:$false }
    Update-SecretList
    Set-Status "Deleted '$sn'."
}

function Invoke-RegisterVault {
    $name = Show-VaultDialog
    if (-not $name) { return }
    $vault = Get-SgVault | Where-Object Name -EQ $name | Select-Object -First 1
    if ($vault -and $vault.IsSecretStore -and -not (Test-SgSecretStoreInitialized)) { [void](Invoke-InitializeStore) }
    Update-VaultList -SelectName $name
    Set-Status "Registered vault '$name'."
}

function Invoke-UnregisterVault {
    $vault = Get-SelectedVault
    if (-not $vault) { return }
    $vn = $vault.Name
    $text = "Unregister vault '$vn'?`n`nThe secrets themselves are not deleted; registering the vault again restores access."
    if ((Show-Message -Text $text -Title 'Unregister vault' -Buttons YesNo -Icon Question) -ne 'Yes') { return }
    Unregister-SgVault -Name $vn -Confirm:$false
    Update-VaultList
    Set-Status "Unregistered vault '$vn'."
}

function Invoke-SetDefaultVault {
    $vault = Get-SelectedVault
    if (-not $vault) { return }
    Set-SgDefaultVault -Name $vault.Name
    Update-VaultList -SelectName $vault.Name
    Set-Status "'$($vault.Name)' is now the default vault."
}

function Invoke-UnlockVault {
    $vault = Get-SelectedVault
    if (-not $vault) { return }
    $vn = $vault.Name
    $answer = Show-PasswordDialog -Title 'Unlock vault' -Message "Enter the password for vault '$vn'."
    if (-not $answer) { return }
    if ($vault.IsSecretStore) { Unlock-SgSecretStore -Password $answer.Password }
    else { Unlock-SecretVault -Name $vn -Password $answer.Password -ErrorAction Stop }
    Set-Status "Vault '$vn' unlocked."
    Update-SecretList
}

function Initialize-MainWindow {
    $script:Main = New-SgWindow MainWindow
    $script:MainUI = $script:Main.UI
    $ui = $script:MainUI
    $w = $script:Main.Window

    # Deferred so the selection finishes (and renders) before a possible modal unlock prompt opens.
    $ui.VaultList.Add_SelectionChanged({
        if ($script:SuppressVaultSelection) { return }
        [void]$script:Main.Window.Dispatcher.BeginInvoke([System.Windows.Threading.DispatcherPriority]::Background,
            [Action]{ Invoke-UiAction { Update-SecretList } })
    })
    $ui.SecretGrid.Add_SelectionChanged({ Update-CommandState })
    $ui.SecretGrid.Add_MouseDoubleClick({ if (Get-SelectedSecret) { Invoke-UiAction { Invoke-ViewSecret } } })
    $ui.SearchBox.Add_TextChanged({
        $script:MainUI.SearchHint.Visibility = if ($script:MainUI.SearchBox.Text) { 'Collapsed' } else { 'Visible' }
        Update-SecretView
    })

    $ui.AddVaultButton.Add_Click({ Invoke-UiAction { Invoke-RegisterVault } })
    $ui.RemoveVaultButton.Add_Click({ Invoke-UiAction { Invoke-UnregisterVault } })
    $ui.DefaultVaultButton.Add_Click({ Invoke-UiAction { Invoke-SetDefaultVault } })
    $ui.UnlockButton.Add_Click({ Invoke-UiAction { Invoke-UnlockVault } })
    $ui.StoreSettingsButton.Add_Click({ Invoke-UiAction { if (Show-StoreSettings) { Update-VaultList } } })
    $ui.AddSecretButton.Add_Click({ Invoke-UiAction { Invoke-NewSecret } })
    $ui.ViewSecretButton.Add_Click({ Invoke-UiAction { Invoke-ViewSecret } })
    $ui.CopySecretButton.Add_Click({ Invoke-UiAction { Invoke-CopySecret } })
    $ui.EditSecretButton.Add_Click({ Invoke-UiAction { Invoke-EditSecret } })
    $ui.RenameSecretButton.Add_Click({ Invoke-UiAction { Invoke-RenameSecret } })
    $ui.CopyToVaultButton.Add_Click({ Invoke-UiAction { Invoke-CopySecretToVault } })
    $ui.DeleteSecretButton.Add_Click({ Invoke-UiAction { Invoke-DeleteSecret } })
    $ui.RefreshButton.Add_Click({ Invoke-UiAction { Update-VaultList } })

    $w.Add_PreviewKeyDown({
        param($sender, $e)
        $ui = $script:MainUI
        $ctrl = ([System.Windows.Input.Keyboard]::Modifiers -band [System.Windows.Input.ModifierKeys]::Control) -ne 0
        $inGrid = $ui.SecretGrid.IsKeyboardFocusWithin
        $handled = $true
        switch ($e.Key) {
            'F5' { Invoke-UiAction { Update-VaultList } }
            'F2' { if (Get-SelectedSecret) { Invoke-UiAction { Invoke-EditSecret } } }
            'N' { if ($ctrl) { Invoke-UiAction { Invoke-NewSecret } } else { $handled = $false } }
            'F' { if ($ctrl) { [void]$ui.SearchBox.Focus(); $ui.SearchBox.SelectAll() } else { $handled = $false } }
            'C' { if ($ctrl -and $inGrid -and (Get-SelectedSecret)) { Invoke-UiAction { Invoke-CopySecret } } else { $handled = $false } }
            'Return' { if ($inGrid -and (Get-SelectedSecret)) { Invoke-UiAction { Invoke-ViewSecret } } else { $handled = $false } }
            'Delete' { if ($inGrid -and (Get-SelectedSecret)) { Invoke-UiAction { Invoke-DeleteSecret } } else { $handled = $false } }
            'Escape' { if ($ui.SearchBox.IsKeyboardFocusWithin -and $ui.SearchBox.Text) { $ui.SearchBox.Text = '' } else { $handled = $false } }
            default { $handled = $false }
        }
        if ($handled) { $e.Handled = $true }
    })

    $w.Add_ContentRendered({
        Invoke-UiAction {
            Update-VaultList
            if (-not @($script:Vaults).Count -and $script:HasSecretStore) {
                $q = 'No secret vaults are registered yet.`n`nRegister a local SecretStore vault now?'
                if ((Show-Message -Text $q -Buttons YesNo -Icon Question) -eq 'Yes') { Invoke-RegisterVault }
            }
        }
    })
    $w.Add_Closed({ Clear-SecretClipboard })
    Update-CommandState
}

#endregion

if ($SmokeTest) {
    foreach ($file in Get-ChildItem -LiteralPath $script:XamlDir -Filter *.xaml | Where-Object Name -NE 'Theme.xaml') {
        $w = New-SgWindow $file.BaseName
        $w.Window.WindowStartupLocation = 'Manual'
        $w.Window.Left = -32000; $w.Window.Top = -32000
        $w.Window.ShowActivated = $false; $w.Window.ShowInTaskbar = $false
        $w.Window.Show(); $w.Window.UpdateLayout(); $w.Window.Close()
        "loaded $($file.BaseName) ($($w.UI.Count) named controls)"
    }
    Initialize-MainWindow
    'SMOKE OK'
    exit 0
}

Initialize-MainWindow
[void]$script:Main.Window.ShowDialog()
