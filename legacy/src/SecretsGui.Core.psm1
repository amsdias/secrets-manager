#Requires -Version 5.1
<#
    SecretsGui.Core - UI-independent logic for the SecretManagement / SecretStore GUI.

    Everything here is plain PowerShell (no WPF) so it can be unit tested with Pester.
    All cmdlet calls use -ErrorAction Stop so failures surface as terminating errors
    that the GUI layer can catch and display.
#>

Set-StrictMode -Version Latest

$script:SecretStoreModuleName = 'Microsoft.PowerShell.SecretStore'
$script:SecretTypes = @('String', 'SecureString', 'PSCredential', 'Hashtable', 'ByteArray')

# Messages the SecretStore / SecretManagement emit when a vault needs a password, or when
# a cmdlet tried to prompt on a non-interactive host. Used to decide when to show the unlock dialog.
$script:LockedErrorPatterns = @(
    'A valid password is required'
    'A password is required for'
    'Use the Unlock-SecretStore cmdlet'
    'Unlock-SecretVault'
    'NonInteractive mode'
    'vault is locked'
)

#region Conversions -----------------------------------------------------------------

function ConvertFrom-SgSecureString {
    <# Returns the plain text of a SecureString (empty string for $null). #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(ValueFromPipeline)][AllowNull()][securestring]$SecureString)
    process {
        if ($null -eq $SecureString) { return '' }
        [System.Net.NetworkCredential]::new('', $SecureString).Password
    }
}

function ConvertTo-SgSecureString {
    <# Plain text -> read-only SecureString. #>
    [CmdletBinding()]
    [OutputType([securestring])]
    param([Parameter(ValueFromPipeline)][AllowEmptyString()][AllowNull()][string]$Text)
    process {
        $ss = [securestring]::new()
        if ($Text) { foreach ($ch in $Text.ToCharArray()) { $ss.AppendChar($ch) } }
        $ss.MakeReadOnly()
        $ss
    }
}

function ConvertFrom-SgKeyValueText {
    <#
        Parses "key=value" lines into an ordered hashtable.
        Blank lines and lines starting with '#' are ignored; only the first '=' splits.
        Used for metadata, hashtable secrets and vault parameters.
    #>
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param([AllowEmptyString()][AllowNull()][string]$Text)

    $result = [ordered]@{}
    if ([string]::IsNullOrWhiteSpace($Text)) { return $result }

    $lineNo = 0
    foreach ($line in ($Text -split "\r?\n")) {
        $lineNo++
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
        $idx = $trimmed.IndexOf('=')
        if ($idx -lt 1) { throw "Line $lineNo is not in 'key=value' form: '$trimmed'" }
        $key = $trimmed.Substring(0, $idx).Trim()
        $value = $trimmed.Substring($idx + 1).Trim()
        if ($result.Contains($key)) { throw "Duplicate key '$key' on line $lineNo." }
        $result[$key] = $value
    }
    $result
}

function ConvertTo-SgKeyValueText {
    <# Inverse of ConvertFrom-SgKeyValueText. SecureString values are revealed only with -Reveal. #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowNull()][System.Collections.IDictionary]$Dictionary,
        [switch]$Reveal
    )
    if ($null -eq $Dictionary -or $Dictionary.Count -eq 0) { return '' }
    $lines = foreach ($key in $Dictionary.Keys) {
        $value = $Dictionary[$key]
        if ($value -is [securestring]) {
            $value = if ($Reveal) { ConvertFrom-SgSecureString $value } else { '********' }
        }
        elseif ($value -is [datetime]) { $value = $value.ToString('o') }
        '{0}={1}' -f $key, $value
    }
    $lines -join [Environment]::NewLine
}

