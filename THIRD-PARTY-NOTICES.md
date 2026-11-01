# 第三方声明

本项目的《恐怖黎明》存档加解密读取、区块遍历、`ARZ` / `ARC` 容器以及物品随机种子处理，参考并移植自
[Grim Dawn Item Assistant](https://github.com/marius00/iagd) 的 MIT 许可实现。对应许可全文见
[`LICENSES/IAGrim-MIT.txt`](LICENSES/IAGrim-MIT.txt)。本程序不依赖 Item Assistant 的可执行程序或在线服务。
物品随机种子部分保留了上游 `MinstdRandom`、`Jitter` 和 `ItemStatEngine` 的独立命名空间，方便逐文件核对。

运行时依赖还包括：

- [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4)，MIT License。
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/)，MIT License。
- [WPF UI](https://github.com/lepoco/wpfui)，MIT License。

桌面界面随程序本地打包以下字体，均采用 SIL Open Font License 1.1：

- **Inter Variable**：`@fontsource-variable/inter`（依赖范围 `^5.3.0`），许可全文见 [`LICENSES/Inter-SIL-OFL-1.1.txt`](LICENSES/Inter-SIL-OFL-1.1.txt)。
- **Noto Sans SC Variable**：`@fontsource-variable/noto-sans-sc`（依赖范围 `^5.3.0`），许可全文见 [`LICENSES/Noto-Sans-SC-SIL-OFL-1.1.txt`](LICENSES/Noto-Sans-SC-SIL-OFL-1.1.txt)。

以上许可文件原样复制自对应已安装的官方 Fontsource 包，保留其版权声明。

实战原生组件使用 [MinHook](https://github.com/TsudaKageyu/minhook)，来源提交为 `c3fcafdc10146beb5919319d0683e44e3c30d537`。`vendor/minhook/include`、`vendor/minhook/src` 与原始 `LICENSE.txt` 从已有实战工具的该提交原样迁入，未修改上游内容；来源记录见 `vendor/minhook/SOURCE.md`。完整 BSD 许可及随附版权声明见 [`LICENSES/MinHook-BSD.txt`](LICENSES/MinHook-BSD.txt)。

《恐怖黎明》及其名称、数据和商标归 Crate Entertainment 所有；本项目是非官方、离线的个人工具。配装分析只读游戏文件；独立实战功能修改当前游戏会话内存，不直接编辑存档文件。

全屏游戏弹窗使用 [Dear ImGui](https://github.com/ocornut/imgui) v1.91.9b，提交 `f5befd2d29e66809cd1110a152e375a7f1981f06`。核心代码及 Win32、DirectX 9、DirectX 11 后端从上游原样复制；MIT 许可全文见 [`LICENSES/Dear-ImGui-MIT.txt`](LICENSES/Dear-ImGui-MIT.txt)，来源记录见 `vendor/imgui/SOURCE.md`。游戏内中文字体读取本机 Windows 字体，不随软件分发。
