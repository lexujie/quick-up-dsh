@echo off
rem Create a desktop shortcut for the DeepSeek Harness launcher.
rem Prefers dsh-launcher.exe; falls back to launch-dsh.vbs.
set "DIR=%~dp0"
if exist "%DIR%dsh-launcher.exe" (
  set "TARGET=%DIR%dsh-launcher.exe"
  set "ICON=%DIR%dsh-launcher.exe,0"
) else (
  if not exist "%DIR%launch-dsh.vbs" (
    echo Neither dsh-launcher.exe nor launch-dsh.vbs was found next to this script.
    pause
    exit /b 1
  )
  set "TARGET=%DIR%launch-dsh.vbs"
  set "ICON=shell32.dll,220"
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; $lnk = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeepSeek Harness.lnk')); $lnk.TargetPath = $env:TARGET; $lnk.WorkingDirectory = $env:DIR; $lnk.IconLocation = $env:ICON; $lnk.Description = 'DeepSeek Harness quick launcher'; $lnk.Save()"
echo.
echo Shortcut "DeepSeek Harness" created on the desktop.
pause
