// ============================================================================
//  DeepSeek Harness 启动器（桌面应用版）— dsh-launcher.exe
// ----------------------------------------------------------------------------
//  双击 dsh-launcher.exe 即可：
//    1. 快速启动 dsh web 服务（直接调用 node，跳过 npx 的网络检查）
//    2. 服务就绪后自动打开浏览器 http://127.0.0.1:<port>
//    3. 窗口内可「打开浏览器」「重启服务」「停止并退出」
//    4. 关闭窗口的行为可选：每次询问 / 直接停止 / 最小化到托盘
//    5. 设置（端口 / 局域网 / 自动开浏览器 / 关闭行为）自动保存到
//       launcher-config.json（与 exe 同目录，删除即恢复默认）
//
//  编译：双击 build-launcher.bat（需要 Windows 自带的 .NET Framework 4.x）
//
//  命令行参数（主要用于自检/脚本）：
//    /smoketest          自检（不弹窗），结果写入 smoketest-out.log
//    /selftest           完整自检（额外拉起一次 dsh web --help 验证链路）
//    /port <n>           本次运行的端口覆盖
//    /lan                本次以局域网模式运行
//    /nobrowser          本次不自动打开浏览器
//
//  说明：
//    - dsh web 的 workspace 根目录 = 本 exe 所在目录（会话文件建在那里）。
//    - 服务输出实时显示在窗口里，并镜像到 dsh-server.log / dsh-server.err.log。
//    - 端口已被占用（已有 DSH 实例在运行）时，本程序只打开浏览器，不重复启动。
//    - 关闭窗口默认会询问；选「直接停止」则立即 taskkill 整个进程树。
// ============================================================================
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace DshLauncher
{
    // ------------------------------------------------------------------ 原生 API
    internal static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern bool FreeConsole();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }

    // ------------------------------------------------------------------ 工具函数
    internal static class Utils
    {
        // 端口连通性探测（带超时）
        public static bool PortOpen(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult iar = client.BeginConnect(host, port, null, null);
                    if (!iar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    client.EndConnect(iar);
                    return true;
                }
            }
            catch { return false; }
        }

        public static string QuoteArg(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        // 在 PATH 上找可执行文件（返回第一个存在的完整路径）
        public static string FindOnPath(string name)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "where.exe"), name);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    string line = p.StandardOutput.ReadLine();
                    if (!p.WaitForExit(1500)) { try { p.Kill(); } catch { } }
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        line = line.Trim();
                        if (File.Exists(line)) return line;
                    }
                }
            }
            catch { }
            return null;
        }

        // 用 taskkill /T /F 杀掉整个进程树
        public static void KillTree(Process p)
        {
            if (p == null) return;
            try { if (p.HasExited) return; } catch { return; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                    string.Format("/PID {0} /T /F", p.Id));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process k = Process.Start(psi))
                {
                    if (!k.WaitForExit(4000)) { try { k.Kill(); } catch { } }
                }
            }
            catch { }
            try { p.WaitForExit(3000); } catch { }
        }

        // 取第一个非回环 IPv4（局域网模式显示用）
        public static string FirstLanIPv4()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                            return ua.Address.ToString();
                    }
                }
            }
            catch { }
            return null;
        }
    }

    // ------------------------------------------------------------------ dsh 入口
    internal class DshEntry
    {
        public string Kind;   // node | cmd | ps1 | npx
        public string Path;
    }

    // ------------------------------------------------------------------ 配置
    internal class Config
    {
        public int Port = 3080;
        public bool Lan = false;
        public bool AutoOpenBrowser = true;
        public string CloseAction = "ask";   // ask | stop | tray

        public string FilePath;

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("//")) continue;
                    Match m = Regex.Match(line, "\"([^\"]+)\"\\s*:\\s*(.+)");
                    if (!m.Success) continue;
                    string k = m.Groups[1].Value;
                    string v = m.Groups[2].Value.Trim().TrimEnd(',').Trim();
                    try
                    {
                        if (k == "port") { int p; if (int.TryParse(v, out p)) Port = p; }
                        else if (k == "lan") { bool b; if (bool.TryParse(v, out b)) Lan = b; }
                        else if (k == "autoOpenBrowser") { bool b; if (bool.TryParse(v, out b)) AutoOpenBrowser = b; }
                        else if (k == "closeAction") CloseAction = v.Trim('"');
                    }
                    catch { }
                }
                if (Port < 1024 || Port > 65535) Port = 3080;
                if (CloseAction != "stop" && CloseAction != "tray") CloseAction = "ask";
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"port\": " + Port + ",");
                sb.AppendLine("  \"lan\": " + (Lan ? "true" : "false") + ",");
                sb.AppendLine("  \"autoOpenBrowser\": " + (AutoOpenBrowser ? "true" : "false") + ",");
                sb.AppendLine("  \"closeAction\": \"" + CloseAction + "\"");
                sb.AppendLine("}");
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    // ============================================================================
    //  主程序
    // ============================================================================
    internal static class Program
    {
        private const string MutexName = "Local\\DSH_Launcher_SingleInstance_v1";
        internal const int StartTimeoutSec = 180;

        [STAThread]
        private static int Main(string[] args)
        {
            bool smoke = false, selfTest = false, noBrowser = false, lanOv = false, lanSet = false;
            int? portOv = null;

            foreach (string a in args)
            {
                string t = a.TrimStart('/', '-').ToLowerInvariant();
                if (t == "smoketest") smoke = true;
                else if (t == "selftest") selfTest = true;
                else if (t == "nobrowser") noBrowser = true;
                else if (t == "lan") { lanOv = true; lanSet = true; }
                else if (t.StartsWith("port"))
                {
                    string v = t.Substring(4).TrimStart('=', ' ', ':');
                    int p;
                    if (int.TryParse(v, out p) && p > 0 && p < 65536) portOv = p;
                }
            }

            if (smoke || selfTest)
                return RunConsoleTests(smoke, selfTest, portOv, lanSet && lanOv);

            Config cfg = new Config();
            cfg.FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher-config.json");
            cfg.Load();
            if (portOv.HasValue) cfg.Port = portOv.Value;
            if (lanSet) cfg.Lan = lanOv;
            bool noBr = noBrowser || !cfg.AutoOpenBrowser;

            // 单实例：重复双击时激活已有窗口
            bool createdNew;
            using (Mutex m = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    IntPtr h = Native.FindWindowW(null, "DeepSeek Harness 启动器");
                    if (h != IntPtr.Zero)
                    {
                        Native.ShowWindow(h, 9);   // SW_RESTORE
                        Native.SetForegroundWindow(h);
                    }
                    return 0;
                }
                return RunApp(cfg, noBr);
            }
        }

        // ------------------------------------------------------------ 常规 GUI
        private static int RunApp(Config cfg, bool noBr)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string patch = Path.Combine(baseDir, "lan.patch.yml");
            string node = ResolveNode();
            DshEntry entry = ResolveDshEntry();

            if (node == null || entry == null)
            {
                MessageBox.Show(
                    "未找到 Node.js 或 dsh 入口。" + Environment.NewLine + Environment.NewLine +
                    "请先安装 Node.js，然后运行：" + Environment.NewLine +
                    "npm install -g @deepseek-ai/dsh" + Environment.NewLine +
                    "（或先手动运行一次 npx @deepseek-ai/dsh web）",
                    "DeepSeek Harness 启动器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            // 端口已被占用 → 已有实例在运行，只打开浏览器，不接管
            if (Utils.PortOpen("127.0.0.1", cfg.Port, 600))
            {
                if (!noBr) OpenBrowser("http://127.0.0.1:" + cfg.Port);
                return 0;
            }

            LauncherForm form = new LauncherForm(cfg, node, entry, patch, baseDir, noBr);
            Application.Run(form);
            return 0;
        }

        public static void OpenBrowser(string url)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        // ------------------------------------------------------------ 路径解析
        private static string ResolveNode()
        {
            string s = Utils.FindOnPath("node");
            if (s != null) return s;
            string[] cands = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "node", "node.exe")
            };
            foreach (string c in cands) { if (File.Exists(c)) return c; }
            return null;
        }

        private static DshEntry ResolveDshEntry()
        {
            // 1) npx 缓存里的 dsh（node 直启，最快）
            string npxCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "npm-cache", "_npx");
            if (Directory.Exists(npxCache))
            {
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string dir in Directory.GetDirectories(npxCache))
                {
                    string f = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                    if (File.Exists(f))
                    {
                        DateTime t = File.GetLastWriteTime(f);
                        if (t > bestTime) { bestTime = t; best = f; }
                    }
                }
                if (best != null)
                {
                    DshEntry e = new DshEntry();
                    e.Kind = "node";
                    e.Path = best;
                    return e;
                }
            }
            // 2) PATH 上的 dsh（npm 全局安装）
            string dsh = Utils.FindOnPath("dsh");
            if (dsh != null)
            {
                DshEntry e = new DshEntry();
                e.Kind = dsh.ToLowerInvariant().EndsWith(".ps1") ? "ps1" : "cmd";
                e.Path = dsh;
                return e;
            }
            // 3) npm 全局 root
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("npm.cmd", "root -g");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string line = p.StandardOutput.ReadLine();
                    if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } }
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        string f = Path.Combine(line.Trim(), "@deepseek-ai", "dsh", "lib", "bin.js");
                        if (File.Exists(f))
                        {
                            DshEntry e = new DshEntry();
                            e.Kind = "node";
                            e.Path = f;
                            return e;
                        }
                    }
                }
            }
            catch { }
            // 4) npx 兜底
            string npx = Utils.FindOnPath("npx");
            if (npx != null)
            {
                DshEntry e = new DshEntry();
                e.Kind = "npx";
                e.Path = npx;
                return e;
            }
            return null;
        }

        // 构造实际执行的命令行（用于日志/自检展示）
        private static string BuildInner(string node, DshEntry entry, string cmdArgs)
        {
            switch (entry.Kind)
            {
                case "node": return Utils.QuoteArg(node) + " " + Utils.QuoteArg(entry.Path) + " web " + cmdArgs;
                case "ps1":  return "powershell.exe -NoProfile -ExecutionPolicy Bypass -File " + Utils.QuoteArg(entry.Path) + " web " + cmdArgs;
                case "cmd":  return Utils.QuoteArg(entry.Path) + " web " + cmdArgs;
                default:     return "npx --no-install @deepseek-ai/dsh web " + cmdArgs;
            }
        }

        // ------------------------------------------------------------ 自检模式
        private static int RunConsoleTests(bool smoke, bool selfTest, int? portOv, bool lan)
        {
            Native.AttachConsole(-1);
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            StringBuilder sb = new StringBuilder();

            int port = portOv.HasValue ? portOv.Value : 3080;
            string node = ResolveNode();
            DshEntry entry = ResolveDshEntry();
            string patch = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lan.patch.yml");
            string home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (string.IsNullOrEmpty(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

            sb.AppendLine("node        = " + (node ?? "NOT FOUND"));
            sb.AppendLine("dsh entry   = " + (entry == null ? "NOT FOUND" : entry.Kind + ":" + entry.Path));
            sb.AppendLine("dsh_home    = " + home);
            sb.AppendLine("port        = " + port + " busy=" + Utils.PortOpen("127.0.0.1", port, 800));
            if (node != null && entry != null)
            {
                string args = "--port " + port;
                if (lan && File.Exists(patch)) args = "--patch " + Utils.QuoteArg(patch) + " " + args;
                sb.AppendLine("cmd line    = " + BuildInner(node, entry, args));
            }
            sb.AppendLine("status = " + (node == null || entry == null ? "MISSING node or dsh" : "OK"));

            int code = 0;
            if (selfTest && node != null && entry != null)
            {
                string args = "--port " + port + " --help";
                if (lan && File.Exists(patch)) args = "--patch " + Utils.QuoteArg(patch) + " " + args;
                string inner = BuildInner(node, entry, args);
                sb.AppendLine("selftest    = running: " + inner);
                string outF = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-out.log");
                string errF = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-err.log");
                try { File.Delete(outF); File.Delete(errF); } catch { }
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c \"" + inner + "\"");
                psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                string so = "", se = "";
                try
                {
                    using (Process p = Process.Start(psi))
                    {
                        so = p.StandardOutput.ReadToEnd();
                        se = p.StandardError.ReadToEnd();
                        if (!p.WaitForExit(60000))
                        {
                            try { p.Kill(); } catch { }
                            sb.AppendLine("selftest    = TIMEOUT");
                            code = 3;
                        }
                        else
                        {
                            sb.AppendLine("selftest    = exitcode " + p.ExitCode);
                            if (p.ExitCode != 0) code = 4;
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("selftest    = error: " + ex.Message);
                    code = 5;
                }
                try { File.WriteAllText(outF, so, Encoding.UTF8); File.WriteAllText(errF, se, Encoding.UTF8); } catch { }
                sb.AppendLine("--- stdout (tail) ---");
                sb.AppendLine(Tail(so, 20));
                sb.AppendLine("--- stderr (tail) ---");
                sb.AppendLine(Tail(se, 20));
            }

            if (node == null || entry == null) code = 2;
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoketest-out.log"), sb.ToString(), Encoding.UTF8); } catch { }
            try { Console.Write(sb.ToString()); } catch { }
            Native.FreeConsole();
            return code;
        }

        private static string Tail(string text, int n)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int start = Math.Max(0, lines.Length - n);
            StringBuilder sb = new StringBuilder();
            for (int i = start; i < lines.Length; i++) sb.AppendLine(lines[i]);
            return sb.ToString();
        }
    }

    // ============================================================================
    //  主窗口
    // ============================================================================
    internal class LauncherForm : Form
    {
        private readonly Config _cfg;
        private readonly string _node;
        private readonly string _entryPath;
        private readonly string _entryKind;
        private readonly string _patch;
        private readonly string _baseDir;
        private readonly bool _noBrowserOverride;
        private readonly string _outLog;
        private readonly string _errLog;
        private readonly string _debugLog;

        private Process _proc;
        private System.Windows.Forms.Timer _timer;
        private bool _opened;
        private bool _exiting;
        private bool _loading;
        private DateTime _startTime;
        private readonly object _fileLock = new object();
        private StreamWriter _outWriter;
        private StreamWriter _errWriter;

        private Label _status;
        private TextBox _log;
        private NumericUpDown _nudPort;
        private CheckBox _chkLan;
        private CheckBox _chkAuto;
        private ComboBox _cmbClose;
        private Button _btnOpen;
        private Button _btnRestart;
        private Button _btnStop;
        private Button _btnApply;
        private NotifyIcon _tray;

        public LauncherForm(Config cfg, string node, DshEntry entry, string patch, string baseDir, bool noBrowserOverride)
        {
            _cfg = cfg;
            _node = node;
            _entryPath = entry.Path;
            _entryKind = entry.Kind;
            _patch = patch;
            _baseDir = baseDir;
            _noBrowserOverride = noBrowserOverride;
            _outLog = Path.Combine(baseDir, "dsh-server.log");
            _errLog = Path.Combine(baseDir, "dsh-server.err.log");
            _debugLog = Path.Combine(baseDir, "launcher-debug.log");

            BuildUI();
            BuildTray();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 500;
            _timer.Tick += OnTick;

            FormClosing += OnFormClosing;
            DebugLog("GUI start, port=" + cfg.Port + ", lan=" + cfg.Lan + ", url=http://127.0.0.1:" + cfg.Port);

            StartServer();
        }

        // ------------------------------------------------------------ UI
        private void BuildUI()
        {
            Text = "DeepSeek Harness 启动器";
            ClientSize = new Size(780, 560);
            MinimumSize = new Size(700, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _status = new Label();
            _status.SetBounds(12, 10, 756, 20);
            _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _status.AutoEllipsis = true;
            SetStatus("正在启动 DeepSeek Harness ...", Color.DimGray);

            _log = new TextBox();
            _log.SetBounds(12, 36, 756, 404);
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.WordWrap = false;
            _log.Font = new Font("Consolas", 9F);

            Label lblPort = new Label();
            lblPort.Text = "端口";
            lblPort.SetBounds(12, 490, 36, 20);
            lblPort.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            _nudPort = new NumericUpDown();
            _nudPort.SetBounds(48, 486, 74, 23);
            _nudPort.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _nudPort.Minimum = 1024;
            _nudPort.Maximum = 65535;
            _nudPort.Increment = 1;
            _nudPort.Value = Math.Max(1024, _cfg.Port);

            _chkLan = new CheckBox();
            _chkLan.Text = "局域网(0.0.0.0)";
            _chkLan.SetBounds(132, 488, 122, 18);
            _chkLan.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _chkLan.Checked = _cfg.Lan;

            _chkAuto = new CheckBox();
            _chkAuto.Text = "自动打开浏览器";
            _chkAuto.SetBounds(262, 488, 124, 18);
            _chkAuto.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _chkAuto.Checked = _cfg.AutoOpenBrowser;

            Label lblClose = new Label();
            lblClose.Text = "关闭窗口时";
            lblClose.SetBounds(394, 490, 66, 20);
            lblClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            _cmbClose = new ComboBox();
            _cmbClose.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbClose.SetBounds(460, 486, 116, 21);
            _cmbClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _cmbClose.Items.Add("每次询问");
            _cmbClose.Items.Add("直接停止");
            _cmbClose.Items.Add("最小化到托盘");
            _cmbClose.SelectedIndex = _cfg.CloseAction == "stop" ? 1 : (_cfg.CloseAction == "tray" ? 2 : 0);

            _btnApply = new Button();
            _btnApply.Text = "应用并重启";
            _btnApply.SetBounds(584, 484, 88, 26);
            _btnApply.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            ToolTip tip = new ToolTip();
            tip.SetToolTip(_btnApply, "端口 / 局域网等修改后，点这里重启服务生效");

            _btnOpen = new Button();
            _btnOpen.Text = "打开浏览器";
            _btnOpen.SetBounds(430, 522, 100, 28);
            _btnOpen.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            _btnRestart = new Button();
            _btnRestart.Text = "重启服务";
            _btnRestart.SetBounds(538, 522, 100, 28);
            _btnRestart.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            _btnStop = new Button();
            _btnStop.Text = "停止并退出";
            _btnStop.SetBounds(646, 522, 122, 28);
            _btnStop.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            Controls.Add(_status);
            Controls.Add(_log);
            Controls.Add(lblPort);
            Controls.Add(_nudPort);
            Controls.Add(_chkLan);
            Controls.Add(_chkAuto);
            Controls.Add(lblClose);
            Controls.Add(_cmbClose);
            Controls.Add(_btnApply);
            Controls.Add(_btnOpen);
            Controls.Add(_btnRestart);
            Controls.Add(_btnStop);

            _btnOpen.Click += delegate { Program.OpenBrowser("http://127.0.0.1:" + _cfg.Port); };
            _btnRestart.Click += delegate { RestartServer(); };
            _btnStop.Click += delegate { StopAndExit(); };
            _btnApply.Click += delegate { ApplySettings(); };

            _loading = true;
            _nudPort.Value = Math.Max(1024, _cfg.Port);
            _chkLan.Checked = _cfg.Lan;
            _chkAuto.Checked = _cfg.AutoOpenBrowser;
            _cmbClose.SelectedIndex = _cfg.CloseAction == "stop" ? 1 : (_cfg.CloseAction == "tray" ? 2 : 0);
            _loading = false;

            _nudPort.ValueChanged += delegate { if (!_loading) { _cfg.Port = (int)_nudPort.Value; _cfg.Save(); } };
            _chkLan.CheckedChanged += delegate { if (!_loading) { _cfg.Lan = _chkLan.Checked; _cfg.Save(); } };
            _chkAuto.CheckedChanged += delegate { if (!_loading) { _cfg.AutoOpenBrowser = _chkAuto.Checked; _cfg.Save(); } };
            _cmbClose.SelectedIndexChanged += delegate
            {
                if (_loading) return;
                _cfg.CloseAction = _cmbClose.SelectedIndex == 1 ? "stop" : (_cmbClose.SelectedIndex == 2 ? "tray" : "ask");
                _cfg.Save();
            };
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            try { _tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            _tray.Text = "DeepSeek Harness 启动器";
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开浏览器", null, delegate { Program.OpenBrowser("http://127.0.0.1:" + _cfg.Port); });
            menu.Items.Add("重启服务", null, delegate { RestartServer(); });
            menu.Items.Add("显示主窗口", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("停止并退出", null, delegate { StopAndExit(); });
            _tray.ContextMenuStrip = menu;

            _tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
        }

        // ------------------------------------------------------------ 状态/日志
        private void SetStatus(string text, Color color)
        {
            if (_status.InvokeRequired)
            {
                _status.BeginInvoke(new Action<string, Color>(SetStatus), text, color);
                return;
            }
            _status.Text = text;
            _status.ForeColor = color;
        }

        private void AppendLog(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (_log.IsDisposed) return;
            if (_log.InvokeRequired)
            {
                try { _log.BeginInvoke(new Action<string>(AppendLog), s); } catch { }
                return;
            }
            try
            {
                _log.AppendText(s + Environment.NewLine);
                if (_log.TextLength > 400000) _log.Text = _log.Text.Substring(_log.TextLength - 300000);
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
            }
            catch { }
        }

        private void DebugLog(string msg)
        {
            try
            {
                File.AppendAllText(_debugLog,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private void OnServerOutput(string text, StreamWriter w)
        {
            if (w != null)
            {
                lock (_fileLock)
                {
                    try { w.WriteLine(text); w.Flush(); } catch { }
                }
            }
            AppendLog(text);
        }

        private void CloseLogWriters()
        {
            lock (_fileLock)
            {
                try { if (_outWriter != null) { _outWriter.Dispose(); _outWriter = null; } } catch { }
                try { if (_errWriter != null) { _errWriter.Dispose(); _errWriter = null; } } catch { }
            }
        }

        // ------------------------------------------------------------ 服务控制
        private void StartServer()
        {
            _opened = false;
            _startTime = DateTime.Now;
            CloseLogWriters();
            try { File.Delete(_outLog); File.Delete(_errLog); } catch { }
            try { _outWriter = new StreamWriter(_outLog, false, new UTF8Encoding(false)); _outWriter.AutoFlush = true; } catch { }
            try { _errWriter = new StreamWriter(_errLog, false, new UTF8Encoding(false)); _errWriter.AutoFlush = true; } catch { }

            string cmdArgs = "--port " + _cfg.Port;
            bool usePatch = _cfg.Lan && File.Exists(_patch);
            if (usePatch) cmdArgs = "--patch " + Utils.QuoteArg(_patch) + " " + cmdArgs;

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.WorkingDirectory = _baseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DSH_HOME")))
                psi.EnvironmentVariables["DSH_HOME"] = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

            string inner;
            if (_entryKind == "node")
            {
                psi.FileName = _node;
                psi.Arguments = Utils.QuoteArg(_entryPath) + " web " + cmdArgs;
                inner = Utils.QuoteArg(_node) + " " + Utils.QuoteArg(_entryPath) + " web " + cmdArgs;
            }
            else
            {
                psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                if (_entryKind == "ps1")
                    inner = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File " + Utils.QuoteArg(_entryPath) + " web " + cmdArgs;
                else if (_entryKind == "cmd")
                    inner = Utils.QuoteArg(_entryPath) + " web " + cmdArgs;
                else
                    inner = "npx --no-install @deepseek-ai/dsh web " + cmdArgs;
                psi.Arguments = "/d /c \"" + inner + "\"";
            }

            DebugLog("spawn: " + inner);
            try
            {
                _proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                DebugLog("spawn failed: " + ex.Message);
                SetStatus("启动失败：无法拉起进程", Color.Red);
                AppendLog("启动失败：" + ex.Message);
                return;
            }
            DebugLog("spawned pid=" + _proc.Id);
            _proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { OnServerOutput(e.Data, _outWriter); };
            _proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { OnServerOutput(e.Data, _errWriter); };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            _timer.Start();
            SetStatus("正在启动服务（0s / " + Program.StartTimeoutSec + "s）...", Color.DimGray);
        }

        private void KillCurrent()
        {
            Process p = _proc;
            _proc = null;
            _timer.Stop();
            if (p != null)
            {
                DebugLog("killing pid=" + p.Id);
                Utils.KillTree(p);
            }
            CloseLogWriters();
        }

        private void RestartServer()
        {
            _btnRestart.Enabled = false;
            _btnStop.Enabled = false;
            _btnApply.Enabled = false;
            try
            {
                AppendLog("===== 重启服务 =====");
                SetStatus("正在停止旧服务...", Color.DimGray);
                KillCurrent();
                SetStatus("正在重启服务...", Color.DimGray);
                StartServer();
            }
            catch (Exception ex)
            {
                SetStatus("重启失败：" + ex.Message, Color.Red);
                DebugLog("restart error: " + ex.Message);
            }
            finally
            {
                _btnRestart.Enabled = true;
                _btnStop.Enabled = true;
                _btnApply.Enabled = true;
            }
        }

        private void ApplySettings()
        {
            _cfg.Port = (int)_nudPort.Value;
            _cfg.Lan = _chkLan.Checked;
            _cfg.AutoOpenBrowser = _chkAuto.Checked;
            _cfg.CloseAction = _cmbClose.SelectedIndex == 1 ? "stop" : (_cmbClose.SelectedIndex == 2 ? "tray" : "ask");
            _cfg.Save();
            if (_cfg.Lan && !File.Exists(_patch))
            {
                MessageBox.Show(this,
                    "未找到 lan.patch.yml，局域网模式不可用，已关闭该选项。",
                    "DeepSeek Harness 启动器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _chkLan.Checked = false;
                return;
            }
            RestartServer();
        }

        private void StopAndExit()
        {
            _exiting = true;
            _timer.Stop();
            DebugLog("stop & exit");
            KillCurrent();
            Close();
        }

        // ------------------------------------------------------------ 定时监控
        private void OnTick(object sender, EventArgs e)
        {
            Process p = _proc;
            if (p == null) return;
            try
            {
                if (p.HasExited)
                {
                    _timer.Stop();
                    if (_opened)
                        SetStatus("服务已停止。可点击「重启服务」重新拉起。", Color.DimGray);
                    else
                        SetStatus("启动失败（进程已退出），请查看下方日志。", Color.Red);
                }
                else if (!_opened)
                {
                    if (Utils.PortOpen("127.0.0.1", _cfg.Port, 400))
                    {
                        _opened = true;
                        string url = "http://127.0.0.1:" + _cfg.Port;
                        string ip = _cfg.Lan ? Utils.FirstLanIPv4() : null;
                        if (ip != null) SetStatus("已启动：" + url + "  （局域网 http://" + ip + ":" + _cfg.Port + "）", Color.FromArgb(26, 127, 55));
                        else SetStatus("已启动：" + url, Color.FromArgb(26, 127, 55));
                        if (!_noBrowserOverride && _cfg.AutoOpenBrowser)
                            Program.OpenBrowser(url);
                    }
                    else
                    {
                        int elapsed = (int)(DateTime.Now - _startTime).TotalSeconds;
                        if (elapsed > Program.StartTimeoutSec)
                        {
                            _timer.Stop();
                            SetStatus("启动超时（超过 " + Program.StartTimeoutSec + " 秒），请查看下方日志。", Color.Red);
                        }
                        else
                        {
                            SetStatus("正在启动服务（" + elapsed + "s / " + Program.StartTimeoutSec + "s）...", Color.DimGray);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog("tick error: " + ex.Message);
                _timer.Stop();
            }
        }

        // ------------------------------------------------------------ 关闭逻辑
        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_exiting)
            {
                FinishCleanup();
                return;
            }

            string act = _cfg.CloseAction;
            if (act == "tray")
            {
                e.Cancel = true;
                Hide();
                _tray.Visible = true;
                _tray.ShowBalloonTip(3000, "DeepSeek Harness 启动器",
                    "服务继续运行中。双击托盘图标可恢复窗口。", ToolTipIcon.Info);
                return;
            }

            if (act == "ask" && _proc != null && !_proc.HasExited)
            {
                DialogResult r = MessageBox.Show(this,
                    "关闭窗口将停止 DSH 服务并断开连接。" + Environment.NewLine + Environment.NewLine +
                    "确定要退出吗？",
                    "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
            }

            DebugLog("form closing (action=" + act + ")");
            _timer.Stop();
            KillCurrent();
            FinishCleanup();
        }

        private void FinishCleanup()
        {
            try { _tray.Visible = false; } catch { }
            try { _tray.Dispose(); } catch { }
            try { _timer.Dispose(); } catch { }
            _cfg.Save();
            DebugLog("exited");
        }
    }
}
