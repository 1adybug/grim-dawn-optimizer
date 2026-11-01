# 弹窗关闭后的鼠标输入修复

## 第一轮：鼠标捕获清理（仍有后续缺陷）

2026-11-01，用户反馈关闭游戏内弹窗后仍无法用鼠标移动人物。

在自己的隐藏窗口中复现了两个缺陷：按住鼠标关闭弹窗后，GetCapture 仍返回弹窗使用的
游戏窗口；ImGui 的待处理事件、按住的按键与软件光标状态也没有清理。原先的关闭流程
只修改 opened/capture 标志，之后的鼠标抬起消息绕过 ImGui，因此无法依靠其 Win32
后端释放先前取得的捕获。修复前的输入回归明确报告这两项失败。

固定在游戏画面里的弹窗现在直接向 ImGui 传入鼠标事件，避免调用后端会取得系统鼠标
捕获的鼠标按键分支；不改变游戏原本已有的鼠标捕获。F10、Esc、关闭按钮、焦点丢失
与渲染错误使用同一个清理流程，清空队列、鼠标、键盘及修饰键状态。正常关闭恢复游戏
最新请求的鼠标限制；焦点丢失消息无条件跳过恢复，避免前台状态更新延迟时框住桌面鼠标。
共享内存协议与锁血功能保持不变。

新增 `native/trainer/overlay-input-tests.cpp`，通过
`prepare-trainer-native.ps1 -RunInputTests` 运行。测试使用自己创建的隐藏 HWND 和
确定性的原函数桩，调用生产窗口过程及 DirectInput / Win32 钩子，验证：

- 首次打开前、按住鼠标关闭后、F10 与 Esc 关闭后，游戏的点击和抬起消息继续送达。
- 关闭后鼠标状态、缓冲数据、DIGDD_PEEK、鼠标定位/限制和键盘数据原样转发。
- 弹窗不取得 Windows 鼠标捕获；游戏原本拥有的捕获不被释放。
- 清理排队事件、按住的移动键/修饰键和软件光标，恢复最新鼠标限制。
- 焦点丢失、心跳过期、后台、多人与仅提醒状态下不拦截游戏输入。
- 打开时屏蔽键鼠，关闭后不重放弹窗期间的缓冲点击；原 API 失败保持原返回值。

主回归日志：`artifacts/trainer-native-build/mouse-input-verification.log`。
最后补充关闭/焦点边界的日志：`mouse-input-close-verification.log`。
这里的输入测试与独立 GPU 测试不等同于实际游戏人物移动验证。

修复后的 x86/x64 输入回归、原生功能模拟及 8 组 DX9/DX11 窗口化/独占全屏 GPU
检查通过。补充的 F10、既有游戏捕获、最新鼠标限制、焦点消息早于前台状态更新、
移动键/修饰键清理及 API 失败保持原结果等输入边界在两种架构上也通过。
NSIS 构建成功，并提取安装包内两份 DLL，与通过验证的源二进制逐一比对 SHA-256 一致。

修复安装包：
`artifacts/fullscreen-preview/GrimDawnOptimizer-1.0.24-mouse-fix-win-x64-setup.exe`。
SHA-256：`304A4B8657572A09D052901E768C8AD2E37E1CC35F0AE8592537D03E04DE7633`。
安装前完全退出游戏和工具；断开重连不能替换已加载的旧版 v6 DLL。

Windows 鼠标捕获的线程归属及释放约束见
[SetCapture](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcapture)
和 [ReleaseCapture](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-releasecapture)。

## 第二轮：核验已安装版本与输入交接

用户在安装第一轮修复后继续报告鼠标点击移动失效，切出游戏再切回可以恢复；
Ctrl+Alt+F10 和 × 关闭都可能触发。只读诊断确认安装目录两种架构 DLL 与第一轮
修复包一致，运行中的 32 位游戏实际加载的也是安装目录下的 v6 x86 DLL。
已加载 PE 的时间戳和映像大小与磁盘文件一致，排除了仅磁盘更新而游戏仍加载旧组件的情况。
现场弹窗已关闭、共享内存心跳正常、无故障、Windows 鼠标捕获为 0；因此第一轮
鼠标捕获清理未覆盖全部原因。一次快速快捷键加 × 关闭后的点击能移动，不能据此
推断人工按住修饰键时也正常。