function ConvertTo-SgSecretValue {
    <#
        Builds the object to hand to Set-Secret from editor input.
          String       -> [string]          (Text)
          SecureString -> [securestring]    (SecureText, or Text)
          PSCredential -> [pscredential]    (UserName + SecureText)
          Hashtable    -> [hashtable]       (Text as key=value lines)
          ByteArray    -> [byte[]]          (Text as Base64)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('String', 'SecureString', 'PSCredential', 'Hashtable', 'ByteArray')]
        [string]$Type,
        [AllowEmptyString()][AllowNull()][string]$Text,
        [AllowNull()][securestring]$SecureText,
        [AllowEmptyString()][AllowNull()][string]$UserName
    )

    switch ($Type) {
        'String' {
            if ([string]::IsNullOrEmpty($Text)) { throw 'A String secret cannot be empty.' }
            return $Text
        }
        'SecureString' {
            if ($null -eq $SecureText -and -not [string]::IsNullOrEmpty($Text)) { $SecureText = ConvertTo-SgSecureString $Text }
            if ($null -eq $SecureText -or $SecureText.Length -eq 0) { throw 'A SecureString secret cannot be empty.' }
            return $SecureText
        }
        'PSCredential' {
            if ([string]::IsNullOrWhiteSpace($UserName)) { throw 'A PSCredential secret needs a user name.' }
            if ($null -eq $SecureText) { $SecureText = ConvertTo-SgSecureString '' }
            return [pscredential]::new($UserName, $SecureText)
        }
        'Hashtable' {
            $ordered = ConvertFrom-SgKeyValueText -Text $Text
            if ($ordered.Count -eq 0) { throw 'A Hashtable secret needs at least one key=value line.' }
            $ht = @{}
            foreach ($k in $ordered.Keys) { $ht[$k] = $ordered[$k] }
            return $ht
        }
        'ByteArray' {
            if ([string]::IsNullOrWhiteSpace($Text)) { throw 'A ByteArray secret needs Base64 content.' }
            try { $bytes = [Convert]::FromBase64String(($Text -replace '\s', '')) }
            catch { throw 'ByteArray content is not valid Base64.' }
            # Comma keeps PowerShell from unrolling the array into individual bytes.
            return , $bytes
        }
    }
}

function Get-SgSecretTypeName {
    <# Maps a secret value (as returned by Get-Secret -AsPlainText) to one of $script:SecretTypes. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([AllowNull()][object]$Value)
    if ($Value -is [pscredential]) { return 'PSCredential' }
    if ($Value -is [securestring]) { return 'SecureString' }
    if ($Value -is [byte[]]) { return 'ByteArray' }
    if ($Value -is [System.Collections.IDictionary]) { return 'Hashtable' }
    return 'String'
}

function ConvertTo-SgDisplayText {
    <# Human-readable rendering of a secret value for the viewer. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) { return '' }
    if ($Value -is [pscredential]) {
        return "UserName: {0}{1}Password: {2}" -f $Value.UserName, [Environment]::NewLine, (ConvertFrom-SgSecureString $Value.Password)
    }
    if ($Value -is [securestring]) { return ConvertFrom-SgSecureString $Value }
    if ($Value -is [byte[]]) { return [Convert]::ToBase64String($Value) }
    if ($Value -is [System.Collections.IDictionary]) {
        $lines = foreach ($k in $Value.Keys) { '{0}={1}' -f $k, (ConvertTo-SgDisplayText $Value[$k]) }
        return ($lines -join [Environment]::NewLine)
    }
    return [string]$Value
}

function Get-SgCopyText {
    <# The text that "Copy" puts on the clipboard: the password for credentials, the value otherwise. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([AllowNull()][object]$Value)
    if ($Value -is [pscredential]) { return ConvertFrom-SgSecureString $Value.Password }
    ConvertTo-SgDisplayText $Value
}

function New-SgPassword {
    <#
        Cryptographically random password. Ambiguous characters (0/O, 1/l/I) are left out.
        Uses rejection sampling so every character is equally likely (works on 5.1 and 7).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [ValidateRange(8, 256)][int]$Length = 24,
        [switch]$NoSymbols
    )
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    if (-not $NoSymbols) { $alphabet += '!#$%&*+-=?@^_~' }
    $limit = 256 - (256 % $alphabet.Length)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $sb = [System.Text.StringBuilder]::new($Length)
        $buffer = [byte[]]::new(1)
        while ($sb.Length -lt $Length) {
            $rng.GetBytes($buffer)
            if ($buffer[0] -lt $limit) { [void]$sb.Append($alphabet[$buffer[0] % $alphabet.Length]) }
        }
        $sb.ToString()
    }
    finally { $rng.Dispose() }
}

#endregion

#region Locked-vault handling ---------------------------------------------------------

