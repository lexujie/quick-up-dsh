#requires -Version 5.1
<#
============================================================================
 DeepSeek Harness 快速启动器（Windows）
============================================================================
 双击 launch-dsh.vbs（无黑窗）或 launch-dsh.bat（带控制台）即可：
   1. 快速启动 dsh web 服务（直接调用 node，跳过 npx 的网络检查）
   2. 服务就绪后自动打开浏览器：http://127.0.0.1:3080
   3. 窗口内可随时「重启服务」（先停旧进程再启新进程）
   4. 关闭启动器窗口 = 自动停止服务并断开连接（taskkill 整个进程树）

 说明：
   - dsh web 的 workspace 根目录 = 本脚本所在目录（launcher 放在哪个文件夹，
     会话就建在哪个文件夹）。
   - 服务日志写入 dsh-server.log / dsh-server.err.log（与脚本同目录）。
   - 如果端口已被占用（说明已有一个 DSH 实例在运行），本程序只负责打开浏览器，
     不会重复启动，也不会关闭那个实例。

 自检命令（不弹出窗口）：
   powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\dsh-launcher.ps1 -SmokeTest
 完整自检（额外拉起一次 dsh web --help 验证启动链路）：
   powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\dsh-launcher.ps1 -SelfTest
============================================================================
#>
param(
    [switch]$SmokeTest,
    [switch]$SelfTest,
    [switch]$NoBrowser,
    [int]$Port = 3080
)

$ErrorActionPreference = 'Stop'

# ---------- 可调配置 ----------
$Url = "http://127.0.0.1:$Port"
$ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$OutLog = Join-Path $ScriptDir 'dsh-server.log'
$ErrLog = Join-Path $ScriptDir 'dsh-server.err.log'
$DebugLog = Join-Path $ScriptDir 'launcher-debug.log'
$StartTimeoutSec = 180

function Write-DebugLog([string]$msg) {
    try { Add-Content -Path $DebugLog -Value ((Get-Date -Format 'HH:mm:ss.fff') + '  ' + $msg) -Encoding UTF8 } catch { }
}

# DSH_HOME 缺省时使用 ~/.dsh（与应用默认一致）
if (-not $env:DSH_HOME) { $env:DSH_HOME = Join-Path $HOME '.dsh' }

# ---------- 工具函数 ----------
function Test-DshPort([int]$TimeoutMs = 800) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $iar = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
            if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs)) { return $false }
            $client.EndConnect($iar)
            return $true
        } finally { $client.Close() }
    } catch {
        # 兜底：慢速但兼容性更好的检查
        try {
            return [bool](Test-NetConnection -ComputerName '127.0.0.1' -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue)
        } catch { return $false }
    }
}

function Resolve-NodeExe {
    $cmd = Get-Command node -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -and (Test-Path $cmd.Source)) { return $cmd.Source }
    foreach ($p in @(
        "$env:ProgramFiles\nodejs\node.exe",
        "$env:LOCALAPPDATA\Programs\nodejs\node.exe",
        "$env:LOCALAPPDATA\hermes\node\node.exe"
    )) { if (Test-Path $p) { return $p } }
    return $null
}

function Resolve-DshEntry {
    # 1) 优先直接使用 npx 缓存里的 dsh 入口（node 直启，最快）
    $cands = @(Get-ChildItem "$env:LOCALAPPDATA\npm-cache\_npx\*\node_modules\@deepseek-ai\dsh\lib\bin.js" -ErrorAction SilentlyContinue)
    if ($cands.Count -gt 0) {
        $best = $cands | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        return @{ Kind = 'node'; Path = $best.FullName }
    }
    # 2) PATH 上的 dsh（npm 全局安装）
    $cmd = Get-Command dsh -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) {
        $ext = [System.IO.Path]::GetExtension($cmd.Source).ToLowerInvariant()
        $kind = switch ($ext) { '.ps1' { 'ps1' } default { 'cmd' } }
        return @{ Kind = $kind; Path = $cmd.Source }
    }
    # 3) npx 兜底
    $npx = Get-Command npx -ErrorAction SilentlyContinue
    if ($npx) { return @{ Kind = 'npx'; Path = $npx.Source } }
    return $null
}

function Get-DshCommandLine($node, $entry) {
    $portArg = '--port ' + $Port
    switch ($entry.Kind) {
        'node' { return ('"{0}" "{1}" web {2}' -f $node, $entry.Path, $portArg) }
        'ps1'  { return ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}" web {1}' -f $entry.Path, $portArg) }
        'cmd'  { return ('"{0}" web {1}' -f $entry.Path, $portArg) }
        'npx'  { return ('npx --no-install @deepseek-ai/dsh web {0}' -f $portArg) }
        default { return ('"{0}" "{1}" web {2}' -f $node, $entry.Path, $portArg) }
    }
}

# 以隐藏方式拉起 cmd /c "<命令> 1> out 2> err"，返回进程对象
function Start-DshHidden($commandLine, $extraArgs) {
    $inner = $commandLine + $extraArgs
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "$env:ComSpec"
    $psi.Arguments = '/d /c "' + $inner + '"'
    $psi.WorkingDirectory = $ScriptDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    return [System.Diagnostics.Process]::Start($psi)
}

