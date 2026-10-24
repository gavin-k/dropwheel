# AcrylicSpike — M0 透明覆盖窗口 + 亚克力轮盘的弹出耗时验证

一次性验证代码（WinUI 3 / Windows App SDK 2.x，非打包、`WindowsAppSDKSelfContained`），回答
`docs/01-tech-stack.md` §7 第 5 项：**`SystemBackdropElement` 在透明窗口上的亚克力效果与 GPU 开销（150 ms 弹出预算）**。
拆成三个可以分别回答的问题：

1. 一个**透明、置顶、不抢焦点**（`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`）的 WinUI 3 窗口，预先创建好隐藏着，`AppWindow.Show(false)` 之后多久真正出现在屏幕上？
2. 出现时前台窗口（即拖拽源）是否被抢走？
3. 窗口里的 `SystemBackdropElement` 圆盘是**真的亚克力**（模糊背后内容）还是回退成纯色？开销多大？

**故意不加入 `DropWheel.slnx`**，也不会被移植进 `src/`；但下面「写给做 M0 的人」一节里的几个坑是 M0 实现必须处理的。

## 构建与运行

- 只能在 Windows 上构建：Mac 上即使加 `-p:EnableWindowsTargeting=true` 也会在 XamlCompiler 处崩溃（`output.json was not created`），所以本 spike 在 Mac 上连编译检查都做不了。把仓库镜像到 `C:\src\dropwheel` 后
  `dotnet build C:\src\dropwheel\spikes\AcrylicSpike\AcrylicSpike.csproj -c Release -r win-arm64`，
  可执行文件在 `bin\Release\net10.0-windows10.0.26100.0\win-arm64\AcrylicSpike.exe`（`-r win-x64` 生成 x64 版；给没有 .NET 的真机用 `dotnet publish -c Release -r win-x64 -p:SelfContained=true -o <目录>` 发布后整个目录拷过去即可运行，见「真机结果」）。
- 必须在**有桌面的交互会话**里跑（`prlctl exec --current-user`，或直接在 VM 里双击）：它会真的显示窗口并 `BitBlt` 抓屏。
- `run-acrylic.ps1`：在交互会话里按顺序跑下表四组，打印每个日志；若「透明效果」系统设置是关的会临时打开并在结束时恢复。幂等。可选：`-Exe` 指定可执行文件；`-Runs N`；`-IdleSeconds N` 多跑一组 acrylic 6 轮、第 1 和第 2 轮之间多隐藏 N 秒（人工清单第 4 项「空闲后再显示」）；`-TransparencyOff` 多跑一组系统「透明效果」关闭下的 acrylic（预期 `Fallback`）；`-Counters` 每秒采样 `\GPU Engine(*)\Utilization Percentage` 与 `\Process(dwm)\% Processor Time`，按 dwm / spike 进程汇总 3D 与全部引擎的均值和最大值（人工清单第 1 项）。
- 通过 SSH 跑真机时 SSH 会话没有桌面，用 [`spikes/run-in-console-session.ps1`](../run-in-console-session.ps1) 把命令放进已登录用户的控制台会话（交互式计划任务）并等待输出。
- 日志与截图在 `%LOCALAPPDATA%\DropWheelSpike\acrylic-<variant>-<时间>.log` / `*.bmp`。

| 参数 | 作用 |
| --- | --- |
| `--variant acrylic`（默认） | 透明宿主窗口 + `SystemBackdropElement` 亚克力圆盘——M0 的目标设计 |
| `--variant none` | 透明宿主窗口 + 普通半透明纯色圆盘：同样的几何，没有 backdrop，作为开销基线 |
| `--variant opaque` | 不透明深色宿主窗口 + 同一个亚克力圆盘：对照组，用来区分「透明窗口的问题」和「亚克力的问题」 |
| `--default-config` | 不覆盖 `SystemBackdropConfiguration`，用 XAML 给的默认值（见发现 2） |
| `--runs N`（默认 20）、`--size px`（默认 600 物理像素） | 显示/隐藏循环次数、圆盘直径 |
| `--idle-ms N` | 第 1 轮隐藏后额外等待 N ms 再做第 2 轮，汇总里单独打印第 2 轮的上屏耗时，对比冷启动第 1 轮和热身后中位数 |

