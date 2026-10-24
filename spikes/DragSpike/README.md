# DragSpike — M0 拖拽侦测技术验证

一次性验证代码（WinForms + 手写钩子），回答 `docs/01-tech-stack.md` §7 里 M0 之前必须确认的问题：
能否侦测系统级拖拽、在拖拽中捕获 Shift 轻点、在不打断拖拽的前提下弹出投放轮盘、并收到投放的文件。
**故意不加入 `DropWheel.slnx`**（会重新引入 MSB4126 平台映射失败），也不会被移植进 `src/`。

## 构建与运行

- Mac 上只做编译检查：`~/.dotnet/dotnet build spikes/DragSpike/DragSpike.csproj -c Release`
- Windows（本项目的 Parallels VM）上构建并运行：把仓库镜像到 `C:\src\dropwheel` 后
  `dotnet build C:\src\dropwheel\spikes\DragSpike\DragSpike.csproj -c Release`，
  可执行文件在 `bin\Release\net10.0-windows10.0.26100.0\DragSpike.exe`。
  给没有 .NET 的 x64 真机：`dotnet publish … -c Release -r win-x64 -p:SelfContained=true -o <目录>`，整个目录拷过去直接运行；
  通过 SSH 跑时用 [`spikes/run-in-console-session.ps1`](../run-in-console-session.ps1) 把命令放进控制台会话。
  自测必须在**有桌面的交互会话**里跑（`prlctl exec --current-user`，或直接在 VM 里双击），
  因为它用 SendInput 真的移动鼠标、真的打开资源管理器。

| 模式 | 作用 |
| --- | --- |
| `DragSpike` | 手动模式：装好钩子，从资源管理器拖任何文件，拖拽中轻点 Shift，投放到扇区；默认 300 s（`--duration N`） |
| `DragSpike --selftest` | 自动：自带 WinForms 拖拽源 + SendInput 驱动，通过返回 0 |
| `DragSpike --selftest --explorer` | 自动：从**真实资源管理器窗口**里拖 `%TEMP%\DropWheelSpike\sample-drop.jpg` |
| `… --winkey` / `… --winkey-pass` | 自测附加步骤（§7 #4）：轮盘出现后、投放前轻点 **Win 键**。`--winkey` 让 LL 钩子在拖拽中吞掉 Win 键并断言开始菜单没弹出；`--winkey-pass` 是对照组，Win 键原样放行，只记录不判定 |
| `uieffects-test.ps1 [-Exe path]` | 在交互会话里跑：关掉「拖动时显示窗口内容」「视觉效果/动画」「窗口阴影」「鼠标指针阴影」四项，再跑一次 `--selftest --explorer`，结束后恢复（§7 #2 后半）。运行前会杀掉无窗口的 explorer.exe（见发现 10） |
| `uieffects-matrix.ps1 [-Exe path]` | 同上但更细：对照 → 四项全关 ×2 → 每项单独关 → 对照，每次一行打印侦测来源 / 轮盘首帧 / 投放耗时；不杀 explorer |
| `DragSpike --uia-dump` | 诊断：旧版托管 UIA 客户端看到的资源管理器窗口树 |
| `--dragsource` / `--locate` | 内部用：拖拽源窗口 / 无钩子的 UIA 定位子进程 |

日志写到 `%LOCALAPPDATA%\DropWheelSpike\spike-<时间>.log`（定位子进程为 `locate-<时间>.log`），时间戳为进程启动后的毫秒数。

## 验证环境

两个环境：

1. Parallels 里的 Windows 11 22H2（22621.4169）**ARM64**，.NET 10.0.13，单显示器 3024×1812 物理像素、192 DPI（200%）。下文未注明的数字都来自这里。
2. 真机 **x64**：2015 款 MacBook Pro（Boot Camp），Windows 10 企业版 LTSC 21H2（19044.7807），Intel Iris Pro 5200，单显示器 1920×1200、120 DPI（125%），机器上没有 .NET，跑的是 `win-x64` 自包含发布；通过 SSH + `run-in-console-session.ps1` 在控制台会话里跑。见「真机 x64 测量值」。

没有 24H2，没有多显示器。

## 对照 §7 清单的结论

