# Changelog

## [1.0.0] - 2026-08-17

### Added
- 系统托盘程序（.NET Framework 4.x 原生，单 exe 零依赖）
- 双击桌面图标：健康秒开界面 / 异常自动清理旧进程并重启（Chrome PWA 优先）
- 托盘右键：打开界面 / 重启（清理旧进程）/ 停止 / 查看日志 / 开机自启 / 退出
- 精准清理：pid 文件 + WMI 命令行匹配（按 --port 精确过滤）+ netstat 端口兜底
- 完整日志：launcher.log + 服务 stdout/stderr 分离、双级轮转、失败自动附 stderr 尾部
- 单实例 + 命名管道命令转发；3 秒健康快检 + 意外退出气泡提醒
- 命令行模式：--open / --start / --restart / --stop / --status / --selftest / --port / --help
- 配置文件 dsh-launcher.conf（port / dsh_home）
- 数据目录不可写时自动回退 %LOCALAPPDATA%
- 鲸鱼徽章图标（从官方 PWA 图标提取重绘，16+32 双尺寸）
