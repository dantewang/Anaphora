# Anaphora

读取一个游戏窗口的画面，从中提取状态信息，并以 overlay 显示在游戏上方的 Windows 桌面应用。

## 提交约定（硬性）

本工程的 commit message **只写正文，不附加任何 Claude / AI 相关的 trailer**。
不要写 `Co-Authored-By: Claude ...`，也不要写 `Claude-Session: ...` 或
`🤖 Generated with ...`。这条约束覆盖全局的 attribution 指示。

## UI 设计约定（硬性）

**所有使用者能看到的界面，美术与 UI 风格都尽量模仿《明日方舟：终末地》本身**——
主界面、配置界面、overlay 全都适用。overlay 尤其重要：它要叠在游戏画面上，风格不一致
会非常刺眼。

**任何 UI 在动手写 Avalonia 代码之前，先做成 artifact 给用户看。** 先定视觉再落实现，
不要直接写 XAML 然后让用户在跑起来的程序里挑毛病。

风格要点（从 `captures/` 的实拍里提取，后续可补充）：

- 深色底，纯黑到深灰的层次，不带蓝调
- 功能色高饱和：主色琥珀/金黄，状态色用青蓝与黄绿，警示用红
- 描边细，直角与 45° 切角混用，大量平行四边形与斜切分隔
- 无衬线窄体，字重对比强，数字用等宽感的字形
- 图标是白色实心剪影，压在深色圆形或圆角方形底板上
- 元素之间靠细线和留白分隔，不靠色块堆叠

## 技术选型

| | |
|---|---|
| Runtime | .NET 10（Windows-only，不考虑跨平台） |
| 抓取 | Windows.Graphics.Capture（WinRT），经 `net10.0-windows10.0.26100.0` TFM 自带的投影调用 |
| GPU | D3D11，绑定用 Vortice.Windows |
| Win32 互操作 | Microsoft.Windows.CsWin32 源生成器（从官方 metadata 生成，签名由生成器保证） |
| UI（主界面与 overlay） | Avalonia 12 |
| MVVM | CommunityToolkit.Mvvm |

选 C# 而非 Rust 的理由：这条链上的 C# 依赖（WinRT 投影、Avalonia、Vortice、CsWin32）
API 稳定，而 Rust 侧的 `windows` / `winit` / `egui` crate 破坏性更新频繁，模型训练语料
里混杂多个不兼容版本，实际写起来会大量消耗在版本错配的编译错误上。

## 项目结构

```
src/Anaphora.Core       profile 模型、ROI 定义、序列化。无 Windows / UI 依赖
src/Anaphora.Analysis   纯 CPU 的读数器：条填充率、图标状态、头像匹配。可单测
src/Anaphora.Capture    WGC 会话、客户区定位、限帧丢帧、GPU 上的 ROI 图集拷贝与 staging 读回
src/Anaphora.Overlay    Avalonia 透明置顶穿透窗口，跟随游戏窗口
src/Anaphora.App        入口 + 配置界面（选窗口、标 ROI、调阈值、实时预览）
tests/Anaphora.Analysis.Tests
tests/Anaphora.Core.Tests
tests/Anaphora.Analysis.Tests/fixtures  连拍帧的遮罩版：只留 HUD，其余涂黑，可进仓库
tools/Anaphora.CaptureProbe  一次性可行性探针：WGC 能不能拿到这个游戏的真实像素
profiles/               每个游戏一个 JSON：窗口匹配、ROI 定义、阈值、头像哈希
```

单进程、两个窗口。Capture 与 Overlay 共享一个进程，不做 IPC。

`Core` 与 `Analysis` 目标 `net10.0`（无平台依赖，跑得快、好测）；其余目标
`net10.0-windows10.0.26100.0`，`SupportedOSPlatformVersion` 压到 `10.0.19041.0`
以保留 Win10 2004 的运行能力。包版本集中在 `Directory.Packages.props`（CPM）。

## Avalonia 12 注意

Avalonia 12 是较新的大版本，与 11.x 有 API 差异，且模型的训练语料主要是 11.x。
**遇到任何 Avalonia API 问题，先查 avalonia-docs MCP，不要凭记忆写。**

### avalonia-docs MCP 是厂商工具，注意商业倾向

这个 MCP 由 Avalonia 官方（AvaloniaUI OÜ）维护。它的 API / 文档查询是可信的，但
**涉及工具链的指引会默认推荐自家商业套件 Avalonia Accelerate，且不会提示授权前提**。
`migrate_diagnostics` 就是一例：它要求安装需要 license 才能打开的 DevTools，
却完全没提这件事。**跟随任何 MCP 给出的工具链安装指引前，先确认授权与费用。**

