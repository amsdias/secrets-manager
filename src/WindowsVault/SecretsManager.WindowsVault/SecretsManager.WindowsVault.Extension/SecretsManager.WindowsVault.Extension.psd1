@{
    ModuleVersion     = '1.0.0'
    GUID              = '5d9e7b21-4f6a-4c0d-8e3b-91a7c2f4d6e8'
    RootModule        = 'SecretsManager.WindowsVault.Extension.psm1'
    FunctionsToExport = @('Get-Secret', 'Get-SecretInfo', 'Set-Secret', 'Set-SecretInfo', 'Remove-Secret', 'Test-SecretVault')
}
