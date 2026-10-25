# Tauri 桌面迁移

本次迁移把面向用户的桌面界面改为 Tauri 2、React、TypeScript 和 shadcn/ui。游戏数据库、存档解析、属性计算与候选搜索继续使用现有 .NET 8 实现，避免为更换界面重写数值模型。

用户最新要求所有分隔线延伸到分区两端：main 取消 1440 px 限宽与外层 padding，分区铺满工作区，阅读内边距移到分区内部。保留旧截图 `artifacts/tauri-ui/screenshots/analysis-compact.png` 的 canvas 顶栏/下划线文字页签、PNG 品牌、正文附近操作和单行候选，不恢复重复标题工具栏。未找到可直接 checkout 的对应 Tauri commit，且 Desktop 当前仍未被 Git 跟踪，因此这是按参考调整，不是源码回滚。保留 shadcn 预设 `b6HGdE1wo`、Inter/Noto、TanStack 表格/列宽记忆、RadioGroup 等增强。当前规范见 `DESIGN.md`、`UX-CONTRACT.md`；旧 WPF 文档仍保留。

## 布局与组件约定

- 62 px 顶部导航与页面使用同一 canvas 背景；左侧品牌后紧随四个无图标文字页签，角色与设置在右侧同组，品牌图标取消白底 chip。方案对比页签不展示结果数量，可见与无障碍名称均仅为“方案对比”，数量留在结果内容中。窗口恢复 Windows 原生标题栏和右上角操作按钮；应用顶栏不再绘制三色圆点或设置拖动区域，浏览器不显示原生窗口按钮。
- 整条 52 px 上下文工具栏删除，TabsContent 内保留 `sr-only h1`。main 无 max-width 和外 padding；main/TabsContent/page/layout 填满工作区高度，页面 grid gap=0 且列 stretch。顶栏/页脚保留 24/18/16 px 内边距，同一阅读边距移到正文分区内部，不能缩短分隔线。现有左指标/装备、右分析条件及 ≤960 px 条件上移规则保持。
- 角色库“数据源”标题右侧放“检测目录”；工坊“分析条件”右侧放“保存配置”，“已装备物品”右侧组合槽位数与“刷新角色”；结果摘要保留“调整条件”。保存的是分析设置，不要求已生成候选结果，仍遵循 `canSaveProfile` 和忙碌状态，不在结果头重复保存入口。
- 结果页 ≥1200 px 时 identity/三个指标/调整条件并为单行 summary grid；低于 1200 px 时 identity/action 一行、指标另行。结果数值为 22 px，≤520 px 为 20 px，上下边距 10–12 px；validity、替换/置信说明、装备本体 DPS、置信区间、预计生存等次级信息保留，不影响工坊指标样式。
- 表格上方合并为 40 px 的单一 comparison-toolbar，与候选项、表头和数据行共用 `--comparison-row-height`：左侧标题/槽位数，右侧“只看变化”与“重置布局”。App 通过表格 renderToolbar 组合控件，重置处理仍归表格；移除重复工具行和可见“前两列固定”，保留 sr-only 键盘/固定列帮助。工具栏保持单行；≤520 px 仅隐藏重置按钮文字，保留图标、aria-label 与 title。
- 四个入口使用现有 shadcn `Tabs/TabsList/TabsTrigger/TabsContent`，`TabsList variant="line"` 显示下划线选中态，取消分段组底色与白底凸起。根 Tabs 受控值为 `view`，变更沿用既有工作区导航，当前 TabsContent 使用相同 value；Radix 保留 tablist/tab/tabpanel、aria-controls 关联、单一 Tab 停靠点、左右方向键与 Home/End 焦点移动。
- 保留所有 shadcn 预设颜色与强制颜色映射；没有物理侧栏也不删除 `--sidebar-*` 变量，其中深色 `--highlight` 仍依赖 `--sidebar-primary`。原自定义银灰顶栏色不再定义当前布局。
- 品牌与关于页复用 `src/GrimDawnOptimizer.Desktop/src-tauri/icons/icon.png` 中的原盔甲 PNG，不换成 SVG；其他操作图标继续用 Lucide。 左上品牌显示为 `Grim Dawn Optimizer`，无障碍名称使用同一品牌并保留“返回角色库”动作说明；仅调整此入口，不扩大到其他标题。
- 桌面 Results 用两行共享网格：第一行由候选标题与摘要共享自然高度，第二行由候选列表与装备清单共同起始；candidate section 通过 row subgrid 保留完整语义。候选 1 对齐清单标题栏、候选 2 对齐表头，后续按 40 px 节奏对齐实际装备行。边线归入工具栏高度，表格容器取消额外上边框，固定列连续边界从 top=0 开始；候选按钮禁用按下位移。≤760 px 改为三行堆叠/横向候选条，仍保留项高，不要求跨列映射。
- 候选列表简化为 40 px 单行 `title + panelDps`，列表头统一标注 DPS，去掉每行重复替换说明、DPS 标签和勾图标。详细替换数仍在结果概要；按钮 aria-label 保留候选名、面板 DPS 数值及替换槽数，选择行为与状态不变。
- 工坊、指标、分析条件、装备、覆盖、数据源、角色库和结果详情均使用无外部间隙的连续分区。横线抵达列两边，竖线贯穿工作区高度；窄屏堆叠同步改变分隔方向，不给两端留白。不用圆角、阴影或背景块替代 Card。表单控件内部仍可保留 20/16 px 间距。
- 数据源与角色库竖线通高，read-note 和读取说明移入数据源列；工坊条件竖线通高，指标/装备/Disclosure 横线到列边；结果候选竖线通高、候选行线占满整列，summary/table/Disclosure 边线抵分区边。阅读标题/内容/操作保持内部 padding。表格总基础列宽不足容器时，仅末列“装备位置”在渲染时补足空白；不改写用户存储列宽或基础默认值，窄屏仍横滚。
- 固定列边界用贯通表头/表体的连续 overlay 边线，随实际固定宽度移动；仅横向溢出时显示 6 px gradient，强制颜色隐藏渐变、保留系统实线。overlay 不拦截单元格、调宽或滚动，不再逐格画阴影。
- 所有装备空值块占满单元格内宽并居中，正常长名称/来源仍左对齐。角色行局部 `translate:none` 防止 Button 按下 1 px 位移触发临时滚动条；列表仍保留 max-height 400 px 与多角色纵向滚动。
- 末列“装备位置”预测上限 400 px、显式调宽上限 4000 px，支持从超宽填充边界继续拖动；rendered fill 不写入偏好，只提交变化列，零位移拖动不生成偏好。
- 装备表格由 TanStack 管理列宽与固定列，并复用 shadcn Table 语义结构：每行单行显示，固定槽位/候选装备列，列宽可拖动且有键盘等效操作。固定列偏移随宽度更新，表头与数据列宽一致；固定单元格在浅/深色和强制颜色下均须以不透明背景遮挡滚动内容。
- 默认列宽按标题与全部候选的完整行数据估算，区分中文/Latin 字符宽度并限制合理范围；候选或筛选切换不改变估宽样本。手动拖动/键盘调整优先于默认值，以 version 1 localStorage 记录持久保存。固定列总预算为容器宽度的 60%，限制在 184–384 px；窄屏临时限制不覆盖已存偏好。双击分隔线或聚焦后按 Enter 清除该列偏好并恢复当前内容估算默认值，“重置布局”清除全部列偏好。存储异常或无效记录回落默认值，表格仍可使用。
- 长装备名、附件和来源可截断但必须能通过点击和键盘查看全文，不能只依赖 hover/title。表格仅一层横向滚动，调整列宽不制造页面级溢出。
- 分析目标与主题使用 RadioGroup；字段、提示、空态和角色/候选选择及常规操作分别复用 Label、Alert、Empty、Button，工作区切换由 Tabs 负责。共享折叠和空态也取消 Card 包裹，各控件保留对应按钮、单选组或页签语义。
- 浮动进度以完整工作区为中心，位置与最大宽度清除原侧栏偏移；shell 网格也不再保留侧栏列。表格只保留一层横向滚动容器。
- 明确字体栈为 `--font-inter: 'Inter Variable', 'Noto Sans SC Variable', sans-serif`，通过 `--font-sans` 应用。`@fontsource-variable/inter` 与 `@fontsource-variable/noto-sans-sc` 均声明 `^5.3.0` 并本地打包，不再依赖微软雅黑作为指定中文字体，也不在运行时下载字体。

