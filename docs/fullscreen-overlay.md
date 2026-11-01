# 全屏弹窗与锁血

2026-11-01：安装包图标显式使用当前应用 ICO；ICO 由现有应用 PNG 生成。
无限生命下方增加“无限 / 锁血”，默认无限，选择模式不会开启生命开关。
锁血保留至少 1 点生命，正常伤害与治疗继续生效，不附带无限模式的护甲、抗性保护。
触发计数由生命扣除钩子递增，游戏内绘制 3 秒文字提醒；连续触发延长显示时间。

Ctrl+Alt+F10 在游戏中显示 / 隐藏实战弹窗，Esc 或关闭按钮也可关闭。
`native/trainer/overlay.cpp` 使用 Dear ImGui 的 Win32、DX9 和 DX11 后端，
直接绘制到游戏帧缓冲，适用于独占全屏、无边框和窗口化。没有额外桌面弹窗。
打开时捕获 Win32 / DirectInput 键鼠输入，阻止游戏重置鼠标位置；通知不捕获输入。
打开前游戏已经收到按下的键，松开事件仍交给游戏；弹窗里按下的键即使关闭后仍按住，
重复与松开事件也不会进入游戏。Win32 消息和 DirectInput 缓冲输入都按这个归属交接。
失去焦点、多人模式、故障或主程序心跳过期时关闭弹窗并恢复输入透传。

游戏内操作经过有界共享内存队列交给同一个 TrainerController 校验并原子保存；
全部关闭另有独立标记，可越过已满队列。v6 共 1132 字节，保留原字段偏移。
重连丢弃旧队列，旧版已加载组件需要完全重启游戏。原有游戏版本哈希、单人模式、
过期关闭及独立分析状态约束保持生效。第三方来源与许可在 `vendor/imgui/SOURCE.md`。

## 验证

- Release .NET：425 项核心测试、52 项 Bridge 测试通过，包括模式持久化、
  游戏操作同步、真实内存映射队列确认、队列异常与独立紧急关闭。
- 原生 x86/x64 模拟通过：无限 / 锁血、致命伤害截断、低于 1 点的存活生命、死亡不复活、
  触发计数、治疗、多人 / 过期 / 关闭透传、队列饱和，以及已有速度 / 药水回归。
- `prepare-trainer-native.ps1 -RunTests -RunOverlayTests`：x86/x64 × DX9/DX11 ×
  窗口化/独占全屏共 8 组独立 GPU 测试通过。DX9 检查 Windowed=false，DX11 检查
  GetFullscreenState=true；读取真实帧缓冲像素验证弹窗和中文提示。覆盖 Reset / ResizeBuffers、
  通知过期、心跳失效、仅弹窗捕获输入的状态判断，以及 DX9 视口 / DX11 计算着色器状态恢复。
- Rust workspace 17 项、发布脚本 75 项测试通过。前端类型检查、生产构建、Prettier、
  JSONL 实进程、HTTP/SSE 隔离和背压、状态顺序检查通过。
- 浏览器实际检查确认锁血选择保持生命开关关闭，重载后仍选中锁血。最终浏览器连接超时，
  没有把最后一次窄屏检查记为通过。严格 UI 静态审计仍为既有 5 项 React/Radix 识别问题，
  没有新增发现；DESIGN lint 为 0 errors / 0 warnings。
- 本地 NSIS 安装包构建成功；提取安装包图标与应用 ICO 的 32×32 像素逐点一致。
  安装包包含两种架构的 v6 DLL、注入辅助程序、计算引擎与 Dear ImGui MIT 许可。

原生日志：`artifacts/trainer-native-build/fullscreen-verification.log`。
帧缓冲原图在同目录的 x86/x64 子目录；预览为
`artifacts/tauri-ui/fullscreen-popup.png` 和 `fullscreen-notice.png`。
本地测试安装包：
`artifacts/fullscreen-preview/GrimDawnOptimizer-1.0.24-fullscreen-win-x64-setup.exe`。
SHA-256：`55F95FBA861055182018D051AB4505DB5CEBA3B063C595CB6AA535F56123499E`。

GPU 测试只创建自己的窗口和设备，没有启动、连接或注入实际游戏。
实际游戏、驱动组合与其他覆盖层共存的兼容性仍需真实会话验证。
本地预览没有生成更新签名，没有发布到 GitHub 或替换已安装应用。
