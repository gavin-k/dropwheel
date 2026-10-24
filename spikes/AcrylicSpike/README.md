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
  可执行文件在 `bin\Release\net10.0-windows10.0.26100.0\win-arm64\AcrylicSpike.exe`（`-r win-x64` 则生成 x64 版，未在本项目中运行过——没有 Intel 机器）。
- 必须在**有桌面的交互会话**里跑（`prlctl exec --current-user`，或直接在 VM 里双击）：它会真的显示窗口并 `BitBlt` 抓屏。
- `run-acrylic.ps1`：在 VM 里按顺序跑下表四组，打印每个日志；若「透明效果」系统设置是关的会临时打开并在结束时恢复。幂等。
- 日志与截图在 `%LOCALAPPDATA%\DropWheelSpike\acrylic-<variant>-<时间>.log` / `*.bmp`。

| 参数 | 作用 |
| --- | --- |
| `--variant acrylic`（默认） | 透明宿主窗口 + `SystemBackdropElement` 亚克力圆盘——M0 的目标设计 |
| `--variant none` | 透明宿主窗口 + 普通半透明纯色圆盘：同样的几何，没有 backdrop，作为开销基线 |
| `--variant opaque` | 不透明深色宿主窗口 + 同一个亚克力圆盘：对照组，用来区分「透明窗口的问题」和「亚克力的问题」 |
| `--default-config` | 不覆盖 `SystemBackdropConfiguration`，用 XAML 给的默认值（见发现 2） |
| `--runs N`（默认 20）、`--size px`（默认 600 物理像素） | 显示/隐藏循环次数、圆盘直径 |

每轮：抓屏（窗口中央 200×200 物理像素）→ 记前台窗口 → `Show(false)` → 工作线程每 ~3 ms 抓屏直到 >2% 像素变化（即「真正上屏」）；同时记录第一次 `CompositionTarget.Rendered`
→ 等 400 ms → 再记前台窗口、`DesktopAcrylicController.State`、圆盘区域统计 → `Hide()` → 等 300 ms。前两轮保存整窗 BMP。
窗口下面先放一个彩色棋盘格窗口当背景（见截图），否则在纯色桌面上看不出有没有模糊。

退出码：0 = 20 轮里前台窗口从未改变、每轮都上屏、**热身后中位上屏耗时 ≤ 150 ms**；1 = 任一不满足；2/3 = 异常。

## 结论

| 问题 | 结论 | 依据（最终二进制，Win11 22H2 build 22621.4169，ARM64 VM，Parallels 显卡，200% DPI） |
| --- | --- | --- |
| 1. 弹出耗时 | ✅ 远在预算内——**热身后** `Show()`→真正上屏中位 54 ms、最大 75 ms（acrylic，19 轮） | 见下表；`none` 72 ms、`opaque` 52 ms 中位，三者差异在抓屏轮询的噪声范围内，**亚克力本身没有带来可测的额外弹出延迟** |
| 1a. 冷启动第 1 轮 | ⚠️ **不稳定，可能超预算**：最终二进制的 acrylic 第 1 轮 219 ms；之前三次构建的 acrylic 第 1 轮分别 48、53、89 ms | 第 1 轮包含 XAML 首次布局/首帧、合成器与 DWM 首次为该窗口建立表面；M0 必须在启动时预热一次（见发现 4） |
| 2. 不抢焦点 | ✅ 65 轮（20+20+20+5）前台窗口全部没变 | `WS_EX_NOACTIVATE` + `AppWindow.Show(activateWindow: false)`；窗口不出现在 Alt+Tab（`IsShownInSwitchers=false`） |
| 3. 亚克力是否真的渲染 | ✅ 在这台 VM 里 `DesktopAcrylicController.State = Active`，截图可见背后棋盘格被模糊（边缘梯度 1.30 vs 无 backdrop 的 1.97）；**前提是把 `IsInputActive` 钉成 true**，用默认配置会回退成纯色（见发现 2） | `shots/02-acrylic-active.png` vs `shots/03-none-plain-overlay.png` vs `shots/05-default-config-fallback.png`（回退色故意设成品红，264 008 个品红像素） |
| 3a. GPU / DWM 开销 | ❓ **未能量化**：非提权进程读不到 `dwm.exe` 的 CPU 时间（拒绝访问），VM 里也没有 GPU 计数器；本进程每轮 CPU 0–109 ms（含抓屏）无法区分亚克力贡献 | 需要在真机上用 Task Manager / PerfMon 的 GPU 引擎计数器或 PIX 看，列入人工清单 |