## 结构与边界

- `src/GrimDawnOptimizer.Desktop`：React 界面和 Tauri 桌面入口。
- `src/GrimDawnOptimizer.Bridge`：本地 .NET 子进程，通过标准输入/输出的逐行 JSON 接收命令并返回界面状态。
- `src/GrimDawnOptimizer.Core`：既有的只读游戏数据、存档和配装分析内核。
- `src/GrimDawnOptimizer.App`：保留的 WPF 兼容入口及共享工作流源码，便于回归比较；新界面不通过嵌入 WPF 窗口运行。

前端经由 Tauri 命令与固定的计算进程通信，不直接执行任意程序。桥接进程沿用现有工作流状态机、来源检查、面板显示规则和候选计算，界面只负责选择、展示及交互反馈。长任务持续推送状态，取消命令不能被长任务阻塞。

目前支持 Windows x64。Tauri 本身支持多平台，不代表依赖 Windows 游戏路径和现有 .NET 目标的本项目已经通过 macOS/Linux 验收。

## 功能迁移验收

| 工作流 | 必须保持的行为 |
|---|---|
| 来源发现 | 发现安装和存档目录；保留手动选择与每类最近一次来源；不推测游戏当前活动角色。 |
| 目录操作 | 系统文件夹选择、直接编辑并明确应用；游戏/存档各只保存最近一次路径，不提供历史菜单或删除记录；取消保留草稿，失败不吞输入、不覆盖已保存值；来源改变使旧角色和结果失效。 |
| 角色载入 | 只读解析数据库及存档；首次加载进度；失败可恢复；多个角色明确选择。 |
| 刷新 | 保留仍存在的已选角色；清空临时增益；不能把旧数据库配上新存档。 |
| 主攻 | 默认武器攻击是明确选项；已失效的技能显示不可用原因，不能悄悄替换。 |
| 自动面板 | 主攻 DPS、默认攻击 DPS、生命与计算覆盖说明直接来自原有计算；主要 DPS 完整显示。 |
| 分析设置 | 普通、老兵、精英、终极；均衡、输出优先、生存优先；临时增益与特殊战斗场景。 |
| 特殊场景 | 战斗时长、目标数量、敌人额外伤害及逐类型玩家降抗；变化使已有结果失效。 |
| 分析执行 | 180 秒预算、十个候选、4,000 次固定种子抽样；可见进度、重复提交保护和取消。 |
| 结果 | 面板与实战 DPS 区分；有效生命、击杀/生存时间、置信范围、缺项说明与评分。 |
| 换装位置 | 全部 14 个槽位；当前/候选装备、组件、增幅及来源；仅附件变化也标记为变化。 |
| 单件对照 | 固定其余装备的单件预测；可选实测值与偏差仅用于对照，不拟合优化模型。 |
| 持久化 | 后端保存本地方案、每类最近一次目录与外观偏好，正常关闭等待写入；前端 localStorage 单独记住列宽偏好，可单列或全部重置；不保存临时增益和实测输入。 |
| 外观及键盘 | 跟随系统/浅色/深色；可见焦点；选择菜单键盘操作；窄窗口可滚动；减少动态效果。 |

