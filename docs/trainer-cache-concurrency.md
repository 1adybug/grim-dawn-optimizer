# 实战组件缓存并发修复

2026-11-11：修复 `TrainerBridgeCache.PrepareAsync` 在并发创建、修复同一份 DLL 缓存时出现的 Windows 文件移动拒绝访问。

## 原因与处理

原实现允许多个调用在校验失败后同时覆盖目标文件，只捕获了 `IOException`。实际复现还会抛出 `UnauthorizedAccessException`；即使补齐异常类型，损坏缓存尚未被其他调用修好时，也不能通过一次事后 SHA-256 校验安全地认定发布成功。

现在使用按目标完整路径生成名称的 Windows 命名 Mutex，将校验、发布和损坏修复放在同一个跨进程临界区。相同内容与路径的调用依次处理，不同缓存条目独立处理。正确的缓存直接复用；需要修复时先写同目录临时文件，再替换目标，最后清理自己的临时文件。

源 DLL 仍异步读取。Mutex 持有期间不使用 `await`，确保获取和释放发生在同一线程；等待可响应取消，获得废弃 Mutex 后会重新验证缓存。真正的访问或占用错误继续传给调用方，不把错误文件当作可用组件。

## 回归验证

| 检查 | 结果 |
| --- | --- |
| 完整 Release .NET 解决方案 | **510/510 通过，零跳过**：计算/应用452，桥接58 |
| 缓存专项连续5个独立测试进程 | 全通过；每轮6个用例，含10轮首次发布、10轮损坏修复，每轮8个并发调用 |
| 游戏式 DLL 映射占用 | 32个并发调用复用已加载的正确缓存，安装目录原文件仍可更新 |
| 真实文件占用错误 | 损坏缓存锁定时明确失败，保留原文件且清理临时文件；解锁后成功修复 |
| 四个应用进程同时发布/修复 | 两种场景均通过，校验最终完整字节与单一目标文件、无临时文件残留 |
| Node 发布流程测试 | **75/75 通过**，使用模拟 GitHub 响应 |
| Rust 协议与便携启动器单元测试 | **15/15 通过** |
| x86/x64 原生模拟与弹窗输入测试 | 两个架构均通过 |
| JSONL、离线实战协议、状态顺序、装备表、HTTP/SSE、大状态背压、安装器占用检查 | 全部通过 |
| TypeScript 检查 | `npm run check` 通过 |

`tests/desktop-bridge/cache-concurrency.py` 使用四个独立 PowerShell 7 进程调用实际编译的托管缓存代码，先等待共同起跑信号，再处理同一缓存路径。测试 DLL 只包含标记字节，不加载或注入游戏。此验证已加入 Windows CI 工作流。

```powershell
dotnet test GrimDawnOptimizer.sln --configuration Release
dotnet test tests/GrimDawnOptimizer.Bridge.Tests/GrimDawnOptimizer.Bridge.Tests.csproj --configuration Release --filter FullyQualifiedName~TrainerBridgeCacheTests
python tests/desktop-bridge/cache-concurrency.py
```

专项重复运行日志保存在 `artifacts/trainer-cache-validation/stress-1.log` 至 `stress-5.log`。

## 同轮检查修复的 HTTP 连接重置

开发传输测试还复现了超大请求返回413时的 `ECONNRESET`。原代码在请求流的 `for await` 循环中提前返回，会销毁流，可能截断413响应或使后续保持连接请求失败。此行为也见 [Node.js 流文档](https://nodejs.org/api/stream.html#readablesymbolasynciterator)。

现在超限后立即返回413、清空缓冲区，并继续丢弃剩余请求内容；读完后结束处理，保留64 KiB缓冲限制。回归测试使用单连接保持连接池，分别发送70 KB和1 MB请求，每种4次，逐次确认完整413 JSON及随后正常请求的200响应。修改前该回归复现连接重置；修改后完整HTTP/SSE测试通过，并连续3个独立进程复跑通过，日志在同目录的 `http-1.log` 至 `http-3.log`。
