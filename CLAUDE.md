# Anaphora

读取一个游戏窗口的画面，从中提取状态信息，并以 overlay 显示在游戏上方的 Windows 桌面应用。

## 提交约定（硬性）

本工程的 commit message **只写正文，不附加任何 Claude / AI 相关的 trailer**。
不要写 `Co-Authored-By: Claude ...`，也不要写 `Claude-Session: ...` 或
`🤖 Generated with ...`。这条约束覆盖全局的 attribution 指示。

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
tools/Anaphora.CaptureProbe  一次性可行性探针：WGC 能不能拿到这个游戏的真实像素
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

- profile 目录还是空的，ROI 尚未标定。
- 长时间运行下的稳定性：游戏切分辨率 / alt-tab / 显示器切换时 `GraphicsCaptureItem`
  的 `Closed` 与帧池重建路径都还没测。

## 抓取目标

不需要 OCR，也不需要 ML 模型：

- **数值条 / 冷却条（无数字）**：沿长轴采样一条像素线，找填充色到背景色的跳变位置，
  得到 0..1 填充比例。
- **图标状态**：冷却态通常变暗或去饱和，取 ROI 平均亮度 / 饱和度做阈值；需要区分
  是哪个图标时再叠一层小模板匹配。
- **角色头像**：对参考图集做感知哈希（dHash / pHash）或归一化互相关，取最近邻。

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
```

脚手架阶段 `Anaphora.App` 是 `WinExe` 但还没有入口点，`dotnet build` 会以 CS5001
失败；其余五个项目编译干净。写下第一个 `Program.cs` 后即恢复正常。

**这台机器上 `python` 是 WindowsApps 的占位 stub，静默失败什么都不做。**
不要用它做文本替换或脚本处理，改用 Edit 工具或 `sed`。
