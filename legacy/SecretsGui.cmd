@echo off
rem Launches the PowerShell Secrets GUI with no console window.
rem -NonInteractive matters: if the SecretStore is set to Interaction=Prompt, its console prompt
rem fails fast (the GUI then asks for the password) instead of hanging on a hidden console.
where pwsh >nul 2>nul
if %errorlevel%==0 (
  start "" pwsh.exe -NoProfile -STA -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0SecretsGui.ps1"
) else (
  start "" powershell.exe -NoProfile -STA -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "%~dp0SecretsGui.ps1"
)