每轮：抓屏（窗口中央 200×200 物理像素）→ 记前台窗口 → `Show(false)` → 工作线程每 ~3 ms 抓屏直到 >2% 像素变化（即「真正上屏」）；同时记录第一次 `CompositionTarget.Rendered`
→ 等 400 ms → 再记前台窗口、`DesktopAcrylicController.State`、圆盘区域统计 → `Hide()` → 等 300 ms。前两轮保存整窗 BMP。
窗口下面先放一个彩色棋盘格窗口当背景（见截图），否则在纯色桌面上看不出有没有模糊。

退出码：0 = 20 轮里前台窗口从未改变、每轮都上屏、**热身后中位上屏耗时 ≤ 150 ms**；1 = 任一不满足；2/3 = 异常。

## 结论

| 问题 | 结论 | 依据（最终二进制，Win11 22H2 build 22621.4169，ARM64 VM，Parallels 显卡，200% DPI） |
| --- | --- | --- |
| 1. 弹出耗时 | ✅ 远在预算内——**热身后** `Show()`→真正上屏中位 54 ms、最大 75 ms（acrylic，19 轮） | 见下表；`none` 72 ms、`opaque` 52 ms 中位，三者差异在抓屏轮询的噪声范围内，**亚克力本身没有带来可测的额外弹出延迟** |
| 1a. 冷启动第 1 轮 | ⚠️ **不稳定，可能超预算**：最终二进制的 acrylic 第 1 轮 219 ms；之前三个构建的三次完整运行里分别 48、89 ms，两次 3/5 轮冒烟 60、53 ms（第一个构建还没有棋盘格场景和 DWM 配方，窗口不完全相同） | 推断：第 1 轮包含 XAML 首次布局/首帧、合成器与 DWM 首次为该窗口建立表面。**注意混杂因素**：`run-acrylic.ps1` 总是先跑 `acrylic`，且每次完整运行都紧跟一次重新构建，所以 acrylic 的第 1 轮同时也是「构建后第一个进程」，219 ms 可能是磁盘/DLL 冷启动而非亚克力本身（同样带亚克力圆盘、排第三个跑的 `opaque` 第 1 轮只有 56 ms）。无论哪种原因，M0 都必须在启动时预热一次（见发现 4） |
| 2. 不抢焦点 | ✅ 65 轮（20+20+20+5）前台窗口全部没变 | `WS_EX_NOACTIVATE` + `AppWindow.Show(activateWindow: false)`；窗口不出现在 Alt+Tab（`IsShownInSwitchers=false`） |
| 3. 亚克力是否真的渲染 | ✅ 在这台 VM 里 `DesktopAcrylicController.State = Active`，截图可见背后棋盘格被模糊（边缘梯度 1.30 vs 无 backdrop 的 1.97）；**前提是把 `IsInputActive` 钉成 true**，用默认配置会回退成纯色（见发现 2） | `shots/02-acrylic-active.png` vs `shots/03-none-plain-overlay.png` vs `shots/05-default-config-fallback.png`（回退色故意设成品红，264 008 个品红像素） |
| 3a. GPU / DWM 开销 | ❓ **未能量化**：非提权进程读不到 `dwm.exe` 的 CPU 时间（拒绝访问），VM 里也没有 GPU 计数器；本进程每轮 CPU 0–109 ms（含抓屏）无法区分亚克力贡献 | `run-acrylic.ps1 -Counters` 可以采样 GPU 引擎计数器（本次没有在真机跑出有效数据：唯一的真机渲染异常，见「真机结果」），仍列入人工清单 |
| 3b. 真机 x64（Win10 21H2，Intel） | ⚠️ 自包含 x64 包能跑、`IsSupported=True`、透明效果开关与控制器状态的关系得到确认；**弹出耗时与 GPU 开销无法测量**：该机所有 D3D 硬件呈现的窗口（WPF 也一样）都显示成纯白矩形 | 见「真机结果」一节 |

### 测量值（`run-acrylic.ps1` 一次完整运行，最终二进制）

