using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshLauncher
{
    /// <summary>
    /// v1.2 救援模式：把 tools 下的救援工具接成托盘菜单动作。
    /// 设计见 v2-救援模式设计-20260929.md（L0 预防 / L1 诊断 / L2 安全模式 / L3 修复 / L4 验收）。
    ///
    /// v1.2 重构后：启动实例**复用** LauncherCore.StartDsh(detached, port, envName, envVal)
    /// （那次改造给它加了端口参数与"注入环境变量"能力，救援不再自带第二套启动逻辑）；
    /// 端口 / 工具目录 / 环境变量名一律取 LauncherConfig（RescuePort / ToolsDir / SafeModeEnv）。
    ///
    /// 编码纪律（见 AGENTS.md §1）：抓子进程输出必须显式声明 UTF-8，否则中文日志按 GBK 解码变乱码；
    /// 本文件与所有 .cs 一样，落盘必须带 BOM（csc 会把无 BOM 的中文按 GBK 读）。
    /// </summary>
    internal static class RescueMode
    {

        // ---------------- 通用：跑 node 工具并展示原始输出 ----------------

        /// <summary>
        /// 跑一个 tools 下的 node 工具，输出写到 logs\rescue-*.txt 再用记事本打开。
        /// 为什么不只弹个"成功"对话框：救援现场要看**原始输出**（哪一行报错、哪个条目没激活），
        /// 这也是社区共识「诊断与修复分开、先留证据」的落地。
        /// </summary>
        public static void RunTool(string script, string args, string title)
        {
            string body = CaptureTool(script, args, title);
            if (body == null) return;
            try
            {
                Directory.CreateDirectory(LauncherConfig.LogDir);
                string outFile = Path.Combine(LauncherConfig.LogDir, "rescue-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(outFile, body, new UTF8Encoding(false));
                LauncherLog.Write("救援动作完成: " + title + " -> " + outFile);
                try { Process.Start("notepad.exe", "\"" + outFile + "\""); } catch { }
            }
            catch (Exception ex)
            {
                LauncherLog.Write("写结果文件失败: " + ex.Message);
                MessageBox.Show("写结果文件失败：\n" + ex.Message, "DSH 救援", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 命令行模式：不弹记事本，直接把报告打到 stdout。
        /// 为什么要有：界面全崩 / 只想看结果 / 需要被脚本或 AI 调用时，命令行入口比弹窗可靠。
        /// 用法：DshLauncher.exe --rescue-verify（见 DshLauncher.Main）
        /// </summary>
        public static void RunToolCli(string script, string args, string title)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            string body = CaptureTool(script, args, title);
            if (body == null) return;
            // ★ 实测教训（2026-09-29）：本程序是 /target:winexe，**没有控制台** →
            //   `Console.WriteLine` 写不出去（用 Start-Process -RedirectStandardOutput 抓到的永远是 0 字节）；
            //   而且 winexe + 重定向会让调用方**挂起等待一个不会来的管道**。
            //   所以 CLI 模式下**结果文件才是可靠出口**，stdout 只是"能写就写"。
            //   （`--status` 等既有模式之所以一直好用，正是因为它们本来就走文件。）
            string outFile = null;
            try
            {
                Directory.CreateDirectory(LauncherConfig.LogDir);
                outFile = Path.Combine(LauncherConfig.LogDir, "rescue-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(outFile, body, new UTF8Encoding(false));
                LauncherLog.Write("救援动作(CLI)完成: " + title + " -> " + outFile);
            }
            catch (Exception ex) { LauncherLog.Write("写结果文件失败: " + ex.Message); }
            try { Console.WriteLine(body); } catch { }
            if (outFile != null) { try { Console.WriteLine("RESULT_FILE=" + outFile); } catch { } }
        }

        /// <summary>跑工具并抓输出（显式 UTF-8，否则中文按 GBK 解成乱码），返回完整报告文本；失败返回 null。</summary>
        private static string CaptureTool(string script, string args, string title)
        {
            string full = Path.Combine(LauncherConfig.ToolsDir, script);
            if (!File.Exists(full))
            {
                Console.Error.WriteLine("找不到工具：" + full);
                return null;
            }
            try { Cursor.Current = Cursors.WaitCursor; } catch { }
            try
            {
                // ★ 不要用 RedirectStandardOutput + 先 ReadToEnd(stdout) 再 ReadToEnd(stderr)：
                //   那是经典死锁写法（stdout 读完才会去读 stderr，子进程 stderr 缓冲写满就互等）。
                //   2026-09-29 实测就卡死在这里：CLI 调用一次挂到 600 秒超时、零输出。
                //   改成 cmd 把两个流合并重定向到临时文件，等退出后读文件 —— 与 StartDsh(detached) 同一套路。
                string tmpOut = Path.Combine(Path.GetTempPath(), "dsh-rescue-" + Guid.NewGuid().ToString("N") + ".txt");
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c \"\"" + LauncherConfig.NodeExe + "\" \"" + full + "\" " + args + " > \"" + tmpOut + "\" 2>&1\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = LauncherConfig.ToolsDir;
                psi.EnvironmentVariables["DSH_HOME"] = LauncherConfig.DshHome;

                Process p = Process.Start(psi);
                p.WaitForExit(240000);
                string all = File.Exists(tmpOut) ? File.ReadAllText(tmpOut, Encoding.UTF8) : "";
                try { File.Delete(tmpOut); } catch { }

                StringBuilder body = new StringBuilder();
                body.AppendLine("=== " + title + " ===");
                body.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                body.AppendLine("命令：node " + script + " " + args);
                body.AppendLine(new string('-', 60));
                body.AppendLine(all);
                return body.ToString();
            }
            catch (Exception ex)
            {
                LauncherLog.Write("救援动作失败: " + script + " " + ex.Message);
                Console.Error.WriteLine("运行 " + script + " 失败: " + ex.Message);
                return null;
            }
            finally { try { Cursor.Current = Cursors.Default; } catch { } }
        }

        // 菜单动作（只读类）
        public static void CollectBrief() { RunTool("collect-rescue-brief.mjs", "", "救援现场简报（只读采集，10 节）"); }
        public static void Verify() { RunTool("rescue-fix.mjs", "--action verify", "救援验收（8 项清单）"); }
        public static void ScanFarm() { RunTool("rescue-fix.mjs", "--action scan-farm", "农场链接体检（只读）"); }
        public static void ListSnapshots() { RunTool("rescue-fix.mjs", "--action list-snapshots", "可用配置快照"); }
        public static void Diagnose() { RunTool("rescue-fix.mjs", "--diagnose", "救援建议（读最近简报）"); }
        public static void ReapplyPatches() { RunTool("rescue-fix.mjs", "--action reapply-patches", "本地补丁状态（dry-run）"); }

        // ---- 命令行入口（和上面菜单一一对应；界面全崩时用这些）----
        public static void CliBrief() { RunToolCli("collect-rescue-brief.mjs", "", "救援现场简报（只读采集，10 节）"); }
        public static void CliDiagnose() { RunToolCli("rescue-fix.mjs", "--diagnose", "救援建议（读最近简报）"); }
        public static void CliVerify() { RunToolCli("rescue-fix.mjs", "--action verify", "救援验收（8 项清单）"); }
        public static void CliScanFarm() { RunToolCli("rescue-fix.mjs", "--action scan-farm", "农场链接体检（只读）"); }
        public static void CliSnapshots() { RunToolCli("rescue-fix.mjs", "--action list-snapshots", "可用配置快照"); }
        public static void CliPatches() { RunToolCli("rescue-fix.mjs", "--action reapply-patches", "本地补丁状态"); }
        public static void CliEnterSafe() { EnterSafeMode(); }
        public static void CliExitSafe() { ExitSafeMode(); }

        // ---------------- 安全模式（进/出） ----------------

        public static void EnterSafeMode()
        {
            if (ModeGate.IsRescue)
            {
                MessageBox.Show("现在已经在安全模式里了。", "DSH 救援", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (LauncherCore.TcpListening(LauncherConfig.RescuePort))
            {
                MessageBox.Show("端口 " + LauncherConfig.RescuePort + " 上已经有实例在跑，先停掉它再进安全模式。", "DSH 救援", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult r = MessageBox.Show(
                "进入安全模式会做三件事：\n\n"
                + "  1. 停掉当前 3080 主实例（DSH 会话会断开，数据都在）\n"
                + "  2. 在 " + LauncherConfig.RescuePort + " 起一个干净实例，**跳过 14 个用户插件**\n"
                + "     （记忆 / 插件市场 / 皮肤 / 浏览器套件 / 上下文面板等不加载，核心照常）\n"
                + "  3. 保留隧道与远程 UI，你的手机通道不受影响\n\n"
                + "继续吗？",
                "DSH 救援 · 进入安全模式", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            LauncherLog.Write("== 进入安全模式 ==");
            try { File.WriteAllText(ModeGate.ModeFile, "rescue", Encoding.ASCII); } catch { }
            LauncherCore.StopDsh();
            Thread.Sleep(1500);
            Process p = LauncherCore.StartDsh(true, LauncherConfig.RescuePort, LauncherConfig.SafeModeEnv, "1");
            if (p == null)
            {
                try { if (File.Exists(ModeGate.ModeFile)) File.Delete(ModeGate.ModeFile); } catch { }
                MessageBox.Show(
                    "安全模式实例没起来，已回退为普通模式标记。\n\n"
                    + "下一步：菜单里点「救援：收集现场」，看 " + LauncherConfig.RescuePort + " 的 stderr 尾部报了什么。",
                    "DSH 救援", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            try { Process.Start(new ProcessStartInfo("http://127.0.0.1:" + LauncherConfig.RescuePort) { UseShellExecute = true }); } catch { }
            MessageBox.Show(
                "已进入安全模式。\n\n"
                + "  安全模式界面：http://127.0.0.1:" + LauncherConfig.RescuePort + "\n"
                + "  用户插件已跳过，核心功能正常。\n\n"
                + "修完之后点菜单「救援：退出安全模式（回正常）」回到 3080。",
                "DSH 救援", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void ExitSafeMode()
        {
            if (!ModeGate.IsRescue)
            {
                MessageBox.Show("当前不在安全模式。", "DSH 救援", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult r = MessageBox.Show(
                "退出安全模式：\n\n"
                + "  1. 停掉 " + LauncherConfig.RescuePort + " 安全模式实例\n"
                + "  2. 清掉救援标记\n"
                + "  3. 在 3080 起回正常实例（用户插件全部恢复加载）\n\n"
                + "继续吗？",
                "DSH 救援 · 退出安全模式", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            LauncherLog.Write("== 退出安全模式 ==");
            LauncherCore.KillPidsOnPort(LauncherConfig.RescuePort, "退出安全模式: 停救援实例");
            Thread.Sleep(1500);
            try { if (File.Exists(ModeGate.ModeFile)) File.Delete(ModeGate.ModeFile); } catch { }
            LauncherCore.KillPidsOnPort(LauncherConfig.DefaultPort, "退出安全模式: 清主实例");
            Thread.Sleep(1000);
            Process p = LauncherCore.StartDsh(true, LauncherConfig.DefaultPort, null, null);
            if (p == null)
            {
                MessageBox.Show("3080 没起来。请点「救援：收集现场」看日志。", "DSH 救援", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            try { Process.Start(new ProcessStartInfo(LauncherConfig.Url) { UseShellExecute = true }); } catch { }
            MessageBox.Show("已退出安全模式，正常实例已回到 " + LauncherConfig.Url, "DSH 救援", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

    }
}
