# DSH 托盘启动器 · DshLauncher

> 让 DeepSeek Harness 变成"双击就能用"的 Windows 程序 —— **而且它坏掉的时候，你能自己救回来。**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D6.svg)](#)
[![Built with: .NET Framework csc](https://img.shields.io/badge/Built%20with-.NET%20Framework%20csc-512BD4.svg)](#)

---

## 这是什么

DeepSeek Harness（DSH）本身是个命令行工具：要开它得敲命令，出了问题得看日志、杀进程、翻配置。

**DshLauncher 是套在它外面的一个小托盘程序**（单 exe，约 50 KB，不需要安装器）：

| 情况 | 双击桌面图标会怎样 |
|---|---|
| **DSH 正常** | 秒开界面（优先复用 Chrome PWA 窗口） |
| **DSH 挂了 / 没启动** | 自动清理卡住的旧进程 → 全新启动 → 打开界面 |
| **界面彻底进不去** | **走救援模式**（命令行也能用，见下） |

---

## ✨ 特性

- 🖱️ **双击即用** —— 桌面就一个「DeepSeek Harness」图标，别的都不用管
- 🩹 **一键修复** —— 清理旧 DSH 进程（**只杀 DSH，不碰其它 node 程序**）→ 重新启动 → 就绪后开界面
- 🚑 **救援模式** —— **本项目重点**：DSH 起不来时的救命通道（详见下节）
- 🧭 **托盘常驻** —— 右键：打开界面 / 重启 / **叫小D来修** / **救援** / 停止 / 查看日志 / 开机自启 / 退出
- 🔁 **单实例** —— 重复双击走命名管道转发给已有实例，不会双开、不弹窗
- 📝 **完整日志** —— 启动器动作日志 + 服务 stdout/stderr 分离落盘、自动轮转、启动失败自动附报错尾部
- 🚨 **崩溃可感知** —— 托盘每 3 秒快检，DSH 意外退出会弹气泡提醒，点一下就能重启
- ⚙️ **零依赖** —— 单 exe（.NET Framework 4.x 系统自带），无需安装器、无需 Visual Studio

---

## 🚑 救援模式（本项目的重点）

### 什么时候用

- DSH 界面报 **Failed to load plugins**，插件一加载就崩
- **装了个插件之后，DSH 整个起不来了**
- 启动直接闪退 / 卡死，根本看不到界面
- 改配置改坏了，进不去又退不回来

### 怎么用

**界面还能开** → 托盘右键 → **救援**

**界面完全开不了** → 命令行（这才是救援模式真正的价值）：

```powershell
DshLauncher.exe --rescue-brief        # 收集现场：只读生成诊断简报
DshLauncher.exe --rescue-diagnose     # 读最近简报，给修复建议
DshLauncher.exe --rescue-verify       # 救援验收：8 项自检清单
DshLauncher.exe --rescue-enter        # 进入安全模式（跳过用户插件，起干净实例）
DshLauncher.exe --rescue-exit         # 退出安全模式，恢复正常
DshLauncher.exe --rescue-scan         # 插件目录链接体检
DshLauncher.exe --rescue-snapshots    # 列出可用配置快照
DshLauncher.exe --rescue-patches      # 本地补丁状态
```

> 结果都会落盘到 `logs\rescue-*.txt`。本程序是**无控制台的窗口程序**，不往屏幕打印，所以以文件为准。

### 安全模式是干什么的

它用 `DSH_SAFE_MODE=1` 启动一个**干净实例**（默认 **3090** 端口，**跳过你装的那些插件**）。

这样即使某个插件把 DSH 搞挂了，你也能进到界面里，**把那个插件关掉或卸掉**，再切回正常模式。

### 它不会乱动你的东西

- 诊断部分**全程只读**
- 需要改配置时，**动手前自动快照**，可一键回滚
- 你的会话数据、记忆、插件都不会被删

---

## 📦 安装

**前置条件**：Windows 10/11 + [Node.js](https://nodejs.org/) + **已经装好 DeepSeek Harness**（`dsh web` 能正常跑）

### 方式一：下载即用（推荐）

1. 到 [Releases](https://github.com/abaicaia/dsh-tray-launcher/releases) 下载最新的 zip
2. 解压到任意目录
3. （可选）双击 `install.cmd` → 创建桌面快捷方式「DeepSeek Harness」
4. 双击 `DshLauncher.exe` 或桌面图标

### 方式二：从源码构建

```powershell
git clone https://github.com/abaicaia/dsh-tray-launcher.git
cd dsh-tray-launcher
build.cmd
```

只用系统自带的 .NET Framework 4.x `csc.exe`，**不需要 Visual Studio**。构建完会自动生成图标并创建桌面快捷方式。

---

## 🚀 使用

### 托盘右键菜单

> 打开界面 · 重启（清理旧进程）· 叫小D来修 · **救援** · 停止 · 查看日志 · 开机自启 · 退出

双击托盘图标 = 打开界面。

### 命令行

```powershell
DshLauncher.exe                         # 启动托盘（DSH 没跑就自动拉起）
DshLauncher.exe --open                  # 托盘 + 打开界面
DshLauncher.exe --start [--noopen]      # 一次性：清理 → 启动 → 打开界面
DshLauncher.exe --restart [--noopen]    # 同 --start
DshLauncher.exe --stop                  # 停止 DSH（弹窗确认）
DshLauncher.exe --status                # 状态报告 → logs\status.txt
DshLauncher.exe --selftest              # 环境自检 → logs\selftest.txt
DshLauncher.exe --port 3199             # 指定端口（默认 3080）
DshLauncher.exe --rescue-*              # 救援模式，见上
DshLauncher.exe --help                  # 帮助
```

---

## 🐛 出问题了怎么查

按顺序看，一般第一步就能定位：

1. **`logs\launcher.log`** —— 启动器自己的动作记录（清理/启动/报错，带时间戳；启动失败会附服务报错尾部）
2. **`logs\dsh-web-3080.stderr.log`** —— DSH 服务本身的报错（自动轮转，上一份存 `.prev`）
3. **`DshLauncher.exe --selftest`** → `logs\selftest.txt` —— 环境自检（node / WMI / netstat / 端口 / 健康状态）
4. **界面都进不去了** → **`--rescue-brief`**（见上）

---

## 📁 目录结构

```
DshLauncher.exe      主程序（单文件，双击即用）
build.cmd            一键编译
ask-xiaod.cmd        叫小D来修（急救入口）
src\                 C# 源码（7 个文件）
scripts\             构建与打包脚本
assets\              图标
docs\                设计文档、开发复盘、编码规范
tests\               测试脚本
logs\                运行日志（不入库）
```

---

## 🔒 它不会碰什么

**清理旧进程时，只杀确实是 DSH 的进程**，不碰其它 node 程序：

- pid 文件里记录的进程
- 命令行含 `@deepseek-ai\dsh` 且端口一致的 node 进程
- 监听目标端口的进程（netstat 兜底）

**救援模式**：诊断只读，改配置前自动快照、可回滚。

---

## 📄 License

MIT —— 见 [LICENSE](LICENSE)
