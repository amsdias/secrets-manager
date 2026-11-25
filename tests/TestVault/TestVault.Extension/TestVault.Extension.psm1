<#
    Test-only SecretManagement extension vault.

    VaultParameters:
      Path     (required) - Clixml file holding the secrets.
      Password (optional) - if set, the vault starts "locked" and Unlock-SecretVault must be
                            called with this password; unlock state is a marker file next to Path.
#>

function Get-TvStore([hashtable]$p) {
    if (Test-Path -LiteralPath $p.Path) { return Import-Clixml -LiteralPath $p.Path }
    @{}
}
function Save-TvStore([hashtable]$p, [hashtable]$store) { $store | Export-Clixml -LiteralPath $p.Path }
function Assert-TvUnlocked([hashtable]$p) {
    if ($p.Password -and -not (Test-Path -LiteralPath "$($p.Path).unlocked")) {
        throw 'A valid password is required to access the TestVault vault. Use Unlock-SecretVault.'
    }
}
function Get-TvType($value) {
    $t = [Microsoft.PowerShell.SecretManagement.SecretType]
    if ($value -is [pscredential]) { return $t::PSCredential }
    if ($value -is [securestring]) { return $t::SecureString }
    if ($value -is [byte[]]) { return $t::ByteArray }
    if ($value -is [hashtable]) { return $t::Hashtable }
    if ($value -is [string]) { return $t::String }
    $t::Unknown
}

function Get-Secret {
    param([string]$Name, [string]$VaultName, [hashtable]$AdditionalParameters)
    Assert-TvUnlocked $AdditionalParameters
    $store = Get-TvStore $AdditionalParameters
    if ($store.ContainsKey($Name)) {
        # Import-Clixml wraps some values (e.g. hashtables) in PSObject; SecretManagement wants the raw object.
        $value = $store[$Name].Value
        if ($null -ne $value) { $value = $value.PSObject.BaseObject }
        Write-Output -NoEnumerate $value
    }
}

function Get-SecretInfo {
    param([string]$Filter, [string]$VaultName, [hashtable]$AdditionalParameters)
    Assert-TvUnlocked $AdditionalParameters
    $store = Get-TvStore $AdditionalParameters
    if (-not $Filter) { $Filter = '*' }
    $pattern = [System.Management.Automation.WildcardPattern]::new($Filter, 'IgnoreCase')
    foreach ($key in $store.Keys) {
        if (-not $pattern.IsMatch($key)) { continue }
        $md = $store[$key].Metadata
        if ($null -ne $md) { $md = $md.PSObject.BaseObject }
        if ($null -eq $md) { $md = @{} }
        $dict = [System.Collections.Generic.Dictionary[string, object]]::new()
        foreach ($k in $md.Keys) { $dict[$k] = $md[$k] }
        [Microsoft.PowerShell.SecretManagement.SecretInformation]::new(
            $key, (Get-TvType $store[$key].Value.PSObject.BaseObject), $VaultName,
            [System.Collections.ObjectModel.ReadOnlyDictionary[string, object]]::new($dict))
    }
}

function Set-Secret {
    param([string]$Name, [object]$Secret, [string]$VaultName, [hashtable]$AdditionalParameters, [hashtable]$Metadata)
    Assert-TvUnlocked $AdditionalParameters
    $store = Get-TvStore $AdditionalParameters
    $existing = $null
    if ($store.ContainsKey($Name)) { $existing = $store[$Name].Metadata }
    if ($null -eq $Metadata) { $Metadata = $existing }
    $store[$Name] = @{ Value = $Secret; Metadata = $Metadata }
    Save-TvStore $AdditionalParameters $store
    $true
}

function Set-SecretInfo {
    param([string]$Name, [hashtable]$Metadata, [string]$VaultName, [hashtable]$AdditionalParameters)
    Assert-TvUnlocked $AdditionalParameters
    $store = Get-TvStore $AdditionalParameters
    if (-not $store.ContainsKey($Name)) { throw "Secret '$Name' not found." }
    $store[$Name].Metadata = $Metadata
    Save-TvStore $AdditionalParameters $store
}

function Remove-Secret {
    param([string]$Name, [string]$VaultName, [hashtable]$AdditionalParameters)
    Assert-TvUnlocked $AdditionalParameters
    $store = Get-TvStore $AdditionalParameters
    if (-not $store.ContainsKey($Name)) { throw "Secret '$Name' not found." }
    $store.Remove($Name)
    Save-TvStore $AdditionalParameters $store
    $true
}

function Test-SecretVault {
    param([string]$VaultName, [hashtable]$AdditionalParameters)
    [bool]$AdditionalParameters.Path
}

function Unlock-SecretVault {
    param([securestring]$Password, [string]$VaultName, [hashtable]$AdditionalParameters)
    $plain = [System.Net.NetworkCredential]::new('', $Password).Password
    if ($plain -ne $AdditionalParameters.Password) { throw 'The provided password is incorrect for the TestVault vault.' }
    Set-Content -LiteralPath "$($AdditionalParameters.Path).unlocked" -Value 'ok'
}