### DevTools：目前没有接，是有意的

Avalonia 12 的 breaking change 之一是移除了免费的 F12 DevTools ——
`Avalonia.Diagnostics` 停在 11.3.20，**不要再引用它，也不要用它的
`AttachDevTools()` API**。官方替代品 `AvaloniaUI.DiagnosticsSupport` +
`AvaloniaUI.DeveloperTools`（`avdt`）属于 Avalonia Accelerate，**需要 license
才能实际打开 DevTools**。本工程未授权，因此这两者都不引用、也不要再自行加回。

**已决定：不接 DevTools。** 主 UI 的 XAML 复杂度不高，overlay 更几乎不需要可视化
调试，现在引入是纯预支成本。**不要主动加回任何 DevTools 依赖。**

将来真被布局问题卡住时，首选 **Avalonia Accelerate Community Edition**（$0，
需在 Avalonia portal 注册账号领 license）。个人开发者（含商业项目）、≤5 并发用户的
非 Enterprise 组织、教育机构均符合资格；Enterprise 界定为 >250 用户或年营收
>€1,000,000。含 Dev Tools、VS 扩展、Parcel 打包，不含 Accelerate 的 UI 组件与技术支持。

另有 `ClassicDiagnostics.Avalonia`（MIT 社区移植，API 仍是 `this.AttachDevTools()`），
但截至写下时仅 0.0.2-preview、下载量约 400，**不推荐**：调试工具本身不稳定时，
分不清 bug 是自己的还是它的。

## 已确定的技术约束与坑

- **不要用 WPF 的 `AllowsTransparency=true`**：会让窗口回退软件渲染。选 Avalonia 的
  一个主要原因就是它在 Windows 上默认走 `WinUIComposition`（底层 DirectComposition），
  透明窗口是硬件合成的。