| § | 项目 | 结论 | 依据 |
| --- | --- | --- | --- |
| 1 | 低级鼠标/键盘钩子在拖拽期间的延迟与稳定性 | ✅ 可行（ARM64 VM 与 x64 真机） | Explorer 拖拽中 Shift 轻点经 WH_KEYBOARD_LL 到达用时 0.2–1.5 ms（距 SendInput 键抬起）；6 次连续 Explorer 自测全部通过，Explorer 的 OLE 拖拽消息循环没有阻塞钩子回调。真机 Win10 x64：12 次 Explorer 自测钩子延迟 0.2–0.4 ms，全部到达 |
| 2 | `SysDragImage` 侦测是否有效 | ✅ 22H2 与 Win10 21H2 有效，关闭拖拽相关视觉效果后仍有效；**24H2 未测** | Explorer 拖拽开始后 45–79 ms 出现 `SysDragImage` 窗口（25 ms 轮询）。用 `uieffects-test.ps1` 关掉四项视觉效果后 2 次都仍出现（第 1 次 +64 ms，第 2 次日志只截到出现本身）。清单里写的「关闭拖拽时显示图标」在 22621 的 27 个文件夹选项里**不存在**，见发现 8。`EVENT_SYSTEM_DRAGDROPSTART` WinEvent 在本机**从未触发**，不能依赖。真机 Win10 x64：`SysDragImage` +48…+67 ms，WinEvent 同样不触发；四项视觉效果全关 7 次里 6 次仍出现（+50…+67 ms），1 次没出现且投放也没到（见发现 10），单项关闭各 1 次都出现 |
| 3 | 分层窗口 `RegisterDragDrop` 多显示器/DPI 坐标 | ⚠️ 单屏 200%（VM）与 125%（真机）通过；**多显示器未测** | WinForms PerMonitorV2 + `AllowDrop`，物理像素坐标下 6 次投放都落在目标扇区；真机 125% 下 12 次也都落在目标扇区；两台都只有一个显示器 |
| 4 | 拖拽中按 Win 键 | ✅ 吞掉即可 | `--winkey`（LL 键盘钩子对 Win 按下/抬起都返回 1）3 次：开始菜单未弹出、拖拽继续、投放正常到达。对照 `--winkey-pass` 3 次：开始菜单（搜索窗口）弹出并成为前台窗口，3 s 内收不到投放。见发现 7。真机 Win10 x64：`--winkey` 1 次通过（吞掉 2 个键事件，前台仍是 spike）；`--winkey-pass` 1 次前台变成「搜索」窗口但投放 11 ms 仍到达，与 22H2 不同，见发现 9 |
| 5 | 亚克力背景 / 150 ms 弹出预算 | ✅ 弹出耗时；⚠️ GPU 开销未量化 | 本 spike 只量了无 backdrop 的 WinForms 窗口（Shift 抬起到首帧 21–93 ms）；真正的 WinUI 3 透明窗口 + `SystemBackdropElement` 由 [`AcrylicSpike`](../AcrylicSpike/README.md) 验证：热身后 `Show()`→上屏中位 54 ms、最大 75 ms，亚克力 `Active`，不抢焦点；冷启动第 1 轮最长 219 ms，M0 需预热；dwm GPU 开销在 VM 里读不到 |
| 6 | Magick.NET / PDFium / FFmpeg ARM64 包 | ✅ 见 [`spikes/MagickSpike`](../MagickSpike/README.md) | Magick.NET 14.9.0 在 ARM64 进程内加载并完成 JPG→PNG/WebP/AVIF/JXL；PDFium、SkiaSharp 包内带 `win-arm64`；BtbN 提供 `winarm64-lgpl` FFmpeg；7-Zip extra 包内有 `arm64/7za.exe` |

### 测量值

最终二进制，同一 VM：

| 指标 | WinForms 拖拽源（1 次） | 真实 Explorer 拖拽源（6 次） |
| --- | --- | --- |
| 拖拽侦测：LL 鼠标越过 8 px 阈值 | 首个 move 后 +1 ms | +1…+3 ms |
| 拖拽侦测：`SysDragImage` 出现 | 不出现（预期，见下） | +45…+79 ms |
| 拖拽侦测：WinEvent DRAGDROPSTART | 不触发 | **不触发** |
| Shift 轻点到达 LL 键盘钩子 | 0.3 ms | 0.2–1.5 ms |
| 轮盘首帧（距 Shift 抬起） | 20.9 ms | 27.7–93.1 ms |
| `DragDrop` 收到（距左键抬起） | 10 ms | 13–41 ms |
| 投放数据格式 | FileDrop, FileNameW, FileName | Shell IDList Array, DragImageBits, FileDrop, FileNameW, FileContents, FileGroupDescriptorW… |
| 拖拽是否被轮盘打断 | 否 | 否（Explorer 的 DoDragDrop 一直持续到投放） |
| 拖拽中轻点 Win 键（`--winkey` 吞掉 / `--winkey-pass` 放行，各 3 次） | 未测 | 吞掉：开始菜单未弹出，投放 31–36 ms 到达 / 放行：开始菜单弹出，投放未到达 |
| 关掉四项拖拽相关视觉效果后 `SysDragImage` 是否出现（`uieffects-test.ps1`，2 次） | 未测 | 仍出现（第 1 次 +64 ms），自测 PASS，四项设置运行后恢复原值 |