| 变体 | 轮数 | `Show()`→`CompositionTarget.Rendered`：第 1 轮 / 热身后 min / 中位 / max | `Show()`→真正上屏：第 1 轮 / 热身后 min / 中位 / max | 前台改变 | 亚克力状态 |
| --- | --- | --- | --- | --- | --- |
| `acrylic` | 20 | 47 / 2 / 6 / 11 ms | **219** / 31 / **54** / 75 ms | 0 | Active |
| `none` | 20 | 20 / 2 / 8 / 24 ms | 65 / 47 / 72 / 106 ms | 0 | n/a |
| `opaque` | 20 | 41 / 3 / 6 / 25 ms | 56 / 28 / 52 / 73 ms | 0 | Active |
| `acrylic --default-config` | 5 | 33 / 2 / 3 / 10 ms | 78 / 25 / 29 / 47 ms | 0 | **Fallback** |

- 窗口从 `new Window()` 到配置完隐藏待命：22–46 ms（四组）。M0 在启动时做一次即可，不在 150 ms 预算里。
- **「热身后」只代表隐藏 300 ms 再显示。** DropWheel 的真实场景是覆盖层上次拖拽后隐藏了几分钟到几小时再显示，DWM / 合成器是否会在长时间隐藏后回收该窗口的表面、启动时预热是否还有效，本 spike 没测（见人工清单第 4 项）。
- `CompositionTarget.Rendered` 比像素真正变化早约 25–65 ms（四组热身后中位数之差）：它只说明 XAML 出了一帧，不代表 DWM 已经合成上屏。做「弹出耗时」的验收必须抓屏，不能只看这个事件。
- 同一二进制、同一场景下圆盘统计值每轮完全一致（灰度均值 58、σ 24.9、梯度 1.30），说明亚克力每次显示都稳定地渲染，没有「先纯色再变模糊」的过渡被抓到（400 ms 后抓的；更早的中间态未抓）。
- 抓屏用的是 `BitBlt(CAPTUREBLT)`，200×200 一次约 1–3 ms（推断：由轮询间隔与结果粒度估计，未单独计时），所以「上屏」时间的分辨率约 5 ms。

### 截图（`shots/`，整窗 680×680 物理像素）

| | |
| --- | --- |
| `01-scene-before-show.png` 显示前：棋盘格背景窗口 | `02-acrylic-active.png` acrylic：宿主窗口透明，圆盘内棋盘格被模糊并加深色 tint |
| `03-none-plain-overlay.png` none：同样透明宿主，圆盘只是 50% 深色，边缘锐利 | `04-opaque-host.png` opaque：宿主不透明深灰，亚克力圆盘仍透过宿主模糊了桌面——系统 backdrop 采样的是窗口**背后**的内容，不是宿主窗口自身 |
| `05-default-config-fallback.png` 默认配置：State=Fallback，整盘品红 | |

## 写给做 M0 的人

1. **光设 `SystemBackdrop` 透明笔刷，窗口不会透明。** 第一版只把 `ICompositionSupportsSystemBackdrop.SystemBackdrop` 设成 alpha=0 的 `CompositionColorBrush`，结果整窗是白底
   （HWND 自己的 GDI 表面是类背景色，DWM 不把它当 per-pixel alpha）。WinUIEx `TransparentTintBackdrop` 的完整配方才有效，本 spike 照搬在 `Backdrops.cs`：
   `DwmExtendFrameIntoClientArea(hwnd, {0})` + `DwmEnableBlurBehindWindow(hwnd, 空区域 CreateRectRgn(-2,-2,-1,-1), DWM_BB_ENABLE|DWM_BB_BLURREGION)`
   + 子类化窗口在 `WM_ERASEBKGND` 用 `0x00000000` 黑刷填充（连接时也填一次）+ `WM_DWMCOMPOSITIONCHANGED` 时重做 DWM 调用。
   另外 Win11 会给这个「看不见的矩形」画 1 px 圆角描边，要 `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE=DONOTROUND)` 和 `DWMWA_BORDER_COLOR=DWMWA_COLOR_NONE`。
2. **不抢焦点的窗口默认拿不到亚克力。** XAML 给的默认 `SystemBackdropConfiguration.IsInputActive` 对从未激活的窗口是 false，`DesktopAcrylicController` 对非活动窗口直接 `Fallback`
   （`--default-config` 5/5 轮 Fallback）。覆盖层永远不会激活，所以必须自己 `SetSystemBackdropConfiguration(new() { IsInputActive = true, Theme = …, IsHighContrast = 默认值 })`，
   并（未验证的建议）在 `OnDefaultSystemBackdropConfigurationChanged` 里重新应用以跟随主题/高对比度变化。这意味着系统的「非活动窗口不画亚克力」省电策略对我们失效，是有意为之。