- Overlay 窗口：**Avalonia 12 里是 `WindowDecorations="None"`，不是 11.x 的
  `SystemDecorations`**。加上 `Background=Transparent`、`TransparencyLevelHint=Transparent`、
  `Topmost`、`ShowInTaskbar=False`、`ShowActivated=False`，再对
  `TryGetPlatformHandle().Handle` 设置
  `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，并调
  `SetLayeredWindowAttributes(alpha 255)`。**`WS_EX_TRANSPARENT` 只有配 `WS_EX_LAYERED`
  才会把点击穿透给其他进程**；和 Avalonia 的 `WS_EX_NOREDIRECTIONBITMAP` 同时存在时仍能正常
  渲染（开发机实测，`WindowFromPoint` 确认穿透）。常驻显示，**不需要点击交互**。
- overlay 窗口只有面板那么大（`SizeToContent`），不是铺满游戏客户区；每 100ms 按游戏客户区
  重新定位，并按 `客户区宽度 / RenderScaling / 1920` 整体缩放，保证占屏比例和视觉稿一致。
  游戏不在前台或最小化时隐藏。
- CsWin32 在 AnyCPU 下生成不了 `GetWindowLongPtr`/`SetWindowLongPtr`（只有 64 位导出），
  扩展样式用 `GetWindowLong`/`SetWindowLong` 即可。
- **截图验证 overlay 时要用带 `CAPTUREBLT` 的 `BitBlt`**；`Graphics.CopyFromScreen` 截不到
  分层窗口，会让人误以为 overlay 没显示。
- 在还没有配置界面之前，状态面板折叠、轴回到开头、跳过一步、退出都在**托盘菜单**里。
  状态面板开关记在 `%LOCALAPPDATA%\Anaphora\settings.json`，日志在同目录 `app.log`。
- Overlay 的渲染实现必须放在接口后面。若 Avalonia 的透明窗口出现 artifact 或性能问题，
  退路是手写 `WS_EX_NOREDIRECTIONBITMAP` + DirectComposition + Direct2D 窗口，
  届时只应替换这一个模块。
- **帧率影响**：无边框全屏下 DWM 可能走 independent flip / MPO 直接扫描输出游戏
  swapchain。任何常驻置顶窗口都会让这条快速路径失效，退回合成路径，掉几个百分点的帧。
  这是本方案的固有代价，已被接受，不要试图"优化"掉。
- WGC 帧按游戏 present 速率推送，**没有内建限帧**。处理不过来时在 `FrameArrived` 里
  立即 `Dispose` 丢帧，不要排队。新版 Windows 上可探测
  `GraphicsCaptureSession.MinUpdateInterval` 直接限速。目标更新率 15–30 Hz。
- **全程不把整帧下到 CPU**：每个 ROI 一次 `CopySubresourceRegion`，直接从捕获纹理
  拷进一张小 staging 纹理（`RoiAtlas` 负责排布），每帧只 Map 一次。**不用 shader**：
  原生分辨率的纯拷贝不需要 HLSL 工具链，也没有采样滤波要操心；原计划的 256×256
  也放不下 648 宽的技能点条，图集按内容定尺寸。4K 下实测 648×192（整帧 1.5%），
  每帧处理约 1ms。
- **读数器不知道图集的存在**：`FrameView` 带原点偏移，每个 ROI 通过一个"坐标重定向"
  的视图去读自己的槽位，客户区坐标原样使用。没有把 ROI 重映射到图集坐标系，所以不会
  出现第二次边缘取整和第一次不一致——图集读数与整帧读数逐位相同，8 帧 fixture 上有测试
  强制这一点。窗口化游戏的标题栏/边框偏移走的是同一个机制（`FrameView.ClientArea`）。
- 客户区在捕获纹理里的位置 = `ClientToScreen(0,0)` 减 `DWMWA_EXTENDED_FRAME_BOUNDS`
  左上角。WGC 窗口捕获覆盖的是 DWM 扩展边框，**不是** `GetWindowRect`（后者含不可见的
  缩放边框）。
- `CaptureSession` 在进程不是 PerMonitorV2 时直接抛异常，不静默出错。
- 限帧：`nextDue += interval` 按整数个间隔推进（不是从"现在"起算），长期速率才是设定值
  而不是游戏帧时间的某个倍数；卡顿后重新对齐而不是连发补帧。支持时同时设
  `MinUpdateInterval = 0.9 × interval`，让系统先挡掉大部分帧。
- `CaptureOptions.VerifyEvery`：每 N 帧额外整帧读回一次，在 CPU 上重建图集逐字节比对
  GPU 结果。诊断用，**任何窗口都能跑，不需要游戏**。开发机上对 Claude 窗口实测 151/151 一致。
- COM/WinRT 桥（`IGraphicsCaptureItemInterop` 等）在 Capture 里仍是手写的，没用 CsWin32：
  这几个接口要从 CsWinRT 投影对象上转型，正是 CsWin32 的 COM 输出和 CsWinRT 封送打架的
  地方，而手写版本已在游戏上验证过。普通 Win32 函数走 CsWin32。
- 用 `Direct3D11CaptureFramePool.CreateFreeThreaded`，格式 `B8G8R8A8UIntNormalized`，
  2 个 buffer，在自己的线程处理。
- ROI 坐标存成相对游戏客户区的归一化值，换分辨率不用重标。
- 反作弊：不注入、不读内存、不 hook，行为等价于 OBS。

### 已验证：WGC 对目标游戏可行（2026-09-06）

目标游戏是 **Endfield**：`Endfield.exe`，窗口类 `UnityWndClass`，窗口标题 `Endfield`，
Unity 引擎。测试环境为无边框窗口 3840×2160，4K 显示器 + Windows 150% 缩放。
用 `tools/Anaphora.CaptureProbe` 实测结果：

- `GetWindowDisplayAffinity` 返回 `WDA_NONE`——**没有设 `WDA_EXCLUDEFROMCAPTURE`**，
  第一优先级的风险排除，方案成立。
- 出帧正常：3.01 秒 156 帧，约 52 fps，跟着游戏 present 速率走，印证了"WGC 不限帧、
  必须自己丢帧"这条。目标 15–30 Hz 意味着大约每两帧丢一帧。
- 内容真实：非黑像素 88.4%，平均亮度 65/255，原分辨率切片里 UI 文字和图标边缘锐利，
  足够做条填充率与图标状态判定。
- 捕获纹理 3840×2160，row pitch 15360 = 宽×4，这台机器上暂时没有 padding；
  **读回时仍必须按 `RowPitch` 逐行拷贝**，不能假设它等于宽×4。
- `IsBorderRequired = false` 被直接接受，**没有先调 `RequestAccessAsync` 也没抛异常**，
  黄框可去。本工程不依赖这一点（有黄框也接受），但既然免费就用上。
- `GetDpiForWindow` 返回 144（150%）。**进程必须 PerMonitorV2**，否则 `GetClientRect`
  会返回 2560×1440 的逻辑尺寸，而 WGC 纹理是 3840×2160 的物理尺寸，ROI 会整体错位。
  探针里是运行时调 `SetProcessDpiAwarenessContext`，正式进程走 `app.manifest`。

### 待验证（尚未确认）

- **连携提示头像：3 号位还没有参考值**，那局里 3 号的连携从没被提示过。另外参考值只对示例
  队伍有效，换队伍要重新登记。
- **新哨兵（血条青色）在背包、地图、菜单这类界面上还没实测过。** 两局连拍里的非战斗帧
  都判对了，但没专门打开过这些界面。低血量（<8%）时会暂时判为不可读，也没实拍过。
- **读数器和轴跟踪器已在真实战斗上离线验证过**（fight1，75 帧 1Hz 全分辨率，用 `replay`）：
  两轮示例轴都跟上了，第二轮在技能点为 0 时正确显示 `1重击` 被条件阻塞。
  **但 App 本身（实时 overlay）还没在游戏机上跑过**；游戏机上的 `serve` 也是旧版，没有 `/hud`。
- **窗口化模式的客户区偏移没实测过**。算法和测试都覆盖了（fixture 贴进带边框的画布），
  但 DWM 边框的实际数值只有游戏切到窗口模式才能验证。
- 长时间运行下的稳定性：游戏切分辨率 / alt-tab / 显示器切换时 `GraphicsCaptureItem`
  的 `Closed` 与帧池重建路径都还没测。

## 抓取目标

不需要 OCR，也不需要 ML 模型：

- **数值条 / 冷却条（无数字）**：沿长轴采样一条像素线，找填充色到背景色的跳变位置，
  得到 0..1 填充比例。
- **图标状态**：冷却态通常变暗或去饱和，取 ROI 平均亮度 / 饱和度做阈值；需要区分
  是哪个图标时再叠一层小模板匹配。
- **角色头像**：对参考图集做感知哈希（dHash / pHash）或归一化互相关，取最近邻。

## 目标 HUD：Endfield 的四个关注项（2026-09-06 实测）

素材是 `captures/combat` 的 89 帧战斗连拍（1 Hz，战斗段约在第 5–42 帧）。下面的像素
坐标是 3840×2160 下目测的近似值，**存 profile 时归一化，精确边界等标注界面做出来再定**。

游戏机制（由用户给出）：多角色组队。普攻分多段，前几段轻击、最后一段重击。同一套
队伍下，重击 / 技能 / 连携 / 大招的**施放顺序**会极大影响攒大招的速度和打出的伤害。
玩家群体管这个顺序叫「**轴**」。

**本程序的最终目的是提示"现在该按什么"，不是集中显示状态。** 状态读数是轴提示的输入。
现阶段的 overlay 先按"状态面板 + 一块下一步提示区"来做，**之后会有较大重构**，
不要为了现在的布局做难以拆掉的抽象。

### 轴的记法（用户定义，硬性）

轴**不按角色设计和提示，只按槽位 1、2、3、4**——重击和连携也记在槽位上：

| 记法 | 含义 | 实际按键 |
|---|---|---|
| `N` | 槽位 N 的技能 | 数字键 N |
| `NE` | 槽位 N 的连携 | E（提示头像里最左边那个先触发） |
| `N重击` | 槽位 N 的重击（N 为当前主控） | 普攻打满最后一段，**没有单独按键** |
| `N大招` | 槽位 N 的大招 | **长按**数字键 N |

文件里也接受 ASCII 别名 `NH`（重击）、`NU`（大招），规范写法是上表的中文。

### 已定的 overlay 决策（2026-09-13）

- 位置 **A**：角色脚下，屏幕水平居中、约 60% 高度。
- 状态面板**可折叠**，前期展开着用来核对读数对不对。overlay 本身鼠标穿透、不可点，
  所以折叠开关不在 overlay 上。
- **重击怎么识别还没有答案**（游戏里重击没有单独按键），之后再讨论。在那之前，
  跟踪器靠"后面某一步被识别到"来越过重击步骤。
- **不读键盘输入。** 步骤是否完成只从画面推断。

**示例轴**（就是 `captures/combat` 里那支队伍，主控为 1）：

```
第一轮：3 → 2 → 4E → 1重击 → 1E → 1 → 2E → 1
```

- 开局按 3、2，触发 4 的连携 → `4E`
- `1重击` 同时触发 1 和 2 的连携，**1 在前** → `1E`，然后 `1`，再 `2E`，再 `1`
- **第二轮的约束**：不能在 2 可以按（技能点回复）之前打出 1 的重击，否则会先触发 2 的连携，
  顺序就乱了。

这说明轴不只是一个固定序列：某些步骤有**前置条件**（"等 2 可放"），而前置条件正是从
HUD 读数里来的（技能点、连携提示的顺序）。overlay 要能把这类条件显示出来。

frame-034 恰好是这支轴里 `1重击` 之后的瞬间：连携提示是 1 大、2 小，对应 `1E → … → 2E`。

### 1. 技能点（全队共享，三段）

**已用 `sample` 量准（3840×2160）**：三段的填充区分别是 x `1599–1802` / `1818–2021` /
`2037–2240`，节距 219，整条含边框 `1596–2244`。纵向填充核心 y `1952–1970`，但只有
**y 1957–1962 是稳定的亮带**（97.5% 亮），上下两侧是渐变过渡，采样窗口必须避开。

颜色实测：满段 `#F8F800`（纯黄，B≈0），正在填充的当前段 `#F8F8F8`（纯白），
空轨道 `#101010`。三者在 RGB 空间里离得很开，一个容差 0.30 的门就能分开。