function Test-SgLockedError {
    <# True when an error record/exception indicates the vault needs a password. #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)][AllowNull()][object]$ErrorObject)

    $messages = [System.Collections.Generic.List[string]]::new()
    $current = $ErrorObject
    if ($current -is [System.Management.Automation.ErrorRecord]) {
        if ($current.ErrorDetails -and $current.ErrorDetails.Message) { $messages.Add($current.ErrorDetails.Message) }
        $current = $current.Exception
    }
    while ($current -is [Exception]) {
        $messages.Add($current.Message)
        $current = $current.InnerException
    }
    if ($ErrorObject -is [string]) { $messages.Add($ErrorObject) }

    foreach ($m in $messages) {
        foreach ($p in $script:LockedErrorPatterns) {
            if ($m -and $m.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
        }
    }
    return $false
}

function Invoke-SgVaultOperation {
    <#
        Runs $Operation. If it fails because the vault is locked, asks $PasswordProvider for a
        password (scriptblock: param($VaultName, $Attempt) -> [securestring] or $null to cancel),
        unlocks via $UnlockAction (scriptblock: param([securestring]$Password)) and retries.
        A wrong password simply leads to another prompt, up to $MaxAttempts prompts.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][scriptblock]$Operation,
        [string]$VaultName,
        [scriptblock]$PasswordProvider,
        [scriptblock]$UnlockAction,
        [ValidateRange(1, 10)][int]$MaxAttempts = 3
    )

    if (-not $UnlockAction) {
        $name = $VaultName
        $UnlockAction = { param([securestring]$Password) Unlock-SecretVault -Name $name -Password $Password -ErrorAction Stop }.GetNewClosure()
    }

    for ($attempt = 1; ; $attempt++) {
        try {
            $result = & $Operation
            # Emit nothing for no output, so @(Invoke-SgVaultOperation ...) is empty rather than @($null).
            # A byte[] is a single secret value and must not be unrolled into bytes; lists are streamed.
            if ($null -eq $result) { return }
            if ($result -is [byte[]]) { Write-Output -NoEnumerate $result } else { $result }
            return
        }
        catch {
            $failure = $_
            if (-not $PasswordProvider -or $attempt -gt $MaxAttempts -or -not (Test-SgLockedError $failure)) { throw $failure }

            $password = & $PasswordProvider $VaultName $attempt
            if ($null -eq $password) {
                throw [System.OperationCanceledException]::new("Unlock of vault '$VaultName' was cancelled.")
            }
            # A wrong password is not fatal: the retried operation fails as locked again and re-prompts.
            try { & $UnlockAction $password } catch { Write-Verbose "Unlock failed: $_" }
        }
    }
}

#endregion

#region Vaults ------------------------------------------------------------------------

function Test-SgIsSecretStoreVault {
    [CmdletBinding()]
    [OutputType([bool])]
    param([AllowNull()][object]$Vault)
    if ($null -eq $Vault) { return $false }
    $module = [string]$Vault.ModuleName
    return ($module -eq $script:SecretStoreModuleName) -or ($module -like "*\$script:SecretStoreModuleName*") -or ($module -like "*/$script:SecretStoreModuleName*")
}

function Get-SgVault {
    <# Registered vaults as flat objects suited for data binding. #>
    [CmdletBinding()]
    param()
    foreach ($v in @(Get-SecretVault -ErrorAction Stop)) {
        [pscustomobject]@{
            Name          = $v.Name
            ModuleName    = $v.ModuleName
            IsDefault     = [bool]$v.IsDefault
            Description   = $v.Description
            IsSecretStore = Test-SgIsSecretStoreVault $v
            Display       = if ($v.IsDefault) { "$($v.Name)  (default)" } else { $v.Name }
            Parameters    = ConvertTo-SgKeyValueText -Dictionary $v.VaultParameters
        }
    }
}

function Register-SgVault {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Name,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ModuleName,
        [string]$Description,
        [string]$VaultParametersText,
        [switch]$DefaultVault
    )
    $params = @{ Name = $Name; ModuleName = $ModuleName; ErrorAction = 'Stop' }
    if ($Description) { $params.Description = $Description }
    $vp = ConvertFrom-SgKeyValueText -Text $VaultParametersText
    if ($vp.Count -gt 0) {
        $ht = @{}
        foreach ($k in $vp.Keys) { $ht[$k] = $vp[$k] }
        $params.VaultParameters = $ht
    }
    if ($DefaultVault) { $params.DefaultVault = $true }
    if ($PSCmdlet.ShouldProcess($Name, 'Register secret vault')) { Register-SecretVault @params }
}

function Unregister-SgVault {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$Name)
    if ($PSCmdlet.ShouldProcess($Name, 'Unregister secret vault (secrets are NOT deleted)')) {
        Unregister-SecretVault -Name $Name -ErrorAction Stop
    }
}

