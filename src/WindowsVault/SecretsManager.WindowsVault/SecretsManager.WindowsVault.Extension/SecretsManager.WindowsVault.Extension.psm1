<#
    SecretManagement extension vault: secrets encrypted for the current Windows account with DPAPI
    (ConvertFrom-SecureString without a key). Windows only.

    VaultParameters:
      Path (optional) - the vault file (Clixml). Default: %LOCALAPPDATA%\SecretsManager\vaults\<VaultName>.clixml

    File: a hashtable name -> @{ Type; Data; Metadata }. Data is the DPAPI-protected value; names, types and metadata are
    stored in plain text (as in SecretStore, metadata is not encrypted).
#>

function Get-WvPath([string]$VaultName, [hashtable]$p) {
    if ($p -and $p.Path) { return $p.Path }
    Join-Path $env:LOCALAPPDATA "SecretsManager\vaults\$VaultName.clixml"
}

function Read-WvStore([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return @{} }
    $store = Import-Clixml -LiteralPath $path
    if ($store -isnot [hashtable]) { $store = $store.PSObject.BaseObject }
    $store
}

function Write-WvStore([string]$path, [hashtable]$store) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = "$path.tmp"
    $store | Export-Clixml -LiteralPath $tmp -Depth 5
    Move-Item -LiteralPath $tmp -Destination $path -Force   # replace in one step: a reader never sees half a file
}

function Protect-WvText([string]$text) {
    # The "v" prefix keeps empty values encryptable (ConvertTo-SecureString rejects an empty string).
    ConvertTo-SecureString -String ('v' + $text) -AsPlainText -Force | ConvertFrom-SecureString
}

function Unprotect-WvText([string]$data) {
    (ConvertTo-SecureString -String $data | ConvertFrom-SecureString -AsPlainText).Substring(1)
}

function ConvertTo-WvEntry($secret) {
    $value = $secret   # not "$value = if (…) {…}": that unrolls a byte[] into object[]
    if ($value -is [psobject]) { $value = $value.PSObject.BaseObject }
    # if/elseif, not switch: switch would enumerate a byte[] element by element.
    if ($value -is [string]) { return @{ Type = 'String'; Text = $value } }
    elseif ($value -is [securestring]) { return @{ Type = 'SecureString'; Text = (ConvertFrom-SecureString $value -AsPlainText) } }
    elseif ($value -is [pscredential]) {
        return @{ Type = 'PSCredential'; Text = (@{ UserName = $value.UserName; Password = $value.GetNetworkCredential().Password } | ConvertTo-Json -Compress) }
    }
    elseif ($value -is [byte[]]) { return @{ Type = 'ByteArray'; Text = [Convert]::ToBase64String($value) } }
    elseif ($value -is [hashtable]) {
        $plain = @{}
        foreach ($k in $value.Keys) {
            $v = $value[$k]
            $plain[$k] = if ($v -is [securestring]) { ConvertFrom-SecureString $v -AsPlainText } else { [string]$v }
        }
        return @{ Type = 'Hashtable'; Text = ($plain | ConvertTo-Json -Compress) }
    }
    throw "Unsupported secret type: $($value.GetType().FullName)"
}

function ConvertFrom-WvEntry([hashtable]$entry) {
    $text = Unprotect-WvText $entry.Data
    switch ($entry.Type) {
        'String'       { return $text }
        'SecureString' { return ConvertTo-SecureString -String $text -AsPlainText -Force }
        'PSCredential' {
            $c = $text | ConvertFrom-Json
            return [pscredential]::new($c.UserName, (ConvertTo-SecureString -String ([string]$c.Password) -AsPlainText -Force))
        }
        'ByteArray'    { return ,[Convert]::FromBase64String($text) }
        'Hashtable'    {
            $h = @{}   # a plain Hashtable: ConvertFrom-Json -AsHashtable gives an OrderedHashtable in PowerShell 7.3+
            $parsed = $text | ConvertFrom-Json -AsHashtable
            foreach ($k in $parsed.Keys) { $h[$k] = [string]$parsed[$k] }
            return $h
        }
    }
    throw "Unknown stored type '$($entry.Type)'."
}

function Get-Secret {
    param([string]$Name, [string]$VaultName, [hashtable]$AdditionalParameters)
    $store = Read-WvStore (Get-WvPath $VaultName $AdditionalParameters)
    if ($store.ContainsKey($Name)) {
        $value = ConvertFrom-WvEntry $store[$Name]
        $value = $value.PSObject.BaseObject   # SecretManagement rejects a PSObject wrapper
        Write-Output -NoEnumerate $value
    }
}

function Get-SecretInfo {
    param([string]$Filter, [string]$VaultName, [hashtable]$AdditionalParameters)
    $store = Read-WvStore (Get-WvPath $VaultName $AdditionalParameters)
    if (-not $Filter) { $Filter = '*' }
    $pattern = [System.Management.Automation.WildcardPattern]::new($Filter, 'IgnoreCase')
    foreach ($key in @($store.Keys | Sort-Object)) {
        if (-not $pattern.IsMatch($key)) { continue }
        $md = $store[$key].Metadata
        $dict = [System.Collections.Generic.Dictionary[string, object]]::new()
        if ($md) { foreach ($k in $md.Keys) { $dict[$k] = $md[$k] } }
        [Microsoft.PowerShell.SecretManagement.SecretInformation]::new(
            $key, [Microsoft.PowerShell.SecretManagement.SecretType]$store[$key].Type, $VaultName,
            [System.Collections.ObjectModel.ReadOnlyDictionary[string, object]]::new($dict))
    }
}

function Set-Secret {
    param([string]$Name, [object]$Secret, [string]$VaultName, [hashtable]$AdditionalParameters, [hashtable]$Metadata)
    $path = Get-WvPath $VaultName $AdditionalParameters
    $store = Read-WvStore $path
    $plain = ConvertTo-WvEntry $Secret
    if ($null -eq $Metadata -and $store.ContainsKey($Name)) { $Metadata = $store[$Name].Metadata }
    $store[$Name] = @{ Type = $plain.Type; Data = (Protect-WvText $plain.Text); Metadata = $Metadata }
    Write-WvStore $path $store
    $true
}

function Set-SecretInfo {
    param([string]$Name, [hashtable]$Metadata, [string]$VaultName, [hashtable]$AdditionalParameters)
    $path = Get-WvPath $VaultName $AdditionalParameters
    $store = Read-WvStore $path
    if (-not $store.ContainsKey($Name)) { throw "Secret '$Name' not found in vault '$VaultName'." }
    $store[$Name].Metadata = $Metadata
    Write-WvStore $path $store
}

function Remove-Secret {
    param([string]$Name, [string]$VaultName, [hashtable]$AdditionalParameters)
    $path = Get-WvPath $VaultName $AdditionalParameters
    $store = Read-WvStore $path
    if (-not $store.ContainsKey($Name)) { throw "Secret '$Name' not found in vault '$VaultName'." }
    $store.Remove($Name)
    Write-WvStore $path $store
    $true
}

function Test-SecretVault {
    param([string]$VaultName, [hashtable]$AdditionalParameters)
    $IsWindows
}
