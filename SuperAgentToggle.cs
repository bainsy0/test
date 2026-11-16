using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SuperAgentToggle
{
    internal static class Program
    {
        internal const string Distro = "Debian";
        internal const string Unit = "dsh-superagent.service";
        internal const string Marker = "/root/dsh-superagent/.artifacts/control/profiles/superagent/manually-stopped";
        internal const string StateDir = "/root/dsh-superagent/.artifacts/control/profiles/superagent";
        internal const string LogDir = "/root/dsh-superagent/.artifacts";

        internal static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        internal static readonly string LogFile = Path.Combine(BaseDir, "app.log");
        internal static readonly string SettingsFile = Path.Combine(BaseDir, "settings.ini");

        // Without DPI awareness Windows renders the window at 96 dpi and then stretches the
        // bitmap to the real screen scale, which is exactly what makes text look blurry on a
        // 200% display. Declaring per-monitor awareness lets WinForms draw at native pixels.
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        private static void EnableDpiAwareness()
        {
            try
            {
                // -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 (Windows 10 1703 and newer).
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        [STAThread]
        private static void Main()
        {
            EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool createdNew;
            using (var mutex = new Mutex(true, "SuperAgentToggle.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("SuperAgent 开关已经在运行了（看托盘图标）。", "SuperAgent 开关",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.Run(new MainForm());
            }
        }

        internal const long MaxLogBytes = 1024 * 1024; // Rotate app.log past 1 MB so an anomaly can't fill the disk.

        // Raised on the logging thread; the UI subscribes so file-only loggers still surface on screen.
        internal static event Action<string> Logged;

        // 普通日志只显示在窗口里，不写文件；只有异常/超时这类错误才落盘，方便排查。
        internal static void Log(string text) { LogCore(text, false); }

        internal static void LogError(string text) { LogCore(text, true); }

        private static void LogCore(string text, bool persist)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text;
            if (persist)
            {
                try
                {
                    RotateIfNeeded();
                    File.AppendAllText(LogFile, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
            }
            var handler = Logged;
            if (handler != null) { try { handler(line); } catch { } }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > MaxLogBytes)
                {
                    var bak = LogFile + ".old";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(LogFile, bak);
                }
            }
            catch { }
        }
    }

    internal enum AgentState { Off, WslOnly, Starting, Running, Broken }

    // Everything in this program is laid out in 96 dpi coordinates and stretched by the real
    // screen scale, so the controls and the text inside them grow together and stay sharp.
    internal static class Dpi
    {
        internal static readonly float Factor = Measure();

        private static float Measure()
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    var f = g.DpiX / 96f;
                    return f < 1f ? 1f : f;
                }
            }
            catch { return 1f; }
        }

        internal static int S(int v) { return (int)Math.Round(v * Factor); }

        internal static int S(float v) { return (int)Math.Round(v * Factor); }

        // Points at 96 dpi turned into real pixels: no bitmap scaling anywhere.
        internal static Font UiFont(float pt, FontStyle style = FontStyle.Regular)
        {
            return new Font("Microsoft YaHei UI", pt * 96f / 72f * Factor, style, GraphicsUnit.Pixel);
        }

        internal static Font MonoFont(float pt, FontStyle style = FontStyle.Regular)
        {
            return new Font("Consolas", pt * 96f / 72f * Factor, style, GraphicsUnit.Pixel);
        }
    }

    internal static class Wsl
    {
        internal class Result
        {
            public int ExitCode;
            public string Output = "";
            public string Error = "";
        }

        internal static Result Run(string bashScript, int timeoutMs = 60000)
        {
            var r = new Result();
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "wsl.exe"),
                Arguments = "-d " + Program.Distro + " -u root -e /bin/bash -lc " + Quote(bashScript),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        r.ExitCode = -1;
                        r.Error = "超时";
                        return r;
                    }
                    r.Output = (outTask.Result ?? "").Replace("\0", "").Trim();
                    r.Error = (errTask.Result ?? "").Replace("\0", "").Trim();
                    r.ExitCode = p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                r.ExitCode = -1;
                r.Error = ex.Message;
            }
            return r;
        }

        private static string Quote(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        internal static bool DistroRunning()
        {
            var r = RawListRunning();
            foreach (var line in r.Split('\n'))
            {
                var t = line.Replace("\0", "").Trim();
                if (t.StartsWith("*")) t = t.Substring(1).Trim();
                if (t.StartsWith(Program.Distro + " ", StringComparison.OrdinalIgnoreCase) &&
                    t.IndexOf("Running", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string RawListRunning()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "wsl.exe"),
                    Arguments = "-l -v --running",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.Unicode
                };
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        return "";
                    }
                    return outTask.Result ?? "";
                }
            }
            catch { return ""; }
        }

        internal static List<Process> Holders()
        {
            var list = new List<Process>();
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='wsl.exe'"))
                {
                    foreach (var o in searcher.Get())
                    {
                        var cmd = o["CommandLine"] as string;
                        if (cmd == null) continue;
                        if (cmd.IndexOf(HolderMarker, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var id = Convert.ToInt32(o["ProcessId"]);
                        try { list.Add(Process.GetProcessById(id)); } catch { }
                    }
                }
            }
            catch { }
            return list;
        }

        internal static bool HolderAlive()
        {
            return Holders().Count > 0;
        }

        // The keep-alive session is our lifeline: its stdin is a pipe owned by this process.
        // When the toggle exits (gracefully or killed) the pipe closes, bash sees EOF and stops
        // the service on the WSL side. That makes the toggle the only way to run SuperAgent.
        internal const string HolderMarker = "superagent-keepalive";
        private static Process _holderProcess;

        internal static void StartHolder()
        {
            if (HolderAlive()) return;
            try
            {
                var watchdog =
                    "cat > /dev/null; " +
                    "mkdir -p " + Program.StateDir + "; touch " + Program.Marker + "; " +
                    "systemctl stop " + Program.Unit + "; " +
                    "tmux kill-session -t dsh-superagent 2>/dev/null; " +
                    "pkill -f '[d]sh --profile superagent' 2>/dev/null; " +
                    "echo stopped >> " + Program.LogDir + "/toggle-watchdog.log";

                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "wsl.exe"),
                    Arguments = "-d " + Program.Distro + " -u root -e /bin/bash -c \"" + watchdog +
                                "\" " + HolderMarker,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true
                };
                var p = Process.Start(psi);
                // Keep the writer open and never dispose it: closing it would look like our death.
                var keep = p.StandardInput;
                GC.KeepAlive(keep);
                _holderProcess = p;
            }
            catch (Exception ex)
            {
                throw new Exception("无法启动 WSL 保活会话: " + ex.Message);
            }
            Thread.Sleep(400);
            if (!HolderAlive()) throw new Exception("WSL 保活会话启动后立即退出");
        }

        internal static void StopHolder()
        {
            var holders = Holders();
            foreach (var h in holders)
            {
                try { h.Kill(); } catch { }
            }
            if (holders.Count > 0) Program.Log("已结束保活会话 (" + holders.Count + " 个)");
        }

        // Remembers whether this program was the one that booted the distro, so stopping can
        // shut WSL down again when nobody else is using it.
        private static readonly string OwnedFile = Path.Combine(Program.BaseDir, "wsl-owned.ini");

        internal static void MarkOwned(bool owned)
        {
            try
            {
                if (owned) File.WriteAllText(OwnedFile, "us", new UTF8Encoding(false));
                else if (File.Exists(OwnedFile)) File.Delete(OwnedFile);
            }
            catch { }
        }

        private static bool WasOwned()
        {
            try { return File.Exists(OwnedFile); } catch { return false; }
        }

        // Used when the agent is not running but we still booted WSL: give it back only when
        // no other wsl.exe client is around.
        internal static void ReleaseOwnedDistro()
        {
            if (WasOwned()) TerminateIfUnused();
            MarkOwned(false);
        }

        // Counts wsl.exe client processes that are not our keep-alive helper. Any of them means
        // a terminal (or another tool) still holds the distro, so it must not be terminated.
        internal static int OtherWslClients()
        {
            var count = 0;
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='wsl.exe'"))
                {
                    foreach (var o in searcher.Get())
                    {
                        var cmd = o["CommandLine"] as string;
                        if (cmd != null && cmd.IndexOf(HolderMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        count++;
                    }
                }
            }
            catch { }
            return count;
        }

        private static void TerminateIfUnused()
        {
            if (!DistroRunning()) return;
            Thread.Sleep(300);
            var others = OtherWslClients();
            if (others > 0)
            {
                Program.Log("检测到 " + others + " 个其他 WSL 会话，保留 WSL 运行");
                return;
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "wsl.exe"),
                    Arguments = "--terminate " + Program.Distro,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi)) { p.WaitForExit(30000); }
                Program.Log("没有其他 WSL 会话，已关闭 WSL");
            }
            catch (Exception ex) { Program.LogError("关闭 WSL 失败: " + ex.Message); }
        }

        // systemd can refuse a manual stop outright (RefuseManualStop=yes), which is far better
        // than racing to restart the unit after somebody stopped it. The drop-in is installed
        // while SuperAgent is supposed to be up and removed again so Stop/Exit still work.
        private const string DropInDir = "/etc/systemd/system/dsh-superagent.service.d";
        private const string DropInFile = DropInDir + "/zz-toggle-guard.conf";

        internal static void SetGuard(bool on)
        {
            try
            {
                if (on)
                {
                    Run("mkdir -p " + DropInDir + "; printf '[Unit]\\nRefuseManualStop=yes\\n\\n" +
                        "[Service]\\nRestart=always\\nRestartSec=3\\n' > " + DropInFile +
                        "; systemctl daemon-reload", 30000);
                }
                else
                {
                    Run("rm -f " + DropInFile + "; systemctl daemon-reload", 30000);
                }
            }
            catch (Exception ex) { Program.LogError("调整守护设置失败: " + ex.Message); }
        }

        internal static bool GuardInstalled()
        {
            var r = Run("test -f " + DropInFile + " && echo yes || echo no", 20000);
            return r.Output.IndexOf("yes", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Stops the service, ends our keep-alive helper and kills any leftover process.
        // WSL is only shut down when we started it and nobody else is using it.
        internal static void StopAll()
        {
            // Removing the guard first is mandatory: RefuseManualStop would block the stop below.
            SetGuard(false);
            if (DistroRunning())
            {
                Run("mkdir -p " + Program.StateDir + "; touch " + Program.Marker +
                    "; systemctl stop " + Program.Unit, 90000);
                Thread.Sleep(500);
            }
            StopHolder();
            var st = GetState();
            if (st == AgentState.Running || st == AgentState.Broken)
            {
                Run("tmux kill-session -t dsh-superagent 2>/dev/null; " +
                    "pkill -f 'dsh --profile superagent' 2>/dev/null", 30000);
                Thread.Sleep(500);
            }
            if (WasOwned()) TerminateIfUnused();
            MarkOwned(false);
        }

        internal static AgentState GetState(bool assumeDistroUp = false)
        {
            if (!assumeDistroUp && !DistroRunning()) return AgentState.Off;
            // One WSL round-trip for everything: unit status, web listener, tmux session.
            // Batching avoids three extra wsl.exe spawns per state check, which is what made
            // the UI feel sluggish during start/stop polling.
            var r = Run(
                "s=$(systemctl is-active " + Program.Unit + " 2>/dev/null); " +
                "ss -ltn 2>/dev/null | grep -qE ':3082[[:space:]]' && w=yes || w=no; " +
                "tmux has-session -t dsh-superagent 2>/dev/null && t=yes || t=no; " +
                "echo \"__SA_STATE__$s|$w|$t\"", 20000);
            // Login shells may emit profile/MOTD text to stdout, so locate our own marker
            // instead of trusting the first line.
            var raw = r.Output ?? "";
            var mark = raw.IndexOf("__SA_STATE__");
            var payload = mark < 0 ? "" : raw.Substring(mark + "__SA_STATE__".Length).Split('\n')[0].Trim();
            var parts = payload.Length == 0 ? new string[0] : payload.Split('|');
            var state = parts.Length > 0 ? parts[0].Trim() : "";
            var web = parts.Length > 1 ? parts[1].Trim() : "no";
            var tmux = parts.Length > 2 ? parts[2].Trim() : "no";
            if (state == "active")
            {
                // systemd calls the unit active as long as the supervisor hangs around, but a
                // killed tmux session leaves it alive without serving anything. Only a listening
                // web port counts as healthy; otherwise report Broken so the guard repairs it.
                return web == "yes" ? AgentState.Running : AgentState.Broken;
            }
            if (state == "activating" || state == "deactivating" || state == "reloading") return AgentState.Starting;
            if (tmux == "yes") return AgentState.Broken;
            return AgentState.WslOnly;
        }

        internal static string FindWebUrl()
        {
            for (int i = 0; i < 20; i++)
            {
                var lg = Run("ls -t " + Program.LogDir + "/dsh-superagent-*.log 2>/dev/null | head -1", 20000);
                var path = lg.Output.Trim();
                if (!string.IsNullOrEmpty(path))
                {
                    var ln = Run("grep -m1 'dsh web:' " + Quote(path), 20000);
                    var m = System.Text.RegularExpressions.Regex.Match(ln.Output,
                        @"http://127\.0\.0\.1:\d+/\?token=[0-9A-Za-z_\-]+");
                    if (m.Success) return m.Value;
                }
                Thread.Sleep(1000);
            }
            return null;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Label _status = new Label();
        private readonly Label _detail = new Label();
        private readonly Button _btnToggle = new Button();
        private readonly Button _btnWeb = new Button();
        private readonly TextBox _log = new TextBox();
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private NotifyIcon _tray;
        private volatile bool _busy;
        private volatile bool _forceClose;
        private volatile bool _exiting;

        // The user asked for SuperAgent to be up: from now on the service must stay alive
        // until the user presses Stop or quits. This flag drives the watchdog timer.
        private volatile bool _desiredRunning;
        private volatile bool _guarding;
        private readonly System.Windows.Forms.Timer _guard = new System.Windows.Forms.Timer();

        internal MainForm()
        {
            Program.Logged += OnLogged;
            BuildUi();
            Load += async (s, e) =>
            {
                WriteLog("开关已加载（缩放 " + Dpi.Factor.ToString("0.##") + "x，DeviceDpi " + DeviceDpi + "）");
                await RefreshUiAsync();
            };
            FormClosing += OnFormClosing;
            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized) HideToTray(true);
            };
        }

        // Real screen scale (1.0 at 96 dpi, 2.0 at 200%). Read once, before any control exists.
        private static readonly float ScaleFactor = GetScaleFactor();

        private static float GetScaleFactor()
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                    return g.DpiX / 96f;
            }
            catch { return 1f; }
        }

        private static int S(int v) { return Dpi.S(v); }

        private static int S(float v) { return Dpi.S(v); }

        // Sizes are given in points at 96 dpi and converted to real pixels, so text keeps the
        // same apparent size on every scale factor without any bitmap stretching.
        private static Font ScaledFont(float pt, FontStyle style = FontStyle.Regular)
        {
            return Dpi.UiFont(pt, style);
        }

        private static Font ScaledMonoFont(float pt, FontStyle style = FontStyle.Regular)
        {
            return Dpi.MonoFont(pt, style);
        }

        private void BuildUi()
        {
            Text = "SuperAgent 开关";
            // Layout below is written for 96 dpi. Once the process declares DPI awareness the
            // window is measured in real pixels, so the whole layout is scaled by the real
            // factor ourselves — letting WinForms auto-scale fights with explicit SetBounds.
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Dpi.S(464), Dpi.S(372));
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = Dpi.UiFont(9f);
            Icon = LoadAppIcon();
            BackColor = Color.FromArgb(248, 249, 250);

            _status.SetBounds(Dpi.S(18), Dpi.S(12), Dpi.S(420), Dpi.S(34));
            _status.Font = Dpi.UiFont(16f, FontStyle.Bold);
            _status.Text = "检查中...";
            Controls.Add(_status);

            _detail.SetBounds(Dpi.S(20), Dpi.S(48), Dpi.S(424), Dpi.S(40));
            _detail.ForeColor = Color.DimGray;
            _detail.Text = "";
            Controls.Add(_detail);

            _btnToggle.SetBounds(Dpi.S(18), Dpi.S(96), Dpi.S(292), Dpi.S(52));
            _btnToggle.Font = Dpi.UiFont(12f, FontStyle.Bold);
            _btnToggle.Text = "启动 SuperAgent";
            _btnToggle.Click += async (s, e) => await ToggleAsync();
            Controls.Add(_btnToggle);

            _btnWeb.SetBounds(Dpi.S(324), Dpi.S(96), Dpi.S(122), Dpi.S(52));
            _btnWeb.Text = "打开网页";
            _btnWeb.Click += async (s, e) => await OpenWebAsync();
            Controls.Add(_btnWeb);

            _log.SetBounds(Dpi.S(18), Dpi.S(160), Dpi.S(428), Dpi.S(192));
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Color.White;
            _log.Font = Dpi.MonoFont(9f);
            Controls.Add(_log);

            var tip = new ToolTip();
            tip.SetToolTip(_btnToggle, "启动或停止 WSL 里的 SuperAgent 服务");
            tip.SetToolTip(_btnWeb, "在浏览器打开 SuperAgent 网页");

            BuildTray();

            _timer.Interval = 5000;
            _timer.Tick += async (s, e) =>
            {
                if (!_busy && Visible) await RefreshUiAsync();
            };
            _timer.Start();

            // Runs even while the window is hidden in the tray: that is exactly when nobody
            // would notice a stopped service.
            _guard.Interval = 4000;
            _guard.Tick += async (s, e) => await GuardTickAsync();
            _guard.Start();

            // A restart of the shell (or a leftover from a previous session) must not look
            // like a fresh "not started" state: if the service is already healthy, keep going.
            Task.Run(() =>
            {
                try
                {
                    if (Wsl.GetState() == AgentState.Running)
                    {
                        if (_exiting || _forceClose) return;
                        Wsl.SetGuard(true);
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke((Action)(() =>
                            {
                                if (_exiting || _forceClose || IsDisposed) return;
                                _desiredRunning = true;
                                WriteLog("检测到 SuperAgent 已在运行，继续守护");
                            }));
                    }
                }
                catch { }
            });
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                var p = Path.Combine(Program.BaseDir, "app.ico");
                if (File.Exists(p)) return new Icon(p);
            }
            catch { }
            try
            {
                // The icon is also embedded in the exe itself, so a lone copy still looks right.
                var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null) return exeIcon;
            }
            catch { }
            return SystemIcons.Application;
        }

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示窗口", null, (s, e) => RestoreFromTray());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("启动 SuperAgent", null, async (s, e) => await StartAgentAsync());
            menu.Items.Add("停止 SuperAgent", null, async (s, e) => await StopAgentAsync());
            menu.Items.Add("打开网页", null, async (s, e) => await OpenWebAsync());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => ExitApp());

            _tray = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "SuperAgent 开关",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, e) => RestoreFromTray();
            _tray.BalloonTipClicked += (s, e) => RestoreFromTray();
        }

        private void HideToTray(bool notify)
        {
            Hide();
            ShowInTaskbar = false;
            if (notify)
            {
                _tray.ShowBalloonTip(3000, "SuperAgent 开关",
                    "已最小化到托盘，双击图标可以再打开。", ToolTipIcon.Info);
            }
        }

        private void RestoreFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            _forceClose = true;
            // Leaving the program is an explicit stop: the watchdog must not fight it.
            _desiredRunning = false;
            _guard.Stop();
            _timer.Stop();

            var state = AgentState.Off;
            try { state = Wsl.GetState(); } catch { }

            if (state == AgentState.Running || state == AgentState.Starting || state == AgentState.Broken)
            {
                try
                {
                    // Exiting must leave nothing behind: stop the unit, end the keep-alive
                    // session, kill leftovers, and release WSL if we were the one who booted it.
                    var t = Task.Run(() => Wsl.StopAll());
                    if (!t.Wait(120000)) WriteError("停止超时，请检查 WSL 状态");
                }
                catch (Exception ex) { WriteError("停止失败: " + ex.Message); }
            }
            else if (state == AgentState.WslOnly)
            {
                Wsl.ReleaseOwnedDistro();
            }

            try { WriteStopSummary(Wsl.GetState()); }
            catch { }

            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Close();
        }

        private void WriteLog(string text)
        {
            Program.Log(text);
        }

        private void WriteError(string text)
        {
            Program.LogError(text);
        }

        private void OnLogged(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action<string>)OnLogged, line); return; }
            if (_log.TextLength > 20000) _log.Clear();
            _log.AppendText(line + Environment.NewLine);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Program.Logged -= OnLogged;
            base.Dispose(disposing);
        }

        private string RememberChoice
        {
            get
            {
                try
                {
                    if (File.Exists(Program.SettingsFile))
                    {
                        var v = File.ReadAllText(Program.SettingsFile).Trim().ToLowerInvariant();
                        if (v == "tray") return "tray";
                        if (v == "exit") return "exit";
                    }
                }
                catch { }
                return "";
            }
            set
            {
                try
                {
                    if (value == "") { if (File.Exists(Program.SettingsFile)) File.Delete(Program.SettingsFile); }
                    else File.WriteAllText(Program.SettingsFile, value, new UTF8Encoding(false));
                }
                catch { }
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_forceClose) return;
            if (e.CloseReason != CloseReason.UserClosing) return;

            e.Cancel = true;
            var saved = RememberChoice;
            if (saved == "tray") { HideToTray(true); return; }
            if (saved == "exit") { ExitApp(); return; }

            using (var dlg = new CloseChoiceDialog())
            {
                var r = dlg.ShowDialog(this);
                if (r == DialogResult.Cancel) return;
                if (dlg.Remember)
                    RememberChoice = dlg.Chosen == CloseAction.Exit ? "exit" : "tray";
                if (dlg.Chosen == CloseAction.Exit) ExitApp();
                else HideToTray(true);
            }
        }

        private async Task RefreshUiAsync()
        {
            var state = AgentState.Off;
            try { state = await Task.Run(() => Wsl.GetState()); } catch { }
            ApplyState(state, await Task.Run(() => Wsl.HolderAlive()));
        }

        private void ApplyState(AgentState state, bool holder)
        {
            switch (state)
            {
                case AgentState.Running:
                    _status.Text = "● 运行中";
                    _status.ForeColor = Color.FromArgb(0, 140, 0);
                    _btnToggle.Text = "停止 SuperAgent";
                    _btnWeb.Enabled = true;
                    break;
                case AgentState.Starting:
                    _status.Text = "● 启动中...";
                    _status.ForeColor = Color.FromArgb(200, 120, 0);
                    _btnToggle.Text = "停止 SuperAgent";
                    _btnWeb.Enabled = false;
                    break;
                case AgentState.Broken:
                    _status.Text = "● 进程在跑但服务不健康";
                    _status.ForeColor = Color.FromArgb(200, 0, 0);
                    _btnToggle.Text = "停止 SuperAgent";
                    _btnWeb.Enabled = false;
                    break;
                case AgentState.WslOnly:
                    _status.Text = "● WSL 已开，服务未运行";
                    _status.ForeColor = Color.FromArgb(200, 120, 0);
                    _btnToggle.Text = "启动 SuperAgent";
                    _btnWeb.Enabled = false;
                    break;
                default:
                    _status.Text = "○ 已停止";
                    _status.ForeColor = Color.DimGray;
                    _btnToggle.Text = "启动 SuperAgent";
                    _btnWeb.Enabled = false;
                    break;
            }
            _detail.Text = "分发版 " + Program.Distro + "   服务 " + Program.Unit +
                           "   保活会话：" + (holder ? "有" : "无");
            _btnToggle.Enabled = !_busy;
            _tray.Text = ("SuperAgent 开关 - " + _status.Text.Replace("● ", "").Replace("○ ", "")).Substring(0,
                Math.Min(63, ("SuperAgent 开关 - " + _status.Text.Replace("● ", "").Replace("○ ", "")).Length));
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _btnToggle.Enabled = !busy;
            _btnWeb.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        // Watchdog: while the user wants SuperAgent up, any stop of the unit (or a dead tmux
        // session) is repaired within a few seconds, no matter how it happened.
        private async Task GuardTickAsync()
        {
            if (!_desiredRunning || _guarding || _busy || _exiting || _forceClose) return;
            _guarding = true;
            try
            {
                var broken = await Task.Run(() =>
                {
                    var st = Wsl.GetState();
                    if (st == AgentState.Running || st == AgentState.Starting) return false;
                    // Off means WSL itself is gone, WslOnly means the unit stopped, Broken means
                    // the supervisor process is around but unhealthy: all three need repair.
                    return true;
                });
                if (!broken) return;

                WriteLog("检测到 SuperAgent 已停止，正在自动重新拉起...");
                var ok = await Task.Run(() =>
                {
                    if (!Wsl.DistroRunning()) Wsl.MarkOwned(true);
                    if (!Wsl.HolderAlive()) Wsl.StartHolder();
                    // RefuseManualStop=yes also rejects manual start/restart, so step aside first.
                    Wsl.SetGuard(false);
                    Wsl.Run("rm -f " + Program.Marker + "; systemctl daemon-reload; " +
                            "systemctl reset-failed " + Program.Unit + " 2>/dev/null; " +
                            "systemctl restart " + Program.Unit + " 2>/dev/null || " +
                            "systemctl start " + Program.Unit, 90000);
                    for (int i = 0; i < 40; i++)
                    {
                        Thread.Sleep(i < 20 ? 500 : 1500);
                        // Stop/Exit may arrive mid-repair; honour it before doing anything else.
                        if (!_desiredRunning || _exiting || _forceClose) return false;
                        if (Wsl.GetState(true) == AgentState.Running)
                        {
                            // Re-check: re-arming the guard after a Stop would pin the service up.
                            if (_desiredRunning && !_exiting && !_forceClose) Wsl.SetGuard(true);
                            return true;
                        }
                    }
                    return false;
                });
                WriteLog(ok ? "SuperAgent 已自动恢复" : "自动恢复未成功，将继续重试");
                if (Visible) await RefreshUiAsync();
            }
            catch (Exception ex) { WriteError("自动恢复出错: " + ex.Message); }
            finally { _guarding = false; }
        }

        private async Task ToggleAsync()
        {
            if (_busy) return;
            AgentState state;
            SetBusy(true);
            try { state = await Task.Run(() => Wsl.GetState()); }
            finally { SetBusy(false); }

            if (state == AgentState.Running || state == AgentState.Starting || state == AgentState.Broken)
                await StopAgentAsync();
            else
                await StartAgentAsync();
        }

        private async Task StartAgentAsync()
        {
            if (_busy) return;
            SetBusy(true);
            // Asking for a start means "keep it up": if this attempt fails, the guard retries.
            _desiredRunning = true;
            WriteLog("正在启动 SuperAgent...");
            var ok = false;
            try
            {
                ok = await Task.Run(() =>
                {
                    // Only a WSL we boot ourselves is ours to shut down later. If it is already
                    // up it belongs to the user; drop any stale flag left by a hard kill so
                    // exiting never terminates a session we did not start.
                    if (!Wsl.DistroRunning()) Wsl.MarkOwned(true);
                    else Wsl.MarkOwned(false);
                    Wsl.StartHolder();
                    // A previous session may have left the guard armed; it would refuse the start.
                    Wsl.SetGuard(false);
                    Wsl.Run("rm -f " + Program.Marker + "; systemctl daemon-reload; " +
                            "systemctl reset-failed " + Program.Unit + " 2>/dev/null; " +
                            "systemctl start " + Program.Unit, 90000);
                    for (int i = 0; i < 60; i++)
                    {
                        // Poll fast at first (the service usually comes up in a few seconds),
                        // then back off so a cold WSL boot still has time to finish.
                        Thread.Sleep(i < 20 ? 500 : 1500);
                        if (Wsl.GetState(true) == AgentState.Running)
                        {
                            // Arm the hard guard only once the unit is healthy.
                            Wsl.SetGuard(true);
                            return true;
                        }
                    }
                    return false;
                });
            }
            catch (Exception ex) { WriteError("启动失败: " + ex.Message); }

            if (ok)
            {
                _desiredRunning = true;
                WriteLog("SuperAgent 已就绪");
                if (!Visible) _tray.ShowBalloonTip(3000, "SuperAgent 已启动", "网页已可打开。", ToolTipIcon.Info);
            }
            else
            {
                WriteError("启动超时，请查看日志 /root/dsh-superagent/.artifacts/supervisor.log");
            }
            SetBusy(false);
            await RefreshUiAsync();
        }

        private async Task StopAgentAsync()
        {
            if (_busy) return;
            SetBusy(true);
            _desiredRunning = false;
            WriteLog("正在停止 SuperAgent...");
            var after = AgentState.Off;
            try
            {
                await Task.Run(() => Wsl.StopAll());
                after = await Task.Run(() => Wsl.GetState());
            }
            catch (Exception ex) { WriteError("停止失败: " + ex.Message); }
            finally { SetBusy(false); }
            WriteStopSummary(after);
            if (after == AgentState.Running || after == AgentState.Broken)
                WriteError("服务未能完全停止，请重试");
            await RefreshUiAsync();
        }

        private void WriteStopSummary(AgentState after)
        {
            if (after == AgentState.Off)
                WriteLog("已停止，WSL 也一并关闭了");
            else
                WriteLog("已停止，WSL 仍在使用中，保持运行");
        }

        private async Task OpenWebAsync()
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                var url = await Task.Run(() => Wsl.FindWebUrl());
                if (string.IsNullOrEmpty(url)) WriteError("找不到网页地址（服务未就绪或无 token）");
                else Process.Start(url);
            }
            catch (Exception ex) { WriteError("打开网页失败: " + ex.Message); }
            finally { SetBusy(false); }
        }
    }

    internal enum CloseAction { Tray, Exit }

    internal sealed class CloseChoiceDialog : Form
    {
        internal CloseAction Chosen = CloseAction.Tray;
        internal bool Remember;

        internal CloseChoiceDialog()
        {
            Text = "关闭 SuperAgent 开关";
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Dpi.S(372), Dpi.S(196));
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = Dpi.UiFont(9f);
            BackColor = Color.FromArgb(248, 249, 250);

            var lbl = new Label
            {
                Text = "关闭窗口后，你希望怎么处理？",
                Font = Dpi.UiFont(11f, FontStyle.Bold)
            };
            lbl.Bounds = new Rectangle(Dpi.S(20), Dpi.S(18), Dpi.S(340), Dpi.S(28));
            Controls.Add(lbl);

            var sub = new Label
            {
                Text = "最小化到托盘后服务继续运行，随时双击图标回来。",
                ForeColor = Color.DimGray
            };
            sub.Bounds = new Rectangle(Dpi.S(22), Dpi.S(48), Dpi.S(340), Dpi.S(24));
            Controls.Add(sub);

            var btnTray = new Button { Text = "最小化到托盘", Bounds = new Rectangle(Dpi.S(20), Dpi.S(82), Dpi.S(160), Dpi.S(44)) };
            btnTray.Font = Dpi.UiFont(10f, FontStyle.Bold);
            btnTray.Click += (s, e) => { Chosen = CloseAction.Tray; Remember = chk.Checked; DialogResult = DialogResult.OK; };
            Controls.Add(btnTray);

            var btnExit = new Button { Text = "直接退出", Bounds = new Rectangle(Dpi.S(192), Dpi.S(82), Dpi.S(160), Dpi.S(44)) };
            btnExit.Click += (s, e) => { Chosen = CloseAction.Exit; Remember = chk.Checked; DialogResult = DialogResult.OK; };
            Controls.Add(btnExit);

            chk = new CheckBox { Text = "记住我的选择（可在托盘右键菜单改）", Bounds = new Rectangle(Dpi.S(22), Dpi.S(136), Dpi.S(340), Dpi.S(24)) };
            Controls.Add(chk);

            var btnCancel = new Button { Text = "取消", Bounds = new Rectangle(Dpi.S(272), Dpi.S(160), Dpi.S(80), Dpi.S(26)), DialogResult = DialogResult.Cancel };
            Controls.Add(btnCancel);
            CancelButton = btnCancel;
            AcceptButton = btnTray;
        }

        private CheckBox chk;
    }
}