### 真机 x64 测量值

Windows 10 LTSC 21H2 x64，Intel Iris Pro 5200，1920×1200 @125%，同一 x64 自包含二进制；日志 `spike-20261025-00????.log`，补跑的 4 次是 `spike-20261025-0117??.log` / `-0118??.log`（机器本地时间）。

| 指标 | 真实 Explorer 拖拽源（`go` 5 次 + 矩阵 7 次 + `uieffects-test` 1 次，之后补跑 `uieffects-test` 4 次） |
| --- | --- |
| 拖拽侦测：LL 鼠标越过阈值（真机 `DragSize` 4×4） | 首个 move 后 +0…+1 ms |
| 拖拽侦测：`SysDragImage` 出现 | +48…+67 ms（16 次）；第一次 `uieffects-test` 没出现 |
| 拖拽侦测：WinEvent DRAGDROPSTART | **不触发** |
| Shift 轻点到达 LL 键盘钩子 | 0.2–0.4 ms |
| 轮盘首帧（距 Shift 抬起） | 12.8–48.9 ms |
| `DragDrop` 收到（距左键抬起） | 9–32 ms（前 12 次；补跑的 4 次只摘了 PASS/FAIL 行，没有这个数）；第一次 `uieffects-test` 3 s 内未收到 |
| 拖拽中轻点 Win 键 | 吞掉（1 次）：开始菜单未弹出、投放 10 ms 到达 / 放行（1 次）：前台变成「搜索」窗口，投放 11 ms **仍到达** |
| 四项视觉效果全关 | 7 次：6 次 PASS（`SysDragImage` +50…+67 ms），1 次 FAIL（见发现 10）；只关「拖动时显示窗口内容」/「视觉效果」/「窗口阴影」各 1 次：全 PASS |
| 结果 | 17 次里 16 次 PASS |

## 过程中的发现（写给做 M0 的人）

1. **不要把 WinForms 自测当成 §7 #1 的答案。** `Control.DoDragDrop` 不会产生 `SysDragImage` 窗口，
   也不触发 WinEvent；只有真实的 Shell 拖拽才有。两种源都保留，是因为 WinForms 源稳定、几秒跑完，适合回归。
2. **`EVENT_SYSTEM_DRAGDROPSTART/END` 在 Explorer 拖拽中不触发**（22621，WINEVENT_OUTOFCONTEXT，钩子安装成功 err 0）。
   M0 的拖拽侦测只能靠 LL 鼠标钩子 + `SysDragImage` 轮询，"拖拽结束"要靠 LL 鼠标的左键抬起。