**只数满格的黄段，输出整数 0..3，不需要小数。** 用户明确说不关心数值精度：绝大多数
技能要一整段才能放，只有极少数例外，所以"能放几个技能"就够了。

**只采每段末尾 20%**（`tailFraction`）。采整段会让覆盖率正比于填充比例，"刚好满"和
"九成满"只差几个百分点，特效一扫就翻转；只看末端就变成"亮/不亮"的干脆判断。
实测满段覆盖率 0.95–1.00，空段 0.00，阈值 0.65 两边都有余量。

亮地面（`captures/live/fight1`）上照样读得对：填充是不透明的，5 帧逐一核对全对。

### 2. 连携冷却（每人一条）

**已量准**：四个槽位 x 起点 `85 / 318 / 552 / 786`，宽 166，节距 233.7（从蓝色血条
量的，血条永远满宽，是最可靠的定位基准）。细条本体 y `1972–1980`，采 `1973` 起 6 行。

读法：**白色满格 = 连携就绪；冷却中是灰色填充从左往右涨**。

**坑：填充亮度在四个槽位之间差很多**，实测同一帧里 luma 从 80 到 246 都有
（`#4F504F`、`#959190`、`#F6F6F3`），而轨道是 16–37。所以颜色门必须够宽：
现在是 `#A7A7A7` 容差 0.37，覆盖 luma 73–261。**第一次按"中点"取 `#A2A2A2`/0.34 时
恰好把纯白排除在外**（白到中灰的距离 0.365），四条全读成 0——这种边界要用实测数据验，
不能靠算。

