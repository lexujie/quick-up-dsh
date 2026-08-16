@echo off
rem ============================================================================
rem  Build the DeepSeek Harness desktop launcher (dsh-launcher.exe)
rem  Requirements: Windows 10/11 (built-in .NET Framework 4.x csc.exe)
rem
rem  Usage:
rem    build-launcher.bat             build + ask to create a desktop shortcut
rem    build-launcher.bat /shortcut   build + create desktop shortcut (no prompt)
rem    build-launcher.bat /noshortcut build only (no prompt)
rem ============================================================================
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found - .NET Framework 4.x is required on this system.
  pause
  exit /b 1
)

echo [1/4] Generating icon (dsh-launcher.ico) ...
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0tools\make-icon.ps1" "%~dp0dsh-launcher.ico"
if errorlevel 1 (
  echo [WARN] Icon generation failed, continuing without embedded icon.
)

echo [2/4] Compiling dsh-launcher.exe ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /win32icon:"%~dp0dsh-launcher.ico" ^
  /out:"%~dp0dsh-launcher.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  "%~dp0src\dsh-launcher.cs"
if errorlevel 1 (
  echo [ERROR] Compile failed.
  pause
  exit /b 1
)

echo [3/4] Smoke test ...
"%~dp0dsh-launcher.exe" /smoketest
if exist "%~dp0smoketest-out.log" type "%~dp0smoketest-out.log"

echo [4/4] Done: dsh-launcher.exe

if /i "%~1"=="/noshortcut" goto :end
if /i "%~1"=="/shortcut" goto :createshortcut
choice /c YN /m "Create a desktop shortcut 'DeepSeek Harness'"
if errorlevel 2 goto :end
:createshortcut
set "SHORTCUT_TARGET=%~dp0dsh-launcher.exe"
set "SHORTCUT_DIR=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ws = New-Object -ComObject WScript.Shell; $lnk = $ws.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'DeepSeek Harness.lnk')); $lnk.TargetPath = $env:SHORTCUT_TARGET; $lnk.WorkingDirectory = $env:SHORTCUT_DIR; $lnk.IconLocation = $env:SHORTCUT_TARGET + ',0'; $lnk.Description = 'DeepSeek Harness quick launcher'; $lnk.Save()"
echo Shortcut "DeepSeek Harness" created on the desktop.
:end
endlocal
exit /b 0
