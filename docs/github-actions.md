# 构建与发布

`.github/workflows/build-release.yml` 将构建和发布分开，并复用 `build-windows.yml`：`main` 的 push、PR 和手动运行执行验证；`v*` 标签只发布已经通过验证的同一提交，不重新编译。只有主分支 push 构建拥有草稿暂存所需的 `contents: write`，PR 和普通手动构建仅有读取权限。

## 构建产物

Windows CI 保留 .NET 全套测试、x86/x64 原生组件模拟与隐藏窗口输入回归、Rust 通信协议测试、前端表格与状态顺序、HTTP/SSE 传输，以及最终打包引擎的计算和离线实战协议 smoke。Tauri 只编译一次并生成 NSIS 安装器，再将同一主程序封装为单文件便携 EXE。主分支发布构建生成签名；PR/普通手动构建使用 `tauri.unsigned.conf.json` 生成不签名的安装器，不读取发布私钥、不暂存 Release。

本地构建目录生成：

- `GrimDawnOptimizer-<version>-win-x64.exe`
- `GrimDawnOptimizer-<version>-win-x64-setup.exe`：安装版
- `GrimDawnOptimizer-<version>-win-x64-setup.exe.sig`：安装器签名
- `latest.json`：版本、安装器下载 URL 与签名
- `SHA256SUMS.txt`
- `release-manifest.json`：`schemaVersion: 2`，记录版本、完整提交 SHA、构建 run ID 和四个公开文件的大小/哈希。

主分支验证全部通过后，将安装版、便携版、签名与 `latest.json` 上传到该版本的 Release 草稿，来源 manifest 记录在草稿正文的 HTML 注释中；标签任务读取它并重新核对文件哈希。这个流程不使用受账户配额限制的 Actions artifact 存储。正式 Release 提供两种 EXE 和两份更新元数据，正文保留隐藏的构建来源记录以支持严格的幂等重跑。

便携启动器内嵌完整程序、计算引擎、实战组件和许可证，首次运行校验并释放到本机缓存目录，随后运行其中的 Tauri 主程序，不执行安装流程。程序依然使用系统 WebView2；用户设置位置保持不变。

Rust 协议逻辑位于 `src-tauri/crates/bridge-protocol`，生产桥直接引用同一库。CI 单独测试协议库和 `portable-launcher`，不再为这些测试额外编译 Tauri/WebView 的 Debug 版本；完整生产外壳仍通过 Tauri Release 构建验证。便携 EXE 还会实际执行无窗口验证和解包，比较所有文件哈希，覆盖中文路径、缓存复用、损坏修复、进程并发，并对释放出的引擎运行原有计算及离线实战 smoke。

## 缓存

- npm：按 `package-lock.json` 缓存下载包。
- NuGet：按 Windows 架构、实际 .NET 8 SDK 和项目依赖配置缓存包目录；成功 restore 后立即保存，命中后仍执行 restore/build/test。无需为了缓存引入 NuGet 锁文件。
- Cargo：缓存 registry 和 `src-tauri/target` 的依赖构建结果，缓存 key 包含工具链和 Cargo 配置。仅 `main` 写入共享 Rust 缓存，即使后续测试失败也保留已编译依赖；不缓存工作区自身产物，前端、资源和桥接引擎仍由本次构建生成。

第一次运行需要建立新缓存，不能用其耗时代表后续缓存命中的耗时。Release 的 LTO、panic、优化级别保持原配置。

## 发布顺序

1. 将版本修改和代码推送到 `main`，等待该完整提交构建成功，并确认对应版本草稿中的四个文件已完整暂存。
2. 将 `v<version>` 标签指向该提交并推送，例如 `v1.0.23` 对应配置中的 `1.0.23`。
3. 标签任务只接受同仓库、同提交、成功完成的 `main` push 构建，通过 Release/asset ID 下载草稿中的四个文件，核对版本、提交、run ID 和 SHA-256。
4. 校验成功后生成发布说明并公开已有草稿；发布阶段不编译、不替换正确的公开资产。

如果先推标签而构建尚未完成，发布会明确失败；等待对应主分支构建成功后重跑标签任务即可。若草稿或文件缺失，重跑那个精确提交的主分支构建，再重跑标签任务。也可以对已有 `v*` 标签手动触发本工作流，仍会执行同样的来源校验。PR、其他分支或其他提交的产物不能替代。

