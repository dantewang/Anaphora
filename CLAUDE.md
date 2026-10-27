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
tests/Anaphora.App.Tests      主界面编辑器的文档模型（RotationDocument）、快捷键解析
tests/Anaphora.Analysis.Tests/fixtures  连拍帧的遮罩版：只留 HUD，其余涂黑，可进仓库
tools/Anaphora.CaptureProbe  一次性可行性探针：WGC 能不能拿到这个游戏的真实像素
profiles/               每个游戏一个 JSON：窗口匹配、ROI 定义、阈值、头像哈希
rotations/              随程序发的轴（示例队伍、分支演示），首次运行复制进用户的轴库
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
- **`OverlayAnchor` 按底边定位**（`AboveBottomHud` = 客户区高度的 0.67），不是按顶边。
  大字上方的软提示块有时有有时没有，顶边定位会让下面的状态条跟着上下跳；底边定位让大字
  向上长，状态条不动。0.67 这个值是压在游戏自己底部 HUD 带上方——第一版按顶边 0.605，
  实拍帧上直接盖住了技能点条和队伍面板。
- CsWin32 在 AnyCPU 下生成不了 `GetWindowLongPtr`/`SetWindowLongPtr`（只有 64 位导出），
  扩展样式用 `GetWindowLong`/`SetWindowLong` 即可。
- **截图验证 overlay 时要用带 `CAPTUREBLT` 的 `BitBlt`**；`Graphics.CopyFromScreen` 截不到
  分层窗口，会让人误以为 overlay 没显示。
- **主界面**（`src/Anaphora.App/Editor`，视觉稿在 artifact「Anaphora 轴编辑器」）：左轴库、中间状态机图
  （`RotationGraph` 自绘：每段一条泳道，软提示叠在卡片上方、等待条件在下方，箭头画去向/分支/下一轮）、
  下方快速输入、右边步骤属性和 overlay 预览；另有设定页。启动时打开，关窗只是隐藏，托盘可再打开。
  托盘仍保留状态面板、隐藏、轴回到开头、跳过一步、退出这些快捷开关，和设定页双向同步。
  设定记在 `%LOCALAPPDATA%\Anaphora\settings.json`，日志在同目录 `app.log`。
- **轴库**在 `%LOCALAPPDATA%\Anaphora\rotations\<id>.json`，随程序发的轴缺哪个补哪个（不覆盖用户改过的）。
  `settings.json` 的 `activeRotation` 是 overlay 跟的那条；`--rotation` 命令行参数优先。
  导入（文件/剪贴板，id 冲突自动改名）、导出（文件/剪贴板）、复制、新建；改了没存的轴留在内存里，
  保存时有错误就不写盘。正在使用的轴保存后 overlay 立即换成新版本。
- 编辑器的文档模型是 Core 不可变记录的**可变镜像**（`RotationDocument`），任何改动触发 `Changed`；
  撤销/重做是整份快照（Ctrl+Z / Ctrl+Y，200 步）。快速输入替换一段时按位置保留原步骤的 id、条件、
  备注和分支，被删掉的步骤的引用改指这一段的第一步。
- **分段工具**（`RotationDocument.SplitAt` / `MoveToSegment` / `MoveSegment` / `RemoveSegment`）：
  工具栏和右键菜单里的「✂ 在这里分段」、步骤属性里的「所属段」下拉、快速输入那一行的 `↑ ↓ ✕`。
  `AddSegment(from)` 用于「从这里分支」，无参的 `AddSegment()` 是工具栏的「＋ 新建分段」：
  在最后加一段、里面一步，**不建任何分支**——想要一段和想要一条通往它的边是两回事。
- **图上分段靠交替底色分开**（`RotationGraph`，2026-10-27），每条泳道顶上还有一条细分隔线。
  段的去向没有卡片可以当起点，所以不画箭头，写在泳道左边的标题下面。泳道的高度要把这几行算进去，
  否则会压到下一段。
- **右键泳道标题弹的是「段」的浮层**，不是步骤的右键菜单：改段名、上下挪、新建、删除。
  `RotationGraph.SegmentAt(point)` 做命中判定（只认标题那条 gutter），代码里在
  `OnGraphContextRequested` 把步骤菜单 `e.Handled = true` 挡掉再 `ShowAttachedFlyout`。