新增回归在隐藏窗口中先把 Ctrl/Alt/鼠标按下交给游戏，再打开弹窗，随后松开。
旧代码会吞掉松开，游戏仍认为这些键被按住；DirectInput 缓冲事件也有同样问题。
修复前日志 `artifacts/trainer-native-build/shortcut-input-before.log` 有 4 项失败，
分别覆盖 Win32 的修饰键与鼠标、DirectInput 的修饰键与鼠标。

第二轮修复按收到按下的一方记录输入归属：

- Win32 的普通键、系统键和五个鼠标键：打开前游戏收到按下，弹窗打开时仍转发对应松开。
- 弹窗内按下的键，即使关闭后仍按住，重复与松开也不会落到游戏；下一次新按下正常送达。
- DirectInput 键盘及鼠标缓冲事件使用相同归属规则；轴移动与弹窗点击仍被屏蔽。
- 捕获期间消耗 PEEK 数据，关闭后保留 PEEK 语义；支持 DX3 的 16 字节记录及当前记录格式。
- 只查询数量时排空弹窗事件，但保留游戏已按下键的松开，供后续读取；不把弹窗点击重放给游戏。
- 焦点变化递增输入代次，避免切回游戏后残留弹窗按键归属屏蔽下一次新按下。

测试通过实际生产窗口过程和输入钩子，覆盖快捷键切换、× 所用关闭函数与 Esc，
并检查 PEEK、数量查询、时间戳保留、DX3 和焦点恢复。
安装核验脚本为 `src/GrimDawnOptimizer.Desktop/scripts/inspect-trainer.ps1`；
可用 `-ReferenceDirectory` 指定待核验二进制目录，默认与当前构建产物比对。
脚本仅枚举模块、读 PE 头与已有状态映射，不注入、不更新心跳、不发送按键或游戏命令。
现场记录在 `artifacts/trainer-native-build/installed-trainer-inspection.json`、
`live-mouse-failure.json`、`manual-shortcut-state.json`。

第二轮 x86/x64 原生功能模拟和全部输入回归通过，日志为
`artifacts/trainer-native-build/shortcut-input-after.log`。前端类型检查、生产构建和
NSIS 构建通过；提取包内两种架构的 DLL 与注入辅助程序，逐一与构建目录比对
SHA-256 一致。此次未重新运行会切换全屏的独立 GPU 窗口测试，避免打断正在运行的游戏；
先前 8 组 GPU 记录验证的是第一轮的渲染路径。第二轮修复尚未安装进用户的运行会话，
不能把隐藏窗口回归写成游戏实际移动验证通过。

第二轮本地未签名预览包：
`artifacts/fullscreen-preview/GrimDawnOptimizer-1.0.24-shortcut-input-fix-win-x64-setup.exe`。
SHA-256：`A0BC6576516838F08E4B4BA9C29926717D28E4E3A2063DBDCF3BCF6499B659B7`。
包内 x86 DLL：`C144A89A8F92A2EA122960A80B8A97EF7C97E7FDA8A49BA8F8B696C3BCB06212`。
包内 x64 DLL：`47F6922C8BF007D133E9D449A7A17A98AE41591CC17031F1361DCD5593EC1BCC`。
完整包校验记录：`artifacts/fullscreen-preview/shortcut-input-fix-verification.json`。
版本号仍为 1.0.24，区分此次预览应使用上述文件名和哈希。
以上是发布前的 1.0.24 预览记录；本轮正式提交将程序版本统一升级为 1.0.25，
CI 增加两种架构的隐藏窗口输入回归，正式产物使用 1.0.25 文件名与更新元数据。
安装需要完全退出游戏与工具；重连不能替换游戏已经加载的 DLL。
重启后应分别用 Ctrl+Alt+F10、×、Esc 关闭，并在不切出游戏的情况下点击地面验证移动；
打开后再松开 Ctrl/Alt 以及按住修饰键关闭均应覆盖。