上传中断会留下草稿，重跑可补齐；已公开版本只允许核对后无操作退出，不覆盖已有不同资产。版本已经公开后，主分支后续提交仅验证构建，不改该版本；发布新变更需要递增版本号。标签任务串行执行，不互相取消。

## 本地验证

```powershell
node --test "tests/ci/*.test.*js"
cd src/GrimDawnOptimizer.Desktop/src-tauri
cargo test --locked --package grim-dawn-bridge-protocol --package grim-dawn-portable-launcher
```

发布脚本测试使用模拟 GitHub API 和临时文件，不创建真实 Release。原生组件模拟与离线实战 smoke 不启动、连接或注入游戏。

## 更新签名与安装版行为

仓库 Actions Secret `TAURI_SIGNING_PRIVATE_KEY` 存放签名私钥，可选 `TAURI_SIGNING_PRIVATE_KEY_PASSWORD` 存放密码。已在本机生成并配置当前仓库 Secret，本机副本位于 `%USERPROFILE%\.tauri\grim-dawn-optimizer.key`；请备份，不提交到 Git。公钥固定在 `tauri.conf.json`。后续版本必须使用同一私钥，否则已有客户端无法验证更新。缺少私钥时主分支发布构建明确失败。签名用于应用更新校验，与 Windows Authenticode 代码签名分别管理。

`latest.json` 仅指向当前标签的安装器，不能指向便携启动器；暂存/验证脚本同时校验版本、平台、URL、签名一致性与四份 SHA-256。安装器包含 `installation.json`，便携封装不包含该标记，因此同一主程序在便携目录运行时不会触发安装式更新。更新器仅在安装版启用，通过 HTTPS 读取 GitHub `releases/latest/download/latest.json`，使用 Tauri 官方 updater 校验签名，安装前再次确认分析空闲、实战已断开，并在退出前关闭本地引擎。

界面默认启动后及每 6 小时检查；自动安装仅在空闲时开始，可分别关闭自动检查或自动安装，关闭检查会同时停止后台安装。`update-settings.json` 原子保存偏好，损坏时停用后台检查并允许重新保存。

浏览器交互回归夹具位于 `src/GrimDawnOptimizer.Desktop/qa/updater-harness.html`，在 Vite 开发服务下访问 `/qa/updater-harness.html?scenario=manual`（或 `automatic`、`network`、`current`、`portable`）。它通过官方 mockIPC 使用真实 hook 与 UI，不下载、不安装，不包含在生产入口中。

## 1.0.23 本地验证记录

- .NET 全套测试 474 项（425 个主测试 + 49 个桥接测试）、发布脚本 75 项、Rust workspace 17 项全部通过；前端类型检查和正式 Tauri/NSIS 构建通过。
- `package-installer.ps1 -RequireSignature` 使用 `examples/verify-update.rs` 和与运行时相同的 minisign 验证库，对实际安装包检查内置公钥。真实签名通过，替换安装包内容后明确拒绝；不执行安装器。
- 实际便携 EXE 通过无窗口自校验、Unicode 目录解包、哈希比较、缓存复用/修复、并发释放及内置引擎的计算/离线实战协议 smoke；确认便携包不存在 installation.json。NSIS 实际生成的输入脚本包含安装版标记、引擎及四个实战组件。
- 两包及更新元数据已通过本地 release staging/verify；`artifacts/release-local-validation` 的来源字段是测试夹具（非 GitHub 构建证明），不能用于正式发布。
- 实际浏览器验证实战离线功能/倍率/自动连接偏好重载、键盘开关、390/960 像素布局、深色主题和 Dialog Escape/焦点恢复。模拟安装版 IPC 覆盖加载、最新版本、自动与手动检查、连接时延后安装、关闭检查后取消待安装、签名/网络失败与重试；自动安装失败后未出现连续重试。界面证据为 `artifacts/tauri-ui/screenshots/combat-memory-1.0.23.jpg`。夹具支持 HMR 卸载，不重复创建 React root。
- DESIGN lint 为 0 error/0 warning。严格 premium 审计仍为 5 项既有 React/Radix 识别误报，无新增发现；原始结果在 `artifacts/premium-audit-updates.json`，不能称为严格审计通过。

本轮未连接或注入真实游戏，未实际运行安装器，也未通过公开 GitHub 版本执行跨版本升级。真实游戏效果与公开版本间的完整安装重启仍需后续实测。代码与标签尚未推送，未创建或公开 Release；当前仓库更新签名 Secret 已配置。
