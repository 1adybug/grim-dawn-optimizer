# 自动药水充能修复（1.0.24）

血药、蓝药共用的旧判断要求 `GetStatus() == 1` 且
`GetCooldownRemaining() <= 0`。第一次使用后，即使还有一次充能，游戏也可能
返回状态 3 和正在恢复充能的倒计时，导致自动功能等待而不使用剩余充能。

现在从已经验证类型的 `Skill_ChargePotion` 调用游戏导出的
`Skill::GetCooldownCharges()` 获取当前剩余充能。只有剩余充能大于 0，且槽位
状态为 1 或 3 时才允许尝试。耗尽充能或其他状态仍会阻止使用；倒计时继续
作为状态数据读取，但不再单独决定能否使用。血药和蓝药使用相同规则。

执行仍调用游戏原本的 `ActivateHealthPotionSlot` / `ActivateManaPotionSlot`，
由游戏检查实际使用条件。没有清除或修改冷却、补充充能，也没有直接写入
血量或蓝量。缺失资源阈值、蓝量预留扣除、单人/存活/前台/未暂停和心跳
限制保持；每 250 毫秒检查，每种药水的两次尝试至少间隔 500 毫秒。

## 本机游戏接口依据

检查对象为仓库白名单中已适配的两个 1.3.1.1 `Game.dll`，只读检查磁盘 PE
导出与反汇编，没有连接、注入或修改真实游戏进程。

| 接口或字段 | x86 RVA / 偏移 | x64 RVA / 偏移 |
| --- | --- | --- |
| `Skill::GetCooldownCharges` | `0x3ccda0` | `0x491bb0` |
| 当前剩余充能字段 | `0x158` | `0x1ec` |
| `Skill::GetChargeCooldown` | `0x3ccd90` | `0x491ba0` |
| 充能恢复倒计时字段 | `0x15c` | `0x1f0` |
| `HotSlotOptionSkill::GetStatus` | `0x29f190` | `0x31c140` |
| `HotSlotOptionSkill::GetCooldownRemaining` | `0x29f140` | `0x31c0a0` |

- x86 SHA-256：`580ad41cc1c3e4cab6d29e71cb60d3a638079582eb3b5da6eec5ffea54627b7d`
- x64 SHA-256：`cd7ecd7942d719f96b42aff4896f8c25d52152b4a41191f71a4a66ea48cecad2`

两个架构中，技能处于空闲状态时，`GetStatus` 在充能恢复倒计时大于 0 时
返回 3，否则返回 1。`GetCooldownRemaining` 优先返回该充能恢复倒计时；
该值本身不能证明没有剩余充能。x64 `StartCooldown` 消耗当前充能，
`EndCooldown` 恢复当前充能，进一步确认 `GetCooldownCharges` 返回当前数量。
新增导出加入 x86 名称映射，并纳入组件初始化的必需接口检查。

## 回归验证

`native/trainer/tests.cpp` 同时模拟血药、蓝药从两次充能开始：第一次使用后
倒计时仍大于 0，250 毫秒时被重试间隔阻止，500 毫秒时使用第二次。还覆盖
充能耗尽（包括 CD 为零）、恢复一次充能、资源缺口已不足、其他不可用状态
和分别开启两种自动功能。原有预留、暂停/后台、死亡、关闭、多人及心跳
过期检查继续执行。

新场景在旧就绪规则下于“第二次使用”断言失败；修复后 x86/x64 模拟均通过。
同时修正构建脚本对 Windows 原生进程负退出码的处理，避免断言失败后仍
报告成功；使用实际失败的模拟程序确认脚本返回失败，再执行修复后的回归。

验证命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File src/GrimDawnOptimizer.Desktop/scripts/prepare-trainer-native.ps1 -RunTests
```

这些是二进制检查与离线模拟结果，尚未完成真实游戏中的双充能实测。
共享内存协议保持 v5；更新后必须完全退出并重启游戏，已加载的旧组件不会
因重启工具而在当前游戏进程内替换。

本地其余验证：474 项 .NET Release 测试、75 项发布脚本测试、15 项 Rust
协议/便携启动器测试、前端类型检查及生产构建均通过；真实 Release 后端的
`trainer-smoke.py` 和 `smoke.py` 通过。版本一致性与两个架构的新增导出名称
也已核对。这些检查没有启动或连接真实游戏。