function Set-SgDefaultVault {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Set-SecretVaultDefault -Name $Name -ErrorAction Stop
}

#endregion

#region Secrets -----------------------------------------------------------------------

function Get-SgSecretList {
    <# Secret infos for one vault, flattened for data binding. Never returns secret values. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Vault,
        [string]$Filter = '*'
    )
    if ([string]::IsNullOrWhiteSpace($Filter)) { $Filter = '*' }
    elseif ($Filter -notmatch '[\*\?]') { $Filter = "*$Filter*" }

    $items = foreach ($info in @(Get-SecretInfo -Vault $Vault -Name $Filter -ErrorAction Stop)) {
        $metadata = $null
        if ($info.PSObject.Properties['Metadata']) { $metadata = $info.Metadata }
        [pscustomobject]@{
            Name         = $info.Name
            Type         = [string]$info.Type
            VaultName    = $info.VaultName
            Metadata     = $metadata
            MetadataText = (ConvertTo-SgKeyValueText -Dictionary $metadata) -replace '\r?\n', '; '
        }
    }
    $items | Sort-Object Name
}

function Get-SgSecretValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Vault
    )
    $value = Get-Secret -Name $Name -Vault $Vault -AsPlainText -ErrorAction Stop
    Write-Output -NoEnumerate $value
}

function Set-SgSecret {
    <# Creates or replaces a secret. Metadata is only sent when non-empty (not every vault supports it). #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Name,
        [Parameter(Mandatory)][string]$Vault,
        [Parameter(Mandatory)][AllowNull()][object]$Value,
        [System.Collections.IDictionary]$Metadata,
        [switch]$NoClobber
    )
    if ($null -eq $Value) { throw 'Secret value cannot be null.' }
    $params = @{ Name = $Name; Vault = $Vault; ErrorAction = 'Stop' }
    if ($Value -is [securestring]) { $params.SecureStringSecret = $Value } else { $params.Secret = $Value }
    if ($Metadata -and $Metadata.Count -gt 0) {
        $ht = @{}
        foreach ($k in $Metadata.Keys) { $ht[$k] = $Metadata[$k] }
        $params.Metadata = $ht
    }
    if ($NoClobber) { $params.NoClobber = $true }
    Set-Secret @params
}

function Set-SgSecretMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Vault,
        [Parameter(Mandatory)][AllowNull()][System.Collections.IDictionary]$Metadata
    )
    $ht = @{}
    if ($Metadata) { foreach ($k in $Metadata.Keys) { $ht[$k] = $Metadata[$k] } }
    Set-SecretInfo -Name $Name -Vault $Vault -Metadata $ht -ErrorAction Stop
}

function Rename-SgSecret {
    <# SecretManagement has no rename: copy to the new name (refusing to overwrite), then remove the old one. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$NewName,
        [Parameter(Mandatory)][string]$Vault
    )
    if ($Name -ceq $NewName) { return }
    Copy-SgSecret -Name $Name -SourceVault $Vault -TargetVault $Vault -NewName $NewName
    Remove-Secret -Name $Name -Vault $Vault -ErrorAction Stop
}

function Copy-SgSecret {
    <# Copies a secret (value + metadata when available) to another vault and/or name. Never overwrites. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$SourceVault,
        [Parameter(Mandatory)][string]$TargetVault,
        [string]$NewName
    )
    if (-not $NewName) { $NewName = $Name }
    $value = Get-Secret -Name $Name -Vault $SourceVault -ErrorAction Stop
    $info = Get-SecretInfo -Name $Name -Vault $SourceVault -ErrorAction Stop | Where-Object Name -EQ $Name | Select-Object -First 1
    $metadata = $null
    if ($info -and $info.PSObject.Properties['Metadata']) { $metadata = $info.Metadata }
    Set-SgSecret -Name $NewName -Vault $TargetVault -Value $value -Metadata $metadata -NoClobber
}

function Remove-SgSecret {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Vault
    )
    if ($PSCmdlet.ShouldProcess("$Vault/$Name", 'Remove secret')) {
        Remove-Secret -Name $Name -Vault $Vault -ErrorAction Stop
    }
}

#endregion

#region SecretStore -------------------------------------------------------------------

function Get-SgSecretStorePath {
    <# Folder where Microsoft.PowerShell.SecretStore keeps its files for the current user. #>
    [CmdletBinding()]
    [OutputType([string])]
    param()
    if ($IsLinux -or $IsMacOS) {
        return (Join-Path $HOME '.secretmanagement/localstore')
    }
    Join-Path $env:LOCALAPPDATA 'Microsoft\PowerShell\secretmanagement\localstore'
}

