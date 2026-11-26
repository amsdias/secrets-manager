# Third-party components

`PowerShellModules/` holds Microsoft's PowerShell modules, unchanged, as published on the PowerShell Gallery. The app embeds them
(SecretsManager.App.csproj) and installs them to `%LOCALAPPDATA%\SecretsManager\Modules`, so it works on a PC that doesn't have
them installed. Each folder keeps its `LICENSE` (MIT) and `ThirdPartyNotices.txt`.

| Module | Version | Source | License |
| --- | --- | --- | --- |
| Microsoft.PowerShell.SecretManagement | 1.1.2 | https://www.powershellgallery.com/packages/Microsoft.PowerShell.SecretManagement/1.1.2 | MIT |
| Microsoft.PowerShell.SecretStore | 1.0.6 | https://www.powershellgallery.com/packages/Microsoft.PowerShell.SecretStore/1.0.6 | MIT |

To update: `Save-PSResource -Name <module> -Version <version> -Path third_party/PowerShellModules -TrustRepository`, delete the old
version folder, check that the DLLs and .psd1 files are Authenticode-signed by Microsoft (`Get-AuthenticodeSignature`), and update
this table.
