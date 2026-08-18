# 第三方声明

本项目的《恐怖黎明》存档加解密读取、区块遍历、`ARZ` / `ARC` 容器以及物品随机种子处理，参考并移植自
[Grim Dawn Item Assistant](https://github.com/marius00/iagd) 的 MIT 许可实现。对应许可全文见
[`LICENSES/IAGrim-MIT.txt`](LICENSES/IAGrim-MIT.txt)。本程序不依赖 Item Assistant 的可执行程序或在线服务。
物品随机种子部分保留了上游 `MinstdRandom`、`Jitter` 和 `ItemStatEngine` 的独立命名空间，方便逐文件核对。

运行时依赖还包括：

- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4)，MIT License。
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/)，MIT License。

《恐怖黎明》及其名称、数据和商标归 Crate Entertainment 所有；本项目是非官方、离线、只读的个人工具。