function Test-SgSecretStoreInitialized {
    <# True when a SecretStore data file already exists (i.e. resetting would destroy data). #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([string]$StorePath = (Get-SgSecretStorePath))
    Test-Path -LiteralPath (Join-Path $StorePath 'storefile')
}

function Initialize-SgSecretStore {
    <#
        Creates a brand-new SecretStore non-interactively (Interaction = None so the GUI,
        not the console, handles password prompts). Refuses to run over existing data.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [ValidateSet('Password', 'None')][string]$Authentication = 'Password',
        [securestring]$Password,
        [ValidateRange(1, 2147483)][int]$PasswordTimeout = 900
    )
    if (Test-SgSecretStoreInitialized) {
        throw 'A SecretStore already exists for this user. Use Reset only if you intend to delete all its secrets.'
    }
    if ($Authentication -eq 'Password' -and ($null -eq $Password -or $Password.Length -eq 0)) {
        throw 'A password is required when Authentication is Password.'
    }
    $params = @{
        Scope           = 'CurrentUser'
        Authentication  = $Authentication
        PasswordTimeout = $PasswordTimeout
        Interaction     = 'None'
        Force           = $true
        ErrorAction     = 'Stop'
    }
    if ($Authentication -eq 'Password') { $params.Password = $Password }
    if ($PSCmdlet.ShouldProcess('SecretStore (CurrentUser)', 'Initialize')) { Reset-SecretStore @params }
}

function Reset-SgSecretStore {
    <# DESTRUCTIVE: deletes every secret in the SecretStore and re-creates it. #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [ValidateSet('Password', 'None')][string]$Authentication = 'Password',
        [securestring]$Password,
        [ValidateRange(1, 2147483)][int]$PasswordTimeout = 900
    )
    if ($Authentication -eq 'Password' -and ($null -eq $Password -or $Password.Length -eq 0)) {
        throw 'A password is required when Authentication is Password.'
    }
    $params = @{
        Scope = 'CurrentUser'; Authentication = $Authentication; PasswordTimeout = $PasswordTimeout
        Interaction = 'None'; Force = $true; ErrorAction = 'Stop'
    }
    if ($Authentication -eq 'Password') { $params.Password = $Password }
    if ($PSCmdlet.ShouldProcess('SecretStore (CurrentUser)', 'Reset - delete ALL secrets')) { Reset-SecretStore @params }
}

function Get-SgSecretStoreConfiguration {
    [CmdletBinding()]
    param()
    $c = Get-SecretStoreConfiguration -ErrorAction Stop
    [pscustomobject]@{
        Scope           = [string]$c.Scope
        Authentication  = [string]$c.Authentication
        PasswordTimeout = [int]$c.PasswordTimeout
        Interaction     = [string]$c.Interaction
    }
}

function Set-SgSecretStoreConfiguration {
    <#
        Applies Authentication / PasswordTimeout / Interaction.
        Switching None -> Password needs -Password (the new password).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Password', 'None')][string]$Authentication,
        [Parameter(Mandatory)][ValidateRange(1, 2147483)][int]$PasswordTimeout,
        [Parameter(Mandatory)][ValidateSet('Prompt', 'None')][string]$Interaction,
        [securestring]$Password
    )
    $params = @{
        Scope = 'CurrentUser'; Authentication = $Authentication; PasswordTimeout = $PasswordTimeout
        Interaction = $Interaction; Confirm = $false; ErrorAction = 'Stop'
    }
    if ($Password) { $params.Password = $Password }
    Set-SecretStoreConfiguration @params
}

function Set-SgSecretStorePassword {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][securestring]$CurrentPassword,
        [Parameter(Mandatory)][securestring]$NewPassword
    )
    if ($NewPassword.Length -eq 0) { throw 'The new password cannot be empty.' }
    Set-SecretStorePassword -Password $CurrentPassword -NewPassword $NewPassword -ErrorAction Stop
}

function Unlock-SgSecretStore {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][securestring]$Password,
        [int]$PasswordTimeout
    )
    $params = @{ Password = $Password; ErrorAction = 'Stop' }
    if ($PasswordTimeout -gt 0) { $params.PasswordTimeout = $PasswordTimeout }
    Unlock-SecretStore @params
}

#endregion

Export-ModuleMember -Function *-Sg*