- **动步骤之前要 `Pin()`，动完要 `Unpin()`。** 没写 `next` 的边含义是「**同一段里的下一步**，
  没有就回开头算新一轮」——步骤一换段，这条边就改了指向。所以所有重排操作都先把隐式边写成显式
  `next`，重排完再把和默认值一致的收回去。`SegmentEditingTests` 里每个操作都有一条
  「重排前后每条默认边的目标完全一致」的断言，改这块时别删。
- **一个段的步骤在 `Steps` 里必须连续**（`DefaultEdge` 按位置找同段的下一步）。`Reorder()` 负责这件事。
- **Avalonia 下拉框的坑**：`ItemsSource` 整体换掉时它会清掉选中项并把 null 写回绑定。所以选项列表
  尽量不换（文字固定的"不指定"项、文档维护的 `Options` 只在步骤名变化时刷新），源属性忽略 null，
  刷新后再主动通知一次。`SelectedValue` + `SelectedValueBinding` 在这里没选中过，改用 `SelectedItem`。
- 用 SendKeys 驱动界面测试时注意：`~` 是回车，中文输入法会把 `"` 变成中文引号。编辑逻辑靠
  `Anaphora.App.Tests` 测，不靠模拟键盘。
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
- 反作弊：不注入、不读内存、不 hook，行为等价于 OBS。键盘监听只轮询 1–4 与 E 的按下状态，
  不装键盘钩子、不截获其他按键；但这仍是全系统按键查询，进游戏前要在游戏机上确认反作弊不拦。

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

- **按键监听只在开发机上用合成按键验证过**（短按 1 = 技能、按住 2 700ms = 大招、E = 连携，
  `KeyboardCastListener` 的 `Cast` 事件都收到了），**没在游戏机上实测**。要确认：反作弊是否对
  `GetAsyncKeyState` 敏感、500ms 阈值和游戏手感差多少、无边框全屏下前台判定是否稳定。
- **2026-10-27 那轮改动（按键为唯一信源）只有单元测试，没在游戏里跑过。** 要在游戏机上确认的是：
  爆发段一串 cut-in 里按的键现在是不是真的都收到了（看 `app.log` 的 `keyboard:` 行），
  跑图/开背包回来时轴会不会乱走，以及按错槽位时 overlay 的「不是这一步」是否好认。
- **连携提示头像：3 号位还没有参考值**，那局里 3 号的连携从没被提示过。另外参考值只对示例
  队伍有效，换队伍要重新登记。
- **新哨兵（血条青色）在背包、地图、菜单这类界面上还没实测过。** 两局连拍里的非战斗帧
  都判对了，但没专门打开过这些界面。低血量（<8%）时会暂时判为不可读，也没实拍过。
- **读数器和轴跟踪器已在真实战斗上离线验证过**（fight1，75 帧 1Hz 全分辨率，用 `replay`）：
  两轮示例轴都跟上了，第二轮在技能点为 0 时正确显示 `1重击` 被条件阻塞。
  **但 App 本身（实时 overlay）还没在游戏机上跑过**；游戏机上的 `serve` 也是旧版，没有 `/hud`。
- **大字 overlay 只在开发机上拿 Claude 窗口核对过几何**（`--process claude --any-foreground`，
  4K + 150%：字高实测 205 物理像素 = 客户区高的 0.095，底边落在 0.67，穿透正常）。
  **白字压不压得住游戏里的亮地面没验过**——底影已经从原规格的 0.15 加到 0.6，但
  `captures/live/fight1` 那种阳光地面只有在游戏机上才能看出来。真压不住的话下一步是
  给大字加一层极淡的暗描边，那就离原规格更远了。
- **窗口化模式的客户区偏移没实测过**。算法和测试都覆盖了（fixture 贴进带边框的画布），
  但 DWM 边框的实际数值只有游戏切到窗口模式才能验证。
- 长时间运行下的稳定性：游戏切分辨率 / alt-tab / 显示器切换时 `GraphicsCaptureItem`
  的 `Closed` 与帧池重建路径都还没测。

