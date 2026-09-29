using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// ============================================================================
//  DSH Launcher — DeepSeek Harness 系统托盘启动器 (v1.1.0 结构重构版)
//  v1.1: 从单文件 static 拆分为多文件（行为零变化，纯结构）:
//    LauncherConfig.cs  - 配置与路径族
//    LauncherLog.cs     - 日志设施
//    LauncherCore.cs    - 探测/进程治理/启停/UI/自启/一次性/报告
//    ModeGate.cs        - 模式状态机（v1.2 救援模式接入点，本版占位）
//    本文件              - Main 入口 + 命令分发表 + 托盘装配 + 管道
//  模式:
//    (无参数)             托盘模式: DSH 未运行则自动清理并启动
//    --open               托盘模式 + 打开界面(健康直接开, 异常清理重启)
//    --start [--noopen]   一次性: 清理 -> 启动 -> 打开界面
//    --restart [--noopen] 一次性: 同 --start
//    --stop               一次性: 停止 DSH (弹窗确认)
//    --status / --selftest  诊断报告 -> logs\status.txt / logs\selftest.txt
//    --port N             覆盖端口 (默认 3080)
//    --help               帮助
// ============================================================================
namespace DshLauncher
{
    internal static class Program
    {
        private const string PipeNameBase = "dsh-launcher-pipe";

        private static NotifyIcon _icon;
        private static bool _lastUp;
        private static Process _serviceProc;   // 保持引用, 保证 stdout/stderr 事件持续收日志
        private static readonly List<string> _commandQueue = new List<string>();
        private static readonly object QueueLock = new object();

        // ---------------- 服务进程引用跟踪 ----------------

        /// <summary>接管一次性启动的进程引用（保持 stdout/stderr 事件流存活）。</summary>
        public static void TrackServiceProcess(Process proc)
        {
            if (_serviceProc != null)
            {
                try { _serviceProc.Dispose(); } catch { }
            }
            _serviceProc = proc;
        }

        // ---------------- 托盘 ----------------

        private static void ShowBalloon(string title, string text)
        {
            try
            {
                if (_icon == null) return;
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = text;
                _icon.ShowBalloonTip(4000);
            }
            catch { }
        }

        private static void EnsureRunningAndOpenUi()
        {
            LauncherLog.Write("命令: 打开界面");
            if (LauncherCore.IsDshHealthy())
            {
                LauncherLog.Write("DSH 正常运行, 直接打开界面");
                LauncherCore.OpenUi();
                return;
            }
            LauncherLog.Write("DSH 未运行或异常, 清理并重新启动");
            LauncherCore.StopDsh();
            Process proc = LauncherCore.StartDsh();
            if (proc != null)
            {
                TrackServiceProcess(proc);
                _lastUp = true;
                ShowBalloon("DSH 已启动", LauncherConfig.Url);
                LauncherCore.OpenUi();
            }
            else ShowBalloon("DSH 启动失败", "请右键图标 -> 查看日志");
        }

        private static void RestartDsh()
        {
            LauncherLog.Write("命令: 重启 DSH (清理所有旧进程)");
            LauncherCore.StopDsh();
            Process proc = LauncherCore.StartDsh();
            if (proc != null)
            {
                TrackServiceProcess(proc);
                _lastUp = true;
                ShowBalloon("DSH 已重启", LauncherConfig.Url);
                LauncherCore.OpenUi();
            }
            else ShowBalloon("DSH 重启失败", "请右键图标 -> 查看日志");
        }

        private static void StopDshTray()
        {
            LauncherCore.StopDsh();
            ShowBalloon("DSH 已停止", "服务已停止, 托盘仍在运行");
        }