## 数据保护

路径交互统一由 `src/components/path-input.tsx`、局部 CSS 和 `src/lib/path-picker.ts` 管理，覆盖游戏目录、存档目录和实战程序。输入改动后出现“使用”，Enter 同样提交，失焦不自动保存；浏览确认属于明确应用，取消保留草稿。游戏目录和存档目录各只记住最近一次路径，成功应用新值后替换，不保留历史菜单及删除操作。失败保留原始输入、关联 inline error，解除忙锁后恢复焦点。提交及选择器初始位置只去除外部空白和一对匹配的外层双引号，不解释 shell。无效输入和取消选择不改写最近路径，未应用草稿与后端保存值保持分离。

生产桌面使用 Tauri dialog plugin；开发预览经现有 loopback/Origin/Host 限制调用 `pickPath`，由独立 STA 的 Windows Common Item Dialog 返回 `{path}`。该返回不发布、不覆盖分析或实战快照；一次只开一个选择器，取消返回 null，超时及父进程 EOF 会请求关闭。这里只选择本机路径，不上传文件、不执行程序，后续应用仍走既有来源/实战路径校验。

开发系统选择器的字体模糊已定位到桥接 EXE 未声明 DPI awareness、独立 STA 未显式设定 DPI。`PathPicking/ThreadDpiAwarenessScope.cs` 在 `WindowsPathPickerDialog.Show` 创建 COM/HWND 前将当前选择线程设为 per-monitor v2，在窗口/COM 全部清理后还原；取消和错误仍走还原路径，预取消不改变 context。不改进程/系统全局缩放，不调整应用字体。Tauri 的 Tao 0.37.2 事件循环已设置 per-monitor v2，其原生 plugin-dialog 路径无需修改。旧桥接 manifest 证据在 `artifacts/bridge-before-dpi.manifest`。

分析迁移不增加存档写入。分析前后仍对源文件做身份和变化检查，导入期间或分析期间存档变化时，应刷新或舍弃结果；分析无法取得尚未落盘的装备状态。新增实战工具在用户明确连接后修改当前单人游戏会话内存，不直接编辑存档文件。其当前角色与遥测不替代分析所选存档角色，也不自动刷新配装结果。不得再用“全应用只读”概括两个流程。

每类最近一次目录、方案和外观偏好由后端存储管理；列宽偏好单独存于前端 localStorage，只保存布局数值，不保存装备数据或路径。测试必须使用隔离后端目录，并在测试列宽持久化时隔离前端存储，避免演示数据测试或主题/列宽测试改写用户实际偏好。

数据源保留规则改为 schema version 2，仍使用 `source-history.json`。游戏/存档数组各最多一项，并与对应 Last 字段一致；removed 兼容数组写为空，不保留已删除路径历史。读取 version 1 先取 LastGameDirectory/LastSaveDirectory，Last 为空才回退旧数组首个非空项，不因路径暂不可用而丢弃；规范化后写回 v2。写回失败仍返回读取到的单值设置，非法 JSON 不覆盖。本次不修改 ChooseRoot 原优先级、路径校验、现有空目录接受规则和检测成功时记忆行为。兼容删除 API 的忽略状态仅在当前会话有效，当前 UI 不再提供删除记录操作。