# ---------- 自检模式 ----------
if ($SmokeTest -or $SelfTest) {
    $node = Resolve-NodeExe
    $entry = Resolve-DshEntry
    Write-Output ('node        = {0}' -f $node)
    Write-Output ('dsh entry   = {0}:{1}' -f $entry.Kind, $entry.Path)
    Write-Output ('dsh_home    = {0}' -f $env:DSH_HOME)
    Write-Output ('port        = {0} busy={1}' -f $Port, (Test-DshPort))
    Write-Output ('cmd line    = {0}' -f (Get-DshCommandLine $node $entry))
    if (-not $node -or -not $entry) { Write-Output 'status = MISSING node or dsh'; exit 2 }
    Write-Output 'status = OK'

    if ($SelfTest) {
        $testOut = Join-Path $ScriptDir 'selftest-out.log'
        $testErr = Join-Path $ScriptDir 'selftest-err.log'
        Remove-Item $testOut, $testErr -ErrorAction SilentlyContinue
        $cmdLine = Get-DshCommandLine $node $entry
        $p = Start-DshHidden $cmdLine (' --help 1> "' + $testOut + '" 2> "' + $testErr + '"')
        if (-not $p.WaitForExit(60000)) { Write-Output 'selftest = TIMEOUT'; exit 3 }
        Write-Output ('selftest    = exitcode {0}' -f $p.ExitCode)
        if (Test-Path $testOut) { Write-Output '--- stdout (tail) ---'; Get-Content $testOut -Encoding UTF8 -Tail 20 -ErrorAction SilentlyContinue }
        if (Test-Path $testErr) { Write-Output '--- stderr (tail) ---'; Get-Content $testErr -Encoding UTF8 -Tail 20 -ErrorAction SilentlyContinue }
    }
    exit 0
}

# ---------- 常规模式：GUI ----------
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if (-not (Resolve-NodeExe) -or -not (Resolve-DshEntry)) {
    [void][System.Windows.Forms.MessageBox]::Show(
        '未找到 Node.js 或 dsh 入口。' + [char]10 +
        '请先安装 Node.js，然后运行：npm install -g @deepseek-ai/dsh',
        'DeepSeek Harness 启动器',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error)
    exit 1
}

# 已有实例在运行：只打开浏览器，不重复启动、不接管
if (Test-DshPort) {
    Start-Process $Url
    exit 0
}

# ---------- 构建窗口 ----------
$form = New-Object System.Windows.Forms.Form
$form.Text = 'DeepSeek Harness 启动器'
$form.ClientSize = New-Object System.Drawing.Size(520, 370)
$form.MinimumSize = New-Object System.Drawing.Size(520, 370)
$form.StartPosition = 'CenterScreen'

$status = New-Object System.Windows.Forms.Label
$status.SetBounds(12, 10, 496, 22)
$status.Text = '正在启动 DeepSeek Harness ...'

$logBox = New-Object System.Windows.Forms.TextBox
$logBox.SetBounds(12, 38, 496, 276)
$logBox.Multiline = $true
$logBox.ReadOnly = $true
$logBox.ScrollBars = 'Vertical'
$logBox.WordWrap = $false
$logBox.Font = New-Object System.Drawing.Font('Consolas', 9)
$logBox.Anchor = 'Top,Bottom,Left,Right'

$note = New-Object System.Windows.Forms.Label
$note.SetBounds(12, 320, 250, 20)
$note.Text = '关闭本窗口 = 停止服务并断开连接'
$note.ForeColor = [System.Drawing.Color]::FromArgb(190, 45, 45)
$note.Anchor = 'Bottom,Left'

$btnRestart = New-Object System.Windows.Forms.Button
$btnRestart.SetBounds(272, 314, 112, 32)
$btnRestart.Text = '重启服务'
$btnRestart.Anchor = 'Bottom,Right'

$btn = New-Object System.Windows.Forms.Button
$btn.SetBounds(396, 314, 112, 32)
$btn.Text = '停止并退出'
$btn.Anchor = 'Bottom,Right'

$form.Controls.Add($status)
$form.Controls.Add($logBox)
$form.Controls.Add($note)
$form.Controls.Add($btnRestart)
$form.Controls.Add($btn)

# ---------- 状态与清理 ----------
$script:proc = $null
$script:opened = $false
$script:stopped = $false
$script:startTime = Get-Date
$script:offsets = @{}

function Stop-DshServer {
    if ($script:stopped) { return }
    $script:stopped = $true
    $p = $script:proc
    if ($p -and -not $p.HasExited) {
        try { & "$env:WINDIR\System32\taskkill.exe" /PID $p.Id /T /F 2>$null | Out-Null } catch { }
        try { $p.WaitForExit(5000) } catch { }
    }
}

