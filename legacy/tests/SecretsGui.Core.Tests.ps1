#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }
<#
    Run:  pwsh -NoProfile -Command "Invoke-Pester ./tests -Output Detailed"

    Integration tests register temporary vaults backed by tests/TestVault (a file-based
    extension vault) and unregister them afterwards. The real SecretStore is never touched.
#>

BeforeAll {
    Import-Module Microsoft.PowerShell.SecretManagement -ErrorAction Stop
    Import-Module (Join-Path $PSScriptRoot '..\src\SecretsGui.Core.psm1') -Force
    $script:TestVaultModule = Join-Path $PSScriptRoot '..\..\tests\TestVault\TestVault.psd1'   # shared with the C# tests

    function New-TestVault {
        param([string]$Password)
        $name = 'SgTest_' + [guid]::NewGuid().ToString('N').Substring(0, 8)
        $path = Join-Path ([IO.Path]::GetTempPath()) "$name.clixml"
        $vp = @{ Path = $path }
        if ($Password) { $vp.Password = $Password }
        Register-SecretVault -Name $name -ModuleName $script:TestVaultModule -VaultParameters $vp -ErrorAction Stop
        [pscustomobject]@{ Name = $name; Path = $path }
    }
    function Remove-TestVault($vault) {
        Unregister-SecretVault -Name $vault.Name -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $vault.Path, "$($vault.Path).unlocked" -ErrorAction SilentlyContinue
    }
}

Describe 'Key/value text' {
    It 'parses lines, skipping blanks and comments, splitting on the first =' {
        $r = ConvertFrom-SgKeyValueText "a=1`r`n`n# note`n b = x=y "
        $r.Keys | Should -Be @('a', 'b')
        $r['b'] | Should -Be 'x=y'
    }
    It 'rejects malformed and duplicate lines' {
        { ConvertFrom-SgKeyValueText 'novalue' } | Should -Throw '*key=value*'
        { ConvertFrom-SgKeyValueText "a=1`na=2" } | Should -Throw '*Duplicate*'
    }
    It 'round-trips and masks secure values unless revealed' {
        $d = [ordered]@{ user = 'bob'; pw = (ConvertTo-SgSecureString 's3cret') }
        ConvertTo-SgKeyValueText $d | Should -Be "user=bob$([Environment]::NewLine)pw=********"
        ConvertTo-SgKeyValueText $d -Reveal | Should -Match 'pw=s3cret'
    }
}

Describe 'ConvertTo-SgSecretValue' {
    It 'builds each secret type' {
        ConvertTo-SgSecretValue -Type String -Text 'abc' | Should -Be 'abc'
        ConvertFrom-SgSecureString (ConvertTo-SgSecretValue -Type SecureString -Text 'abc') | Should -Be 'abc'
        $c = ConvertTo-SgSecretValue -Type PSCredential -UserName 'me' -SecureText (ConvertTo-SgSecureString 'pw')
        $c.UserName | Should -Be 'me'
        $c.GetNetworkCredential().Password | Should -Be 'pw'
        (ConvertTo-SgSecretValue -Type Hashtable -Text "k=v").k | Should -Be 'v'
        $bytes = ConvertTo-SgSecretValue -Type ByteArray -Text ([Convert]::ToBase64String([byte[]](1, 2, 3)))
        $bytes.GetType() | Should -Be ([byte[]])
        $bytes.Length | Should -Be 3
    }
    It 'rejects empty or invalid input' {
        { ConvertTo-SgSecretValue -Type String -Text '' } | Should -Throw
        { ConvertTo-SgSecretValue -Type PSCredential -UserName '' } | Should -Throw
        { ConvertTo-SgSecretValue -Type ByteArray -Text 'not base64!' } | Should -Throw '*Base64*'
    }
    It 'renders values for display and copy' {
        $c = [pscredential]::new('me', (ConvertTo-SgSecureString 'pw'))
        ConvertTo-SgDisplayText $c | Should -Match 'UserName: me'
        Get-SgCopyText $c | Should -Be 'pw'
        Get-SgSecretTypeName ([byte[]](1)) | Should -Be 'ByteArray'
        Get-SgSecretTypeName @{ a = 1 } | Should -Be 'Hashtable'
    }
}