## 实战工具集成

本轮把旧项目 `grim-dawm-tool` 的“游戏控制”单页合并为第四个“实战工具”页签，不复制其 WinForms 外壳、玻璃配色、任务指引或已退役的独立护甲功能。现有分析功能、指定 shadcn 预设、Inter/Noto 字体、文字下划线页签与通边分隔线保持。

UI 归属为 `src/GrimDawnOptimizer.Desktop/src/components/combat-tools.tsx`、`combat-tools.css`、`src/lib/use-trainer.ts`、`trainer-types.ts`。连接与路径操作在前；九项功能按既有顺序放入单一 combat-feature-grid，移除分类标题及三组包裹。列数由 `repeat(auto-fit, minmax(min(100%, 280px), 1fr))` 随可用宽度变化，窄屏自然单列，grid gap 为 0。分隔线左右贯通到工作区边缘，最后一行不足整行也保留完整边界。这里只改变展示布局，不改功能顺序、命令、默认状态或后端。复用 Switch、RadioGroup、Button、Tooltip/Dialog 和 Alert；功能说明既可聚焦查看，也可点击打开完整说明。实战页隐藏分析角色 chip，不把当前游戏角色与已选磁盘存档混为一谈。

功能/快捷键严格保持九项顺序：F1 无限生命（合并护甲抗性）、F2 无限蓝量（保留预留）、F3 一击必杀、F4 移动速度、F5 最大攻击速度、F6 最大施法速度、F7 技能无冷却、F8 自动加血、F9 自动回蓝，均使用 Ctrl+Alt 修饰键。F12 全部关闭。倍率只允许 2/4/8/16X，每次工具会话默认 2X，选择倍率本身不启用；全部功能默认关闭。

实战路径独立保存在 `%LOCALAPPDATA%/GrimDawnOptimizer/combat-settings.json`。没有新文件时可只读导入旧 `%LOCALAPPDATA%/CairnCompanion/settings.json` 的路径，不写回旧文件；指定隔离数据目录时不读取旧偏好。功能请求与倍率不持久保存。

实战状态、忙锁和请求/确认与分析独立。连接、启动和改路径都是明确操作；不随页签导航执行。只在连接后注册全局快捷键，断开或退出即注销；F1–F9 要求角色可用，F12 清除请求不受 available 限制。连接中的重复命令与过期会话命令不可重放。

强化恢复规则是本轮明确决定，不能沿用原工具的暂存请求行为：多人模式、等待角色、心跳过期都会清除 requested，恢复可用时不会自动启用。断开、游戏退出、故障及应用关闭也清零。界面区分 requested 与 applied，未得到游戏确认不得显示已生效；开启未确认显示“待确认”，关闭请求尚未确认显示“关闭中”。全部关闭/断开不被配装分析的长任务阻塞。

保留原组件对 1.3.1.1 Game.dll 的完整 SHA-256 白名单、x86/x64 匹配、旧组件检查和协议校验。原药水功能按实际回复量、原生冷却、能量预留、单人/存活/前台/未暂停条件判断；原 0.6.0 只有二进制与双架构模拟回归，未完成真实游戏会话验证。

原生源码迁入 `native/trainer`。MinHook 上游提交 `c3fcafdc10146beb5919319d0683e44e3c30d537` 的 include/src/LICENSE 原样迁入 `vendor/minhook`，完整许可另存于 `LICENSES/MinHook-BSD.txt`；原生 bridge 仅因新目录调整 include 相对路径。来源及许可见 `vendor/minhook/SOURCE.md` 和 `THIRD-PARTY-NOTICES.md`。

浏览器开发仍使用受既有 loopback/Origin/Host 约束的真实本地服务，不能把开发预览描述成无副作用模拟器。本轮验证不启动游戏、不连接或注入真实游戏。新增 `python tests/desktop-bridge/trainer-smoke.py` 已通过真实 Release JSONL 子进程，覆盖九项默认关闭、倍率合法性/不隐式启用、离线启用拒绝、独立事件、占位文件路径校验/重启记忆、分析中全部关闭/断开及时响应且不取消分析、EOF 退出；测试命令白名单禁止 connect/launch，假文件未被执行且字节保持。原 `smoke.py` 已修复新 trainer 事件的独立分流并通过。最终 Release 再次通过两项 smoke。独立运行 `dotnet test tests/GrimDawnOptimizer.Bridge.Tests/GrimDawnOptimizer.Bridge.Tests.csproj -c Release --no-build --filter FullyQualifiedName~TrainerControllerTests`，19 项通过，覆盖连接取消/旧完成隔离、不可用清请求且不恢复、断开时 off/Dispose 异常仍释放快捷键、Poll 错误留在实战状态、关闭双向确认、非有限遥测与旧偏好只读导入。实现者另报告该测试程序集原 7 项加新 19 项共 26 项通过。浏览器UI、原生模拟与打包结果另行记录。下方既有三页分析验收均发生在实战接入前，不构成第四页或真实效果验证。