        private static void ProcessQueuedCommands()
        {
            List<string> cmds = null;
            lock (QueueLock)
            {
                if (_commandQueue.Count > 0)
                {
                    cmds = new List<string>(_commandQueue);
                    _commandQueue.Clear();
                }
            }
            if (cmds == null) return;
            // 连续重复命令折叠 (连点两次只开一个界面)
            List<string> final = new List<string>();
            foreach (string c in cmds)
                if (final.Count == 0 || final[final.Count - 1] != c) final.Add(c);
            foreach (string c in final)
            {
                if (c == "open") EnsureRunningAndOpenUi();
                else if (c == "restart") RestartDsh();
                else if (c == "stop") StopDshTray();
            }
        }

        private static void PipeServerLoop()
        {
            string name = PipeNameBase + "-" + Environment.UserName;
            while (true)
            {
                try
                {
                    // InOut: 收到命令后回写 ACK, 让 SendCommand 能确认"命令真的被接收"
                    // (修 2026-09-03: 旧版只读, 连上僵尸句柄也算成功, fallback 无法触发)
                    using (NamedPipeServerStream server = new NamedPipeServerStream(name, PipeDirection.InOut, 1))
                    {
                        server.WaitForConnection();
                        using (StreamReader reader = new StreamReader(server, Encoding.UTF8))
                        using (StreamWriter writer = new StreamWriter(server, Encoding.UTF8))
                        {
                            string cmd = reader.ReadLine();
                            if (!string.IsNullOrEmpty(cmd))
                            {
                                LauncherLog.Write("收到命令: " + cmd);
                                lock (QueueLock) _commandQueue.Add(cmd);
                            }
                            try
                            {
                                writer.WriteLine("ACK");
                                writer.Flush();
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LauncherLog.Write("管道服务异常: " + ex.Message);
                    Thread.Sleep(1000);
                }
            }
        }

        private static bool SendCommand(string cmd)
        {
            string name = PipeNameBase + "-" + Environment.UserName;
            // 超时收紧 (修 2026-09-03 #2): 本机命名管道正常毫秒级连上, 旧 3s×5≈17.5s 的失败重试
            // 让 fallback 触发延迟过长, 撞上调用方超时窗口。压到 800ms×3≈3.9s, fallback 秒级触达。
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    // InOut + 等 ACK: 只有对方确认收到才算成功
                    // (修 2026-09-03: 旧版只写不等回执, 连上僵尸管道句柄也返回 true,
                    //  命令被丢弃且 fallback 分支永远进不去)
                    using (NamedPipeClientStream client = new NamedPipeClientStream(".", name, PipeDirection.InOut))
                    {
                        client.Connect(800);
                        using (StreamWriter writer = new StreamWriter(client, Encoding.UTF8))
                        using (StreamReader reader = new StreamReader(client, Encoding.UTF8))
                        {
                            writer.WriteLine(cmd);
                            writer.Flush();
                            // 读 ACK, 带超时保护(ReadToEnd 会阻塞到对端关连接, 用 Read 逐字节+超时)
                            string ack = ReadLineWithTimeout(reader, 2000);
                            if (ack == "ACK") return true;
                            LauncherLog.Write("SendCommand 未收到 ACK (cmd=" + cmd + ", 收到: " + (ack ?? "null") + ")");
                        }
                    }
                }
                catch { Thread.Sleep(500); }
            }
            return false;
        }

        /// <summary>从管道读一行, 带毫秒超时; 超时返回 null。避免 ReadLine 无限阻塞。</summary>
        private static string ReadLineWithTimeout(StreamReader reader, int timeoutMs)
        {
            try
            {
                reader.BaseStream.ReadTimeout = timeoutMs;
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    int ch = reader.Read();   // ReadTimeout 触发时抛 IOException
                    if (ch < 0) return sb.Length > 0 ? sb.ToString() : null;   // 流结束
                    if (ch == '\n') return sb.ToString().TrimEnd('\r');
                    sb.Append((char)ch);
                }
            }
            catch (IOException) { return null; }   // 读超时
            catch { return null; }
        }

        // ---------------- 托盘主循环 ----------------

