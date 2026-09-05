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
```

单进程、两个窗口。Capture 与 Overlay 共享一个进程，不做 IPC。

`Core` 与 `Analysis` 目标 `net10.0`（无平台依赖，跑得快、好测）；其余目标
`net10.0-windows10.0.26100.0`，`SupportedOSPlatformVersion` 压到 `10.0.19041.0`
以保留 Win10 2004 的运行能力。包版本集中在 `Directory.Packages.props`（CPM）。

## Avalonia 12 注意

Avalonia 12 是较新的大版本，与 11.x 有 API 差异，且模型的训练语料主要是 11.x。
**遇到任何 Avalonia API 问题，先查 avalonia-docs MCP，不要凭记忆写。**

### DevTools

`Avalonia.Diagnostics` **已废弃**，没有 12.x 版本，任何情况下都不要再引用它。
替代品是 `AvaloniaUI.DiagnosticsSupport` 包（已在 CPM 中，2.2.3）＋
`AvaloniaUI.DeveloperTools` 全局工具（已安装，命令为 `avdt`）。它是独立版本线，
不跟随 Avalonia 版本号。

写下 `Program.cs` / `App.axaml.cs` 时必须同时接上这两处，缺一不可 ——
`.WithDeveloperTools()` 启用基础设施，`AttachDeveloperTools()` 才真正连上 DevTools 进程：

```csharp
// Program.cs
public static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithDeveloperTools()
        .LogToTrace();

// App.axaml.cs
public override void Initialize()
{
    AvaloniaXamlLoader.Load(this);
#if DEBUG
    this.AttachDeveloperTools();
#endif
}
```

API 叫 `AttachDeveloperTools()`，不是 11.x 时代的 `AttachDevTools()`；也不要写
`using Avalonia.Diagnostics;`。连不上时把调用改成
`this.AttachDeveloperTools(o => o.DiagnosticLogger = DiagnosticLogger.CreateConsole())`
（需 `using AvaloniaUI.DiagnosticsProtocol;`），日志直接打到 stdout。

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

### 待验证（尚未确认）

- 目标游戏是否设置了 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`。若设置了，
  WGC 只会拿到黑帧且无解，项目不成立。**这是第一优先级的验证项。**
- 去掉 WGC 黄色捕获边框需要 Win11 + `GraphicsCaptureSession.IsBorderRequired = false`，
  且需先调用 `GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)`；
  打包应用还需 `graphicsCaptureWithoutBorder` 受限能力。需实测能否通过。
- 目标游戏尚未确定，profile 目录还是空的。

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
avdt                                  # Avalonia DeveloperTools（全局工具）
```

脚手架阶段 `Anaphora.App` 是 `WinExe` 但还没有入口点，`dotnet build` 会以 CS5001
失败；其余五个项目编译干净。写下第一个 `Program.cs` 后即恢复正常。

**这台机器上 `python` 是 WindowsApps 的占位 stub，静默失败什么都不做。**
不要用它做文本替换或脚本处理，改用 Edit 工具或 `sed`。