## 验证记录

已被无分组自适应网格取代的实战页分组布局浏览器验收（历史记录）：四个文字页签、九个开关初始全关且禁用、仅 2X 初始选中；选择 4X 不启用任何功能。无效路径不保存且保留输入；帮助 Dialog 的 Escape、旧路径输入 Dialog 的取消都把焦点还给触发按钮（路径 Dialog 证据发生在共享 inline PathInput 替换前）；外观调整不跳出实战页。390/960/约 1521 px 下没有正文横向溢出，分隔线通边，最后恢复跟随系统主题。`artifacts/tauri-ui/screenshots/combat-tools.png` 使用最新后端；`combat-dark.png`、`combat-390.png`、`combat-960.png` 是相同 UI 在旧后端离线错误状态残留修复前的布局/主题证据，不作为最终错误状态证据。`npm run check`、web build 通过；Vite 提示单 chunk 约 526 kB，不是构建失败。没有实际启动、连接或注入游戏，原生包结果单独记录。

2026-10-25，迁移前工作区基线：

```powershell
dotnet test .\GrimDawnOptimizer.sln --no-restore
```

结果：416 个测试通过，0 失败，0 跳过。此结果是计算及既有工作流基线，不证明 Tauri 窗口、桥接协议或新界面已经通过验收。迁移后的验证应分别覆盖 .NET 回归、桥接协议、前端构建、Tauri 构建与实际界面交互。

桥接迁移验证（2026-10-25）：

```powershell
python .\tests\desktop-bridge\smoke.py
python .\tests\desktop-bridge\smoke.py --complete
node .\tests\desktop-bridge\dev-transport.mjs
```

以上三项在历史迁移阶段均已通过。实际 .NET 子进程验证了演示数据、14 槽装备、默认攻击、配置原子校验、忙碌状态保护、取消恢复、完整候选分析、候选选择、单件实测对照、设置变化后结果失效、当时的目录历史删除不删文件（当前已移除此界面行为）、方案保存、标准输入关闭后退出及外观偏好恢复。测试使用临时目录，不改写实际用户方案和偏好。

迁移后再次执行 `dotnet test GrimDawnOptimizer.sln --no-restore`：原有 416 项与新增桥接 7 项全部通过，共 423 项，0 失败、0 跳过。普通进程 smoke 与 HTTP/SSE 测试也在最新桥接版本上再次通过。

开发传输测试使用真实桥接进程，覆盖 HTTP 请求、SSE 状态事件、错误返回、Origin/Host 检查、请求体限制及 JSON 校验。测试监听独立的本机 1421 端口，并显式发送应用要求的 Host，避免占用日常开发端口 1420。该测试需要 Node 22.18+ 或 Node 24，以读取 TypeScript 插件源码。

开发浏览器仅在 Vite 开发服务中通过 `scripts/dev-engine.ts` 接入本地计算进程，接口只允许本机同源请求，生产构建不会暴露此 HTTP/SSE 接口。可用 `GRIM_DAWN_DATA_DIR` 环境变量把浏览器验收状态存入隔离目录。桌面发布版通过 Tauri 命令和匿名管道通信。

Windows 安装包在目标机器缺少 WebView2 时可能需要下载其运行时；安装后的计算功能没有新增网络依赖。

文档/静态验证：更新后的 `DESIGN.md` 经 `npx --yes -p @google/design.md designmd lint DESIGN.md` 检查返回 0 错误、0 警告。移除路径历史 UI 后刷新严格 premium 静态审计，原始结果保存在 `artifacts/tauri-ui-audit.json`：退出码 1、5 项 error、0 warning。原五项识别误报保留，路径历史 Select 随组件移除而不再产生第六项；App 当前行号为 422。以下均经代码核查为 React/Radix 识别误报，不能将严格审计称为通过；渲染与交互使用实际浏览器证据。

| 位置（Desktop/src） | 静态发现 | 代码核查结果 |
| --- | --- | --- |
| `App.tsx:422` | Button 没有可识别操作 | 误报：设置按钮是 `DialogTrigger asChild` 子元素，Radix 提供打开行为；此前浏览器已操作设置和 Escape 关闭。 |
| `components/shared.tsx:196` | Button 没有可识别操作 | 误报：`ActionButton` 通过 `{...props}` 转发调用处的 `onClick`；目录载入和分析按钮均传入操作，完整演示分析已实测。 |
| `components/ui/dialog.tsx:72` | Button 没有可识别操作 | 误报：关闭图标位于 `DialogPrimitive.Close asChild`，操作由 Radix 注入。 |
| `components/ui/dialog.tsx:118` | Button 没有可识别操作 | 误报：可选底部关闭按钮同样由 `DialogPrimitive.Close asChild` 注入操作。 |
| `components/shared.tsx:48` | 原生 select 与 authored 所有权冲突 | 误报：这是导入的 shadcn `Select`，内部为 `SelectPrimitive.Root`；审计扫描器将标签名 casefold 后把它视为原生 select。 |