3. **`ICompositionSupportsSystemBackdrop.SystemBackdrop` 的类型是 `Windows.UI.Composition.CompositionBrush`**（系统命名空间，不是 `Microsoft.UI.Composition`），
   而 `Window.Compositor` 是 Microsoft.UI 的，类型不兼容。要用 `new Windows.UI.Composition.Compositor()`，而它要求线程上有 `Windows.System.DispatcherQueue`——WinUI 3 线程只有 Microsoft.UI 的那个，
   需要先 `CreateDispatcherQueueController`（CoreMessaging.dll）。见 `SystemCompositor`。
4. **第一次显示要预热。** 冷启动第 1 轮 48–219 ms 波动（部分可能是进程冷启动而非亚克力，见上表 1a），最终二进制那次超了 150 ms；热身后稳定在 30–75 ms。M0 启动时把覆盖层在屏幕外（或 `Opacity=0`）显示一帧再隐藏。
5. **Shell 的计划与此不一致。** `src/DropWheel.Shell/DropTargetHost.cs` 的 TODO 写的是 `WS_EX_LAYERED` 分层窗口；WinUI 3 窗口的透明走的是上面的 DWM 配方，`WS_EX_LAYERED` + `SetLayeredWindowAttributes` 会和合成器的呈现方式冲突（未测，是推断）。
   M0 要么让放置目标窗口（Shell，纯 Win32）与轮盘窗口（App，WinUI）是两个窗口，各用各的透明机制；要么统一到 WinUI 窗口上 `RegisterDragDrop`。这是 M0 开工前要定的设计决定，本 spike 不替它定。
6. `WindowsAppSDKSelfContained=true` 的非打包 WinUI 3 应用在 ARM64 VM 上直接从输出目录运行，无需安装运行时；首帧 `CompositionTarget.Rendered` 在 `Show()` 后 20–47 ms。
7. 在 VM 里 `DesktopAcrylicController.IsSupported()` 为 true 且状态 Active；之前担心的「VM / 远程桌面下亚克力一律回退」在 Parallels 22H2 ARM64 这台机器上**没有发生**。真机 Win10 21H2 x64 上 `IsSupported` 同样为 true，但状态由系统「透明效果」决定（见下一节）。
8. **「透明效果」注册表值缺失不等于开启。** 真机上 `HKCU\…\Themes\Personalize\EnableTransparency` 不存在时 `UISettings.AdvancedEffectsEnabled=False`、控制器每轮 `Fallback`；写入 1 并广播 `WM_SETTINGCHANGE("ImmersiveColorSet")` 后不用重启就变成 True、控制器 `Active`。M0 不要用「值不存在」推断用户开着透明效果，直接读 `UISettings.AdvancedEffectsEnabled`，并订阅 `AdvancedEffectsEnabledChanged`。
9. **事件都对、像素全白是真实存在的故障模式。** 真机上 `CompositionTarget.Rendered` 照常在 `Show()` 后 +25 ms 触发、控制器报 `Active`，屏幕上却是白块。本 spike 的「上屏」判据抓的是像素，所以抓到了。M0 的启动预热应顺便自检一次：显示一帧后 `BitBlt` 自己的窗口区域，若整块是单一颜色就记日志并降级（纯色 GDI 轮盘，或提示更新显卡驱动），不要默默显示一个白方块。

## 真机结果（x64，Windows 10 LTSC 21H2，Intel Iris Pro 5200）

环境：2015 款 MacBook Pro（Boot Camp），Windows 10 企业版 LTSC 21H2 build 19044.7807 x64，Intel Iris Pro 5200、驱动 20.19.15.4531（2016-09，WDDM 2.0，FL 11_1），
内屏 1920×1200 @125%（dpi 120），另装有一块没接显示器的第三方虚拟显示适配器（远程串流软件的 IddCx 驱动，WDDM 1.3）。机器上没有 .NET，运行的是 VM 上 `dotnet publish -r win-x64 -p:SelfContained=true` 的输出目录；
通过 SSH + `run-in-console-session.ps1` 在控制台会话里跑，日志为 `acrylic-*-20261025-00????.log`（机器本地时间）。