# 启动（或重启）服务进程：清空旧日志、重置状态与计时
function Start-DshServerProcess {
    Remove-Item $OutLog, $ErrLog -ErrorAction SilentlyContinue
    $cmdLine = Get-DshCommandLine (Resolve-NodeExe) (Resolve-DshEntry)
    Write-DebugLog ('spawn: cmd /c "' + $cmdLine + ' ..."')
    $p = Start-DshHidden $cmdLine (' 1> "' + $OutLog + '" 2> "' + $ErrLog + '"')
    Write-DebugLog ('spawned pid=' + $p.Id)
    $script:proc = $p
    $script:offsets[$OutLog] = 0
    $script:offsets[$ErrLog] = 0
    $script:opened = $false
    $script:stopped = $false
    $script:startTime = Get-Date
}

# 重启：先停掉旧进程树，再拉起新进程，并恢复定时监控
function Restart-DshServer {
    $old = $script:proc
    $script:proc = $null   # 先摘除旧进程，避免定时器把它的退出误判为新服务失败
    if ($old -and -not $old.HasExited) {
        $status.Text = '正在停止旧服务...'
        Write-DebugLog ('restart: killing old pid=' + $old.Id)
        try { & "$env:WINDIR\System32\taskkill.exe" /PID $old.Id /T /F 2>$null | Out-Null } catch { }
        try { $old.WaitForExit(5000) } catch { }
    }
    $status.Text = '正在重启服务...'
    Start-DshServerProcess
    $status.Text = '正在启动服务（0s / 180s）...'
    $timer.Start()
    Write-DebugLog 'restart completed'
}

# ---------- 启动服务（隐藏窗口，日志写入文件） ----------
Write-DebugLog ('GUI start, port=' + $Port + ', url=' + $Url)
try {
    Start-DshServerProcess
} catch {
    Write-DebugLog ('spawn failed: ' + $_.Exception.Message)
    [void][System.Windows.Forms.MessageBox]::Show('服务启动失败：' + $_.Exception.Message, 'DeepSeek Harness 启动器', 'OK', 'Error')
    exit 1
}

# ---------- 定时器：尾部读日志 / 等待端口 / 监控进程 ----------
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 500
$timer.Add_Tick({
    try {
        # 1) 尾部读取服务日志
        foreach ($f in @($OutLog, $ErrLog)) {
            if (-not (Test-Path $f)) { continue }
            try {
                $len = (Get-Item $f).Length
                $off = $script:offsets[$f]
                if (-not $off) { $off = 0 }
                if ($len -gt $off) {
                    $fs = [System.IO.File]::Open($f, 'Open', 'Read', 'ReadWrite')
                    try {
                        [void]$fs.Seek($off, 'Begin')
                        $buf = New-Object byte[] ($len - $off)
                        $read = $fs.Read($buf, 0, $buf.Length)
                        if ($read -gt 0) {
                            $text = [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
                            $logBox.AppendText($text)
                            $logBox.SelectionStart = $logBox.TextLength
                            $logBox.ScrollToCaret()
                        }
                    } finally { $fs.Close() }
                    $script:offsets[$f] = $len
                }
            } catch { }
        }

        # 2) 状态更新
        $p = $script:proc
        if ($p) {
            if ($p.HasExited) {
                if ($script:opened) { $status.Text = '服务已停止。关闭窗口退出。' }
                else { $status.Text = '启动失败（进程已退出），请查看下方日志。' }
                $timer.Stop()
            }
            elseif (-not $script:opened) {
                if (Test-DshPort 400) {
                    $script:opened = $true
                    $status.Text = "已启动：$Url"
                    if (-not $NoBrowser) {
                        try { Start-Process $Url } catch {
                            $status.Text = "已启动：$Url （浏览器打开失败，请手动访问）"
                        }
                    }
                }
                else {
                    $elapsed = [int]((Get-Date) - $script:startTime).TotalSeconds
                    if ($elapsed -gt $StartTimeoutSec) {
                        $status.Text = "启动超时（超过 ${StartTimeoutSec} 秒），请查看日志。"
                        $timer.Stop()
                    }
                    else {
                        $status.Text = "正在启动服务（${elapsed}s / ${StartTimeoutSec}s）..."
                    }
                }
            }
        }
    } catch {
        Write-DebugLog ('tick error: ' + $_.Exception.Message)
        $timer.Stop()
    }
})
$timer.Start()

$btnRestart.Add_Click({
    $btnRestart.Enabled = $false
    $btn.Enabled = $false
    try {
        Restart-DshServer
    } catch {
        $status.Text = '重启失败：' + $_.Exception.Message
        Write-DebugLog ('restart error: ' + $_.Exception.Message)
    } finally {
        $btnRestart.Enabled = $true
        $btn.Enabled = $true
    }
})
$btn.Add_Click({ $form.Close() })
$form.Add_FormClosing({
    Write-DebugLog 'form closing'
    $timer.Stop()
    Stop-DshServer
})

Write-DebugLog 'entering message loop'
try {
    [void][System.Windows.Forms.Application]::Run($form)
} catch {
    Write-DebugLog ('run error: ' + $_.Exception.Message)
}
Write-DebugLog 'message loop ended'

# 窗口关闭后的收尾（幂等）
Stop-DshServer
$timer.Dispose()
exit 0
