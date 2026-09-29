using System;
using System.IO;
using System.Reflection;

namespace DshLauncher
{
    /// <summary>
    /// 配置与路径族：数据目录解析、node/DSH/Chrome 路径探测。
    /// v1.1 重构：从 Program 原样搬出（行为零变化）。
    /// </summary>
    internal static class LauncherConfig
    {
        public const int DefaultPort = 3080;

        // ---- v1.2 救援模式 ----
        /// <summary>救援实例端口：与主端口分开，安全模式实例跑这里。</summary>
        public const int RescuePort = 3090;
        /// <summary>救援工具目录（collect-rescue-brief.mjs / rescue-fix.mjs 所在）。</summary>
        public static string ToolsDir = @"J:\2\gonzuo\tools";
        /// <summary>安全模式开关的环境变量名（必须与 cordis.patch.yml 里 !!js 表达式的变量名一致）。</summary>
        public const string SafeModeEnv = "DSH_SAFE_MODE";
        public const string PwaAppId = "hgiemfgfjhalibdoboikeiepnnjapnpc";

        public static readonly string ExeDir =
            Path.GetDirectoryName(typeof(LauncherConfig).Assembly.Location);
        public static readonly string DataDir = ResolveDataDir();
        public static readonly string LogDir = Path.Combine(DataDir, "logs");

        /// <summary>当前端口（conf 或 --port 覆盖，启动后不变）。</summary>
        public static int Port = DefaultPort;

        public static string LauncherLog { get { return Path.Combine(LogDir, "launcher.log"); } }
        public static string StatusFile { get { return Path.Combine(LogDir, "status.txt"); } }
        public static string SelfTestFile { get { return Path.Combine(LogDir, "selftest.txt"); } }
        public static string PidFile { get { return Path.Combine(DataDir, "dsh-web-" + Port + ".pid"); } }
        public static string StdoutLog { get { return Path.Combine(LogDir, "dsh-web-" + Port + ".stdout.log"); } }
        public static string StderrLog { get { return Path.Combine(LogDir, "dsh-web-" + Port + ".stderr.log"); } }

        /// <summary>托盘图标提示文案用的地址。</summary>
        public static string Url { get { return "http://127.0.0.1:" + Port; } }

        /// <summary>按指定端口算文件路径（救援实例用，避免覆盖主端口那份）。</summary>
        public static string PidFileFor(int port) { return Path.Combine(DataDir, "dsh-web-" + port + ".pid"); }
        public static string StdoutLogFor(int port) { return Path.Combine(LogDir, "dsh-web-" + port + ".stdout.log"); }
        public static string StderrLogFor(int port) { return Path.Combine(LogDir, "dsh-web-" + port + ".stderr.log"); }

        /// <summary>
        /// 数据目录优先 exe 旁（可写时），否则 %LOCALAPPDATA%\DSHLauncher。
        /// </summary>
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


        public static string NodeExe
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

        public static string DshHome
        {
            get
            {
                string h = Environment.GetEnvironmentVariable("DSH_HOME");
                if (!string.IsNullOrEmpty(h)) return h;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            }
        }

        public static string DshBin
        {
            get { return Path.Combine(DshHome, "profiles", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"); }
        }

        public static string ChromeProxyPath
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

        public static string PwaDataDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Google", "Chrome", "User Data", "Default", "Web Applications", "_crx_" + PwaAppId);
            }
        }
    }
}