3. **轮盘窗口必须 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST` 且 `ShowWithoutActivation`**，
   这样显示时不会抢焦点、不会中断 OLE 拖拽。`DragOver` 里要主动设置 `Effect = Copy`，否则 Explorer 显示禁止光标。
4. 钩子回调里的 `injected` 标志在自测中为 `True`（SendInput），真人操作时为 `False`；M0 不要据此过滤。
5. **用 UIA 定位 Explorer 里的文件，是整个 spike 最不稳定的部分**，和产品无关，但决定自测能不能自动跑：
   - 旧版托管客户端 `System.Windows.Automation` 在装了 LL/WinEvent 钩子的进程里对这棵子树几乎总是返回空；
     改为无钩子的子进程（`--locate`）后才有一半成功率。
   - 换成 UIAutomationCore 的 COM 客户端（`IUIAutomation`，CsWin32 生成）后仍有约 1/4 的窗口永远不暴露
     `UIItemsView`，而且这种窗口**连 `WM_CLOSE` 都不响应**（同一个 HWND 跨多次运行反复出现）——
     托管它的 explorer.exe 已经卡死。现在的做法：每个窗口只给 4 s，超时就关窗；不关就杀掉该 explorer.exe（排除 shell 本体），再重开。
   - `explorer.exe /select,…` 必须经 ShellExecute 启动；每次启动都会留下一个无窗口的 explorer.exe，
     新的 `/select` 请求可能被路由到正在退出的进程而丢失，所以每次尝试前先清掉这些孤儿进程。
   - 调整后连续 6 次通过（其中 1 次经历了一次自愈重试）。更稳的做法是不用 UIA、直接用 `IShellFolderView`/`IFolderView::GetItemPosition` 取坐标，留给需要的人。
6. 在 Parallels 里用 `prlctl exec` 跑：命令行超过约 2 KB 会静默挂起，脚本要先落盘再 `-File` 执行。
7. **Win 键（§7 #4）：拖拽中由 WH_KEYBOARD_LL 吞掉 `VK_LWIN`/`VK_RWIN` 的按下和抬起就够了**，开始菜单不会弹出，
   Explorer 的 OLE 拖拽不受影响。放行时开始菜单会弹出并抢走前台，投放落不到轮盘上；
   在一次误发了 Esc 的早期运行里（当时开始菜单检测还有 bug，无法确认 Esc 是否真的关掉了菜单），放行 Win 键之后投放仍然到达了，
   由此**推测**开始菜单只是挡住了投放而没有取消拖拽——产品不能指望这一点，M0 应直接吞键。
   另外产品里只应吞掉「自己吞过按下」的那次抬起：若 Win 在拖拽开始前就已按下，只吞抬起会让系统一直认为 Win 被按住。两个坑：
   - 判断开始菜单是否打开不能只看 `IsWindowVisible`——标题为「开始」的 `Windows.UI.Core.CoreWindow` 一直是 WS_VISIBLE，
     关闭时只是被 DWM cloak（`DWMWA_CLOAKED`），要两者一起查。
   - 拖拽中**不要发 Esc**：没有菜单吃掉它时，Esc 会直接取消 OLE 拖拽。
8. **「关闭拖拽时显示图标」不是一个真实存在的设置。** 枚举 22621 的 `Explorer\Advanced\Folder` 得到 27 个文件夹选项，
   `Explorer\VisualEffects` 得到 19 个视觉效果，没有一项控制拖拽图像。最接近的四项（`SPI_SETDRAGFULLWINDOWS`、
   `SPI_SETUIEFFECTS`、`SPI_SETDROPSHADOW`、`SPI_SETCURSORSHADOW`）全部关掉后 `SysDragImage` 照样出现。
   视觉效果里需要注销才生效的项（`ListviewShadow`、`ThumbnailsOrIcon` 等）没有测。
9. **Win10 21H2 上放行 Win 键的后果和 Win11 22H2 不同。** 真机 `--winkey-pass`：前台变成标题为「搜索」的窗口，但投放 11 ms 后仍到达轮盘；22H2 上则是开始菜单挡住投放。另外 spike 的 `StartMenuVisible()` 只找标题为 Start/开始 的 `CoreWindow`，在 Win10 上对这个「搜索」窗口返回 False——所以 `--winkey` 在 Win10 上的 PASS 依据是 `foreground="DropWheelSpike"` 和 `swallowed=2`，不是那个检测函数。结论不变（M0 直接吞键），但 Win10 下如果要检测开始菜单/搜索是否弹出，要把搜索窗口也算上。
10. **关视觉效果的 7 次全关运行里有 1 次失败，没有复现，原因不明。** 失败的那次（`uieffects-test.ps1`）：LL 鼠标侦测到拖拽、Shift 轻点到达、轮盘 46 ms 画出，但 `SysDragImage` 没出现、左键抬起后 3 s 内没有 `DragDrop`——像是 Explorer 根本没开始 OLE 拖拽。它是唯一一次在运行前刚执行过「杀掉无窗口 explorer.exe」步骤的运行；随后 `uieffects-matrix.ps1`（不杀 explorer）全关 2 次与单项各 1 次都 PASS。当时推断是那一步杀掉了正要为新窗口服务的 explorer 进程，但之后又用 `uieffects-test.ps1` 连跑 4 次，其中 3 次运行前确实杀掉了一个无窗口 explorer，4 次全 PASS（`SysDragImage` +50…+53 ms），所以这个推断不成立，只能记为偶发。产品侧的提醒只有一条：拖拽侦测不能只等 `SysDragImage`，LL 鼠标越过阈值要作为独立来源（本 spike 已如此）。

## 还需要人工做的事

自动化能做的都已在 VM 和 x64 真机上做完。剩下的需要真人或现在没有的硬件，暂缓，见 TODO。

## TODO（暂缓）

1. **真人操作。** 在桌面上运行 `DragSpike.exe`（手动模式），从资源管理器拖一个文件，拖拽中轻点 Shift，确认轮盘出现在光标处且拖拽图标未消失，投放到某个扇区后日志出现 `DROPPED`。
   再用真人键盘在拖拽中按一下 **Win 键**：自测用的是 SendInput；手动模式下钩子不吞键，只记录 `LL key: Win down`，用来确认真实键盘的行为与 `--winkey-pass` 一致。
2. **多显示器 / 不同缩放。** 接上第二块显示器（Parallels 可加虚拟显示器），两块屏设成不同缩放，分别在两块屏上做第 1 项，确认轮盘位置与扇区命中（§7 #3）。目前只测过单屏 200%（VM）和单屏 125%（真机）。
3. **Windows 11 24H2。** 重跑 `--selftest --explorer`、`uieffects-test.ps1` 和 `uieffects-matrix.ps1`（§7 #2）。Win11 22H2 ARM64 和 Win10 21H2 x64 已跑，不必重复。

§7 #5 的亚克力结论见 [`AcrylicSpike/README.md`](../AcrylicSpike/README.md)。