RGB 欧氏距离本来就不适合表达"任意亮度的中性色"。现在的门在 8 帧素材上都对，但如果
以后发飘，正解是换成"亮度区间 + 饱和度上限"的判据，而不是继续调这个球的半径。

### 3. 大招可用（每人一个）

**已量准**：圆心 y `1781`，x `3153 / 3345 / 3537 / 3729`，节距正好 192。**ROI 是 88px 见方**
（不是最初的 72），因为就绪外环在半径 39–43px，超出了 72 的边界。圈下的数字 1–4 既是按键
也对应左下角四个角色的顺序。

**读法是看两道环，不看内盘**（`radial` 命令量的）：

| 半径 | 充能中 | 就绪 |
|---|---|---|
| 0–15 | 半透明内盘，透出压暗的背景 | 图标 |
| **16–25** | **充能弧**：从 12 点顺时针扫，亮橙 `#F7B22E` / 亮绿 `#ABC400`，luma ~180 | 金/绿填充 |
| 32–36 | 半透明浅色轨道 | 暗金 |
| **39–43** | 背景 | **就绪外环**：亮金 / 亮绿，luma ~180，饱和度 ≥0.8 |

- **就绪 = 外环点亮的周长占比 ≥ 0.6。** 实测就绪 86–89%（外环本身顶端留了一段缺口），
  充能中两局所有帧都是 0%。
- **充能百分比 = 从 12 点顺时针连续点亮的弧长**，容忍 1 根辐条的空隙。overlay 以后可以直接用。
- **第一版读法（内盘平均亮度 ≥ 100）在亮地图上完全失效**：内盘是半透明的，阳光下的地面透上来
  和就绪图标一样亮，fight1 里四个充能中的大招被读成可放，还凭空生出了"大招已用"事件。
  暗地图上看不出这个问题——**任何颜色阈值都要在亮、暗两种场景上各验一次**。
