using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// ============================================================================
//  DSH Launcher — DeepSeek Harness 系统托盘启动器 (v1.0.0)
//  功能: 托盘常驻; 双击秒开界面; 异常/未运行时自动清理旧进程并重启; 全程日志可排障。
//  模式:
//    (无参数)             托盘模式: DSH 未运行则自动清理并启动
//    --open               托盘模式 + 打开界面(健康直接开, 异常清理重启)
//    --start [--noopen]   一次性: 清理 -> 启动 -> 打开界面
//    --restart [--noopen] 一次性: 同 --start
//    --stop               一次性: 停止 DSH (弹窗确认)
//    --status / --selftest  诊断报告 -> logs\status.txt / logs\selftest.txt
//    --port N             覆盖端口 (默认 3080)
//    --help               帮助
//  配置文件: <数据目录>\dsh-launcher.conf   (port=... / dsh_home=...)
// ============================================================================
namespace DshLauncher
{
    internal static class Program
    {
        private const int DefaultPort = 3080;
        private const string PipeNameBase = "dsh-launcher-pipe";
        private const string PwaAppId = "hgiemfgfjhalibdoboikeiepnnjapnpc";

        private static readonly string ExeDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
        private static readonly string DataDir = ResolveDataDir();
        private static readonly string LogDir = Path.Combine(DataDir, "logs");
        private static readonly string LauncherLog = Path.Combine(LogDir, "launcher.log");
        private static readonly string StatusFile = Path.Combine(LogDir, "status.txt");
        private static readonly string SelfTestFile = Path.Combine(LogDir, "selftest.txt");
        private static readonly object LogLock = new object();
        private static readonly object QueueLock = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private static int Port = DefaultPort;
        private static string ConfigDshHome;
        private static NotifyIcon _icon;
        private static bool _lastUp;
        private static Process _serviceProc;   // 保持引用, 保证 stdout/stderr 事件持续收日志
        private static List<string> _commandQueue = new List<string>();

        static Program()
        {
            LoadConfig();
        }

        // ---------------- 目录与配置 ----------------

        private static string ResolveDataDir()
        {
            string exe = ExeDir;
            try
            {
                string probe = Path.Combine(exe, ".dshlauncher-write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return exe;
            }
            catch
            {
                string alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSHLauncher");
                try { Directory.CreateDirectory(alt); return alt; }
                catch { return exe; }
            }
        }

        private static void LoadConfig()
        {
            try
            {
                string conf = Path.Combine(DataDir, "dsh-launcher.conf");
                if (!File.Exists(conf)) return;
                foreach (string raw in File.ReadAllLines(conf, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim().Trim('"');
                    if (key == "port")
                    {
                        int p;
                        if (int.TryParse(val, out p) && p > 0 && p < 65536) Port = p;
                    }
                    else if (key == "dsh_home")
                    {
                        ConfigDshHome = val;
                    }
                }
            }
            catch { }
        }

        private static string NodeExe
        {
            get
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
                if (File.Exists(p)) return p;
                p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe");
                if (File.Exists(p)) return p;
                string env = Environment.GetEnvironmentVariable("PATH");
                if (env != null)
                {
                    foreach (string dir in env.Split(';'))
                    {
                        string c = Path.Combine(dir.Trim('"'), "node.exe");
                        if (File.Exists(c)) return c;
                    }
                }
                return "node.exe";
            }
        }

        private static string DshHome
        {
            get
            {
                if (!string.IsNullOrEmpty(ConfigDshHome)) return ConfigDshHome;
                string h = Environment.GetEnvironmentVariable("DSH_HOME");
                if (!string.IsNullOrEmpty(h)) return h;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            }
        }

        private static string DshBin
        {
            get { return Path.Combine(DshHome, "profiles", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"); }
        }

        private static string Url { get { return "http://127.0.0.1:" + Port; } }
        private static string PidFile { get { return Path.Combine(DataDir, "dsh-web-" + Port + ".pid"); } }
        private static string StdoutLog { get { return Path.Combine(LogDir, "dsh-web-" + Port + ".stdout.log"); } }
        private static string StderrLog { get { return Path.Combine(LogDir, "dsh-web-" + Port + ".stderr.log"); } }
        private static string ConfigFile { get { return Path.Combine(DataDir, "dsh-launcher.conf"); } }

        private static string ChromeProxyPath
        {
            get
            {
                string[] candidates = new string[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome_proxy.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome_proxy.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome_proxy.exe")
                };
                foreach (string c in candidates)
                    if (File.Exists(c)) return c;
                return null;
            }
        }

        private static string PwaDataDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Google", "Chrome", "User Data", "Default", "Web Applications", "_crx_" + PwaAppId);
            }
        }

        // ---------------- 日志 ----------------

        private static void Log(string msg)
        {
            lock (LogLock)
            {
                try
                {
                    Directory.CreateDirectory(LogDir);
                    FileInfo fi = new FileInfo(LauncherLog);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = LauncherLog + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LauncherLog, old);
                    }
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
                    File.AppendAllText(LauncherLog, line + Environment.NewLine, Utf8NoBom);
                }
                catch { }
            }
        }