保留 `authored` 选择器声明和原始审计输出，没有用改动所有权或忽略源码来掩盖结果。Radix 源码核查不替代每个可选关闭入口的运行时验收。

`npm run check` 与 `npm run build:web` 已通过。此前原生窗口验收已确认目录选择对话框、分析取消，以及分析中关闭窗口后计算子进程退出并完成偏好写入。浏览器验收已确认指定预设的选择器展开及键盘操作、主题切换、分析取消和完整分析：返回十套候选，完整展示 14 个槽位，演示面板 DPS 为 21,775。

最新 macOS 风格的无边框窗口控制及能力声明已通过构建检查。实际窗口交互自动化被用户 Escape 中断，因此不声称这些新控制已经通过原生点击/拖动验收。桌面包另存为 `artifacts/tauri-macos-style-win-x64` 及同名 ZIP，避免覆盖用户仍在运行的旧程序。

完整浏览器分析曾暴露开发 SSE 接口在大状态快照触及缓冲阈值后错误关闭连接的问题，现已修复：写入背压等待 `drain`，尚未发送的状态只保留最新版，错误事件保序，并限制缓存大小。新增 `node tests/desktop-bridge/sse-backpressure.mjs` 通过实际 HTTP 套接字验证 1,215,974 字节的候选形状快照、大消息连接保持、版本合并、后续状态、错误保留及 UTF-8 分块。该大消息回归与使用真实计算程序的普通传输测试均通过。生产桌面版使用匿名管道，不使用这个开发接口。

保留 WPF 源码作为数值及工作流比较入口；待新界面真实存档验收和发布包验收完成后，再单独评估移除旧入口，不能在清理中删除未提交的旧界面修改。

已被最新分隔线布局取代的 Card、对齐、RadioGroup 和中文字体版本的浏览器实测（2026-10-25，历史记录）：

- computed body font 准确为 `Inter Variable, Noto Sans SC Variable, sans-serif`。约 1200 px 窗口中，工具栏标题与首张 Card 均位于 x=212，document width 与 viewport 相同，无全局横向溢出；折叠展开后的标题与内容均位于 x=232.67。
- 两个目录 Select/Button 配对均为 36 px 高、相同 y 坐标，且位于 Card 内。浅色和深色 Card 已截图检查；主题 RadioGroup 方向键切换后保持焦点，Escape 关闭设置。
- 以真实 960 × 680 布局 viewport 的 iframe 检查窄桌面布局：分析条件移到装备上方，结果表独立横向滚动。390 × 844 检查发现并修复导航按钮 flex 与候选条过宽，复测三个导航入口均可见。
- 完整演示分析返回十套候选、14 个装备槽位；大状态快照稳定，候选 03 切换成功。证据为 `artifacts/tauri-ui/screenshots/cards-sources-light.png`、`cards-results-light.png`、`cards-results-dark.png`、`cards-build-light.png`、`cards-build-dark.png`、`cards-build-960.png`。
- 连续配置改为独立 FIFO 队列并等待已有请求完成，已复核导航交错时 A→B 保序、导航/配置拒绝及同步异常后计数归零；目标选择鼠标与键盘复测保持焦点，控制台无 error/warn。

该版本类型检查、noUnused 与 Prettier 检查通过。以上证据不声称完成 1440 px 全套验收、新原生窗口按钮点击/拖动测试，或再次运行全部单元测试。

新装备表格由 `src/GrimDawnOptimizer.Desktop/src/components/equipment-comparison-table.tsx`、`equipment-table-model.ts` 和局部 CSS 负责，TanStack 管理八列的宽度与固定位置，shadcn Table 保留单一滚动容器。槽位/候选装备固定区域按容器宽度限制，其他列保留可滚动宽度；键盘左右调整 8 px、Shift 调整 24 px，Home/End 到边界，Enter/双击复位。单元格通过按钮打开共享 Dialog 查看全文，关闭回到触发位置。单件实测指标仍使用独立紧凑表格。

最终 `node tests/desktop-bridge/equipment-table.mjs` 已通过实际 TanStack handler/model 测试，覆盖拖动期间更新、固定列偏移、范围与键盘调宽、中英文字宽估算、全候选稳定默认、列宽持久化 reload、坏 JSON/非法宽度/禁用存储、单列与全局重置、筛选稳定、窄屏保留普通列宽及完整附件来源。列宽记录使用 `grim-dawn-optimizer.equipment-table-layout.v1`，只保存显式偏好；全局重置清除该键。该结果属于程序化验证，与下方浏览器实测分开记录。

此前侧栏版本的分隔线布局与 TanStack 内容估宽/列宽持久化浏览器实测（坐标和截图为历史记录）：

