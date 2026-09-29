using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace DshLauncher
{
    /// <summary>
    /// DSH 进程与界面核心操作：探测、进程治理、启停、打开界面、开机自启、一次性命令、诊断报告。
    /// v1.1 重构：从 Program 原样搬出（行为零变化）。
    /// 供托盘/命令表调用；本类不持有 UI 状态（图标/菜单在 Program）。
    /// </summary>
    internal static class LauncherCore
    {
        // 启停成功后由托盘更新的提示位（原 _lastUp 语义保留在 Program）
        // ---------------- 探测 ----------------

        public static bool TcpListening(int port)
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

        /// <summary>
        /// 全检: HTTP 可达性。兼容 v1.0（无认证, 200 页面含 __DSH_BOOT__）与
        /// 0.1.2-alpha+（token 认证, 无 token 返回 401）。
        /// 判定: 能拿到任何 HTTP 响应（200/401/403…）都视为服务就绪——服务在应答只是要认证;
        /// 只有连接失败（超时/拒绝, 无任何响应）才算不健康。
        /// 2026-09-03 修复: alpha.5 加 token 认证后 401 被误判为失败, 导致启动器反复"清理重启"。
        /// </summary>
        public static bool HttpMarkerOk()
        {
            return HttpProbeStatus() >= 0;
        }

        /// <summary>探测 HTTP 状态码: 返回 >=0 = 拿到 HTTP 响应（200/401/403…）; -1 = 连不上(无响应)。</summary>
        public static int HttpProbeStatus()
        {
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(LauncherConfig.Url);
                req.Timeout = 5000;
                req.ReadWriteTimeout = 5000;
                req.UserAgent = "DSHLauncher/1.1";
                req.AllowAutoRedirect = false;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    return (int)resp.StatusCode;
                }
            }
            catch (WebException wex)
            {
                // 4xx/5xx 在 HttpWebRequest 里以 WebException 抛出, 但 Response 非空 = 服务在应答
                HttpWebResponse eresp = wex.Response as HttpWebResponse;
                if (eresp != null)
                {
                    try { return (int)eresp.StatusCode; }
                    catch { return -1; }
                }
                return -1;   // 无响应 = 连不上
            }
            catch { return -1; }
            finally
            {
                if (req != null) { try { req.Abort(); } catch { } }
            }
        }

        // 快检: 仅看端口监听, 供 3 秒一次的托盘监视器用 (避免频繁下载整页 HTML)
        public static bool IsDshListening()
        {
            return TcpListening(LauncherConfig.Port);
        }

        // 全检: 端口 + HTTP 特征标记, 供用户动作与启动轮询用
        public static bool IsDshHealthy()
        {
            if (!TcpListening(LauncherConfig.Port)) return false;
            return HttpMarkerOk();
        }

        public static int PortFromCmdLine(string cmd)
        {
            Match m = Regex.Match(cmd, @"--port[=\s]+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int p;
                if (int.TryParse(m.Groups[1].Value, out p)) return p;
            }
            return LauncherConfig.DefaultPort;
        }

        // ---------------- 进程治理 ----------------

        // WMI: 找出命令行匹配 dsh 且端口一致的所有 node 进程 ("清理旧的一切进程"的核心)
        public static List<int> FindDshNodePids()
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
                            if (PortFromCmdLine(cmd) == LauncherConfig.Port) pids.Add(Convert.ToInt32(o["ProcessId"]));
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { LauncherLog.Write("WMI 枚举失败: " + ex.Message); }
            return pids;
        }

        // netstat: 找出监听目标端口的 PID (兜底, 防漏网)
        // v1.2：加端口参数 —— 救援实例要查 3090，原先写死 LauncherConfig.Port 用不了。
        public static List<int> FindPortPids() { return FindPortPids(0); }

        public static List<int> FindPortPids(int port)
        {
            if (port <= 0) port = LauncherConfig.Port;
            List<int> pids = new List<int>();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netstat.exe", "-ano");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    string needle = ":" + port + " ";
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
            catch (Exception ex) { LauncherLog.Write("netstat 枚举失败: " + ex.Message); }
            return pids;
        }

        /// <summary>停掉监听某端口的进程（救援实例切换用）。只按端口找，不碰别的。</summary>
        public static void KillPidsOnPort(int port, string why)
        {
            List<int> pids = FindPortPids(port);
            if (pids.Count == 0) { LauncherLog.Write("  端口 " + port + " 上没有监听进程"); return; }
            foreach (int pid in pids) KillPid(pid, why);
        }

        private static bool IsAlive(int pid)
        {
            try { using (Process p = Process.GetProcessById(pid)) { return true; } }
            catch { return false; }
        }

        private static void KillPid(int pid, string why)
        {
            if (!IsAlive(pid))
            {
                LauncherLog.Write("  PID " + pid + " 已不存在 (跳过: " + why + ")");
                return;
            }
            try
            {
                using (Process p = Process.GetProcessById(pid))
                    LauncherLog.Write("  清理: PID " + pid + " (" + p.ProcessName + ") - " + why);
            }
            catch { }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process k = Process.Start(psi))
                {
                    string o = k.StandardOutput.ReadToEnd();
                    string e = k.StandardError.ReadToEnd();
                    k.WaitForExit(15000);
                    LauncherLog.Write("  taskkill " + pid + ": " + (o + e).Trim());
                }
            }
            catch (Exception ex)
            {
                LauncherLog.Write("  taskkill " + pid + " 异常: " + ex.Message);
                try { using (Process p = Process.GetProcessById(pid)) p.Kill(); } catch { }
            }
        }

        public static void SweepKill()
        {
            HashSet<int> killed = new HashSet<int>();
            if (File.Exists(LauncherConfig.PidFile))
            {
                try
                {
                    string s = File.ReadAllText(LauncherConfig.PidFile).Trim();
                    int pid;
                    if (int.TryParse(s, out pid))
                    {
                        LauncherLog.Write("  pid 文件: " + LauncherConfig.PidFile + " -> " + pid);
                        KillPid(pid, "pid 文件记录");
                        killed.Add(pid);
                    }
                }
                catch { }
            }
            List<int> wmi = FindDshNodePids();
            foreach (int pid in wmi)
            {
                if (!killed.Contains(pid)) { KillPid(pid, "命令行匹配 dsh (端口 " + LauncherConfig.Port + ")"); killed.Add(pid); }
            }
            List<int> port = FindPortPids();
            foreach (int pid in port)
            {
                if (!killed.Contains(pid)) { KillPid(pid, "监听端口 " + LauncherConfig.Port); killed.Add(pid); }
            }
            if (killed.Count == 0) LauncherLog.Write("  未发现需要清理的 DSH 进程");
        }

        private static bool WaitPortRelease(int tries, int delayMs)
        {
            for (int i = 0; i < tries; i++)
            {
                if (!TcpListening(LauncherConfig.Port)) return true;
                Thread.Sleep(delayMs);
            }
            return !TcpListening(LauncherConfig.Port);
        }

        // ---------------- 启停 ----------------

        public static bool StopDsh()
        {
            LauncherLog.Write("== 停止 DSH (端口 " + LauncherConfig.Port + ") ==");
            SweepKill();
            bool clean = WaitPortRelease(20, 500);
            if (!clean)
            {
                LauncherLog.Write("  端口仍被占用, 追加一轮清理");
                SweepKill();
                clean = WaitPortRelease(20, 500);
            }
            try { if (File.Exists(LauncherConfig.PidFile)) File.Delete(LauncherConfig.PidFile); } catch { }
            LauncherLog.Write("== 停止完成, 端口已释放: " + clean + " ==");
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

        /// <summary>启动 DSH 进程并轮询就绪。返回 true=成功。原 StartDsh 语义：返回前进程引用归调用方管理。
        /// detached=true 时 DSH 的 stdout/stderr 直接重定向到日志文件（不经过启动器管道），
        /// 让 DSH 在启动器（一次性命令）退出后仍能独立存活，避免管道读端关闭导致 EPIPE 崩溃。
        /// 托盘模式（常驻）传 false，保持原管道实时日志行为。</summary>
        public static Process StartDsh(bool detached = false) { return StartDsh(detached, 0, null, null); }

        /// <summary>
        /// 起一个 DSH 实例（v1.2 起支持救援实例，合并原先 RescueMode 里那套重复启动逻辑）。
        /// port 传 0 → 用 LauncherConfig.Port；extraEnvName/Val 用于安全模式（DSH_SAFE_MODE=1）。
        /// 救援实例（非主端口）**不写主 PID 文件**，也**不跑 HttpMarkerOk()**（它按主端口探活），
        /// 日志与 pid 一律按端口分开，避免和主实例互相覆盖。
        /// </summary>
        public static Process StartDsh(bool detached, int port, string extraEnvName, string extraEnvVal)
        {
            if (port <= 0) port = LauncherConfig.Port;
            bool isMainPort = (port == LauncherConfig.Port);
            string pidFile = LauncherConfig.PidFileFor(port);
            string outLog = LauncherConfig.StdoutLogFor(port);
            string errLog = LauncherConfig.StderrLogFor(port);
            LauncherLog.Write("== 启动 DSH (端口 " + port + ")" + (detached ? " [detached]" : "") + (isMainPort ? "" : " [救援实例]") + " ==");
            try { Directory.CreateDirectory(LauncherConfig.LogDir); } catch { }
            if (!File.Exists(LauncherConfig.NodeExe)) { LauncherLog.Write("错误: node.exe 不存在: " + LauncherConfig.NodeExe); return null; }
            if (!File.Exists(LauncherConfig.DshBin)) { LauncherLog.Write("错误: 未找到 DSH 入口: " + LauncherConfig.DshBin); return null; }
            RotateServiceLog(outLog);
            RotateServiceLog(errLog);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = LauncherConfig.NodeExe;
            psi.Arguments = "\"" + LauncherConfig.DshBin + "\" web --port " + port;
            psi.WorkingDirectory = LauncherConfig.DshHome;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["DSH_HOME"] = LauncherConfig.DshHome;
            if (extraEnvName != null) psi.EnvironmentVariables[extraEnvName] = extraEnvVal;

            Process proc = new Process();
            proc.StartInfo = psi;

            if (detached)
            {
                // 一次性模式(--start/--restart)：用 cmd 包装 + 文件重定向。
                // DSH 的 stdout/stderr 由 cmd 重定向到日志文件（文件句柄独立于启动器生命周期），
                // 启动器退出后 DSH 继续存活并持续写日志，不再依赖启动器管道读端。
                // 注意：proc.Id 此时是 cmd.exe 的 PID；StopDsh 用 taskkill /T 会连带杀掉 node。
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c \"\"" + LauncherConfig.NodeExe + "\" \"" + LauncherConfig.DshBin
                    + "\" web --port " + port
                    + " > \"" + outLog + "\" 2> \"" + errLog + "\"\"";
                psi.RedirectStandardOutput = false;
                psi.RedirectStandardError = false;
                try { proc.Start(); }
                catch (Exception ex) { LauncherLog.Write("启动进程失败: " + ex.Message); return null; }
            }
            else
            {
                // 托盘模式(常驻)：持有管道收实时日志（读端在托盘进程，随托盘存活）。
                // 必须显式声明 StandardOutputEncoding：node 写出的是 UTF-8，
                // 不设置时 .NET 按进程控制台代码页（中文系统=GBK）解码 → 中文日志乱码。
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) LauncherLog.AppendServiceLog(outLog, e.Data);
                };
                proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) LauncherLog.AppendServiceLog(errLog, e.Data);
                };
                try { proc.Start(); }
                catch (Exception ex) { LauncherLog.Write("启动进程失败: " + ex.Message); return null; }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            int pid = proc.Id;
            LauncherLog.Write("  已启动: PID " + pid + "  " + psi.FileName + " " + psi.Arguments);
            if (isMainPort) { try { File.WriteAllText(pidFile, pid.ToString(), Encoding.ASCII); } catch { } }

            DateTime deadline = DateTime.Now.AddSeconds(120);
            bool ready = false;
            while (DateTime.Now < deadline)
            {
                Thread.Sleep(1000);
                if (proc.HasExited)
                {
                    int code = -1;
                    try { code = proc.ExitCode; } catch { }
                    LauncherLog.Write("  进程提前退出, 退出码: " + code);
                    break;
                }
                if (TcpListening(port) && (isMainPort ? HttpMarkerOk() : true)) { ready = true; break; }
            }
            if (ready)
            {
                LauncherLog.Write("== 启动成功: http://127.0.0.1:" + port + " (PID " + pid + ") ==");
                return proc;
            }
            LauncherLog.Write("== 启动失败 ==");
            try { if (proc.HasExited && isMainPort && File.Exists(pidFile)) File.Delete(pidFile); } catch { }
            LauncherLog.Write("--- stderr 尾部 ---");
            string tail = LauncherLog.ReadTail(errLog, 20);
            foreach (string line in tail.Split('\n'))
                if (line.Trim().Length > 0) LauncherLog.Write("  | " + line.Trim());
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            try { proc.Dispose(); } catch { }
            return null;
        }

        // ---------------- 打开界面 ----------------

        public static void OpenUi()
        {
            string chromeProxy = LauncherConfig.ChromeProxyPath;
            if (chromeProxy != null && Directory.Exists(LauncherConfig.PwaDataDir))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = chromeProxy;
                    psi.Arguments = "--profile-directory=Default --app-id=" + LauncherConfig.PwaAppId;
                    psi.UseShellExecute = false;
                    Process p = Process.Start(psi);
                    if (p != null) { LauncherLog.Write("已打开 DSH 窗口 (Chrome PWA)"); return; }
                }
                catch (Exception ex) { LauncherLog.Write("PWA 启动失败, 改用浏览器: " + ex.Message); }
            }
            try
            {
                Process.Start(LauncherConfig.Url);
                LauncherLog.Write("已打开浏览器: " + LauncherConfig.Url);
            }
            catch (Exception ex) { LauncherLog.Write("打开浏览器失败: " + ex.Message); }
        }

        // ---------------- 急救：叫小D来修 ----------------

        /// <summary>
        /// 界面打不开 / 被 "Failed to load plugins" 盖住时的后路：在**新控制台窗口**里跑
        /// ask-xiaod.cmd（一次性 headless agent —— 独立 profile，不吃 web 的插件树，不依赖网页）。
        /// 路径可用环境变量 DSH_ASK_CMD 覆盖（测试 / 迁移用）。
        /// </summary>
        public static void OpenAskXiaoD()
        {
            string cmd = Environment.GetEnvironmentVariable("DSH_ASK_CMD");
            if (string.IsNullOrEmpty(cmd)) cmd = Path.Combine(LauncherConfig.ExeDir, "ask-xiaod.cmd");
            if (!File.Exists(cmd))
            {
                LauncherLog.Write("叫小D失败: 找不到 " + cmd);
                MessageBox.Show("找不到急救入口:\n" + cmd +
                    "\n\n它应与启动器放在同一目录（ask-xiaod.cmd + ask-xiaod.ps1）。",
                    "DSH Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c \"" + cmd + "\"";
                psi.WorkingDirectory = LauncherConfig.ExeDir;
                // 本程序是 winexe（没有自己的控制台）→ 必须 shell execute 才会弹出新的控制台窗口
                psi.UseShellExecute = true;
                Process.Start(psi);
                LauncherLog.Write("已打开急救入口: " + cmd);
            }
            catch (Exception ex)
            {
                LauncherLog.Write("叫小D失败: " + ex.Message);
                MessageBox.Show("打开急救入口失败: " + ex.Message, "DSH Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------- 开机自启 ----------------

        public static string StartupLnkPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DSH Launcher.lnk"); }
        }

        public static bool AutoStartEnabled()
        {
            return File.Exists(StartupLnkPath);
        }

        public static void ToggleAutoStart()
        {
            if (AutoStartEnabled())
            {
                try { File.Delete(StartupLnkPath); LauncherLog.Write("已关闭开机自启"); }
                catch (Exception ex) { LauncherLog.Write("关闭开机自启失败: " + ex.Message); }
                return;
            }
            try
            {
                Type wsType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(wsType);
                object lnk = wsType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { StartupLnkPath });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk,
                    new object[] { LauncherConfig.ExeDir + "\\DshLauncher.exe" });
                lt.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "" });
                lt.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { LauncherConfig.DataDir });
                lt.InvokeMember("WindowStyle", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { 7 });
                lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
                LauncherLog.Write("已开启开机自启: " + StartupLnkPath);
            }
            catch (Exception ex)
            {
                LauncherLog.Write("开启开机自启失败: " + ex.Message);
                MessageBox.Show("无法创建开机自启快捷方式: " + ex.Message, "DSH Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------- 一次性模式 ----------------

        public static void OneShotBoot(bool noOpen)
        {
            LauncherLog.Write("命令行启动: 清理并启动 (端口 " + LauncherConfig.Port + ")");
            StopDsh();
            Process proc = StartDsh(true);   // detached：一次性命令启动，stdout/stderr 走文件，DSH 不依赖启动器生命周期
            if (proc != null)
            {
                Program.TrackServiceProcess(proc);
                if (!noOpen) OpenUi();
            }
            else
            {
                string tail = LauncherLog.ReadTail(LauncherConfig.StderrLog, 10);
                // 一次性模式是命令行/脚本工具: 不弹 GUI 窗(会阻塞无人值守), 走日志+控制台
                // (修 2026-09-03: 旧版 MessageBox.Show 在脚本/定时任务场景卡死进程)
                string msg = "DSH 启动失败。\n\n--- stderr 末尾 ---\n" + tail + "\n\n完整日志: " + LauncherConfig.LauncherLog;
                LauncherLog.Write(msg);
                LauncherLog.TryConsole(msg);
            }
        }

        public static void OneShotStop()
        {
            bool had = TcpListening(LauncherConfig.Port);
            StopDsh();
            // 一次性模式不弹窗(见 OneShotBoot 注释), 结果走日志+控制台
            string msg = had
                ? "DSH 已停止。\n日志: " + LauncherConfig.LauncherLog
                : "未发现运行中的 DSH 进程。\n日志: " + LauncherConfig.LauncherLog;
            LauncherLog.Write(msg.Replace("\n", " "));
            LauncherLog.TryConsole(msg);
        }

        // ---------------- 诊断报告 ----------------

        public static string BuildStatusReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DSH Launcher 状态检查   时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("版本: 1.2.0");
            sb.AppendLine("执行目录: " + LauncherConfig.ExeDir);
            sb.AppendLine("数据目录: " + LauncherConfig.DataDir);
            sb.AppendLine("端口: " + LauncherConfig.Port + "   URL: " + LauncherConfig.Url);
            sb.AppendLine("node.exe: " + LauncherConfig.NodeExe + "   存在=" + File.Exists(LauncherConfig.NodeExe));
            sb.AppendLine("DSH_HOME: " + LauncherConfig.DshHome);
            sb.AppendLine("dsh bin.js: " + LauncherConfig.DshBin + "   存在=" + File.Exists(LauncherConfig.DshBin));
            sb.AppendLine("web profile: " + Path.Combine(LauncherConfig.DshHome, "profiles", "web") + "   存在=" + Directory.Exists(Path.Combine(LauncherConfig.DshHome, "profiles", "web")));
            sb.AppendLine("TCP 监听 " + LauncherConfig.Port + ": " + TcpListening(LauncherConfig.Port));
            sb.AppendLine("HTTP 可达检查: " + HttpMarkerOk() + " (状态码 " + HttpProbeStatus() + ")");
            sb.AppendLine("健康状态: " + (IsDshHealthy() ? "正常" : "异常 / 未运行"));
            string pidText = "";
            try
            {
                if (File.Exists(LauncherConfig.PidFile)) pidText = File.ReadAllText(LauncherConfig.PidFile).Trim();
            }
            catch { }
            sb.AppendLine("pid 文件: " + LauncherConfig.PidFile + "   存在=" + File.Exists(LauncherConfig.PidFile) + (pidText.Length > 0 ? "   内容=" + pidText : ""));
            sb.AppendLine("Chrome PWA 界面可用: " + (LauncherConfig.ChromeProxyPath != null && Directory.Exists(LauncherConfig.PwaDataDir)));
            List<int> wmi = FindDshNodePids();
            sb.AppendLine("WMI 匹配 dsh 进程 (" + wmi.Count + "): " + string.Join(", ", wmi));
            List<int> portPids = FindPortPids();
            sb.AppendLine("端口 " + LauncherConfig.Port + " 监听 PID (" + portPids.Count + "): " + string.Join(", ", portPids));
            return sb.ToString();
        }

        public static void WriteStatusFile()
        {
            try { Directory.CreateDirectory(LauncherConfig.LogDir); } catch { }
            string report = BuildStatusReport();
            try { File.WriteAllText(LauncherConfig.StatusFile, report, new UTF8Encoding(false)); } catch { }
            LauncherLog.TryConsole(report);
        }

        public static void WriteSelfTestFile()
        {
            try { Directory.CreateDirectory(LauncherConfig.LogDir); } catch { }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("==== DSH Launcher 自检 ====   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(BuildStatusReport());
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(LauncherConfig.NodeExe, "--version");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
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
                psi.StandardOutputEncoding = Encoding.UTF8;
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
            try { File.WriteAllText(LauncherConfig.SelfTestFile, report, new UTF8Encoding(false)); } catch { }
            LauncherLog.TryConsole(report);
        }

        public static void WriteHelp()
        {
            string help =
"DSH Launcher - DeepSeek Harness 系统托盘启动器 (v1.2.0)\n" +
"\n" +
"用法: DshLauncher.exe [选项]\n" +
"  (无参数)               启动托盘; DSH 未运行则自动清理并启动\n" +
"  --open                 启动托盘并打开 DSH 界面 (健康直接开, 异常清理重启)\n" +
"  --start [--noopen]     一次性: 清理旧进程 -> 启动 -> 打开界面\n" +
"  --restart [--noopen]   一次性: 同 --start\n" +
"  --ask                  叫小D来修: 新控制台窗口里跑 ask-xiaod.cmd (界面打不开时用)\n" +
"  --stop                 一次性: 停止 DSH (弹窗确认)\n" +
"  --status               状态报告 -> logs\\status.txt\n" +
"  --selftest             环境自检 -> logs\\selftest.txt\n" +
"  --port N               覆盖端口 (默认 3080)\n" +
"  --help                 本帮助\n" +
"\n" +
"-- 救援模式 (v1.2; 也可在托盘菜单「救援」里点) --\n" +
"  --rescue-brief         收集现场: 只读生成诊断简报 (10 节)\n" +
"  --rescue-diagnose      读最近简报并给修复建议\n" +
"  --rescue-verify        救援验收: 8 项清单 (核心版本 / dump / patch / 补丁 / 农场 / 端口 / safe-mode)\n" +
"  --rescue-scan          农场链接体检 (只读)\n" +
"  --rescue-snapshots     列出可用配置快照\n" +
"  --rescue-patches       本地补丁状态 (dry-run)\n" +
"  --rescue-enter         进入安全模式: 停主实例 -> 在 3090 起干净实例 (跳过用户插件)\n" +
"  --rescue-exit          退出安全模式: 停 3090 -> 清标记 -> 起回主实例\n" +
"  (以上结果均落盘到 logs\\rescue-*.txt: 本程序是 winexe, 没有控制台输出)\n" +
"\n" +
"数据/日志目录: " + LauncherConfig.DataDir + "\n" +
"托盘菜单: 打开界面 / 重启(清理旧进程) / 叫小D来修 / 救援(收集现场·验收·进/出安全模式) / 停止 / 查看日志 / 开机自启 / 退出\n";
            LauncherLog.Write(help);
            LauncherLog.TryConsole(help);
        }
    }
}
