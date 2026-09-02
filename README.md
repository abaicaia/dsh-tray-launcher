# DSH 启动器（DshLauncher）

把 DeepSeek Harness 变成「一个程序」：双击桌面图标就能打开，出问题双击它就会
**自动清理旧的 DSH 进程 → 重新启动 → 打开界面**；任务栏托盘常驻，右键可随时
重启 / 停止 / 查看日志 / 开机自启。

## 文件清单

| 文件 | 用途 |
|---|---|
| `DshLauncher.exe` | 主程序（托盘程序，双击即用，无需安装） |
| `DshLauncher.cs` | C# 源码（改功能后运行 `build.cmd` 重新编译） |
| `build.cmd` / `build-launcher.ps1` | 一键编译 + 重新生成桌面快捷方式 |
| `package.ps1` | 打包社区分发 zip → `release\` |
| `package-src\` | 社区文档源（README/LOGS/CHANGELOG/LICENSE/install） |
| `release\DSHLauncher-v1.0.0.zip` | 社区发布包 |
| `AssemblyInfo.cs` | 版本信息（v1.0.0） |
| `dsh-launcher.ico` | 程序图标（深蓝圆形徽章 + 白色鲸鱼，从 pwa-logo.ico 提取重绘） |
| `pwa-logo.ico` | 鲸鱼图标源（DSH 官方 PWA logo，构建时用于生成程序图标） |
| `logs\launcher.log` | 启动器自己的操作日志（清理/启动/报错全过程） |
| `logs\dsh-web-3080.stdout.log` / `.stderr.log` | DSH 服务输出日志（重启时自动轮转，旧日志存 `.prev`） |
| `dsh-web-3080.pid` | 服务 PID 记录 |

## 用法

- **双击桌面「DeepSeek Harness」图标**：DSH 正常 → 秒开界面；DSH 挂了/没启动 →
  自动清理旧进程 → 重新启动 → 打开界面。托盘图标同时出现。
- **托盘右键菜单**：
  - 打开 DSH 界面 —— 同上逻辑
  - 重启 DSH（清理旧进程）—— 强制清理全部旧 DSH 进程后全新启动（会断开当前会话，自动恢复）
  - 停止 DSH —— 只停服务，托盘不退
  - 查看日志 —— 打开 logs 文件夹
  - 开机自启 —— 勾选后登录 Windows 自动启动托盘并拉起 DSH
  - 退出（停止 DSH 并退出）
- **双击托盘图标** = 打开 DSH 界面。
- 桌面只保留「DeepSeek Harness」一个图标；停止服务用托盘右键（命令行 `DshLauncher.exe --stop` 也可）。

## 命令行模式（高级）

```
DshLauncher.exe --open                 托盘 + 打开界面
DshLauncher.exe --start [--noopen]     一次性: 清理 -> 启动 -> 打开界面
DshLauncher.exe --restart [--noopen]   一次性: 同 --start
DshLauncher.exe --stop                 一次性: 停止 (弹窗确认)
DshLauncher.exe --status               状态报告 (logs\status.txt)
DshLauncher.exe --selftest             环境自检 (logs\selftest.txt)
DshLauncher.exe --port 3199            指定端口 (默认 3080)
DshLauncher.exe --help                 帮助
```

## 配置文件（可选）

程序目录下 `dsh-launcher.conf`：`port=3080`（端口）、`dsh_home=...`（DSH 安装目录，默认 $DSH_HOME 或 ~/.dsh）。

## 出问题了怎么查

1. 打开 `logs\launcher.log` —— 每次清理/启动都有时间戳记录，启动失败会附 stderr 尾部。
2. `logs\dsh-web-3080.stderr.log` —— 服务本身的报错（启动器会自动轮转，保留上一次的 `.prev`）。
3. 运行 `DshLauncher.exe --selftest` 看 `logs\selftest.txt` —— 环境自检（node/WMI/netstat/端口/健康状态）。

## 清理旧进程的范围（安全设计）

只清理**确实是 DSH 的进程**，不碰其它 node 程序：
- pid 文件记录的进程；
- 命令行含 `@deepseek-ai\dsh` 且端口一致的 node 进程（WMI 枚举）；
- 监听目标端口的进程（netstat 兜底）。
杀掉后等待端口释放并删除 pid 文件，再全新启动。

## 重新编译 / 打包

- 改过 `DshLauncher.cs` 后双击 `build.cmd`（会重新生成桌面快捷方式）
- 社区分发：`powershell -File package.ps1` → 生成 `release\DSHLauncher-v1.0.0.zip`
- 构建会从 `pwa-logo.ico` 提取鲸鱼轮廓重绘徽章图标；缺失则回退 PWA 源文件，再缺失用蓝色 D 兜底
