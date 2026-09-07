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
src/Anaphora.Capture    WGC 会话、D3D11 设备、ROI 图集 shader、staging 读回
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
- Overlay 窗口：`SystemDecorations=None` + `Background=Transparent` +
  `TransparencyLevelHint=Transparent` + `Topmost` + `ShowInTaskbar=false`，再对
  `TopLevel.TryGetPlatformHandle().Handle` 设置
  `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`。常驻显示，**不需要点击交互**。
- Overlay 的渲染实现必须放在接口后面。若 Avalonia 的透明窗口出现 artifact 或性能问题，
  退路是手写 `WS_EX_NOREDIRECTIONBITMAP` + DirectComposition + Direct2D 窗口，
  届时只应替换这一个模块。
- **帧率影响**：无边框全屏下 DWM 可能走 independent flip / MPO 直接扫描输出游戏
  swapchain。任何常驻置顶窗口都会让这条快速路径失效，退回合成路径，掉几个百分点的帧。
  这是本方案的固有代价，已被接受，不要试图"优化"掉。
- WGC 帧按游戏 present 速率推送，**没有内建限帧**。处理不过来时在 `FrameArrived` 里
  立即 `Dispose` 丢帧，不要排队。新版 Windows 上可探测
  `GraphicsCaptureSession.MinUpdateInterval` 直接限速。目标更新率 15–30 Hz。
- **全程不把整帧下到 CPU**：一个 pixel shader 把所有 ROI 打包进一张小图集
  （约 256×256），一次 `CopyResource` 到 staging，每帧只 Map 一次。
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

- **连携提示的头像识别还没法做**：`profiles/endfield.json` 的 `portraits` 是空的，
  两个提示槽的坐标也还是目测的（±3px）。原因是连拍里出现提示的是第 33/34/35 帧，
  而全分辨率只存了 4 的倍数，手上没有一张带提示的原图。**需要再连拍一次，
  `fullEvery` 设 1、间隔 500ms，并在战斗中刻意攒出连携提示。** 有了原图才能量准坐标、
  给队伍四人算 dHash。
- **技能点归零时的哨兵行为仍未验证**。现在的双侧对比判据在理论上成立（轨道提供暗、
  边框提供非暗），但连拍里技能点从没归零过，没有实拍样本。
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

**最终目标是做轴提示**——告诉玩家下一步该按什么。但那很复杂，**先从读取界面、把信息
显示出来开始**。所以现阶段 overlay 只做"如实呈现"，不做推荐；四个关注项要一起看，
价值在于让顺序决策不用靠记忆和余光扫屏。

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

这个区域同时兼任 HUD 哨兵，见下面「HUD 有效性判定」。

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

**已量准**：四个小圈直径 72，圆心 y `1781`，左边缘 `3117 / 3309 / 3501 / 3693`，
节距正好 192。圈下的数字 1–4 既是按键也对应左下角四个角色的顺序。

读法：**圈内有彩色图标 = 可释放；圈内空的深色、只有环上一段弧 = 充能中**。
实测内盘平均亮度：可释放 170–191，充能中 9–50，阈值取 100，两边都是数倍余量。

**只采内盘的圆形区域（`innerRadius` 0.62），不能采外接正方形**：正方形的四角会碰到
圆环，而环上那段弧正是不能influence判定的东西。

**饱和度在这里完全没用，必须关掉。** 空圈实测 `#001018`，几乎全黑，但 HSV 饱和度算
出来是 **1.00**——极暗色的饱和度本来就不稳定。只用亮度。

技能大圈本身的状态（底部往上涨的液面 + 去饱和）复杂，**用户明确说先不关注**。

### 4. 连携提示（按 E 触发）

**位置固定，不跟随角色或敌人**——用连续三帧（033/034/035）同一矩形验证过，`E` 角标
和主头像像素级重合。

- 主提示圆形头像中心约 (2358, 987)，直径约 150
- 左侧约 (2223, 975) 是 `E` 按键角标，头像上方有连携标志
- 同时有多个可选时，第二个头像在右侧约 (2529, 1011)、直径约 120，**更小更靠下**
- **最左边最大的那个是按 E 会先触发的**

识别用感知哈希对队伍四人头像做最近邻。每个头像右下角还有一个职业/类型小徽章。

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

**哨兵位置用技能点条**（用户的建议，实测成立），但**判据不能是颜色或亮度，必须是
"这块区域内部有没有对比"**——既要有足够的暗，也要有足够的非暗。

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
```

脚手架阶段 `Anaphora.App` 是 `WinExe` 但还没有入口点，`dotnet build` 会以 CS5001
失败；其余五个项目编译干净。写下第一个 `Program.cs` 后即恢复正常。

**这台机器上 `python` 是 WindowsApps 的占位 stub，静默失败什么都不做。**
不要用它做文本替换或脚本处理，改用 Edit 工具或 `sed`。
