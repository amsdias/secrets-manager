<#
.SYNOPSIS
    Publishes Secrets Manager and installs it for the current user, with a Start menu shortcut.
.DESCRIPTION
    Builds a release into a temporary folder, mirrors it into the install folder
    (default %LOCALAPPDATA%\Programs\SecretsManager) and creates a "PowerShell Secrets" Start menu shortcut.
    No admin rights. Your vaults, secrets and settings (%LOCALAPPDATA%\SecretsManager) are never touched.

    The app is two files: SecretsManager.exe (self-contained, compressed single file: no .NET needed; each new version
    extracts itself to %TEMP%\.net\SecretsManager on first start) and secrets.exe (native, for "secrets get"; the install folder is added to your user PATH).
    -FrameworkDependent builds a larger, uncompressed SecretsManager.exe that needs the .NET 11 Desktop Runtime.
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
    # Uncompressed single file that uses the installed .NET 11 Desktop Runtime.
    [switch]$FrameworkDependent,
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
function Set-UserPathEntry([string]$Folder, [bool]$Present) {
    # Edit HKCU\Environment\Path directly: [Environment]::SetEnvironmentVariable(..., 'User') would rewrite it as REG_SZ and
    # expand entries like %USERPROFILE%\bin. Windows joins it with the system PATH; only new terminals see the change.
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    try {
        $old = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $kind = if ($old) { $key.GetValueKind('Path') } else { [Microsoft.Win32.RegistryValueKind]::ExpandString }
        $entries = @($old -split ';' | Where-Object { $_ -and $_.TrimEnd('\') -ne $Folder.TrimEnd('\') })
        if ($Present) { $entries += $Folder }
        $new = $entries -join ';'
        if ($new -eq $old) { return }
        $key.SetValue('Path', $new, $kind)
    }
    finally { $key.Dispose() }
    # Tell Explorer (and so new terminals) that the environment changed: setting any user variable broadcasts WM_SETTINGCHANGE.
    [Environment]::SetEnvironmentVariable('SecretsManagerPathRefresh', $null, 'User')
    if ($Present) { "Added $Folder to your user PATH (new terminals)" } else { "Removed $Folder from your user PATH" }
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
    Set-UserPathEntry $Destination $false
    "Settings in $(Join-Path $env:LOCALAPPDATA 'SecretsManager') were kept. Vaults and secrets are not affected."
    exit 0
}

$staging = $null
if (-not $Source) {
    $staging = Join-Path ([IO.Path]::GetTempPath()) "SecretsManager-publish-$([guid]::NewGuid().ToString('N'))"
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    # The newest release tag (v1.2.3 -> 1.2.3), so the app shows it and compares it with GitHub's latest release.
    $tag = git -C $repo describe --tags --abbrev=0 --match 'v[0-9]*' 2>$null
    $version = if ($LASTEXITCODE -eq 0 -and $tag -match '^v(\d+\.\d+\.\d+)$') { $Matches[1] } else { '0.0.0' }
    dotnet publish (Join-Path $repo 'src\SecretsManager.App') -c Release -p:Platform=x64 -r win-x64 --self-contained $selfContained `
        -p:PublishSingleFile=true -p:DebugType=embedded -p:Version=$version -o $staging
    if ($LASTEXITCODE) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
    # The console front end for "get" (PowerShell doesn't capture a GUI exe's output); NativeAOT, set in its csproj.
    dotnet publish (Join-Path $repo 'src\SecretsManager.Cli') -c Release -r win-x64 -p:Version=$version -o $staging
    if ($LASTEXITCODE) { throw "dotnet publish of the CLI failed (exit $LASTEXITCODE)" }
    $Source = $staging
}
try {
    if (-not (Test-Path (Join-Path $Source $exeName))) { throw "$Source doesn't contain $exeName" }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    robocopy $Source $Destination /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying to $Destination failed (robocopy exit $LASTEXITCODE)" }
    Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $Destination 'LICENSE.txt')
    Copy-Item (Join-Path $repo 'NOTICE') (Join-Path $Destination 'NOTICE.txt')
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
Set-UserPathEntry $Destination $true
"Installed to $Destination"
exit 0