        private static void RunTray(bool openUiOnReady)
        {
            _icon = new NotifyIcon();
            try { _icon.Icon = Icon.ExtractAssociatedIcon(typeof(Program).Assembly.Location); }
            catch { _icon.Icon = SystemIcons.Application; }
            _icon.Visible = true;
            _icon.Text = "DSH Launcher";

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开 DSH 界面", null, delegate { EnsureRunningAndOpenUi(); });
            menu.Items.Add("重启 DSH（清理旧进程）", null, delegate { RestartDsh(); });
            menu.Items.Add("叫小D来修（界面打不开时）", null, delegate { LauncherCore.OpenAskXiaoD(); });
            menu.Items.Add(new ToolStripSeparator());
            // —— v1.2 救援模式（设计见 v2-救援模式设计-20260929.md）——
            ToolStripMenuItem rescue = new ToolStripMenuItem("救援（DSH 出问题时用这里）");
            rescue.DropDownItems.Add("收集现场（诊断简报，只读）", null, delegate { RescueMode.CollectBrief(); });
            rescue.DropDownItems.Add("救援建议（读最近简报）", null, delegate { RescueMode.Diagnose(); });
            rescue.DropDownItems.Add("救援验收（8 项清单）", null, delegate { RescueMode.Verify(); });
            rescue.DropDownItems.Add("体检：农场链接（只读）", null, delegate { RescueMode.ScanFarm(); });
            rescue.DropDownItems.Add("体检：可用快照 / 补丁状态", null, delegate { RescueMode.ListSnapshots(); });
            rescue.DropDownItems.Add("补丁状态（dry-run）", null, delegate { RescueMode.ReapplyPatches(); });
            rescue.DropDownItems.Add(new ToolStripSeparator());
            rescue.DropDownItems.Add("进入安全模式（跳过用户插件）", null, delegate { RescueMode.EnterSafeMode(); });
            rescue.DropDownItems.Add("退出安全模式（回正常 3080）", null, delegate { RescueMode.ExitSafeMode(); });
            menu.Items.Add(rescue);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("停止 DSH", null, delegate { StopDshTray(); });
            menu.Items.Add("查看日志", null, delegate { try { Process.Start(LauncherConfig.LogDir); } catch { } });
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem auto = new ToolStripMenuItem("开机自启");
            auto.Checked = LauncherCore.AutoStartEnabled();
            auto.Click += delegate { LauncherCore.ToggleAutoStart(); auto.Checked = LauncherCore.AutoStartEnabled(); };
            menu.Items.Add(auto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出（停止 DSH 并退出）", null, delegate
            {
                DialogResult r = MessageBox.Show("确定停止 DSH 服务并退出启动器吗？\n\n以后想再启动, 双击桌面「DeepSeek Harness」图标即可。",
                    "DSH Launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes)
                {
                    LauncherCore.StopDsh();
                    Application.Exit();
                }
            });
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += delegate { EnsureRunningAndOpenUi(); };
            _icon.BalloonTipIcon = ToolTipIcon.Info;

            Thread pipeThread = new Thread(PipeServerLoop);
            pipeThread.IsBackground = true;
            pipeThread.Start();

            _lastUp = LauncherCore.IsDshHealthy();
            if (_lastUp)
            {
                LauncherLog.Write("托盘启动: DSH 已在运行 " + LauncherConfig.Url);
                _icon.Text = "DSH 运行中 " + LauncherConfig.Url;
                if (openUiOnReady) LauncherCore.OpenUi();
            }
            else
            {
                LauncherLog.Write("托盘启动: DSH 未运行, 清理并自动启动 ...");
                ShowBalloon("DSH 未运行", "正在清理旧进程并自动启动 ...");
                LauncherCore.StopDsh();
                Process proc = LauncherCore.StartDsh();
                _lastUp = proc != null;
                if (proc != null)
                {
                    TrackServiceProcess(proc);
                    _icon.Text = "DSH 运行中 " + LauncherConfig.Url;
                    ShowBalloon("DSH 已启动", LauncherConfig.Url);
                    if (openUiOnReady) LauncherCore.OpenUi();
                }
                else
                {
                    _icon.Text = "DSH 启动失败";
                    ShowBalloon("DSH 启动失败", "请右键图标 -> 查看日志");
                }
            }

            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 3000;
            timer.Tick += delegate
            {
                ProcessQueuedCommands();
                // 快检: 只看端口, 避免每 3 秒下载整页 HTML
                bool up = LauncherCore.IsDshListening();
                if (up && !_lastUp)
                {
                    _lastUp = true;
                    _icon.Text = "DSH 运行中 " + LauncherConfig.Url;
                    ShowBalloon("DSH 已启动", LauncherConfig.Url);
                    LauncherLog.Write("检测到 DSH 已运行");
                }
                else if (!up && _lastUp)
                {
                    _lastUp = false;
                    _icon.Text = "DSH 未运行";
                    ShowBalloon("DSH 意外退出", "可右键图标 -> 重启 DSH; 日志已保存");
                    LauncherLog.Write("检测到 DSH 停止运行");
                }
                if (_serviceProc != null && _serviceProc.HasExited)
                {
                    try { _serviceProc.Dispose(); } catch { }
                    _serviceProc = null;
                }
            };
            timer.Start();

            Application.Run();

            timer.Stop();
            _icon.Visible = false;
            _icon.Dispose();
        }

        // ---------------- 入口 ----------------

        private class Options
        {
            public string Mode = "";
            public bool OpenUi;
            public bool NoOpen;
        }

        /// <summary>
        /// 命令分发表：加新命令 = 注册一行 + 实现一个方法，不碰既有分发逻辑。
        /// v1.2 将加入: --rescue / --exit-rescue（经 ModeGate 互斥检查）。
        /// 注意: 目标编译器为 .NET Framework 4.0 csc，勿用 C# 6+ 语法（索引初始化器/插值字符串）。
        /// </summary>
        private static readonly Dictionary<string, Action<Options>> Commands = BuildCommandTable();

        private static Dictionary<string, Action<Options>> BuildCommandTable()
        {
            Dictionary<string, Action<Options>> t =
                new Dictionary<string, Action<Options>>(StringComparer.OrdinalIgnoreCase);
            t.Add("--help", o => LauncherCore.WriteHelp());
            t.Add("-h", o => LauncherCore.WriteHelp());
            t.Add("--status", o => LauncherCore.WriteStatusFile());
            t.Add("--selftest", o => LauncherCore.WriteSelfTestFile());
            t.Add("--stop", o => LauncherCore.OneShotStop());
            t.Add("--start", o => LauncherCore.OneShotBoot(o.NoOpen));
            t.Add("--restart", o => LauncherCore.OneShotBoot(o.NoOpen));
            t.Add("--ask", o => LauncherCore.OpenAskXiaoD());
            return t;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                try { LauncherLog.Write("未处理异常: " + e.Exception); } catch { }
                try
                {
                    MessageBox.Show("DSH Launcher 发生内部错误: " + e.Exception.Message + "\n\n详见日志: " + LauncherConfig.LauncherLog,
                        "DSH Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try { LauncherLog.Write("未处理异常(进程级): " + e.ExceptionObject); } catch { }
            };

            Options opt = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--open") opt.OpenUi = true;
                else if (a == "--noopen") opt.NoOpen = true;
                else if (a == "--port" && i + 1 < args.Length)
                {
                    int p;
                    if (int.TryParse(args[i + 1], out p) && p > 0 && p < 65536) LauncherConfig.Port = p;
                }
                else if (a.StartsWith("--") && opt.Mode == "") opt.Mode = a;
            }

            // ---- v1.2 救援模式命令行入口（与托盘菜单一一对应）----
            // ★ 必须放在 mutex 判断**之前**：救援命令的用意正是"任何情况下都能跑"。
            //   2026-09-29 实测踩到：放进 if (!created) 块里之后，没有托盘在跑时它会掉进"新建托盘"分支 ——
            //   日志显示 模式=--rescue-verify 紧接着 托盘启动: DSH 已在运行，救援动作根本没执行。
            if (opt.Mode == "--rescue-brief")     { RescueMode.CliBrief(); return; }
            if (opt.Mode == "--rescue-diagnose")  { RescueMode.CliDiagnose(); return; }
            if (opt.Mode == "--rescue-verify")    { RescueMode.CliVerify(); return; }
            if (opt.Mode == "--rescue-scan")      { RescueMode.CliScanFarm(); return; }
            if (opt.Mode == "--rescue-snapshots") { RescueMode.CliSnapshots(); return; }
            if (opt.Mode == "--rescue-patches")   { RescueMode.CliPatches(); return; }
            if (opt.Mode == "--rescue-enter")     { RescueMode.CliEnterSafe(); return; }
            if (opt.Mode == "--rescue-exit")      { RescueMode.CliExitSafe(); return; }

            bool created;
            Mutex mutex = new Mutex(true, "Local\\DSHLauncher-" + Environment.UserName, out created);
            try
            {
                LauncherLog.Write("启动器启动 模式=" + (opt.Mode == "" ? "托盘" : opt.Mode) + " 端口=" + LauncherConfig.Port +
                    " 参数=" + string.Join(" ", args));

                if (!created)
                {
                    // 已有实例占着 mutex (托盘常驻 或 一次性 --start/--restart 正在跑)
                    // 状态类命令本进程直跑; 动作类命令优先转发给托盘
                    if (opt.Mode == "--status") { LauncherCore.WriteStatusFile(); return; }
                    if (opt.Mode == "--selftest") { LauncherCore.WriteSelfTestFile(); return; }
                    // 急救入口与托盘的管道无关：直接在本进程弹新控制台窗口，不必转发
                    if (opt.Mode == "--ask") { LauncherCore.OpenAskXiaoD(); return; }

                    string cmd = "open";
                    if (opt.Mode == "--restart") cmd = "restart";
                    else if (opt.Mode == "--stop") cmd = "stop";
                    LauncherLog.Write("已有实例在运行, 转发命令: " + cmd);
                    if (SendCommand(cmd))
                    {
                        LauncherLog.Write("命令已转发给运行中的托盘实例");
                        return;
                    }

                    // 转发失败 = 占 mutex 的不是托盘(一次性模式无管道服务), 或托盘尚未就绪。
                    // fallback: 本地直接执行动作命令 (一次性模式期间的 --stop/--restart 场景)。
                    // 竞态说明: mutex 持有者若是一次性 --start, 它正在等就绪(最长120s)。
                    //   --stop   本地清理进程树, 一次性进程会察觉子进程退出而结束 -> 安全。
                    //   --restart 本地 stop+start; 若一次性进程已把 3080 拉起, stop 会清掉它再起新。
                    //   --open   无托盘可转发 = 托盘确实没起来, 本进程直接接管进托盘。
                    if (opt.Mode == "--stop")
                    {
                        LauncherLog.Write("转发失败, 无托盘实例 -> 本地直接停止 DSH");
                        LauncherCore.OneShotStop();
                        return;
                    }
                    if (opt.Mode == "--restart" || opt.Mode == "--start")
                    {
                        LauncherLog.Write("转发失败, 无托盘实例 -> 本地直接清理并启动");
                        LauncherCore.OneShotBoot(opt.NoOpen);
                        return;
                    }
                    // --open / 无参数且 mutex 被占: 等托盘自己就绪, 本进程退出 (原行为)
                    LauncherLog.Write("转发失败 (托盘实例可能尚未就绪)");
                    return;
                }

                // 命令分发表: 一次性命令直接执行; 未知/空则进托盘
                Action<Options> handler;
                if (opt.Mode.Length > 0 && Commands.TryGetValue(opt.Mode, out handler))
                {
                    handler(opt);
                    return;
                }
                RunTray(opt.OpenUi);
            }
            finally
            {
                // 仅当本实例创建并持有 mutex 时才能释放, 否则 ReleaseMutex 抛异常
                if (created)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                try { mutex.Dispose(); } catch { }
            }
        }
    }
}