- 角色库、工坊与结果页已检查；960 × 680、390 × 844 工坊布局无裁切。截图为 `artifacts/tauri-ui/screenshots/lines-sources-light.png`、`lines-build-light.png`、`lines-build-960.png`、`lines-build-390.png`。
- 14 个装备行均为 40 px 高。最终演示默认列宽按槽位/候选/当前/组件/组件来源/增幅/增幅来源/位置分别为 80/124/112/88/192/96/264/208 px，总宽 1164 px。表格容器宽 736 px，scrollLeft 到 418 时固定表头 x=422/502 保持不变；390 px 宽度也已验证横向滚动。
- 实际拖动当前装备列 112→182 px，候选列 Shift+Right 调整 124→148 px；跨页面往返和 reload 后前三列仍为 80/148/182。双击当前列分隔线恢复为 112，候选 148 不变，reload 后仍为 80/148/112。重置布局后恢复默认 80/124/112。此前候选 03/筛选切换保留手动列宽也已实测。
- 单元格 Enter 打开完整来源 Dialog，Escape 关闭后焦点回到原单元格按钮。最终表格截图为 `artifacts/tauri-ui/screenshots/lines-results-light.png` 与 `lines-results-scrolled.png`。
- 新打开的页面控制台无 errors；旧主窗口保留此前依赖热更新的历史 error，不能称其整个历史记录无错误。

最终浏览器已验证列宽持久化、按全部候选内容估算默认值以及单列/全部偏好清除。以上不声称本轮已完成深色全套、原生交互或全部单元测试重跑。

此前分段页签版本的浏览器实测（历史记录）：点击三个工作区均可切换；从配装工坊按 ArrowRight 自动选中方案对比且焦点留在对应页签，Home 选择角色库，reload 后 End 选择方案对比。当前页签是唯一 tabindex=0，其余均为 -1；active trigger 的 aria-controls 等于 panel.id，panel 的 aria-labelledby 等于 active trigger.id。

该历史版本实际 953 px viewport 的 document scrollWidth 也是 953，无全局横向溢出；shell grid gap=0，滚动后 `.app-chrome` 保持 y=0，页签组 y=10。fresh reload 的 console errors 为空。历史截图 `artifacts/tauri-ui/screenshots/tabs-390.png`、`tabs-results.png`、`tabs-build.png` 已保存。此前非 Tabs 的 topnav 截图属于更早过渡稿，侧栏截图属于旧布局。

此前带外边距的下划线顶栏与正文操作版本的浏览器实测（历史记录）：约 1521 px 主窗中 `.app-chrome` 高 62.67 px，`.app-header` 已不存在，h1 保留 1 px 的 sr-only 形式；PNG 的 naturalWidth 为 1024，页面无横向溢出。十个候选均为 40 px 高，点击候选 03 后详情与装备 caption 同步；从工坊按 ArrowRight 进入结果，仅选中页签 tabindex=0。

390 px iframe 中三个页签与设置均可见，候选横向滚动，“调整条件”在结果标题右侧；点击后进入工坊，“保存配置”可见且可用。960 px 工坊无裁切，保存/刷新分别位于分析条件和已装备物品区域。最新截图为 `artifacts/tauri-ui/screenshots/underline-build.png`、`underline-results.png`、`underline-390.png`。`npm run check`、Prettier 与 diff 检查通过，临时预览文件已清理。

本轮记录只证明上述浏览器观察与轻量检查；原生窗口按钮/拖动、最终原生包结果另行记录，不用旧版本验收代替。

此前全宽分区版本浏览器实测：main 宽 1521.33 px、padding=0，与顶栏和页脚同宽；candidate-panel/build-config 从 y=62.67 延伸至 y=1214，正好接到 footer top=1214。当时 current-overview 与 result-summary 自身 padding=0、指标内部 padding=24 px，来源说明在数据源列内；无全局横向溢出，浏览器 error logs 为空。结果指标的旧内边距已被当前紧凑 summary 规则取代。

390 px 下数据源横分区占满宽度；结果候选可横滚，指标两行的边线相接，表格视口全宽。表格横向滚到装备位置后，槽位与候选装备两列仍固定可见。960 px 结果分区正常。最新截图为 `artifacts/tauri-ui/screenshots/full-bleed-build.png`、`full-bleed-results.png`、`full-bleed-sources.png`、`full-bleed-390.png`、`full-bleed-960.png`；390 px 截图已回到页面顶部保存。旧 1440 px 正文限宽已从现行规范移除；原生窗口初始尺寸和旧版本验收保留各自含义。

