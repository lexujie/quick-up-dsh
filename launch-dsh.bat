@echo off
rem DeepSeek Harness quick launcher (console visible).
rem Closing this console window stops the service (disconnects).
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0dsh-launcher.ps1"
if errorlevel 1 (
  echo.
  echo Launcher exited with an error. See the message above.
  pause
)
