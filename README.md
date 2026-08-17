# DSH Launcher · DSH 托盘启动器

> 把 DeepSeek Harness 变成「一个程序」：**双击秒开界面；挂了双击它，自动清理旧进程并重新完美打开；托盘右键随时重启/停止；全程日志可排障。**
> Turn DeepSeek Harness into a single program: double-click to open the UI; when it breaks, double-click again to auto-clean stale processes and relaunch; tray menu for restart/stop; everything is logged.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D6.svg)](#)
[![Built with: csc](https://img.shields.io/badge/Built%20with-.NET%20Framework%20csc-512BD4.svg)](#)

## ✨ 特性 Features

- 🖱️ **双击即用**：桌面「DeepSeek Harness」图标 = 健康时秒开界面（优先复用 Chrome PWA 窗口，无则默认浏览器）；异常/未启动时自动修复
- 🩹 **一键修复**：自动清理一切旧 DSH 进程（pid 文件 + WMI 命令行匹配 + netstat 端口兜底，只杀 DSH，不碰其它 node 程序）→ 全新启动 → 就绪后打开界面
- 🧭 **托盘常驻**：右键菜单 = 打开界面 / 重启（清理旧进程）/ 停止 / 查看日志 / 开机自启 / 退出；双击托盘图标 = 打开界面
- 🔁 **单实例**：重复双击走命名管道转发给托盘实例，不会双开、不会弹窗
- 📝 **完整日志**：启动器操作日志 + 服务 stdout/stderr 分离落盘、自动轮转、启动失败自动附 stderr 尾部；`--selftest` 一键环境自检
- 🚨 **崩溃可感知**：托盘 3 秒一次快检，DSH 意外退出弹气泡提醒，点一下即可重启
- ⚙️ **零依赖安装**：单 exe（.NET Framework 4.x 自带）+ 可选配置文件，无需安装器

## 📦 安装 Installation

前置：Windows 10/11 + Node.js + 已安装 DeepSeek Harness（`dsh web` 可正常启动）。

### 方式一：预编译（推荐）

1. 下载 Release 的 zip 并解压到任意目录
2. （可选）双击 `install.cmd` 创建桌面快捷方式「DeepSeek Harness」
3. 双击 `DshLauncher.exe` 或桌面图标即可

### 方式二：从源码构建

1. 下载源码，双击 `build.cmd`（需要 .NET Framework 4.x 自带的 csc.exe，无需 Visual Studio）
2. 构建会自动生成鲸鱼徽章图标并安装桌面快捷方式

## 🚀 使用 Usage

### 桌面图标

| DSH 状态 | 双击桌面图标的行为 |
|---|---|
| 正常运行 | 秒开界面，不动服务 |
| 已停止/崩溃 | 清理旧进程 → 启动 → 就绪后打开界面 |

### 托盘右键菜单

| 菜单 | 行为 |
|---|---|
| 打开 DSH 界面 | 同上（健康直开，异常修复） |
| 重启 DSH（清理旧进程） | 强制清理全部旧 DSH 进程后全新启动（会断开当前会话，自动恢复） |
| 停止 DSH | 只停服务，托盘不退 |
| 查看日志 | 打开 logs 文件夹 |
| 开机自启 | 勾选后登录 Windows 自动启动托盘并拉起 DSH |
| 退出（停止 DSH 并退出） | 停服务 + 退出托盘 |

### 命令行模式

```text
DshLauncher.exe --open                 托盘 + 打开界面
DshLauncher.exe --start [--noopen]     一次性：清理 → 启动 → 打开界面
DshLauncher.exe --restart [--noopen]   一次性：同 --start
DshLauncher.exe --stop                 一次性：停止（弹窗确认）
DshLauncher.exe --status               状态报告 → logs/status.txt
DshLauncher.exe --selftest             环境自检 → logs/selftest.txt
DshLauncher.exe --port 3199            指定端口（默认 3080）
DshLauncher.exe --help                 帮助
```

### 配置文件

程序目录下 `dsh-launcher.conf`（可选，INI 风格）：

```ini
# 端口（默认 3080）
port=3080
# DSH 安装目录（默认 $DSH_HOME 或 ~/.dsh）
dsh_home=C:\Users\you\.dsh
```

## 📝 日志与排障 Logs & Troubleshooting

日志目录 = 程序目录 `logs\`（若程序目录不可写，自动落到 `%LOCALAPPDATA%\DSHLauncher\logs\`）。

| 文件 | 内容 | 轮转 |
|---|---|---|
| `launcher.log` | 启动器操作全过程（清理/启动/命令/异常），时间戳精确到秒 | > 512KB 转 `.old` |
| `dsh-web-<port>.stdout.log` | DSH 服务标准输出 | 每次启动转 `.prev`；> 8MB 转 `.prev` |
| `dsh-web-<port>.stderr.log` | DSH 服务报错（**排障第一入口**） | 同上 |
| `status.txt` / `selftest.txt` | `--status` / `--selftest` 输出 | 每次覆盖 |

排障三步：

1. 看 `launcher.log` —— 每次清理/启动都有时间戳，启动失败会自动附 stderr 尾部
2. 看 `dsh-web-<port>.stderr.log` —— 服务本身报什么错
3. 跑 `DshLauncher.exe --selftest` —— 环境自检（node/WMI/netstat/端口/健康状态/pid/PWA）

详细说明见 [LOGS.md](LOGS.md)。

## 🧹 清理范围（安全设计）

只清理**确实是 DSH 的进程**：

1. pid 文件记录的进程
2. 命令行含 `@deepseek-ai\dsh` 且端口一致的 node 进程（WMI 枚举）
3. 监听目标端口的进程（netstat 兜底）

杀掉后等待端口释放（必要时追加一轮清扫）再全新启动；不碰其它 node 程序。

## ❓ FAQ

- **重启会断开会话吗？** 会。DSH 重启后旧页面需刷新，会话从磁盘恢复；重启后启动器会自动打开界面。
- **老会话重启后首条消息卡几分钟？** 是 compaction 在压缩历史，等 2-5 分钟；日常开新会话最省心。
- **托盘图标看不清？** 程序图标为深蓝圆形徽章 + 白色鲸鱼，深浅色任务栏都可见；不喜欢可自行替换（见构建说明）。
- **杀错了进程？** 不会——三层匹配都限定 DSH 特征；清理全程记录在 launcher.log 可复查。
- **想换端口？** 配置文件 `port=xxxx` 或 `--port xxxx`。

## 🛠 构建 Build

```text
build.cmd           一键编译 + 安装桌面快捷方式
package.ps1         打包 release zip（社区分发用）
```

构建产物 `DshLauncher.exe`（winexe，无控制台闪烁），图标构建时从 `pwa-logo.ico` 提取鲸鱼轮廓重绘为蓝色徽章（源缺失时回退蓝色 D）。

## ⚠️ 图标素材

鲸鱼图形取自 DeepSeek Harness 官方 Chrome PWA 图标，商标归 DeepSeek 所有；本仓库仅用于衍生程序图标。

## 📄 License

[MIT](LICENSE) © 2026 DSHLauncher Authors