Describe 'New-SgPassword' {
    It 'honours length and the NoSymbols switch' {
        (New-SgPassword -Length 40).Length | Should -Be 40
        New-SgPassword -Length 64 -NoSymbols | Should -Match '^[A-Za-z0-9]{64}$'
        New-SgPassword | Should -Not -Be (New-SgPassword)
    }
}

Describe 'Locked-vault detection and retry' {
    It 'recognises SecretStore locked messages' {
        Test-SgLockedError 'A valid password is required to access the Microsoft.PowerShell.SecretStore vault.' | Should -BeTrue
        Test-SgLockedError ([Exception]::new('outer', [Exception]::new('Use the Unlock-SecretStore cmdlet to provide the required password'))) | Should -BeTrue
        Test-SgLockedError 'Secret not found' | Should -BeFalse
    }
    It 'prompts, unlocks and retries until the operation succeeds' {
        $state = @{ unlocked = $false; prompts = 0 }
        $result = Invoke-SgVaultOperation -VaultName 'v' `
            -Operation { if (-not $state.unlocked) { throw 'A valid password is required' }; 'done' } `
            -PasswordProvider { $state.prompts++; ConvertTo-SgSecureString 'pw' } `
            -UnlockAction { param($p) $state.unlocked = $true }
        $result | Should -Be 'done'
        $state.prompts | Should -Be 1
    }
    It 'throws OperationCanceledException when the user cancels' {
        { Invoke-SgVaultOperation -VaultName 'v' -Operation { throw 'A valid password is required' } -PasswordProvider { $null } -UnlockAction { } } |
            Should -Throw -ExceptionType ([System.OperationCanceledException])
    }
    It 'does not prompt for unrelated errors' {
        $state = @{ prompts = 0 }
        { Invoke-SgVaultOperation -Operation { throw 'boom' } -PasswordProvider { $state.prompts++ } -UnlockAction { } } | Should -Throw 'boom'
        $state.prompts | Should -Be 0
    }
    It 'returns nothing (not $null) when the operation has no output' {
        @(Invoke-SgVaultOperation -Operation { }).Count | Should -Be 0
    }
    It 'streams lists so @() gives a flat array' {
        $r = @(Invoke-SgVaultOperation -Operation { [pscustomobject]@{ n = 1 }, [pscustomobject]@{ n = 2 } })
        $r.Count | Should -Be 2
        $r[1].n | Should -Be 2
    }
    It 'keeps byte arrays intact' {
        $r = Invoke-SgVaultOperation -Operation { , [byte[]](1, 2, 3) }
        $r.GetType() | Should -Be ([byte[]])
    }
}

Describe 'SecretStore guard' {
    It 'refuses to initialise over an existing store' {
        InModuleScope SecretsGui.Core {
            Mock Test-SgSecretStoreInitialized { $true }
            Mock Reset-SecretStore { }
            { Initialize-SgSecretStore -Password (ConvertTo-SgSecureString 'x') } | Should -Throw '*already exists*'
            Should -Invoke Reset-SecretStore -Times 0
        }
    }
    It 'requires a password for Password authentication' {
        InModuleScope SecretsGui.Core {
            Mock Test-SgSecretStoreInitialized { $false }
            Mock Reset-SecretStore { }
            { Initialize-SgSecretStore -Authentication Password } | Should -Throw '*password is required*'
        }
    }
}

Describe 'Vault and secret operations (TestVault)' {
    BeforeAll { $script:v = New-TestVault }
    AfterAll { Remove-TestVault $script:v }

    It 'lists the registered vault' {
        $vault = Get-SgVault | Where-Object Name -EQ $script:v.Name
        $vault | Should -Not -BeNullOrEmpty
        $vault.IsSecretStore | Should -BeFalse
        $vault.Parameters | Should -Match 'Path='
    }
    It 'stores and reads every secret type' {
        $values = @{
            s    = 'plain'
            ss   = (ConvertTo-SgSecureString 'hidden')
            cred = [pscredential]::new('me', (ConvertTo-SgSecureString 'pw'))
            ht   = @{ k = 'v' }
            bin  = [byte[]](9, 8, 7)
        }
        foreach ($k in $values.Keys) { Set-SgSecret -Name $k -Vault $script:v.Name -Value $values[$k] }
        (Get-SgSecretList -Vault $script:v.Name).Name | Should -Be @('bin', 'cred', 'ht', 's', 'ss')
        Get-SgSecretValue -Name s -Vault $script:v.Name | Should -Be 'plain'
        Get-SgSecretValue -Name ss -Vault $script:v.Name | Should -Be 'hidden'
        (Get-SgSecretValue -Name cred -Vault $script:v.Name).UserName | Should -Be 'me'
        (Get-SgSecretValue -Name ht -Vault $script:v.Name).k | Should -Be 'v'
        (Get-SgSecretValue -Name bin -Vault $script:v.Name).Length | Should -Be 3
    }
    It 'filters by substring' {
        (Get-SgSecretList -Vault $script:v.Name -Filter 's').Name | Should -Be @('s', 'ss')
    }
    It 'stores and updates metadata' {
        Set-SgSecret -Name meta -Vault $script:v.Name -Value 'x' -Metadata ([ordered]@{ env = 'prod' })
        (Get-SgSecretList -Vault $script:v.Name -Filter 'meta').MetadataText | Should -Be 'env=prod'
        Set-SgSecretMetadata -Name meta -Vault $script:v.Name -Metadata @{ env = 'dev' }
        (Get-SgSecretList -Vault $script:v.Name -Filter 'meta').MetadataText | Should -Be 'env=dev'
    }
    It 'honours NoClobber' {
        { Set-SgSecret -Name s -Vault $script:v.Name -Value 'again' -NoClobber } | Should -Throw
    }
    It 'renames a secret keeping its metadata' {
        Rename-SgSecret -Name meta -NewName meta2 -Vault $script:v.Name
        $list = @(Get-SgSecretList -Vault $script:v.Name -Filter 'meta')
        $list.Name | Should -Be 'meta2'
        $list.MetadataText | Should -Be 'env=dev'
    }
    It 'copies a secret to another vault' {
        $other = New-TestVault
        try {
            Copy-SgSecret -Name cred -SourceVault $script:v.Name -TargetVault $other.Name
            (Get-SgSecretValue -Name cred -Vault $other.Name).GetNetworkCredential().Password | Should -Be 'pw'
        }
        finally { Remove-TestVault $other }
    }
    It 'removes a secret' {
        Remove-SgSecret -Name s -Vault $script:v.Name -Confirm:$false
        (Get-SgSecretList -Vault $script:v.Name).Name | Should -Not -Contain 's'
    }
}

Describe 'Locked vault end-to-end (TestVault with password)' {
    BeforeAll { $script:lv = New-TestVault -Password 'letmein' }
    AfterAll { Remove-TestVault $script:lv }

    It 'fails as locked without unlocking' {
        $err = $null
        try { Get-SgSecretList -Vault $script:lv.Name } catch { $err = $_ }
        $err | Should -Not -BeNullOrEmpty
        Test-SgLockedError $err | Should -BeTrue
    }
    It 're-prompts after a wrong password, then succeeds' {
        $answers = [System.Collections.Queue]::new([object[]]@('wrong', 'letmein'))
        $state = @{ prompts = 0 }
        $vaultName = $script:lv.Name
        Invoke-SgVaultOperation -VaultName $vaultName -Operation { Set-SgSecret -Name a -Vault $vaultName -Value '1' } `
            -PasswordProvider { $state.prompts++; ConvertTo-SgSecureString $answers.Dequeue() }
        $state.prompts | Should -Be 2
        (Get-SgSecretList -Vault $vaultName).Name | Should -Be 'a'
    }
}