- 背景条纹（橙 luma 130 / 饱和度 0.59，黄 luma 145 / 0.64）够不上外环的门槛（160 / 0.75）。
- 战斗结束 HUD 淡出时，就绪外环会暗到阈值以下而被读成充能中（旧 040 帧）。已接受：
  最多在战斗末尾多一条"已用"事件，跟踪器 8 秒后本来就会重置。

技能大圈本身的状态（底部往上涨的液面 + 去饱和）复杂，**用户明确说先不关注**。

### 4. 连携提示（按 E 触发）

**位置固定，不跟随角色或敌人**——用连续三帧（033/034/035）同一矩形验证过，`E` 角标
和主头像像素级重合。

- 主提示圆形头像中心 **(2352, 986)**，直径约 150（`radial` 在 fight1 上量的，白色描边环的半径
  四个方向一致）
- 左侧约 (2223, 975) 是 `E` 按键角标，头像上方有连携标志
- 同时有多个可选时，第二个头像中心 **(2524, 1007)**、直径约 120，**更小更靠下**
- **最左边最大的那个是按 E 会先触发的**

**识别：dHash 取圆内 80% 见方，对 profile 里登记的头像做最近邻，距离 ≤12 才算认出。**
fight1 实测：同一人跨帧 0–5，次提示的小圈对同一人 8（尺寸不同也能通用），不同人之间最小 18，
没有提示的背景离最近的参考也有 18。提示弹出/消失动画的中间帧和谁都不像，会被判为"没人"——
这正是想要的。

已登记示例队伍的 1、2、4 号位（fight1 的 048/067、049–051/072–074、064 帧）。**3 号位在那局里
一次都没被提示过，还没有参考值。** 头像目前按槽位登记在 profile 里，这是临时做法：哪个干员在哪个
槽位本应属于"队伍"，等队伍建模时要挪走。

**踩过的坑**：拼图（montage）是按行优先排的，数格子对应帧号时数错了一次，把没有提示的 062、
071 当成了样本，哈希矩阵里表现为和同角色距离 30+。**用样本登记参考值前，先裁原图确认。**

### 明确不做

- **敌人血条**：用户明确不关心。它也锚在世界坐标跟着敌人走，不是固定 ROI，要先做检测，
  成本高一个量级。
- **技能图标状态**：见上。

### HUD 有效性判定

HUD 不是一直在的，读之前必须先判断这一帧可不可信。已经观察到三种"不可读"的情况：

1. **非战斗**：战斗一结束，右下技能区、底部状态栏全是纯背景。
2. **大招全屏特写**：施放角色大招时是整屏的角色演出（第 24 帧就是一例），
   **HUD 完全不存在**，不是被半透明盖住。
3. **连携发动切入**：左侧一大块平行四边形的角色特写，遮挡左半屏，
   左下角的队伍面板会被压住。

**现在的哨兵：玩家血条最左端的青色**（x 1604 起 90×10，签名 `#12CCF9` 容差 0.15，
覆盖 ≥60%）。血条填充不透明、高饱和，从右往左掉血，所以左端只要人活着就一直是青色；
而大招特写是白、结算界面是灰、战后暗场景是 `#06212F`，都离得很远。实测：两局所有有 HUD 的帧
都是 `#16CFFD`，所有没有 HUD 的帧都不匹配。已知代价：血量低于约 8% 时覆盖率会掉到阈值下，
那几帧会被判为不可读、沿用上一帧。

**下面是第一版哨兵的历史，留着是因为它的失败方式很有代表性。** 当时哨兵放在技能点条上
（用户的建议），判据是"区域内部有没有对比"——既要有足够的暗，也要有足够的非暗。
**它在暗地图上完美，在亮地图（fight1）上把整场战斗判成"无 HUD"**：技能点轨道是半透明的，
亮地面透上来就没有"暗"了。在开发机上它还把 Claude 页面的文字判成了 HUD。

实测技能点条那一块（x 1596 起 648 宽，y 1949 起 26 行）的亮度分布：

| 帧 | 情况 | unlit ≤40 | 非 unlit |
|---|---|---|---|
| 008/016/028/032/036/040 | 战斗中，HUD 在 | 17–42% | 58–83% |
| 024 | 大招全屏特写 | **0.0%** | 100% |
| 084 | 结算界面 | **0.0%** | 100% |
| 044 / 060 | 战后 / 菜单 | 100% | **0%** |
| 048 | 战后 | 95.8% | **4.2%** |
| 020 | 战斗中，但特效糊住了整条 | **0.1%** | 99.9% |

