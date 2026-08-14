' DeepSeek Harness quick launcher - no console window
' Double-click this file to start dsh web and open the browser.
' Closing the launcher window stops the service (disconnects).
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
base = fso.GetParentFolderName(WScript.ScriptFullName)
ps1 = base & "\dsh-launcher.ps1"
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File """ & ps1 & """"
sh.Run cmd, 0, False