        private static void AppendServiceLog(string path, string text)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(LogDir);
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 8 * 1024 * 1024)
                    {
                        string prev = path + ".prev";
                        if (File.Exists(prev)) File.Delete(prev);
                        File.Move(path, prev);
                    }
                    File.AppendAllText(path, text + Environment.NewLine, Utf8NoBom);
                }
            }
            catch { }
        }

        private static string ReadTail(string path, int lines)
        {
            try
            {
                if (!File.Exists(path)) return "(暂无日志)";
                List<string> all = new List<string>(File.ReadAllLines(path, Encoding.UTF8));
                if (all.Count <= lines) return string.Join(Environment.NewLine, all);
                return string.Join(Environment.NewLine, all.GetRange(all.Count - lines, lines));
            }
            catch (Exception ex) { return "(读取日志失败: " + ex.Message + ")"; }
        }

        // ---------------- 探测 ----------------

        private static bool TcpListening(int port)
        {
            try
            {
                IPEndPoint[] listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                foreach (IPEndPoint l in listeners)
                    if (l.Port == port) return true;
            }
            catch { }
            return false;
        }

        private static bool HttpMarkerOk()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Url);
                req.Timeout = 5000;
                req.ReadWriteTimeout = 5000;
                req.UserAgent = "DSHLauncher/1.0";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return false;
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string body = sr.ReadToEnd();
                        return body.IndexOf("__DSH_BOOT__", StringComparison.Ordinal) >= 0;
                    }
                }
            }
            catch { return false; }
        }

        // 快检: 仅看端口监听, 供 3 秒一次的托盘监视器用 (避免频繁下载整页 HTML)
        private static bool IsDshListening()
        {
            return TcpListening(Port);
        }

        // 全检: 端口 + HTTP 特征标记, 供用户动作与启动轮询用
        private static bool IsDshHealthy()
        {
            if (!TcpListening(Port)) return false;
            return HttpMarkerOk();
        }

        private static int PortFromCmdLine(string cmd)
        {
            Match m = Regex.Match(cmd, @"--port[=\s]+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int p;
                if (int.TryParse(m.Groups[1].Value, out p)) return p;
            }
            return DefaultPort;
        }

        // WMI: 找出命令行匹配 dsh 且端口一致的所有 node 进程 ("清理旧的一切进程"的核心)
        private static List<int> FindDshNodePids()
        {
            List<int> pids = new List<int>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'node.exe'"))
                {
                    foreach (ManagementBaseObject o in searcher.Get())
                    {
                        try
                        {
                            object cl = o["CommandLine"];
                            if (cl == null) continue;
                            string cmd = cl.ToString();
                            if (cmd.IndexOf("deepseek-ai\\dsh", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (PortFromCmdLine(cmd) == Port) pids.Add(Convert.ToInt32(o["ProcessId"]));
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log("WMI 枚举失败: " + ex.Message); }
            return pids;
        }

        // netstat: 找出监听目标端口的 PID (兜底, 防漏网)
        private static List<int> FindPortPids()
        {
            List<int> pids = new List<int>();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netstat.exe", "-ano");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    string needle = ":" + Port + " ";
                    foreach (string line in output.Split('\n'))
                    {
                        if (line.IndexOf(needle, StringComparison.Ordinal) < 0) continue;
                        if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 5)
                        {
                            int pid;
                            if (int.TryParse(parts[4], out pid)) pids.Add(pid);
                        }
                    }
                }
            }
            catch (Exception ex) { Log("netstat 枚举失败: " + ex.Message); }
            return pids;
        }

        // ---------------- 清理与启动 ----------------

        private static bool IsAlive(int pid)
        {
            try { using (Process p = Process.GetProcessById(pid)) { return true; } }
            catch { return false; }
        }

        private static void KillPid(int pid, string why)
        {
            if (!IsAlive(pid))
            {
                Log("  PID " + pid + " 已不存在 (跳过: " + why + ")");
                return;
            }
            try
            {
                using (Process p = Process.GetProcessById(pid))
                    Log("  清理: PID " + pid + " (" + p.ProcessName + ") - " + why);
            }
            catch { }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process k = Process.Start(psi))
                {
                    string o = k.StandardOutput.ReadToEnd();
                    string e = k.StandardError.ReadToEnd();
                    k.WaitForExit(15000);
                    Log("  taskkill " + pid + ": " + (o + e).Trim());
                }
            }
            catch (Exception ex)
            {
                Log("  taskkill " + pid + " 异常: " + ex.Message);
                try { using (Process p = Process.GetProcessById(pid)) p.Kill(); } catch { }
            }
        }

        private static void SweepKill()
        {
            HashSet<int> killed = new HashSet<int>();
            if (File.Exists(PidFile))
            {
                try
                {
                    string s = File.ReadAllText(PidFile).Trim();
                    int pid;
                    if (int.TryParse(s, out pid))
                    {
                        Log("  pid 文件: " + PidFile + " -> " + pid);
                        KillPid(pid, "pid 文件记录");
                        killed.Add(pid);
                    }
                }
                catch { }
            }
            List<int> wmi = FindDshNodePids();
            foreach (int pid in wmi)
            {
                if (!killed.Contains(pid)) { KillPid(pid, "命令行匹配 dsh (端口 " + Port + ")"); killed.Add(pid); }
            }
            List<int> port = FindPortPids();
            foreach (int pid in port)
            {
                if (!killed.Contains(pid)) { KillPid(pid, "监听端口 " + Port); killed.Add(pid); }
            }
            if (killed.Count == 0) Log("  未发现需要清理的 DSH 进程");
        }

        private static bool WaitPortRelease(int tries, int delayMs)
        {
            for (int i = 0; i < tries; i++)
            {
                if (!TcpListening(Port)) return true;
                Thread.Sleep(delayMs);
            }
            return !TcpListening(Port);
        }

        private static bool StopDsh()
        {
            Log("== 停止 DSH (端口 " + Port + ") ==");
            SweepKill();
            bool clean = WaitPortRelease(20, 500);
            if (!clean)
            {
                Log("  端口仍被占用, 追加一轮清理");
                SweepKill();
                clean = WaitPortRelease(20, 500);
            }
            try { if (File.Exists(PidFile)) File.Delete(PidFile); } catch { }
            if (_serviceProc != null)
            {
                try { _serviceProc.Dispose(); } catch { }
                _serviceProc = null;
            }
            Log("== 停止完成, 端口已释放: " + clean + " ==");
            return clean;
        }

        private static void RotateServiceLog(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    string prev = path + ".prev";
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(path, prev);
                }
            }
            catch { }
        }

        private static bool StartDsh()
        {
            Log("== 启动 DSH (端口 " + Port + ") ==");
            try { Directory.CreateDirectory(LogDir); } catch { }
            if (!File.Exists(NodeExe)) { Log("错误: node.exe 不存在: " + NodeExe); return false; }
            if (!File.Exists(DshBin)) { Log("错误: 未找到 DSH 入口: " + DshBin); return false; }
            RotateServiceLog(StdoutLog);
            RotateServiceLog(StderrLog);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = NodeExe;
            psi.Arguments = "\"" + DshBin + "\" web --port " + Port;
            psi.WorkingDirectory = DshHome;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.EnvironmentVariables["DSH_HOME"] = DshHome;

            Process proc = new Process();
            proc.StartInfo = psi;
            proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null) AppendServiceLog(StdoutLog, e.Data);
            };
            proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null) AppendServiceLog(StderrLog, e.Data);
            };
            try { proc.Start(); }
            catch (Exception ex) { Log("启动进程失败: " + ex.Message); return false; }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            int pid = proc.Id;
            _serviceProc = proc;   // 保持引用, 日志事件持续收流
            Log("  已启动: PID " + pid + "  " + psi.FileName + " " + psi.Arguments);
            try { File.WriteAllText(PidFile, pid.ToString(), Encoding.ASCII); } catch { }

            DateTime deadline = DateTime.Now.AddSeconds(120);
            bool ready = false;
            while (DateTime.Now < deadline)
            {
                Thread.Sleep(1000);
                if (proc.HasExited)
                {
                    int code = -1;
                    try { code = proc.ExitCode; } catch { }
                    Log("  进程提前退出, 退出码: " + code);
                    break;
                }
                if (TcpListening(Port) && HttpMarkerOk()) { ready = true; break; }
            }
            if (ready)
            {
                Log("== 启动成功: " + Url + " (PID " + pid + ") ==");
                return true;
            }
            Log("== 启动失败 ==");
            try { if (proc.HasExited && File.Exists(PidFile)) File.Delete(PidFile); } catch { }
            Log("--- stderr 尾部 ---");
            string tail = ReadTail(StderrLog, 20);
            foreach (string line in tail.Split('\n'))
                if (line.Trim().Length > 0) Log("  | " + line.Trim());
            return false;
        }

        // ---------------- 打开界面 ----------------

        private static void OpenUi()
        {
            string chromeProxy = ChromeProxyPath;
            if (chromeProxy != null && Directory.Exists(PwaDataDir))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = chromeProxy;
                    psi.Arguments = "--profile-directory=Default --app-id=" + PwaAppId;
                    psi.UseShellExecute = false;
                    Process p = Process.Start(psi);
                    if (p != null) { Log("已打开 DSH 窗口 (Chrome PWA)"); return; }
                }
                catch (Exception ex) { Log("PWA 启动失败, 改用浏览器: " + ex.Message); }
            }
            try
            {
                Process.Start(Url);
                Log("已打开浏览器: " + Url);
            }
            catch (Exception ex) { Log("打开浏览器失败: " + ex.Message); }
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
            Log("命令: 打开界面");
            if (IsDshHealthy())
            {
                Log("DSH 正常运行, 直接打开界面");
                OpenUi();
                return;
            }
            Log("DSH 未运行或异常, 清理并重新启动");
            StopDsh();
            bool ok = StartDsh();
            if (ok)
            {
                _lastUp = true;
                ShowBalloon("DSH 已启动", Url);
                OpenUi();
            }
            else ShowBalloon("DSH 启动失败", "请右键图标 -> 查看日志");
        }

        private static void RestartDsh()
        {
            Log("命令: 重启 DSH (清理所有旧进程)");
            StopDsh();
            bool ok = StartDsh();
            if (ok)
            {
                _lastUp = true;
                ShowBalloon("DSH 已重启", Url);
                OpenUi();
            }
            else ShowBalloon("DSH 重启失败", "请右键图标 -> 查看日志");
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
                else if (c == "stop") { StopDsh(); ShowBalloon("DSH 已停止", "服务已停止, 托盘仍在运行"); }
            }
        }

        private static void PipeServerLoop()
        {
            string name = PipeNameBase + "-" + Environment.UserName;
            while (true)
            {
                try
                {
                    using (NamedPipeServerStream server = new NamedPipeServerStream(name, PipeDirection.In, 1))
                    {
                        server.WaitForConnection();
                        using (StreamReader reader = new StreamReader(server, Encoding.UTF8))
                        {
                            string cmd = reader.ReadLine();
                            if (!string.IsNullOrEmpty(cmd))
                            {
                                Log("收到命令: " + cmd);
                                lock (QueueLock) _commandQueue.Add(cmd);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("管道服务异常: " + ex.Message);
                    Thread.Sleep(1000);
                }
            }
        }

        private static bool SendCommand(string cmd)
        {
            string name = PipeNameBase + "-" + Environment.UserName;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    using (NamedPipeClientStream client = new NamedPipeClientStream(".", name, PipeDirection.Out))
                    {
                        client.Connect(3000);
                        using (StreamWriter writer = new StreamWriter(client, Encoding.UTF8))
                        {
                            writer.WriteLine(cmd);
                            writer.Flush();
                        }
                    }
                    return true;
                }
                catch { Thread.Sleep(500); }
            }
            return false;
        }

        // ---------------- 开机自启 ----------------

        private static string StartupLnkPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DSH Launcher.lnk"); }
        }

        private static bool AutoStartEnabled()
        {
            return File.Exists(StartupLnkPath);
        }

        private static void ToggleAutoStart()
        {
            if (AutoStartEnabled())
            {
                try { File.Delete(StartupLnkPath); Log("已关闭开机自启"); }
                catch (Exception ex) { Log("关闭开机自启失败: " + ex.Message); }
                return;
            }
            try
            {
                Type wsType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(wsType);
                object lnk = wsType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { StartupLnkPath });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk,
                    new object[] { typeof(Program).Assembly.Location });
                lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { "" });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { DataDir });
                lt.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, lnk, new object[] { 7 });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                Log("已开启开机自启: " + StartupLnkPath);
            }
            catch (Exception ex)
            {
                Log("开启开机自启失败: " + ex.Message);
                MessageBox.Show("无法创建开机自启快捷方式: " + ex.Message, "DSH Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
            menu.Items.Add("停止 DSH", null, delegate { StopDsh(); ShowBalloon("DSH 已停止", "服务已停止, 托盘仍在运行"); });
            menu.Items.Add("查看日志", null, delegate { try { Process.Start(LogDir); } catch { } });
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem auto = new ToolStripMenuItem("开机自启");
            auto.Checked = AutoStartEnabled();
            auto.Click += delegate { ToggleAutoStart(); auto.Checked = AutoStartEnabled(); };
            menu.Items.Add(auto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出（停止 DSH 并退出）", null, delegate
            {
                DialogResult r = MessageBox.Show("确定停止 DSH 服务并退出启动器吗？\n\n以后想再启动, 双击桌面「DeepSeek Harness」图标即可。",
                    "DSH Launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes)
                {
                    StopDsh();
                    Application.Exit();
                }
            });
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += delegate { EnsureRunningAndOpenUi(); };
            _icon.BalloonTipIcon = ToolTipIcon.Info;

            Thread pipeThread = new Thread(PipeServerLoop);
            pipeThread.IsBackground = true;
            pipeThread.Start();

            _lastUp = IsDshHealthy();
            if (_lastUp)
            {
                Log("托盘启动: DSH 已在运行 " + Url);
                _icon.Text = "DSH 运行中 " + Url;
                if (openUiOnReady) OpenUi();
            }
            else
            {
                Log("托盘启动: DSH 未运行, 清理并自动启动 ...");
                ShowBalloon("DSH 未运行", "正在清理旧进程并自动启动 ...");
                StopDsh();
                bool ok = StartDsh();
                _lastUp = ok;
                if (ok)
                {
                    _icon.Text = "DSH 运行中 " + Url;
                    ShowBalloon("DSH 已启动", Url);
                    if (openUiOnReady) OpenUi();
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
                bool up = IsDshListening();
                if (up && !_lastUp)
                {
                    _lastUp = true;
                    _icon.Text = "DSH 运行中 " + Url;
                    ShowBalloon("DSH 已启动", Url);
                    Log("检测到 DSH 已运行");
                }
                else if (!up && _lastUp)
                {
                    _lastUp = false;
                    _icon.Text = "DSH 未运行";
                    ShowBalloon("DSH 意外退出", "可右键图标 -> 重启 DSH; 日志已保存");
                    Log("检测到 DSH 停止运行");
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

        // ---------------- 一次性模式 ----------------

        private static void OneShotBoot(bool noOpen)
        {
            Log("命令行启动: 清理并启动 (端口 " + Port + ")");
            StopDsh();
            bool ok = StartDsh();
            if (ok)
            {
                if (!noOpen) OpenUi();
            }
            else
            {
                string tail = ReadTail(StderrLog, 10);
                MessageBox.Show("DSH 启动失败。\n\n--- stderr 末尾 ---\n" + tail + "\n\n完整日志: " + LauncherLog,
                    "DSH Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void OneShotStop()
        {
            bool had = TcpListening(Port);
            StopDsh();
            MessageBox.Show(had
                ? "DSH 已停止。\n日志: " + LauncherLog
                : "未发现运行中的 DSH 进程。\n日志: " + LauncherLog,
                "DSH Launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------- 诊断报告 ----------------

        private static string BuildStatusReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DSH Launcher 状态检查   时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("版本: 1.0.0");
            sb.AppendLine("执行目录: " + ExeDir);
            sb.AppendLine("数据目录: " + DataDir);
            sb.AppendLine("端口: " + Port + "   URL: " + Url);
            sb.AppendLine("配置文件: " + ConfigFile + "   存在=" + File.Exists(ConfigFile));
            sb.AppendLine("node.exe: " + NodeExe + "   存在=" + File.Exists(NodeExe));
            sb.AppendLine("DSH_HOME: " + DshHome);
            sb.AppendLine("dsh bin.js: " + DshBin + "   存在=" + File.Exists(DshBin));
            sb.AppendLine("web profile: " + Path.Combine(DshHome, "profiles", "web") + "   存在=" + Directory.Exists(Path.Combine(DshHome, "profiles", "web")));
            sb.AppendLine("TCP 监听 " + Port + ": " + TcpListening(Port));
            sb.AppendLine("HTTP 标记检查: " + HttpMarkerOk());
            sb.AppendLine("健康状态: " + (IsDshHealthy() ? "正常" : "异常 / 未运行"));
            string pidText = "";
            try
            {
                if (File.Exists(PidFile)) pidText = File.ReadAllText(PidFile).Trim();
            }
            catch { }
            sb.AppendLine("pid 文件: " + PidFile + "   存在=" + File.Exists(PidFile) + (pidText.Length > 0 ? "   内容=" + pidText : ""));
            sb.AppendLine("Chrome PWA 界面可用: " + (ChromeProxyPath != null && Directory.Exists(PwaDataDir)));
            List<int> wmi = FindDshNodePids();
            sb.AppendLine("WMI 匹配 dsh 进程 (" + wmi.Count + "): " + string.Join(", ", wmi));
            List<int> portPids = FindPortPids();
            sb.AppendLine("端口 " + Port + " 监听 PID (" + portPids.Count + "): " + string.Join(", ", portPids));
            return sb.ToString();
        }

        private static void WriteStatusFile()
        {
            try { Directory.CreateDirectory(LogDir); } catch { }
            string report = BuildStatusReport();
            try { File.WriteAllText(StatusFile, report, Utf8NoBom); } catch { }
            TryConsole(report);
        }

        private static void WriteSelfTestFile()
        {
            try { Directory.CreateDirectory(LogDir); } catch { }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("==== DSH Launcher 自检 ====   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(BuildStatusReport());
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(NodeExe, "--version");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string v = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    sb.AppendLine("node --version: " + v);
                }
            }
            catch (Exception ex) { sb.AppendLine("node --version 失败: " + ex.Message); }
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Process"))
                {
                    s.Get();
                    sb.AppendLine("WMI 可用: 是");
                }
            }
            catch (Exception ex) { sb.AppendLine("WMI 可用: 否 - " + ex.Message); }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netstat.exe", "-ano");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    sb.AppendLine("netstat 可用: 是 (输出 " + o.Length + " 字符)");
                }
            }
            catch (Exception ex) { sb.AppendLine("netstat 可用: 否 - " + ex.Message); }
            sb.AppendLine("==== 自检结束 ====");
            string report = sb.ToString();
            try { File.WriteAllText(SelfTestFile, report, Utf8NoBom); } catch { }
            TryConsole(report);
        }

        private static void WriteHelp()
        {
            string help =
"DSH Launcher - DeepSeek Harness 系统托盘启动器 (v1.0.0)\n" +
"\n" +
"用法: DshLauncher.exe [选项]\n" +
"  (无参数)               启动托盘; DSH 未运行则自动清理并启动\n" +
"  --open                 启动托盘并打开 DSH 界面 (健康直接开, 异常清理重启)\n" +
"  --start [--noopen]     一次性: 清理旧进程 -> 启动 -> 打开界面\n" +
"  --restart [--noopen]   一次性: 同 --start\n" +
"  --stop                 一次性: 停止 DSH (弹窗确认)\n" +
"  --status               状态报告 -> logs\\status.txt\n" +
"  --selftest             环境自检 -> logs\\selftest.txt\n" +
"  --port N               覆盖端口 (默认 3080)\n" +
"  --help                 本帮助\n" +
"\n" +
"配置文件: " + ConfigFile + "\n" +
"  port=3080       端口\n" +
"  dsh_home=...    DSH 安装目录 (默认 $DSH_HOME 或 ~/.dsh)\n" +
"\n" +
"数据/日志目录: " + DataDir + "\n" +
"托盘菜单: 打开界面 / 重启(清理旧进程) / 停止 / 查看日志 / 开机自启 / 退出\n";
            Log(help);
            TryConsole(help);
        }

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        private static void TryConsole(string text)
        {
            try
            {
                AttachConsole(-1);
                Console.WriteLine(text);
                FreeConsole();
            }
            catch { }
        }

        // ---------------- 入口 ----------------

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                try { Log("未处理异常: " + e.Exception); } catch { }
                try
                {
                    MessageBox.Show("DSH Launcher 发生内部错误: " + e.Exception.Message + "\n\n详见日志: " + LauncherLog,
                        "DSH Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try { Log("未处理异常(进程级): " + e.ExceptionObject); } catch { }
            };

            string mode = "";
            bool openUi = false;
            bool noOpen = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--open") openUi = true;
                else if (a == "--noopen") noOpen = true;
                else if (a == "--port" && i + 1 < args.Length)
                {
                    int p;
                    if (int.TryParse(args[i + 1], out p) && p > 0 && p < 65536) Port = p;
                }
                else if ((a == "--help" || a == "-h") && mode == "") mode = a;
                else if (a.StartsWith("--") && mode == "") mode = a;
            }

            bool created;
            Mutex mutex = new Mutex(true, "Local\\DSHLauncher-" + Environment.UserName, out created);
            try
            {
                Log("启动器启动 模式=" + (mode == "" ? "托盘" : mode) + " 端口=" + Port +
                    " 参数=" + string.Join(" ", args));

                if (!created)
                {
                    if (mode == "--status") { WriteStatusFile(); return; }
                    if (mode == "--selftest") { WriteSelfTestFile(); return; }
                    string cmd = "open";
                    if (mode == "--restart") cmd = "restart";
                    else if (mode == "--stop") cmd = "stop";
                    Log("已有实例在运行, 转发命令: " + cmd);
                    if (SendCommand(cmd)) Log("命令已转发给运行中的托盘实例");
                    else Log("转发失败 (托盘实例可能尚未就绪)");
                    return;
                }

                if (mode == "--help" || mode == "-h") { WriteHelp(); return; }
                if (mode == "--status") { WriteStatusFile(); return; }
                if (mode == "--selftest") { WriteSelfTestFile(); return; }
                if (mode == "--stop") { OneShotStop(); return; }
                if (mode == "--start" || mode == "--restart") { OneShotBoot(noOpen); return; }

                RunTray(openUi);
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
