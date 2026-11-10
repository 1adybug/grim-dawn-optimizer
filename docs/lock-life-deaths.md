# 锁血死亡计数

自 1.0.27 起（2026-11-10），锁血在伤害达到当前生命值时拦截扣血，并立即回满生命。
同一轮 `GameEngine::Update` 内的多段致命伤害合计只记 1 次死亡，下一轮可独立触发。
1.0.26 最初的实现把生命留在 1 点，后续伤害段可能各触发一次。
已核对两种架构的原生 `IncrementDeaths` 指令，每次调用递增 1。

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
本次修正沿用 v7 布局。完全退出游戏再启动，重新连接后加载修正后的组件。

x86/x64 模拟验证了立即回满生命、同轮五段致命伤害只记一次、下一轮重新计数，
以及保存请求、角色切换、溢出和接口故障。普通伤害，包括恰好留下 1 点生命的
伤害，仍交给游戏处理。477 项 .NET 测试和离线桥接检查通过。
实际游戏里的伤害序列和角色存档重载仍需实测。
