# DeepSeek Harness 启动器（Windows 桌面应用）

> GitHub: <https://github.com/lexujie/quick-up-dsh>

双击 `dsh-launcher.exe` 即可启动 DeepSeek Harness 网页版（`dsh web`），
**关闭窗口（或点「停止并退出」）即自动停止服务并断开连接**，不留后台残留。

## 快速开始

```sh
git clone https://github.com/lexujie/quick-up-dsh.git
cd quick-up-dsh
# 双击 dsh-launcher.exe 即可（真正的桌面应用，带图标、托盘、设置记忆）
```

> 想放到桌面：双击 `create-desktop-shortcut.bat`，或直接在构建时选择创建快捷方式。

## 文件说明

| 文件 | 用途 |
|---|---|
| `dsh-launcher.exe` | **推荐入口**：桌面应用，双击启动（原生 WinForms，无控制台窗口） |
| `build-launcher.bat` | 一键构建/重新编译 `dsh-launcher.exe`（源码在 `src/`，无需安装任何东西） |
| `src/dsh-launcher.cs` | 启动器源码（C#，Windows 自带 .NET Framework 4.x 即可编译） |
| `dsh-launcher.ico` | 程序图标 |
| `create-desktop-shortcut.bat` | 在桌面创建「DeepSeek Harness」快捷方式（自动优先指向 exe） |
| `launch-dsh.vbs` / `launch-dsh.bat` / `launch-dsh-lan.vbs` | 旧版脚本入口（保留作备用） |
| `dsh-launcher.ps1` | 旧版核心逻辑（保留作备用/参考） |
| `lan.patch.yml` | 局域网模式补丁：把 webserver 绑定到 `0.0.0.0` |
| `launcher-config.json` | 运行时配置（自动生成，删除即恢复默认） |

## 使用方法

1. 把本文件夹放到你希望作为工作目录的位置（`dsh web` 的 workspace 根目录 = exe 所在目录，会话文件都建在这里）。
2. 双击 `dsh-launcher.exe`。
3. 窗口出现，服务就绪后自动打开浏览器 `http://127.0.0.1:3080`。
4. 需要重启服务时（换配置、清空内存状态），点「**重启服务**」；「**打开浏览器**」随时回到页面。
5. 用完直接**关闭窗口** → 服务进程树被自动终止（`taskkill /T /F`），连接断开；也可以点「**停止并退出**」。

> 端口 3080 已被占用（已有 DSH 实例在运行）时，启动器只打开浏览器，不会重复启动，也不会关闭那个实例。

## 设置（窗口底部，自动记忆）

| 设置 | 说明 |
|---|---|
| 端口 | 换端口运行（如 8080），改完点「应用并重启」生效 |
| 局域网(0.0.0.0) | 绑定所有网卡，同一局域网设备可访问（需 `lan.patch.yml`） |
| 自动打开浏览器 | 服务就绪后自动打开页面 |
| 关闭窗口时 | **每次询问** / **直接停止** / **最小化到托盘**（服务继续后台运行，托盘图标可恢复窗口、重启、退出） |

设置保存在 exe 同目录的 `launcher-config.json`，修改即保存；删除该文件恢复默认。

## 桌面快捷方式（可选）

- 双击 `create-desktop-shortcut.bat` → 桌面出现「DeepSeek Harness」快捷方式（自动优先指向 exe）。
- 或重新构建时选择创建：`build-launcher.bat /shortcut`。

## 工作原理

- 直接调用 `node <dsh 的 bin.js> web --port <端口>`，跳过 npx 的网络检查，启动更快；局域网模式额外带 `--patch lan.patch.yml`。
- 启动器先探测端口：**已在运行 → 只开浏览器**；未运行 → 拉起服务并等待端口就绪（最多 180 秒，首次初始化 profile 较慢）。
- 服务输出**实时显示**在窗口日志区，并镜像到 `dsh-server.log` / `dsh-server.err.log`（与 exe 同目录）。
- 「重启服务」= 对旧进程树执行 `taskkill /T /F` 后重新拉起。
- 关闭窗口 = 对服务进程树执行 `taskkill /T /F`，连接即断开（托盘模式除外）。
- 单实例保护：重复双击只会把已有窗口调到前台，不会重复启动。

## 从源码构建（可选）

```bat
build-launcher.bat          rem 编译 + 询问是否创建桌面快捷方式
build-launcher.bat /shortcut
```

需要 Windows 10/11（自带 .NET Framework 4.x 的 csc.exe），无需安装任何工具链。
构建产物：`dsh-launcher.exe`（约 70 KB）。

## 高级参数（命令行）

```powershell
# 自检（不弹窗）：检查 node / dsh 是否可用、端口状态，结果写入 smoketest-out.log
dsh-launcher.exe /smoketest

# 完整自检：额外实际拉起一次 dsh web --help 验证启动链路
dsh-launcher.exe /selftest

# 本次运行的临时覆盖（不改配置文件）
dsh-launcher.exe /port 8080
dsh-launcher.exe /lan
dsh-launcher.exe /nobrowser
```

旧版 PowerShell 入口仍支持原参数：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\dsh-launcher.ps1 -Port 8080 -Lan -NoBrowser
```

## 局域网访问（可选）

`dsh web --host 0.0.0.0` 在当前版本会被 DeepSeek Harness 故意拒绝（提示会向局域网暴露远程代码执行能力）。启动器勾选「局域网(0.0.0.0)」后会用 `lan.patch.yml` 覆盖 webserver 的绑定地址，等效实现：

```powershell
$node = (Get-Command node).Source
$dsh = (Get-ChildItem "$env:LOCALAPPDATA\npm-cache\_npx\*\node_modules\@deepseek-ai\dsh\lib\bin.js" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
& $node $dsh web --patch "$PWD\lan.patch.yml" --port 3080
```

启动后状态栏会显示本机局域网地址（如 `http://192.168.0.107:3080`）。如果你用域名或其它地址访问，还需要给 `dsh web` 追加 `--trusted-host <地址>`。

> **安全提醒**：局域网模式会把 Harness（含可执行命令的能力）暴露给同一局域网内的所有设备，请只在可信网络中使用。

## 环境要求

- Windows 10 / 11（.NET Framework 4.x 系统自带）
- 已安装 Node.js
- 已安装 DeepSeek Harness：`npm install -g @deepseek-ai/dsh`（或使用过 `npx @deepseek-ai/dsh web`，会命中 npx 缓存，无需全局安装）

## 常见问题

- **提示找不到 Node.js 或 dsh**：安装 Node.js，再执行 `npm install -g @deepseek-ai/dsh`；或者先手动运行一次 `npx @deepseek-ai/dsh web` 生成 npx 缓存。
- **启动失败（进程已退出）**：看窗口里的日志，或打开 `dsh-server.err.log`。最常见原因是端口被占用（换端口或关掉占用程序）。
- **服务已启动但浏览器没打开**：手动访问窗口状态栏显示的地址，或点「打开浏览器」。
- **排查**：`launcher-debug.log` 记录了启动、spawn、关闭等关键步骤的时间线。
- **构建报错**：确认系统是 Windows 10/11；直接使用仓库里现成的 `dsh-launcher.exe` 即可，无需自行构建。

## 注意

- 关闭窗口默认**先询问**（可改为直接停止），关闭即强杀（`taskkill /F`），未保存的会话状态可能丢失；关闭前请先在网页里确认。
- 启动器与正在运行的 3080 实例共用 `%USERPROFILE%\.dsh`（profiles / sessions / storages），请勿同时用两个不同端口长时间跑两个实例做同一件事。
