@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-local.ps1"
if errorlevel 1 (
    echo Installation failed. See the message above.
) else (
    echo Installation complete. Open the new desktop shortcut.
)
pause