### 踩过的坑：仓库目录被打上 Low 完整性标签，`dotnet test` 就全废

2026-10-26 21:45:09 发生，2026-10-27 清掉。症状是 testhost 一启动就 `OpenProcess` 被拒
（Win32 错误 5），测试代码一行都没跑到；`dotnet build` 却完全正常。

原理：进程的完整性级别取「父进程 token」和「**exe 文件自身的标签**」里更低的那个。
`dotnet.exe` 在 Program Files（无标签）所以编译没事，而 `testhost.exe`、`Anaphora.App.exe`
这些都在仓库的 `bin` 里，**从这棵树里启动就掉到 Low**。Low 进程打不开 Medium 进程的句柄
（testhost 要监视父进程退出），也**写不了 `%LOCALAPPDATA%\Anaphora`**——所以那阵子从仓库里
`dotnet run` 起来的 App 读不到也写不了 settings、轴库和 app.log。

怎么认出来：`icacls D:\Repos\Anaphora`，根目录上有显式的
`Mandatory Label\Low Mandatory Level:(OI)(CI)(NW)`，干净的仓库一条标签都没有。
NTFS 的 change time（安全描述符被改写时更新）能定位是什么时候刷的：九月就没动过的文件，
change time 却是同一秒，说明是一次 `/T` 递归。

**`icacls` 删不掉标签**：`/remove` 和 `/reset /T` 都留着它，`/setintegritylevel` 按帮助说的
只能「explicitly **adds**」。覆盖成 Medium 能恢复功能，但会留下一条显式 Medium 标签。
真要删干净得走 `SetNamedSecurityInfo` + `LABEL_SECURITY_INFORMATION` + 空 SACL，而且要
**逐个文件走**——标签的变化不会自动向子项重新传播。同来的还有
`Everyone:(CI)(DENY)(DC)`（删子目录会失败）和一条沙箱 SID 的授权，`icacls /remove` 对那个
不可解析的 SID 无效，要用 `Get-Acl` / `Set-Acl` 摘掉。

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

| 记法 | 含义 | 实际按键 | 类别 |
|---|---|---|---|
| `N` | 槽位 N 的技能 | 数字键 N | 硬操作 |
| `NE` | 槽位 N 的连携 | E（提示头像里最左边那个先触发） | 硬操作 |
| `N大招` | 槽位 N 的大招 | **长按**数字键 N | 硬操作 |
| `N重击` | 槽位 N 的重击（N 为当前主控） | 普攻打满最后一段，**没有单独按键** | 软提示 |
| `N普攻K` | 按了 N 的技能之后的第 K 下普攻（技能让普攻变了样的角色） | 普攻 | 软提示 |

文件里也接受 ASCII 别名 `NU`（大招）、`NH`（重击）、`NAK`（普攻 K），规范写法是上表的中文。
"技能后第 K 下普攻"没有学名，用户描述为"按下主控角色技能（进入技能状态）之后的第 N 次普攻，
跟此人技能相关"。

### 轴是状态机（schema 2，2026-09-17 与用户确认）

- **节点只放硬操作**（技能/连携/大招）——只有它们能从画面或按键确认"做过了"，跟踪器只跟它们。
- **软提示**（`before`）挂在它引出的那个硬操作前面：重击、技能后第 K 下普攻、自定义文字。
  跟踪器不等它、不检测它；overlay 在"现在"区把它和硬操作**一起**显示（虚线黄绿块 › 实心块），
  软提示块和硬操作块一样大。
- **等待条件**（`when`：`sp>=N`、`chain:N`、`ult:N`、`prompt:N`）挂在节点上，连同它的软提示一起卡住。
- **去向**：`next` 指定下一步，没写就是同段的下一个，段尾没写就回到第一步并算新一轮；`newRound`
  让这条边轮数 +1。`branches` 是条件分支：**条件决定提示哪条路，实际走哪条看识别到的操作**
  （玩家不按提示走也跟得上）。停在某步之前，提示会随 HUD 变化重新选路。
