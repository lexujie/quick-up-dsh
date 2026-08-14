# DeepSeek Harness 快速启动器（Windows）

> GitHub: <https://github.com/lexujie/quick-up-dsh>

一键启动 DeepSeek Harness 网页版（`dsh web`），**关闭启动器窗口即自动停止服务并断开连接**。

## 快速开始

```sh
git clone https://github.com/lexujie/quick-up-dsh.git
cd quick-up-dsh
# 双击 launch-dsh.vbs（或 launch-dsh.bat）即可
```

## 文件说明

| 文件 | 用途 |
|---|---|
| `launch-dsh.vbs` | **推荐入口**：双击启动，无黑色控制台窗口 |
| `launch-dsh.bat` | 备用入口：双击启动，显示控制台 |
| `dsh-launcher.ps1` | 核心逻辑（启动服务 / 打开浏览器 / 关闭时自动断开） |
| `create-desktop-shortcut.bat` | 双击后在桌面创建「DeepSeek Harness」快捷方式 |

## 使用方法

1. 把本文件夹放到你希望作为工作目录的位置（`dsh web` 的 workspace 根目录 = 启动器所在目录，会话文件都建在这里）。
2. 双击 `launch-dsh.vbs`（或 `launch-dsh.bat`）。
3. 启动器窗口出现，服务就绪后自动打开浏览器 `http://127.0.0.1:3080`。
4. 需要重启服务时（例如换配置、清空内存状态），点窗口里的「**重启服务**」按钮：先停掉旧服务进程，再拉起新服务，窗口与浏览器保持可用。
5. 用完直接**关闭启动器窗口** → 服务进程树会被自动终止（`taskkill /T /F`），连接断开，不留后台残留；也可以点「停止并退出」按钮，效果相同。

> 如果端口 3080 已被占用（说明已有一个 DSH 实例在运行），启动器只会打开浏览器，不会重复启动，也不会关闭那个实例。

## 桌面快捷方式（可选）

双击 `create-desktop-shortcut.bat`，桌面会出现「DeepSeek Harness」快捷方式，之后点它即可启动。

## 工作原理

- 直接调用 `node <dsh 的 bin.js> web --port 3080`，跳过 npx 的网络检查，启动更快。
- 启动器会先探测端口：**已在运行 → 只开浏览器**；未运行 → 拉起服务并等待端口就绪（最多 180 秒，首次初始化 profile 可能较慢）。
- 「重启服务」= 对旧进程树执行 `taskkill /T /F` 后重新拉起，并重置日志与计时。
- 服务输出重定向到 `dsh-server.log` / `dsh-server.err.log`（与启动器同目录），并实时显示在窗口里。
- 关闭窗口时对 cmd 进程树执行 `taskkill /T /F`，node 服务随之终止，连接即断开。

## 高级参数

```powershell
# 换端口启动（例如 8080）
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\dsh-launcher.ps1 -Port 8080

# 不自动打开浏览器
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\dsh-launcher.ps1 -NoBrowser

# 自检（不弹窗）：检查 node / dsh 是否可用、端口状态
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\dsh-launcher.ps1 -SmokeTest

# 完整自检：额外实际拉起一次 dsh web --help 验证启动链路
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\dsh-launcher.ps1 -SelfTest
```

## 环境要求

- Windows 10 / 11
- 已安装 Node.js
- 已安装 DeepSeek Harness：`npm install -g @deepseek-ai/dsh`（或使用过 `npx @deepseek-ai/dsh web`，会命中 npx 缓存，无需全局安装）

## 常见问题

- **提示找不到 Node.js 或 dsh**：安装 Node.js，再执行 `npm install -g @deepseek-ai/dsh`；或者先手动运行一次 `npx @deepseek-ai/dsh web` 生成 npx 缓存。
- **启动失败（进程已退出）**：看窗口里的日志，或打开 `dsh-server.err.log`。最常见原因是端口被占用（换 `-Port` 或关掉占用程序）。
- **服务已启动但浏览器没打开**：手动访问窗口状态栏显示的地址。
- **排查**：`launcher-debug.log` 记录了启动、spawn、窗口关闭等关键步骤的时间线。

## 注意

- 关闭启动器窗口是**立即强杀**（`taskkill /F`），未保存的会话状态可能丢失；关闭前请先在网页里确认。
- 启动器与正在运行的 3080 实例共用 `%USERPROFILE%\.dsh`（profiles / sessions / storages），请勿同时用两个不同端口长时间跑两个实例做同一件事。
