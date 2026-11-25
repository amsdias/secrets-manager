@{
    ModuleVersion     = '1.0.0'
    GUID              = '0b7e2a4d-8b1e-4f0e-9f7d-2a9a8f5c3e21'
    RootModule        = 'TestVault.Extension.psm1'
    RequiredAssemblies = @()
    FunctionsToExport = @('Get-Secret', 'Get-SecretInfo', 'Set-Secret', 'Set-SecretInfo', 'Remove-Secret', 'Test-SecretVault', 'Unlock-SecretVault')
}
