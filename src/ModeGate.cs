using System.IO;

namespace DshLauncher
{
    /// <summary>
    /// 启动器模式状态机（v1.1 骨架，v1.2 救援模式接入点）。
    ///
    /// 设计（见 v2-设计讨论记录.md §三）：
    ///   normal  - 3080 主 DSH 正常运行/可启动（默认）
    ///   rescue  - 救援模式：主进程已停，3090 干净实例在跑（v1.2 实现）
    ///   stopped - 全停（托盘驻留但 DSH 未运行）
    ///
    /// v1.1 只提供：当前模式读取 + Rescue 占位；Enter/Exit 留 v1.2。
    /// 模式持久化：launcher-mode 文件（normal|rescue），不存在=normal（向后兼容）。
    /// 所有命令入口将来在此挂互斥检查（救援中禁止启动 3080；normal 中救援菜单灰显）。
    /// </summary>
    internal enum LauncherMode
    {
        Normal = 0,
        Rescue = 1,
        Stopped = 2
    }

    internal static class ModeGate
    {
        public static string ModeFile
        {
            get { return Path.Combine(LauncherConfig.DataDir, "launcher-mode"); }
        }

        /// <summary>当前模式（进程生命周期内不变；mode 文件由模式切换动作写入）。</summary>
        public static LauncherMode Current
        {
            get
            {
                try
                {
                    if (File.Exists(ModeFile))
                    {
                        string v = File.ReadAllText(ModeFile).Trim().ToLowerInvariant();
                        if (v == "rescue") return LauncherMode.Rescue;
                    }
                }
                catch { }
                return LauncherMode.Normal;
            }
        }

        public static bool IsRescue
        {
            get { return Current == LauncherMode.Rescue; }
        }

        // v1.2 预留：
        // public static bool TryEnterRescue(out string error)   // 停 3080 -> 写 mode -> 起 3090
        // public static bool TryExitRescue(out string error)    // 停 3090 -> 清 mode -> 起 3080
        // public static string Describe()                        // 状态报告用
    }
}