- **一条分支的多个条件默认是「且」，`"match": "any"` 改成「或」**（2026-10-27）。没有嵌套：
  一条分支要么全是且、要么全是或，更复杂的就写成两条指向同一步的分支。`all` 是默认值，不写进文件。
  **三条各带一个条件的分支是「或」，不是「且」**——用户第一版轴就栽在这里：想要「1、2、4 大招齐了
  才爆发」，写成三条单条件分支，结果 1 号位一充满就切，早了一轮半。
- **段**（`segments`）是用户划分的**阶段**："开局"、"循环"。示例轴 = 两段 + 两条"下一轮"边。
  它既是图上的一条泳道，也是 **overlay 轴显示器一次显示的单位**——所以"一段多长"是用户的编辑决定，
  程序不需要任何截断或滚动逻辑；一段长到看不过来，是用户该再切一刀。
  （2026-10-24 从"只是画图用的泳道"升格。）
- **段也能有自己的去向**（`segments[].branches`，2026-10-27）。和步骤上的分支的区别只有一个，
  但很要紧：**步骤的分支只在它自己那一步做完时判一次，段的去向在本段每一步之后都判**。
  用户的场景是「三轮充能打完还差一丝，不想再走完整一轮，随便放个技能把大招填满就想切爆发」——
  挂在段尾那一步上的分支要等走完一整轮才有机会判，挂在段上就不用。
  `Rotation.Edges()` 的顺序是：步骤自己的分支 → 段的去向 → 默认边；步骤的更具体所以在前。
  **段的去向不能指回本段**（会变成没有出口的循环），验证器报错并跳过。
- **选路要连续两帧一致才换**（`RotationTrackerOptions.SettleFrames`，默认 2）。大招外环、连携条
  都是颜色门，在边界上会抖；没有这个窗口，一帧误读就能把光标丢进另一段再弹回来。玩家一旦做出
  下一个动作，`from` 就换了，也就不再回头。
- schema 1（`opening` + `cycle` 两个平列表，重击是独立一步）仍能读，读进来自动转换。
- 快速输入的一行写法：`3 → 2 → 4E → ~1重击 1E → 1 → 2E → 1`，`~` 开头是软提示，
  `~"文字"` 是自定义文字（`RotationText`）。分支和条件不在一行写法里。

**还没做**：轮数条件（`round>=N`）和分支边的 `newRound`。用户那条轴不需要——它的闸门是
「1、2、4 大招齐了」，不是轮数，三轮只是它通常发生的时间点；按轮数写反而会在充能变慢时切错。
真要做的话，`RotationBranch` 要加 `newRound`，因为现在**分支边永远不涨轮数**
（`Edges()` 里写死 `NewRound: false`），轮数会数不对。

### 已定的 overlay 决策（2026-09-13，大字版 2026-10-24）

- 位置 **A**：角色脚下，屏幕水平居中；底边压在客户区高度的 **0.67**（见上面的 `OverlayAnchor`）。
- **下一个该按的键是一块「雷霆大字」**——就是游戏进新区域 / 见 Boss 时那块白色大字。
  技能画数字 `3`，连携画 `E`（第二行写触发谁），大招画数字、第二行写「长按」。
  视觉稿是 artifact「Anaphora Overlay」v2。
- 状态面板**可折叠**，前期展开着用来核对读数对不对。overlay 本身鼠标穿透、不可点，
  所以折叠开关不在 overlay 上。
- 状态区只剩两行：`接着` 的后三步小块 + 一行轮次/等待条件；技能点 + 四个槽位。
  **连携提示并进了槽位**——会被 E 触发的槽位在数字牌上方挂白色 `E1`/`E2` 牌，
  不再有单独一行。头像没匹配到槽位时才退回一行文字（`PromptNote`）。
- **按错了要说出来**（2026-10-27）：按了轴上没有的槽位，光标按设计停在原地，可是"停住"和
  "没看见"在屏幕上长得一模一样。所以大字底下挂一条 2 秒的红牌「按了 3 技能 · 不是这一步」
  （`OverlayViewModel.Misfire`，计时在 `AnaphoraHost`，`MisfireLinger`）。它占真实版面——
  又是 `SizeToContent` 那个坑，负边距挂在窗口外就一个像素都看不见。

