# 锁血死亡计数

1.0.26（2026-11-04）：仅在“锁血”模式的生命保护实际触发时，角色已有死亡次数 +1。
沿用原有保留至少 1 点生命的触发条件；停留在 1 点生命时连续挡住伤害，
每次都独立计数，不做冷却或合并计数。无限模式、普通扣血、治疗、敌人伤害、
护甲和抗性计算不增加该统计。不开启功能、心跳过期、多人或桥接故障时不增加。

通过游戏原生 `Character::GetPlayStats`、`PlayStats::GetNumberOfDeaths`、
`PlayStats::IncrementDeaths` 和 `Player::ForceSave` 读取总数、递增并请求保存。
不解析或覆盖 `player.gdc`。ForceSave 设置待保存标志，磁盘写入由游戏后续处理，
不代表钩子返回时已同步落盘。不会执行真正死亡、经验惩罚、复活或专家角色死亡流程。

接口依据来自引用项目对同一组 1.3.1.1 x86/x64 Game.dll 的本机二进制检查；
当前项目仍校验 `NativeTrainerSession.SupportedHashes` 的完整 SHA-256 白名单。

| 接口 / 字段 | x86 RVA / 布局 | x64 RVA / 布局 |
| --- | --- | --- |
| Character::GetPlayStats | 0x4e490 / character + 0x11c8 | 0x63ec0 / character + 0x1790 |
| PlayStats::GetNumberOfDeaths | 0xe47e0 / stats + 0x28 | 0x3234e0 / stats + 0x44 |
| PlayStats::IncrementDeaths | 0x34cea0 | 0x3fdf60 |
| PlayStats::WriteProperties | 0x34ee80，写入该死亡字段 | 0x400950，写入该死亡字段 |
| Player::ForceSave | 0x32e940 / +0x3388 byte、+0x338c int 置 1 | 0x3d6030 / +0x4b28 byte、+0x4b2c int 置 1 |

游戏内仍使用现有 DX9 / DX11 提醒，显示“你已经死亡了 XXX 次”，持续 3 秒。
连续触发更新最新总数并延长时间，不接管输入。切换角色清除上一角色的提醒；
每次更新读取当前角色原有统计，真实死亡和更换角色后的次数不会丢失。
uint32 达上限时保持最大值，避免回绕为零；接口异常关闭全部效果并报告故障。

协议升至 v7，在原有字段末尾 offset 1132 追加 uint32 deathCount，总长 1136。
原有字段偏移不变；C#、原生组件、便携包和安装资源统一使用 v7。
已加载 v6 或更旧组件的游戏必须完全退出后重启，不能靠断开重连替换。

验证通过 x86/x64 模拟：已有次数、每次触发的递增与原生保存请求、连续触发、
非触发不计数、角色切换、溢出及接口故障；477 项 .NET 测试、桥接集成、
前端检查和构建也通过。独立 DX9 截图确认了提醒文案。完整 GPU 回归受前台焦点
丢失影响：DX9 设备丢失，DX11 因后台状态而停止绘制，未宣称完整矩阵通过。
独立模拟和渲染检查不连接游戏，实际角色的保存后退出重载仍需游戏内验收。