每种"不可读"的情况都有一侧塌到 0，而 HUD 在的时候两侧都不空。阈值取
`minimumDark 0.08` + `minimumLit 0.10`，两边都有 2 倍以上余量。

**为什么不能用亮度或颜色签名**：大招特写那帧是个白色角色铺满全屏，技能点条位置
90% 是纯白——任何"够不够亮"的判据都会说 HUD 在。这是最危险的误判，因为紧接着读出来的
四项全是演出画面的噪声。

**为什么不能只认黄色**：段边框实测只有 luma 63，技能点归零时整条没有任何亮色，
黄色签名会漏判。而"暗 + 非暗"的判据在归零时仍然成立（轨道提供暗，边框提供非暗）。

frame-020 是个有价值的样本：**战斗中**但特效把整条洗白了，unlit 只剩 0.1%。哨兵正确地
拒绝了它。这就是设计意图——宁可标"已失效"沿用上一帧，也不要输出猜测值。

## 命令

```
dotnet restore Anaphora.slnx
dotnet build Anaphora.slnx
dotnet test Anaphora.slnx
dotnet run --project src/Anaphora.App

# 开发机上没有游戏时，拿任意窗口检查 overlay 的定位、缩放与鼠标穿透（读数是假的）。
# 结果看 %LOCALAPPDATA%\Anaphora\app.log，里面会写 "overlay is click-through"。
dotnet run --project src/Anaphora.App -- --process claude --any-foreground

# WGC 可行性探针。参数：进程名（默认 Endfield）、输出目录。
# 打印窗口信息 / display affinity / 帧率 / 亮度统计，并落三张 PNG：全图、1280 宽预览、
# 左上角原分辨率切片。换游戏或换机器时重跑一次。
dotnet run --project tools/Anaphora.CaptureProbe -- probe Endfield captures

# 连拍，用来收集标 ROI 的素材。参数依次为：进程名、输出目录、时长秒、间隔毫秒、
# 前置等待秒、每隔几张存一张原分辨率（其余只存 1280 宽缩略）。
# 4K PNG 单张约 15MB，别对每一帧都存全分辨率。产物含 manifest.csv（序号/时刻/平均亮度）。
dotnet run --project tools/Anaphora.CaptureProbe -- burst Endfield captures/combat 90 1000 0 4

# 从存下来的帧里切一块出来看。坐标是像素，写成带小数点的就按整帧比例解释；
# 末尾可加放大倍数（最近邻，保持像素边界锐利）。会同时打印像素与归一化坐标。
dotnet run --project tools/Anaphora.CaptureProbe -- crop captures/combat/frame-036.png 1570 1915 700 105 out.png 2

# 把同一个矩形从很多帧里切出来拼成一张图，看某个 HUD 元素在整场战斗里怎么变。
# 参数：目录、文件通配（Directory.GetFiles 只认 * 和 ?，没有字符类）、x y w h、
# 输出、列数、放大倍数。格与格之间是品红分隔线，不会跟 HUD 内容混淆。
dotnet run --project tools/Anaphora.CaptureProbe -- montage captures/combat "frame-0??.png" 1570 1915 700 105 sp.png 1

# 量一块区域：平均色、亮度分布（unlit/mid/lit 三档）、主导色直方图。
# 加 scan 还会按列（或按行）分类出一条 ASCII 带，并列出连续段的起止像素——
# 这是定条形元素精确边界最快的办法。所有阈值都应该出自这个命令，不要目测。
dotnet run --project tools/Anaphora.CaptureProbe -- sample captures/combat/frame-036.png 1596 1953 648 17 scan

# 把整帧里除指定矩形外全部涂黑，尺寸不变。用来做能进仓库的测试素材：
# 归一化坐标不用改，4K 全黑 PNG 只有 200–400KB（原图 15MB）。
dotnet run --project tools/Anaphora.CaptureProbe -- mask captures/combat/frame-036.png out.png 1590,1943,660,37 80,1962,880,26

# 在游戏机上把抓取做成 HTTP 服务。参数：进程名、端口（默认 8750）、burst 落盘目录
# （默认 captures/remote）。游戏不必先开着，窗口每次请求时重新查找。
dotnet run --project tools/Anaphora.CaptureProbe -- serve Endfield 8750

# 在开发机上调用它（见下节「跨机器开发」）。所有端点都接受 ?process= 换目标窗口。
tools/remote.sh "/info?measure=3"                                       # 窗口/DPI/affinity，measure 顺带数 N 秒帧率
tools/remote.sh "/frame?x=1596&y=1949&w=648&h=26" -o captures/live/sp.png  # 原分辨率裁好再传；带小数点按比例
tools/remote.sh "/frame?down=3" -o captures/live/preview.png            # 整帧缩到 1280 宽
tools/remote.sh "/burst?duration=20&interval=500&leadin=5&full=1" -X POST | tar -x -C captures/live/b1
tools/remote.sh "/hud?seconds=30&rate=20&verify=20"                     # 生产管线读实时游戏：读数变化时间线 + 速率 + 图集校验

# 本地跑生产管线（Anaphora.Capture + Analysis）。参数：进程名、秒数、Hz、每 N 帧校验一次图集
# （0 关闭）、profile 路径（默认 profiles/endfield.json）。只有读数变化时才打一行。
dotnet run --project tools/Anaphora.CaptureProbe -- hud Endfield 30 20 20

# 把一次 burst 按真实时间戳喂给读数器和轴跟踪器，逐帧打印 App 会显示什么（含大招外环覆盖率）。
# 读数器或阈值有任何改动，都应该对 captures/combat 和 captures/live/fight1 各回放一遍。
dotnet run --project tools/Anaphora.CaptureProbe -- replay captures/live/fight1

# 圆形元素的径向分布：沿某个角度（顺时针，0 = 12 点）打印每个半径上的颜色、亮度、饱和度。
dotnet run --project tools/Anaphora.CaptureProbe -- radial captures/live/fight1/frame-066.png 3153 1781 46 90 6

# 同一矩形在多帧上的 dHash 和两两汉明距离，用来登记头像参考值。
dotnet run --project tools/Anaphora.CaptureProbe -- hash 2292 926 120 120 a=frame-048.png b=frame-067.png
```