此前紧凑结果区浏览器 QA（共享 40 px 行对齐改动前的历史记录）：summary=83.05 px、toolbar=44 px，table width/scrollWidth/viewport 均为 1301 px，无全局横向溢出，console errors 为空；抽查空值块占满单元格内宽且居中。960 px 横滚至增幅/来源/装备位置时，槽位与候选列固定，6 px gradient 从表头连续到末行。390 px toolbar 单行可用，三个指标并列。“只看变化”在合并工具栏中已实际开启再关闭，最终保持关闭。证据为 `artifacts/tauri-ui/screenshots/compact-results.png`、`compact-summary-960-scroll.png`、`compact-summary-390.png`。早期截图中的方案对比数量徽标仅为历史状态，最终页签可见与无障碍名称均为“方案对比”。

角色行真实点击后 clientHeight=scrollHeight=69 px、translate=none，没有按下导致的临时滚动条；证据为 `artifacts/tauri-ui/screenshots/character-row-stable.png`。表格新增回归通过，覆盖 canonical/rendered 宽度分离、末列填宽、只提交变化列、2800 px 宽窗从可见边界拖动 +24 px 与零位移拖动；末列显式上限 4000 px、预测上限 400 px。最新类型检查、Prettier 与 diff 检查通过，临时预览已关闭清理；原生包结果另行记录。

共享 40 px 行对齐浏览器验收：约 1521 px 下十个候选、comparison-toolbar、表头与 14 个数据行全部为 40 px；候选 1/标题栏、候选 2/表头、候选 3–10/前八个数据行的顶部偏移均为 0。960 px 摘要两行时仍对齐；390 px 横候选条与单行工具栏保持 40 px。有效截图为 `artifacts/tauri-ui/screenshots/aligned-results-960.png`、`aligned-results-390.png`；宽屏同名截图曾被并行导航覆盖，不作为证据。

移除历史菜单前的共享 PathInput 浏览器验收（历史记录）：角色库手输无效目录并 Enter 后，原始草稿保留、aria-invalid=true、焦点回输入；打开历史后 Escape 仍保留草稿。实战页输入带双引号的不存在 exe 并 Enter 后，同样保留草稿、内联错误及焦点。原有保存值未变，恢复可见草稿没有隐式提交。前端 TypeScript/noUnused 检查通过。后端报告 Release 桥接 41 项通过，其中 15 项选择器用例包含三个真实 COM 对象配置但替换 Show 的实例；配置/取消与隔离传输有程序化证据，本轮未实际 Show 系统窗口，不声称原生窗口交互通过，也未启动/连接游戏。

仅记最近一次目录的界面验收：两个路径 Input 均为 36 px，历史入口和数据源 combobox 数量均为 0；输入带双引号的现有路径并 Enter 后成功规范化；1280 px viewport 下没有 document 横向溢出。390 px 下两行输入与浏览按钮无裁切，截图为 `artifacts/tauri-ui/screenshots/recent-source-390.png`，临时 iframe harness 已删除。此记录不替代持久化迁移回归或系统选择器 DPI 验证。

最近路径与 DPI 回归：实现代理报告主测试 425 项、桥接 44 项共 469 项通过，Release 桥接 44 项也通过。新存储用例覆盖旧 v1 压缩、Last 优先/缺失回退、路径暂不可用、取消写入、坏 JSON 不覆盖；FileShare.Read 阻止迁移写回时仍返回正确单值且原文件字节不变。三个 DPI 用例使用真实 COM 但替换 Show，验证进入 per-monitor v2、取消/异常后还原原 unaware context、预取消不改 context。这里验证的是 DPI context 与清理行为，未实际 Show 选择器，也没有高缩放清晰度截图；不把程序化结果等同于视觉验收。ChooseRoot 优先级与原校验规则均未改动。

无分组实战网格验收：1280 px 下为 4 × 320 px，grid x=0/width=1280，document 无横溢出，九个开关仍禁用且关闭。真实 iframe 在 600/390/960 px 分别显示两/一/三列；截图为 `artifacts/tauri-ui/screenshots/combat-flat-600.png`、`combat-flat-390.png`、`combat-flat-960.png`。真实点击“无限生命说明”并 Escape 后焦点回原按钮；forced-colors 已补系统色 outline，避免阴影隐藏后内部线消失。此次仅调整布局与左上品牌名称，无后端行为改动，未启用游戏效果。

最终品牌/自适应实测：约 1521 px 主窗为五列，每列 304.26 px，无 document 横溢出；`artifacts/tauri-ui/screenshots/combat-flat-grid.png` 已覆盖为最新 Optimizer 品牌与扁平功能网格。390 px 下品牌无裁切、四页签可见，截图为 `brand-optimizer-390.png`。结合此前 1280/960/600/390 px，列数依次为五/四/三/二/一。`recent-source.png` 已在最终预览重启后更新，展示最近单值目录 UI 和 Optimizer 品牌。全部临时 viewport harness 已删除。

扁平实战网格与 Optimizer 品牌冻结后再次刷新 strict audit：仍为原 5 项 React/Radix 识别误报，位置保持 App:422、shared:196/48、dialog:72/118，无新增发现；原始 JSON 已更新。DESIGN lint 0 错误/0 警告，文档 diff 检查通过。本轮未重复后端测试。
