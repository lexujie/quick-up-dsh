@echo off
rem Create a desktop shortcut for the DeepSeek Harness launcher.
set "VBS=%~dp0launch-dsh.vbs"
set "DIR=%~dp0"
if not exist "%VBS%" (
  echo launch-dsh.vbs not found next to this script.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; $lnk = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeepSeek Harness.lnk')); $lnk.TargetPath = $env:VBS; $lnk.WorkingDirectory = $env:DIR; $lnk.IconLocation = 'shell32.dll,220'; $lnk.Description = 'DeepSeek Harness quick launcher'; $lnk.Save()"
echo.
echo Shortcut "DeepSeek Harness" created on the desktop.
pause
