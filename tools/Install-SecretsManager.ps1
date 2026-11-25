<#
.SYNOPSIS
    Publishes Secrets Manager and installs it for the current user, with a Start menu shortcut.
.DESCRIPTION
    Builds a release into a temporary folder, mirrors it into the install folder
    (default %LOCALAPPDATA%\Programs\SecretsManager) and creates a "PowerShell Secrets" Start menu shortcut.
    No admin rights. Your vaults, secrets and settings (%LOCALAPPDATA%\SecretsManager) are never touched.

    By default the app is ONE framework-dependent file, SecretsManager.exe, which needs the .NET 11 Desktop Runtime
    (included with the .NET 11 SDK). On first start it extracts itself to %TEMP%\.net\SecretsManager.
    -SelfContained builds the larger folder version instead, which needs no .NET installed.
.EXAMPLE
    pwsh -File tools/Install-SecretsManager.ps1              # build and install (or update)
.EXAMPLE
    pwsh -File tools/Install-SecretsManager.ps1 -Desktop     # also put a shortcut on the desktop
.EXAMPLE
    pwsh -File tools/Install-SecretsManager.ps1 -Uninstall   # remove the app and its shortcuts
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\SecretsManager'),
    [switch]$Desktop,
    [switch]$Uninstall,
    # Folder build that includes the .NET runtime (no single file).
    [switch]$SelfContained,
    # An existing publish folder to install instead of building one.
    [string]$Source
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$exeName = 'SecretsManager.exe'
$shortcutName = 'PowerShell Secrets.lnk'
$startMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) $shortcutName
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) $shortcutName

function Assert-OurFolder {
    # /MIR and -Uninstall delete files: only ever do that to a folder that holds this app.
    if ((Test-Path $Destination) -and (Get-ChildItem $Destination -Force | Select-Object -First 1) -and
        -not (Test-Path (Join-Path $Destination $exeName))) {
        throw "$Destination exists and doesn't contain $exeName; refusing to overwrite or delete it."
    }
}
function Assert-NotRunning {
    $running = Get-Process SecretsManager -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($Destination, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) { throw "Secrets Manager is running from $Destination. Close it first." }
}

Assert-OurFolder
Assert-NotRunning

if ($Uninstall) {
    foreach ($link in $startMenuLink, $desktopLink) { if (Test-Path $link) { Remove-Item $link; "Removed $link" } }
    if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force; "Removed $Destination" }
    "Settings in $(Join-Path $env:LOCALAPPDATA 'SecretsManager') were kept. Vaults and secrets are not affected."
    exit 0
}

$staging = $null
if (-not $Source) {
    $staging = Join-Path ([IO.Path]::GetTempPath()) "SecretsManager-publish-$([guid]::NewGuid().ToString('N'))"
    $mode = if ($SelfContained) { @('--self-contained') }
            else { @('--self-contained', 'false', '-p:PublishSingleFile=true', '-p:DebugType=embedded') }
    dotnet publish (Join-Path $repo 'src\SecretsManager.App') -c Release -p:Platform=x64 -r win-x64 @mode -o $staging
    if ($LASTEXITCODE) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
    # The console front end for "get" (PowerShell doesn't capture a GUI exe's output).
    dotnet publish (Join-Path $repo 'src\SecretsManager.Cli') -c Release -r win-x64 @mode -o $staging
    if ($LASTEXITCODE) { throw "dotnet publish of the CLI failed (exit $LASTEXITCODE)" }
    $Source = $staging
}
try {
    if (-not (Test-Path (Join-Path $Source $exeName))) { throw "$Source doesn't contain $exeName" }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    robocopy $Source $Destination /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying to $Destination failed (robocopy exit $LASTEXITCODE)" }
}
finally {
    if ($staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
}

$exe = Join-Path $Destination $exeName
$shell = New-Object -ComObject WScript.Shell
foreach ($link in @($startMenuLink) + @(if ($Desktop) { $desktopLink })) {
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $Destination
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'Manage PowerShell SecretManagement vaults and secrets'
    $shortcut.Save()
    "Shortcut: $link"
}
"Installed to $Destination"
exit 0