### 测量值（`run-acrylic.ps1` 一次完整运行，最终二进制）

| 变体 | 轮数 | `Show()`→`CompositionTarget.Rendered`：第 1 轮 / 热身后 min / 中位 / max | `Show()`→真正上屏：第 1 轮 / 热身后 min / 中位 / max | 前台改变 | 亚克力状态 |
| --- | --- | --- | --- | --- | --- |
| `acrylic` | 20 | 47 / 2 / 6 / 11 ms | **219** / 31 / **54** / 75 ms | 0 | Active |
| `none` | 20 | 20 / 2 / 8 / 24 ms | 65 / 47 / 72 / 106 ms | 0 | n/a |
| `opaque` | 20 | 41 / 3 / 6 / 25 ms | 56 / 28 / 52 / 73 ms | 0 | Active |
| `acrylic --default-config` | 5 | 33 / 2 / 3 / 10 ms | 78 / 25 / 29 / 47 ms | 0 | **Fallback** |

- 窗口从 `new Window()` 到配置完隐藏待命：22–46 ms（四组）。M0 在启动时做一次即可，不在 150 ms 预算里。
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
   并在 `OnDefaultSystemBackdropConfigurationChanged` 里重新应用。这意味着系统的「非活动窗口不画亚克力」省电策略对我们失效，是有意为之。
3. **`ICompositionSupportsSystemBackdrop.SystemBackdrop` 的类型是 `Windows.UI.Composition.CompositionBrush`**（系统命名空间，不是 `Microsoft.UI.Composition`），
   而 `Window.Compositor` 是 Microsoft.UI 的，类型不兼容。要用 `new Windows.UI.Composition.Compositor()`，而它要求线程上有 `Windows.System.DispatcherQueue`——WinUI 3 线程只有 Microsoft.UI 的那个，
   需要先 `CreateDispatcherQueueController`（CoreMessaging.dll）。见 `SystemCompositor`。
4. **第一次显示要预热。** 冷启动第 1 轮 48–219 ms 波动，最终二进制那次超了 150 ms；热身后稳定在 30–75 ms。M0 启动时把覆盖层在屏幕外（或 `Opacity=0`）显示一帧再隐藏。
5. **Shell 的计划与此不一致。** `src/DropWheel.Shell/DropTargetHost.cs` 的 TODO 写的是 `WS_EX_LAYERED` 分层窗口；WinUI 3 窗口的透明走的是上面的 DWM 配方，`WS_EX_LAYERED` + `SetLayeredWindowAttributes` 会和合成器的呈现方式冲突（未测，是推断）。
   M0 要么让放置目标窗口（Shell，纯 Win32）与轮盘窗口（App，WinUI）是两个窗口，各用各的透明机制；要么统一到 WinUI 窗口上 `RegisterDragDrop`。这是 M0 开工前要定的设计决定，本 spike 不替它定。
6. `WindowsAppSDKSelfContained=true` 的非打包 WinUI 3 应用在 ARM64 VM 上直接从输出目录运行，无需安装运行时；首帧 `CompositionTarget.Rendered` 在 `Show()` 后 20–47 ms。
7. 在 VM 里 `DesktopAcrylicController.IsSupported()` 为 true 且状态 Active；之前担心的「VM / 远程桌面下亚克力一律回退」在 Parallels 22H2 ARM64 这台机器上**没有发生**。真机结果仍需确认（人工清单）。

## 还需要人工做的事

1. 在真机（非 VM）上跑 `run-acrylic.ps1`，确认 `ACRYLIC: Active` 且截图里有模糊；同时打开任务管理器「性能 → GPU」或 PerfMon `GPU Engine` 计数器，比较 `acrylic` 与 `none` 两组运行期间的 GPU 占用（问题 3a）。
2. 在系统设置里关掉「透明效果」，再跑一次 `--variant acrylic`，确认行为是 `Fallback`（纯色）而不是黑块/白块；这是省电模式与高对比度下的预期降级路径，本 spike 没有覆盖（`run-acrylic.ps1` 只会把它临时打开）。
3. 多显示器 / 不同 DPI 下 `DisplayArea` 与 `MoveAndResize` 的物理像素坐标是否与抓屏区域对齐（本机单屏 200%）。
