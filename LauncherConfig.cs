using System;
using System.IO;
using System.Reflection;

namespace DshLauncher
{
    /// <summary>
    /// 配置与路径族：数据目录解析、dsh-launcher.conf 读取、node/DSH/Chrome 路径探测。
    /// v1.1 重构：从 Program 原样搬出（行为零变化）。
    /// </summary>
    internal static class LauncherConfig
    {
        public const int DefaultPort = 3080;
        public const string PwaAppId = "hgiemfgfjhalibdoboikeiepnnjapnpc";

        public static readonly string ExeDir =
            Path.GetDirectoryName(typeof(LauncherConfig).Assembly.Location);
        public static readonly string DataDir = ResolveDataDir();
        public static readonly string LogDir = Path.Combine(DataDir, "logs");

        /// <summary>当前端口（conf 或 --port 覆盖，启动后不变）。</summary>
        public static int Port = DefaultPort;

        private static string ConfigDshHome;

        public static string LauncherLog { get { return Path.Combine(LogDir, "launcher.log"); } }
        public static string StatusFile { get { return Path.Combine(LogDir, "status.txt"); } }
        public static string SelfTestFile { get { return Path.Combine(LogDir, "selftest.txt"); } }
        public static string ConfigFile { get { return Path.Combine(DataDir, "dsh-launcher.conf"); } }
        public static string PidFile { get { return Path.Combine(DataDir, "dsh-web-" + Port + ".pid"); } }
        public static string StdoutLog { get { return Path.Combine(LogDir, "dsh-web-" + Port + ".stdout.log"); } }
        public static string StderrLog { get { return Path.Combine(LogDir, "dsh-web-" + Port + ".stderr.log"); } }

        /// <summary>托盘图标提示文案用的地址。</summary>
        public static string Url { get { return "http://127.0.0.1:" + Port; } }

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

        public static void LoadConfig()
        {
            try
            {
                string conf = ConfigFile;
                if (!File.Exists(conf)) return;
                foreach (string raw in File.ReadAllLines(conf, System.Text.Encoding.UTF8))
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
                if (!string.IsNullOrEmpty(ConfigDshHome)) return ConfigDshHome;
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
