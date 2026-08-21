# 架构与可信度边界

## 数据流

```text
Steam / GOG / 手动目录
        │
        ├─ ARZ / ARC ── SHA-256 ── SQLite 可重建缓存
        │
        └─ player.gdc + transfer.gst|gsh + formulas.gst（只读）
                              │
                        CharacterSnapshot
                              │
       实物 × 合法组件 × 合法增幅 → 签名分组 / Pareto 剪枝
                              │
                Beam Search → 一槽 / 两槽局部搜索
                              │
              确定性战斗报告 → 固定种子 Monte Carlo
```

核心边界由 `IGameDataSource`、`ISaveReader`、`ICharacterStatEngine`、`ICombatSimulator` 和 `ILoadoutOptimizer` 分隔。WPF 层只处理目录选择、方案编辑、进度、取消和结果展示；`MainViewModel` 额外维护数据源、构筑、搜索和结果四个工作区的导航状态，不改变核心契约。

## 只读保证

解析器全部使用 `File.ReadAllBytes` 或以 `FileAccess.Read` 打开的流。应用在优化前后对选定存档目录下的存档文件计算 SHA-256；任何变化都会转成显式错误。唯一写入位置是 `%LOCALAPPDATA%\GrimDawnOptimizer` 下的 JSON 方案、目录历史、独立的 `ui-settings.json` UI 偏好和 SQLite 缓存。

## 搜索确定性

相同角色、游戏数据库、方案和时间预算下，候选排序、签名比较和 Monte Carlo 种子稳定。由于时间预算受机器速度影响，截止点附近的候选集合可能不同；这也是界面不宣称全局最优的原因。

## 未建模效果

不能可靠映射的 DBR 记录作为 `UnsupportedEffect` 随候选传播。0 个未知效果为高置信度，1–2 个为中置信度，更多为低置信度。未知效果不向得分贡献伪造的零值。