#### 大字的排版规格（照抄社区生成器）

社区的 [终末地白色大字生成器](https://endfieldtext.ccomm.top/)（GPLv3，原项目
EndfieldTextGenerator 的 web 移植）把常量写在源码里，**这些数字是抄来的，不是目测**：

| 原规格 | 本工程怎么用 |
|---|---|
| 中文 `NotoSansHans-Black` | 就是 Noto Sans SC 900。本工程走系统的 **Microsoft YaHei UI Bold** |
| 西文 `NovecentoWideUltraBold` | 商业字体，不能发。换成系统自带的 **Arial Black** |
| 主标题高 = 屏高 `0.2472` | 本工程取 **0.095**（约游戏的 38%），即 1080 下字高 102.6 DIP，`FontSize 143`（Arial Black 的 cap 是 0.7158 em） |
| 字距 `0.021875`（相对字高 8.9%），西文再 ×`0.35` | `LetterSpacing 3.2` |
| 第二行 `0.108/0.184`，淡出 `0.3` | 压到主字的 30%：游戏里第二行是地名的下半句，这里是说明文字。**唯一明显偏离原规格的地方** |
| 萨卡兹副标题 `0.025`，贴 1px，淡出 `0.5` | 保留位置、尺寸比和向右淡出（`OpacityMask`），内容换成 `SKILL` / `CHAIN` / `ULTIMATE` |
| 底影长短轴 `1.2 : 1`，不透明度 `0.15` | 同比例的 `RadialGradientBrush`，但**加重到 0.6**：游戏放大字时压暗整屏，overlay 不能压暗游戏 |

- **字体全部用系统自带的，不内置任何字体文件。** 大字用 **Arial Black**：比 Segoe UI Black
  更宽更钝，更接近游戏自己那块 Novecento Wide。退路是 `Segoe UI Black`
  （`seguibl.ttf`，字形和微软雅黑的西文同源，接缝更小但偏窄）。
  两者的 `usWeightClass` 都是 900、子族却是 Regular，所以 `FontWeight` 写 `Normal`；
  写成 `Segoe UI` + `FontWeight="Black"` 拿到的是另一张脸。
  它不是等宽的，`1` 和 `E` 的卡片宽度略有差别，但 `Place()` 每 100ms 重新居中，动的是边缘不是字。
- **白字黑边走 `OutlinedText`，不要叠偏移副本。** Avalonia 没有 `text-stroke`，叠八份黑字
  只会把字加粗，不会给它镶边。`OutlinedText` 用 `FormattedText.BuildGeometry()` 拿到字形轮廓，
  先 `DrawGeometry(null, pen, …)` 描边再 `DrawGeometry(fill, null, …)` 填充——描边只往外长，
  字形不变。`FormattedText` 的构造参数里前景画刷**不能传 null**，否则 `BuildGeometry` 给不出东西。
  字距靠逐个字素排版实现（`FormattedText` 本身没有 letter-spacing），所以带字距的串不是整体整形的；
  这里都是短标签和数字，没有连字要顾。
- **想收紧大字的行距，不要用 `LineHeight`——它会把字切掉。** Arial Black 的行盒是
  `(2254 + 634) / 2048 = 1.41 em`，`FontSize 143` 下有 202 高（字高才 102，上面 55 的行距、
  基线下 44）。把 `LineHeight` 设成比它小，Avalonia 不是收紧而是裁切，`3` 的下沿会被切平。
  用**负 `Margin`** 收：动的是版面盒，不动画出来的东西。
  但**上边的负值不能给太多**：`SizeToContent` 按负边距收缩后的盒子来量窗口，字从盒子上方
  溢出多少，窗口顶边就压过来多少。`-48` 时 4K 上只剩 6px 余量（1080p 就是 2px），
  现在用 `-34`，实测余量 36px。
  想内置字体时只能挑静态 TTF/OTF，而且要 `<AvaloniaResource Include="..." />` 才会打进程序集，
  引用写 `avares://<程序集>/<目录>#<字体内部族名>`。
- **重击怎么识别还没有答案**（游戏里重击没有单独按键），之后再讨论。在那之前，
  重击现在是软提示，跟踪器根本不等它。
### 按键是唯一的信源（2026-10-27 用户决定，硬性）

**只有 1–4 和 E 能推进光标。画面一步也推不动。** 纯画面模式连同 `--no-keyboard`、
`settings.json` 的 `readKeyboard` 一起删掉了——保留一个跟不了轴的模式没有意义。

为什么不能让画面推进：技能点是全队共享的一条，"少了一格"**不带槽位**，`Explains` 对任何
`Skill` 都返回 true，于是玩家按错槽位光标照样前进。用户的要求正相反：**按错就停在原地**。
所以匿名的技能点消耗降级成只判条件（`sp>=N`）、不完成任何步骤，`Diff` / `ObservedKind` /
`SkillSpent` 整个删掉，`Observation` 现在就是"解析出槽位的 `CastInput`"。

读键用 `GetAsyncKeyState` 轮询，**不是 hook、不注入、不读内存**：后台线程每几毫秒问一次这五个
键是否按下，并且**只在游戏窗口拥有前台时**问。短按数字键 = 技能（抬起时确认），按住超过
`ultimateHoldMs` = 大招（到阈值就上报，不等抬起），E 按下 = 连携。

- **按键不受 HUD 可读性的任何限制**（修 2026-10-26 那版的主 bug）。旧版要求"1.5 秒内画面可读
  过"才收按键，而大招/连携 cut-in 恰恰把 HUD 遮掉好几秒——爆发段一串特写，按的键全被扔掉，
  表现就是"轴根本不往下走"。现在 `OnKeyboardCast` 只要求游戏窗口还在，"这个键属不属于一场
  战斗"交给跟踪器，在那里按键和读数是一起看的。
- **离开战斗的判据是「画面读不到 **且** 没有按键」**，取两者里较晚的那个时刻算起超过
  `ResetAfterAbsence`（8 秒）才重置。旧版只看画面，两段连着的特写就够把光标推回第 1 步。
  不在战斗里时，**只有 HUD 回来才算新的一场**：此时的按键（菜单里按的）一律丢弃。
- **不向前找匹配**（`LookAhead` 删了）。按键不是当前这一步，就是"按了别的"，光标不动，
  overlay 上闪一条"按了 X · 不是这一步"（`OverlayViewModel.Misfire`，2 秒）。往前走 2–3 步
  找同槽位的匹配，等于悄悄奖励一次误触；真的从头打了由对齐逻辑兜。
- **对齐（resync）只看「连续没被解释的按键」**。匹配成功就清空 history——已经解释过的按键是
  用掉的证据，留着会让"一次正常操作 + 两次乱按"凑够 3 个触发误对齐。阈值仍是连续 3 个。
  （所有按键都带槽位之后，旧的"必须含一个带槽位的操作"这条守卫就恒真，删了。）
- E 是共用键：槽位优先取按键那一刻**上一帧**的连携提示头像（`OnKeyboardCast` 在 cut-in
  吃掉提示前抓），且那一帧必须是 1.5 秒内的（`PromptFreshness`），否则宁可不认——认错人比
  不认更糟。头像认不出（slot 0）时退化成"当前这一步如果是连携就算它"。
- **不要在整格技能点为 0 时丢弃技能键**：不足一整格的技能正是画面看不见的那类，而且当前段
  可能只充了一部分。只在 `app.log` 里记一条警告。
- `ultimateHoldMs`（默认 500，夹在 100–2000）在**设定页的 KEYBOARD 卡片**里改，立刻生效
  （`KeyboardCastReader.UltimateHold` 可写，用 `Interlocked` 存 ticks，轮询线程直接读）。
  按几次大招、看 `app.log` 里记下的按住时长，再定这个值。
- 反作弊口径不变：仍然不注入、不读内存、不 hook；`GetAsyncKeyState` 是公开查询 API，
  和游戏自己读键用的是同一类机制。**不要换成 `WH_KEYBOARD_LL` 或 Raw Input 的
  `RIDEV_INPUTSINK`**：前者正是反作弊盯的那类，后者在后台收全部按键，行为画像比"每几毫秒问
  五个键"更难看。真正的风险是游戏机上的反作弊是否对全系统按键查询敏感，以及 500ms 的按住
  阈值和游戏实际手感差多少，**这两条没在游戏机上验过**。

**画面现在只负责三件事**：条件（技能点、冷却、大招就绪、提示头像）、给 E 补槽位、
判断这一帧可不可信。下面这些曾经的漏判路径，连同它们所在的 `Diff`，已经不存在了：
技能消耗不足一整格、不可读帧被哨兵丢掉、一帧内多次消耗只看净差、ready→not ready 的跳变
正好落在不可读期间。剩下的真实风险只有两条：**连携头像认不出**（dHash 距离 >12 判无人，
3 号位还没有参考值，影响 `prompt:N` 条件和 E 的槽位），以及**游戏吞掉的按键**——
技能点不够时游戏不放技能，但按键照样上报，光标会多走一步。后者还没有对策。

**不要为了识别技能去加技能图标 ROI**（「明确不做」里那条）：按键已经把"哪个槽、什么时候"
说清楚了。唯一还值得考虑它的场景是将来又想要一条不依赖键盘的路，那时它是**唯一**能指名槽位
的画面信号（给自己上 buff 的技能一样进 CD）。

**小数技能点也没做**（见下）。轮数条件和分支边的 `newRound` 同样没做，理由写在上面的状态机一节。

- **"轴回到开头"的快捷键**默认 `Ctrl+Alt+R`，`settings.json` 的 `resetHotkey` 可改（空串关闭）。
  注册了的组合会从所有程序（包括游戏）手里拿走，所以**只在游戏在前台时注册**，切走就注销。
  `RegisterHotKey` 把 `WM_HOTKEY` 投给注册它的线程，而 UI 线程的消息循环归 Avalonia 管，
  所以快捷键有自己的线程和消息循环（`GlobalHotkey`）。被别的程序占用时 `app.log` 里有 `[warn]`。
- **「调整位置」模式**（2026-10-24）：`Ctrl+Alt+M`（`moveHotkey` 可改）、托盘、设定页三个入口。
  开着的时候**只去掉 `WS_EX_TRANSPARENT` 一位**（`Win32Window.SetClickThrough`），
  `WS_EX_NOACTIVATE` 留着，所以 overlay 接管鼠标但**游戏不丢前台**；同时忽略
  `hideWhenHudAbsent` 和「游戏不在前台就隐藏」，否则一松手它就没了。退出是再按一次快捷键、
  点 overlay 上的「完成」、或托盘——**没有 Esc**，`NOACTIVATE` 的窗口拿不到键盘焦点。
  拖动自己实现（记 `PointerPressed` 的屏幕点，`PointerMoved` 算增量写回锚点），
  不用 `BeginMoveDrag`：那会走系统的移动循环、可能抢前台。
- **位置存成归一化锚点** `overlayAnchor: { x, y }`：x 是窗口水平中心、y 是底边，
  和 ROI 坐标同一套道理，换分辨率不用重标。**吸附中线只对累计的原始位置判一次，
  不能每个增量都判**——拖动是一串小位移，每步 30px 都在 1% 容差里，逐帧吸附会把每一步都撤销，
  慢慢拖永远离不开中线。所以 surface 里留一个未吸附的 `raw`，显示和摆放用 `raw.Snapped()`。
- **定位模式的边框、提示牌、读数条必须占真实版面**，不能用负边距挂在 overlay 外面——
  又是 `SizeToContent` 那个坑：它只量版面盒，溢出的部分就在窗口外，一个像素都看不见。
- **HUD 读不到一段时间后自动隐藏 overlay**（地图、背包、战斗之间跑图）：`hideWhenHudAbsent`
  默认开，`hideAfterSeconds` 默认 5 秒，要长过大招特写，免得闪。托盘里有开关，
  以后挪到主界面（调设定、编辑/导入轴）。刚开始抓取时算作"刚读到过"，先显示 5 秒再判断。

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

**乱轴后从头打要能跟上**（用户实测提出的问题）。跟踪器只会从当前位置往后匹配，玩家在战斗中途
乱了轴再从头打，光标就永远追不上。现在的做法：一旦出现当前步骤解释不了的按键，就把**最近 12 秒
里连续没被解释的**按键序列倒着对到轴的每个位置上，跳到能接上的地方。**必须连续对上至少 3 个才跳**
——两次操作也可能只是误触。**匹配成功就清空这段记忆**：解释过的按键是用掉的证据，留着的话
"一次正常操作 + 两次乱按"就会凑够 3 个，把光标甩到别处去。
打平时优先不动、再优先从当前位置往前走最近的（开局只打一次，所以从头打会对到下一轮的循环），再优先文件里靠前的。
示例轴里从头打 `3 → 2 → 4E`，看到 4E 的那一刻就会对齐到循环段的 `~1重击 1E`；在那之前的两次技能不足以判断。

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
- **轴显示器的「游戏图标」档**（2026-10-24 否决）。设想是把轴里的格子从按键换成游戏自己的
  图标：技能图标、大招图标、队伍头像，从画面实时截。**用户判断这个体验更差，砍掉**——
  轴显示器只保留按键档，不留开关、不留半成品。从没写进代码，只在 artifact v3 里出现过。
  当时已经量好的东西记在这里，免得将来重新论证一遍：要 12 个新 ROI（3 类 × 4 槽位），
  技能图标在 x `3153 / 3345 / 3537 / 3729`、y ≈ `1933`、约 120px，和大招圈同一组 x；
  大招图标 y ≈ `1775`、88px；队伍头像 x `92 + 234×(n−1)`、y ≈ `1798`、约 158px。
  三个坑：**大招图标只在就绪时才画**（充能中是空圈，只能见过一次就缓存）；
  **当前主控的技能图标外面多一圈白环、整体更大**，四个槽位尺寸不一致且主控会换；
  技能图标底部的琥珀液面就是冷却显示，实时截会自带进来。
  代价：图集从 124k 像素涨到约 313k（整帧的 3.8%），每帧读回 1ms → 2–3ms，
  而且要新开一条「ROI 像素 → `WriteableBitmap`」的通道——现在读数器只读数、不保留像素。

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

# 同上，但 --demo 在 HUD 读不到时顶上一组写死的读数，overlay 就会画成正常态而不是
# 38% 的失效态——专门用来肉眼看 overlay 长什么样。只看样子，不能拿来判断读数。
dotnet run --project src/Anaphora.App -- --process claude --any-foreground --demo

# 按键监听没有开关：1-4 和 E 是唯一能推进轴的东西。拿别的窗口试 overlay 时注意，
# 在那个窗口里打 1-4 或 E 一样会推进轴。

# 发到游戏机上的自带运行时单文件 exe（约 130MB）。DebugType=none 去掉托管符号；
# SkiaSharp 和 HarfBuzz 的原生 .pdb 是 NuGet 包带进来的，运行用不上，发包前删掉。
# profiles/ 和 rotations/ 是 exe 旁边的散文件，不在 exe 里面，要一起拷过去。
dotnet publish src/Anaphora.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish/app

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
- **App 本身也能这么发**（命令见上面的「命令」一节）。2026-10-24 在开发机上验过发布版：
  WGC 抓取、overlay 窗口尺寸、鼠标穿透、全局快捷键在单文件自带运行时模式下都正常。
  **但 `profiles/` 和 `rotations/` 是 exe 旁边的散文件，不在 exe 里**，拷过去要带上，
  否则起来就找不到 profile。
- overlay 的端到端测试、alt-tab / 切分辨率的稳定性测试只能在游戏机上做。
- **不要用串流（Moonlight / Parsec / Steam）的画面标定阈值**：视频压缩会改颜色、通常也不是
  4K，而连携条颜色门在 0.365 这种边界上就会判错。串流只适合看布局。

整个解决方案现在应当 0 警告 0 错误编译通过。

**这台机器上 `python` 是 WindowsApps 的占位 stub，静默失败什么都不做。**
不要用它做文本替换或脚本处理，改用 Edit 工具或 `sed`。
