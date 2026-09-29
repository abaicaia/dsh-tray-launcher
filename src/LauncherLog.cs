using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DshLauncher
{
    /// <summary>
    /// 日志设施：launcher.log（512KB 轮转）、服务 stdout/stderr（8MB 轮转）、ReadTail。
    /// v1.1 重构：从 Program 原样搬出（行为零变化）。
    /// </summary>
    internal static class LauncherLog
    {
        private static readonly object LogLock = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void Write(string msg)
        {
            lock (LogLock)
            {
                try
                {
                    Directory.CreateDirectory(LauncherConfig.LogDir);
                    FileInfo fi = new FileInfo(LauncherConfig.LauncherLog);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = LauncherConfig.LauncherLog + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LauncherConfig.LauncherLog, old);
                    }
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
                    File.AppendAllText(LauncherConfig.LauncherLog, line + Environment.NewLine, Utf8NoBom);
                }
                catch { }
            }
        }

        public static void AppendServiceLog(string path, string text)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(LauncherConfig.LogDir);
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

        public static string ReadTail(string path, int lines)
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

        // ---------------- 无控制台程序的控制台输出辅助 ----------------

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        /// <summary>尝试附到父进程控制台打印（Winexe 无控制台；失败静默）。</summary>
        public static void TryConsole(string text)
        {
            try
            {
                AttachConsole(-1);
                Console.WriteLine(text);
                FreeConsole();
            }
            catch { }
        }
    }
}
