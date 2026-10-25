# 构建与发布

`.github/workflows/build-release.yml` 将构建和发布分开：`main` 的 push、PR 和手动运行执行验证；`v*` 标签只发布已经通过验证的同一提交，不重新编译。

## 构建产物

Windows CI 保留 .NET 全套测试、x86/x64 原生组件模拟、Rust 通信协议测试、前端表格与状态顺序、HTTP/SSE 传输，以及最终打包引擎的计算和离线实战协议 smoke。Tauri 只构建一次，同时生成 NSIS 安装包和便携 ZIP。

验证全部通过后，上传 `windows-x64-release` artifact，保留 30 天，包含：

- `grim-dawn-optimizer-<version>-win-x64.zip`
- `GrimDawnOptimizer-<version>-x64-setup.exe`
- `SHA256SUMS.txt`
- `release-manifest.json`：版本、完整提交 SHA、构建 run ID 和两个包的大小/哈希。

Rust 协议逻辑位于 `src-tauri/crates/bridge-protocol`，生产桥直接引用同一库。CI 执行 `cargo test --locked --package grim-dawn-bridge-protocol`，不再为这五项测试额外编译 Tauri/WebView 的 Debug 版本；完整生产外壳仍通过 Tauri Release 构建验证。

## 缓存

- npm：按 `package-lock.json` 缓存下载包。
- NuGet：按 Windows 架构、实际 .NET 8 SDK 和项目依赖配置缓存包目录；命中后仍执行 restore/build/test。无需为了缓存引入 NuGet 锁文件。
- Cargo：缓存 registry 和 `src-tauri/target` 的依赖构建结果，缓存 key 包含工具链和 Cargo 配置。仅 `main` 写入共享 Rust 缓存，不缓存工作区自身产物；前端、资源和桥接引擎仍由本次构建生成。

第一次运行需要建立新缓存，不能用其耗时代表后续缓存命中的耗时。Release 的 LTO、panic、优化级别保持原配置。

## 发布顺序

1. 将版本修改和代码推送到 `main`，等待该完整提交的 `Build and test` 成功，并确认 `windows-x64-release` 已上传。
2. 将 `v<version>` 标签指向该提交并推送，例如 `v1.0.21` 对应配置中的 `1.0.21`。
3. 标签任务只接受同仓库、同提交、成功完成的 `main` push 构建，通过 artifact ID 下载产物，核对版本、提交、run ID 和每个文件的 SHA-256。
4. 先创建草稿 Release，四个资产上传并校验成功后才公开。

如果先推标签而构建尚未完成，发布会明确失败；等待对应主分支构建成功后重跑标签任务即可。若 artifact 已过期，重跑那个精确提交的主分支构建，再重跑标签任务。PR、其他分支或其他提交的产物不能替代。

上传中断会留下草稿，重跑可补齐；已公开版本只允许核对后无操作退出，不覆盖已有不同资产。标签任务串行执行，不互相取消。

## 本地验证

```powershell
node --test "tests/ci/*.test.*js"
cd src/GrimDawnOptimizer.Desktop/src-tauri
cargo test --locked --package grim-dawn-bridge-protocol
```

发布脚本测试使用模拟 GitHub API 和临时文件，不创建真实 Release。原生组件模拟与离线实战 smoke 不启动、连接或注入游戏。
