<#
.SYNOPSIS
    Installs the MSI for the current user, checks what it promises, uses it, and uninstalls it. Meant for CI.
.DESCRIPTION
    Checks: both exes, the Start menu shortcut and the user PATH entry are installed; a new shell finds "secrets";
    "secrets get/list" read a Windows-account vault created with the bundled modules (nothing else is installed on the
    runner); uninstalling removes the files, the shortcut and the PATH entry.
    It refuses to run outside GitHub Actions unless -Force: on a PC with the app installed, it would uninstall it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Msi,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
if (-not $env:GITHUB_ACTIONS -and -not $Force) { throw 'This installs and then uninstalls PowerShell Secrets for your account. Use -Force to run it outside CI.' }

$Msi = (Resolve-Path $Msi).Path
$dir = Join-Path $env:LOCALAPPDATA 'Programs\SecretsManager'
$link = Join-Path ([Environment]::GetFolderPath('Programs')) 'PowerShell Secrets.lnk'
$logs = Join-Path ([IO.Path]::GetTempPath()) "msi-test-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $logs | Out-Null

function Invoke-Msi([string]$Action, [string]$Log) {
    $p = Start-Process msiexec.exe -ArgumentList $Action, "`"$Msi`"", '/qn', '/l*v', "`"$Log`"" -Wait -PassThru
    if ($p.ExitCode -ne 0) { Get-Content $Log -Tail 60 | Write-Host; throw "msiexec $Action exited $($p.ExitCode)" }
}
function Test-OnUserPath {
    $entries = [Environment]::GetEnvironmentVariable('Path', 'User') -split ';' | ForEach-Object { $_.TrimEnd('\') }
    $entries -contains $dir
}
# What a terminal opened after the install sees: the system PATH followed by the user PATH, read fresh from the registry.
function Invoke-NewShell([string]$Command) {
    $fresh = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    $psi = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    foreach ($a in '-NoProfile', '-Command', $Command) { $psi.ArgumentList.Add($a) }
    $psi.Environment['Path'] = $fresh
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd()
    if (-not $p.WaitForExit(180000)) { $p.Kill(); throw "timed out: $Command" }
    [pscustomobject]@{ Out = $out.Trim(); Code = $p.ExitCode }
}
function Pass([string]$What) { Write-Host "PASS $What" }

Invoke-Msi '/i' (Join-Path $logs 'install.log')
foreach ($f in 'SecretsManager.exe', 'secrets.exe') { if (-not (Test-Path (Join-Path $dir $f))) { throw "$f was not installed in $dir" } }
if (-not (Test-Path $link)) { throw "no Start menu shortcut at $link" }
if (-not (Test-OnUserPath)) { throw "$dir is not on the user PATH" }
Pass 'installed: both exes, Start menu shortcut, user PATH entry'

$help = Invoke-NewShell '(Get-Command secrets).Source; secrets --help'
if ($help.Code -ne 0 -or $help.Out -notmatch [regex]::Escape((Join-Path $dir 'secrets.exe')) -or $help.Out -notmatch 'secrets list') {
    throw "a new shell didn't run the installed secrets (exit $($help.Code)): $($help.Out)" }
Pass 'a new shell finds secrets on PATH'

# The first run above installed the bundled modules; SecretManagement isn't installed on the runner otherwise.
$modules = Join-Path $env:LOCALAPPDATA 'SecretsManager\Modules'
$env:PSModulePath += ";$modules"
$vault = "CiMsi$([guid]::NewGuid().ToString('N').Substring(0, 6))"
$vaultFile = Join-Path $logs "$vault.clixml"
Register-SecretVault -Name $vault -ModuleName (Join-Path $modules 'SecretsManager.WindowsVault\SecretsManager.WindowsVault.psd1') -VaultParameters @{ Path = $vaultFile }
try {
    Set-Secret -Vault $vault -Name ci-key -Secret 'ci-value-42' -Metadata @{ env = 'ci' }
    $got = Invoke-NewShell "secrets get ci-key --vault $vault"
    if ($got.Code -ne 0 -or $got.Out -ne 'ci-value-42') { throw "secrets get returned '$($got.Out)' (exit $($got.Code))" }
    $listed = Invoke-NewShell "secrets list --vault $vault --json"
    $items = $listed.Out | ConvertFrom-Json
    if ($listed.Code -ne 0 -or $items.name -notcontains 'ci-key' -or $listed.Out -match 'ci-value-42') { throw "secrets list returned '$($listed.Out)'" }
    Pass 'secrets get and list read a Windows-account vault made with the bundled modules'
}
finally { Unregister-SecretVault -Name $vault -ErrorAction SilentlyContinue }

Invoke-Msi '/x' (Join-Path $logs 'uninstall.log')
if (Test-Path (Join-Path $dir 'SecretsManager.exe')) { throw "uninstall left $dir behind" }
if (Test-Path $link) { throw 'uninstall left the Start menu shortcut' }
if (Test-OnUserPath) { throw 'uninstall left the user PATH entry' }
Pass 'uninstalled: files, shortcut and PATH entry removed'
Remove-Item $logs -Recurse -Force -ErrorAction SilentlyContinue
