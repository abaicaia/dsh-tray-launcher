# LOGS.md · 日志说明

DSH Launcher 的所有输出都落在**数据目录**（= 程序目录；若程序目录不可写，自动落到 `%LOCALAPPDATA%\DSHLauncher`）。

## 1. 文件布局

```text
<数据目录>\
├── dsh-launcher.conf                 配置文件（可选）
├── dsh-web-<port>.pid                服务 PID 记录
└── logs\
    ├── launcher.log                  启动器操作日志（本文件的主角）
    ├── launcher.log.old              轮转出的上一段 launcher.log
    ├── dsh-web-<port>.stdout.log     DSH 服务标准输出
    ├── dsh-web-<port>.stderr.log     DSH 服务报错（排障第一入口）
    ├── dsh-web-<port>.stdout.log.prev  上一次运行的服务输出
    ├── dsh-web-<port>.stderr.log.prev  上一次运行的服务报错
    ├── status.txt                    --status 输出
    └── selftest.txt                  --selftest 输出
```

## 2. launcher.log 字段

每行格式：`yyyy-MM-dd HH:mm:ss` + 两个空格 + 内容。内容为带 `==` 标记的阶段与缩进的细节：

```text
2026-08-17 15:12:05  命令: 重启 DSH (清理所有旧进程)      ← 谁触发的什么动作
2026-08-17 15:12:05  == 停止 DSH (端口 3080) ==           ← 阶段开始
2026-08-17 15:12:05    清理: PID 48196 (node) - 命令行匹配 dsh (端口 3080)
2026-08-17 15:12:05    taskkill 48196: 成功: 已终止 PID 48196 ...
2026-08-17 15:12:05  == 停止完成, 端口已释放: True ==
2026-08-17 15:12:05  == 启动 DSH (端口 3080) ==
2026-08-17 15:12:05    已启动: PID 52452  node.exe "...\bin.js" web --port 3080
2026-08-17 15:12:08  == 启动成功: http://127.0.0.1:3080 (PID 52452) ==
2026-08-17 15:12:09  已打开 DSH 窗口 (Chrome PWA)
```

常见动作前缀：`命令:`（用户/转发的动作）、`托盘启动:`（托盘启动时的判断）、`检测到`（3 秒监视器的状态迁移）、`未处理异常`（内部错误）、`收到命令:`（单实例管道）。

## 3. 轮转规则

| 文件 | 触发 | 去向 |
|---|---|---|
| launcher.log | > 512 KB | 覆盖写 `.old` |
| 服务 stdout/stderr | 每次启动前 | 覆盖写 `.prev`（保留上一次运行） |
| 服务 stdout/stderr | 运行中 > 8 MB | 覆盖写 `.prev` |

即：**任何时候都有「当前」+「上一份」可对比**。

## 4. 排障流程

1. **界面打不开 / 点了没反应** → 看 launcher.log 最后一次 `启动成功/启动失败`；失败时其后紧跟 `--- stderr 尾部 ---`，直接就是根因（端口占用、node 缺失、bin.js 路径错误等）
2. **服务崩了** → 托盘会弹「DSH 意外退出」气泡；看 `dsh-web-<port>.stderr.log` 最后的异常栈
3. **托盘没反应** → 看 launcher.log 有没有 `未处理异常`，再跑 `--selftest`
4. **怀疑杀错进程** → launcher.log 里每一次 `清理:` 都记录了 PID、进程名和理由，可复查

## 5. 常见报错对照

| launcher.log / stderr 关键字 | 含义 | 处理 |
|---|---|---|
| `启动失败` + stderr 有 `EADDRINUSE` / 端口被占用 | 端口 3080 被别的程序占了 | 看 `清理:` 记录；换端口（conf `port=`）或先停止占用程序 |
| `错误: node.exe 不存在` | 没装 Node 或不在标准路径 | 装 Node.js 或建 conf 指定 |
| `错误: 未找到 DSH 入口` | DSH_HOME 不对 | conf 里 `dsh_home=` 指向真实目录 |
| `WMI 枚举失败` | 系统 WMI 服务异常（罕见） | 重启 WMI 服务；netstat 兜底仍可用 |
| `进程提前退出, 退出码: N` | DSH 启动即崩 | 看紧接的 stderr 尾部 |
| `转发失败` | 托盘实例尚未就绪 | 稍等几秒再点 |

## 6. 自检报告 selftest.txt

覆盖：版本、目录、端口、配置文件、node.exe、DSH_HOME、bin.js、web profile、TCP 监听、HTTP 标记、健康状态、pid 文件、Chrome PWA 可用性、WMI 匹配进程、端口监听 PID、node --version、WMI/netstat 可用性。发 issue 时直接贴这份报告。
