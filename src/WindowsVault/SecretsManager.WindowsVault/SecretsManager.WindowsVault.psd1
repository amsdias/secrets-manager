@{
    ModuleVersion     = '1.0.0'
    GUID              = 'c3a5f0de-6a0b-4f43-9c1e-6c3a1e9d2b47'
    Author            = 'SecretsManager'
    Description       = 'SecretManagement extension vault whose secrets are encrypted for the current Windows account (DPAPI). Reading needs no password or prompt: anything running as you can read them, like SecretStore without a password.'
    PowerShellVersion = '7.2'
    NestedModules     = @('.\SecretsManager.WindowsVault.Extension')
    FunctionsToExport = @()
    CmdletsToExport   = @()
    AliasesToExport   = @()
}
