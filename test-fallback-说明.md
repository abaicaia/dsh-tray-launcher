# DSH Launcher v1.1 fallback 端到端测试 — 执行说明

> 写给执行者（OpenClaw / 小白）。测试目标：验证 DSH Launcher v1.1 的两处修复。
> 由**独立进程**执行（不能在 DSH 会话内跑——见下方红线）。

## 执行方式

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "J:\2\gonzuo\启动器\test-fallback.ps1"
```

脚本会自动输出每步 PASS/FAIL，末尾汇总，exit 0=全过 / 1=有失败。

## 测什么

| 测试 | 验证点 | 对应修复 |
|---|---|---|
| A | 有托盘 + DSH 在跑时，托盘**不误判**"不健康"去杀进程重启 | 健康检查兼容 alpha.5 token 认证 |
| B | **无托盘**时 `--stop` 不再静默放弃，fallback **本地直停** | 管道 fallback 修复 |
| C | 测试后 3080 托盘恢复正常、DSH 全程没被误杀 | 自保 + 回归 |

## 关键设计（为什么这么测）

- **B 的核心技巧**：先停 3080 托盘释放 mutex → 起一次性 `--start --port 3099`（它会持 mutex 等就绪）→ 立刻 `--stop --port 3099`。此时 stop 发现"已有实例"→ 转发 → 一次性进程无管道 → 转发失败 → **应走 fallback 本地直停**。日志出现 `转发失败, 无托盘实例` = 命中修复。
- **用 3099 端口**：绝不碰 3080（主会话），测完清理 3099。
- **B 试 3 次**：因为 `--start` 就绪很快（几秒就释放 mutex），窗口可能错过；3 次里命中 1 次即 PASS。

## 红线（违反会出事故）

1. **脚本会停 3080 托盘进程**（Stop-Process DshLauncher）——这是故意的，为了释放 mutex 测无托盘场景。
2. **但绝不杀 3080 的 node 进程**（Dsh 主服务）。脚本内置自检 B0：若 3080 掉了立即中止。
3. **不要在 DSH 会话里跑**（比如通过 DSH 的终端工具）——执行者必须是与 DSH 无关的独立进程（OpenClaw 正合适），否则停托盘会连带杀掉执行者自己。
4. 脚本会短暂弹窗（OneShotStop 的确认框）——脚本会清理卡住的进程，属预期。

## 预期成功输出（节选）

```
[PASS] B1 --stop 无托盘时触发 fallback(本地直停)
[PASS] C2 3080 PID 全程未变(主会话安全)
[RESULT] ALL PASS   (exit 0)
```

## 如果 FAIL

1. 收集完整输出 + `J:\2\gonzuo\启动器\logs\launcher.log` 尾部 30 行
2. 检查 3080 是否还活着（`Get-NetTCPConnection -State Listen -LocalPort 3080`）
3. 把结果发给小D（老板转达）分析

## 测试后必做

确认桌面「DeepSeek Harness」图标双击能打开、托盘在任务栏右下角正常。