| 问题 | 结论 | 依据 |
| --- | --- | --- |
| 启动 | ✅ 自包含 x64 包在无 .NET 的 Win10 上直接运行，`DesktopAcrylicController.IsSupported=True`，窗口创建 47–102 ms（5 次） | 日志首行 `Windows 21H2 build 19044.7807, X64, process X64, .NET 10.0.13` |
| 「透明效果」开关 ↔ 控制器状态 | ✅ `EnableTransparency` 值**不存在** → `AdvancedEffectsEnabled=False` → 每轮 `Fallback`（acrylic ×3 次运行）；写 1 + 广播 → `True`、`Active`（acrylic、opaque 各 2 轮）；删掉值 → 又回到 False | 人工清单原第 2 项的「状态是 Fallback 而非别的」部分得到确认；像素外观无法确认（见下） |
| 弹出耗时、GPU 开销、空闲后再显示、透明关闭时的像素 | ❌ **无法测量**：acrylic / none / opaque 每轮都 `on-screen none within 2 s`，圆盘区域灰度均值 255、σ 0；桌面截图里 spike 的两个窗口都是**纯白矩形** | `cycle 1: … Rendered +25 ms, on-screen none within 2 s, acrylic=Active, … mean=255 sd=0.0` |

白块的诊断，已证实的：

1. 不是抓屏问题：对同一窗口 `PrintWindow(PW_RENDERFULLCONTENT)` 与屏幕 `BitBlt` 结果一致（都是白）；桌面上的 GDI 窗口（资源管理器、cmd）抓得正常。
2. 不是 WinUI 3 独有：WPF（PowerShell ISE）硬件渲染时同样整窗白（40×40 采样灰度均值 248、6 种颜色）；写 `HKCU\SOFTWARE\Microsoft\Avalon.Graphics\DisableHWAcceleration=1` 后 ISE 立刻正常（均值 77、116 种颜色，截图可见菜单与控制台）。也就是说这台机器上**凡经 D3D 硬件呈现的窗口内容都是白的**，GDI 内容正常。
3. 不是注入：spike 进程 129 个模块全部来自 `C:\Windows` 或应用目录，用的是 Intel UMD `igd10iumd64.dll 20.19.15.4531`；`AppInit_DLLs` 为空；资源管理器里也没有第三方模块。
4. 与每应用 GPU 偏好（`DirectX\UserGpuPreferences` 设 1 或 2）无关；Application / System 事件日志里没有相关错误；dxdiag 两块适配器 `D3D Status: Enabled`。

未判别的候选原因（两个都没有排除）：(a) 2016 年的 Intel 驱动；(b) 那块虚拟显示适配器。判别办法是停用虚拟适配器再截一次 ISE，或更新 Intel 驱动——两者都改机器状态，本次没有做，列入人工清单。
**对 M0 的意义**见发现 8、9。

## 还需要人工做的事

1. 找一台 D3D 硬件渲染正常的真机（判据：PowerShell ISE 不是白窗口）。上面那台要么更新 Intel 驱动，要么先停用那块虚拟显示适配器再试；两者都是机器主人的决定。
2. 在那台机器上跑一次 `run-acrylic.ps1 -Exe <x64 发布目录>\AcrylicSpike.exe -Runs 20 -IdleSeconds 300 -TransparencyOff -Counters`（约 10 分钟），一次回答：`acrylic` 与 `none` 的 dwm / spike GPU 引擎占用差（问题 3a）、系统「透明效果」关闭时是 `Fallback` 纯色而非黑块/白块（状态部分已在 Win10 真机确认，像素部分未确认）、隐藏 5 分钟后第 2 轮上屏耗时是否仍接近热身后水平（原第 4 项；`--idle-ms` 代码已在真机以 idleMs=0 跑过，非零空闲还没有跑过）。
3. 多显示器 / 不同 DPI 下 `DisplayArea` 与 `MoveAndResize` 的物理像素坐标是否与抓屏区域对齐（VM 单屏 200%，真机单屏 125%，都没有第二块屏）。