## 跨机器开发（游戏机 ≠ 开发机）

开发机没有独显、跑不动游戏。**只有"抓一帧"必须发生在游戏机上**；`crop` / `montage` /
`sample` / `mask` 和单元测试都只吃 PNG，本来就在开发机上跑。所以游戏机上跑
`CaptureProbe serve`，开发机用 `tools/remote.sh`（curl 的薄封装）拉图进 `captures/live/`，
再用现有命令量数。

- **`serve` 必须从游戏机已登录的桌面启动**（双击或它自己的终端）。经 SSH / 服务起的进程
  在别的 session，看不到游戏窗口，WGC 拿不到帧。session 0 会直接拒绝启动。
- 鉴权：Bearer token，首次启动生成并存到游戏机的 `%LOCALAPPDATA%\Anaphora\probe-token.txt`，
  之后复用；环境变量 `ANAPHORA_TOKEN` 优先。只应答 loopback、RFC1918、链路本地和
  Tailscale（100.64/10、ULA）的对端。明文 HTTP，局域网内 token 可被嗅探，走 Tailscale 则加密。
- 开发机的地址与 token 写在仓库根的 `remote.local.env`（已 gitignore），
  `serve` 启动时会把这两行原样打出来；环境变量同名时覆盖文件。
- 首次监听会弹 Windows 防火墙，要允许"专用网络"。网络被识别为"公用"时要改成专用。
- `/frame` 每次新开一个 WGC 会话、取第一帧、关掉，约 70ms（不含编码）。静态窗口也会立刻给
  第一帧，所以游戏暂停时 `/frame` 不会挂起；但 `burst` 只在内容变化时出帧，暂停的游戏几乎
  攒不到帧。
- 同一时刻只有一个抓取：burst 期间 `/frame` 等 5 秒后返回 409。burst 的 PNG 在游戏机上
  也留一份，传输断了可以去那边拿，也要记得定期清。
- 部署：游戏机 clone 仓库直接 `dotnet run`，或
  `dotnet publish tools/Anaphora.CaptureProbe -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish`
  出一个约 130MB 的单文件 exe 拷过去（已验证 WGC 在单文件模式下正常）。
- overlay 的端到端测试、alt-tab / 切分辨率的稳定性测试只能在游戏机上做。
- **不要用串流（Moonlight / Parsec / Steam）的画面标定阈值**：视频压缩会改颜色、通常也不是
  4K，而连携条颜色门在 0.365 这种边界上就会判错。串流只适合看布局。

整个解决方案现在应当 0 警告 0 错误编译通过。

**这台机器上 `python` 是 WindowsApps 的占位 stub，静默失败什么都不做。**
不要用它做文本替换或脚本处理，改用 Edit 工具或 `sed`。
