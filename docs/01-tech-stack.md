# 01 · 技术栈调研与选型

> 调研日期：2026-10-24。版本号以当日公开资料为准，首次 `dotnet restore` 前请再核对 NuGet。

## 0. 结论速览

| 决策 | 选择 | 一句话理由 |
|---|---|---|
| UI 框架 | **WinUI 3 / Windows App SDK 2.x** | 唯一能原生拿到 Mica / Acrylic / Win11 控件与动效的框架；2.0 起 `SystemBackdropElement` 可把亚克力用在任意元素上，正好做轮盘 |
| 语言 / 运行时 | **C# / .NET 10 (LTS)** | 生态最全；OCR / 文件系统 / 包标识 API 都有托管投影 |
| 应用形态 | **MSIX 打包为主**，`外部位置打包`（稀疏包）给 MSI 直装 | Win11 新式右键菜单、通知、Store、winget 都要求包标识 |
| 转换引擎 | 进程内托管库优先，重型引擎走 **sidecar 子进程** | 隔离崩溃、隔离许可证（LGPL/GPL）、可按需下载 |
| 架构 | 核心库与 UI 分离，引擎用插件接口 | 让 Core 在 Linux CI 上也能跑单测 |

## 1. 平台与框架

### 1.1 Windows App SDK / WinUI 3

- Stable 频道最新为 **2.5.1（2026-09-16）**；2.0 为当前主版本（2026-04-29 发布，服务期至 2027-04-29），1.8 已进入维护期。Windows App SDK 向下兼容到 Windows 10 1809，但只在仍受支持的 Windows 版本上获得支持。
- 2.0 之后 WinUI 以独立包 `Microsoft.WindowsAppSDK.WinUI` 发布（NuGet 可见 2.1.x–2.3.x），元包 `Microsoft.WindowsAppSDK` 仍可整体引用。
- 2.0 新增与本项目直接相关的能力：
  - **`SystemBackdropElement`**：Mica / Acrylic 作为 XAML 元素放进视觉树，窗口背景可透明——轮盘覆盖层正好需要「透明全屏窗口 + 局部亚克力圆环」。
  - `SplitMenuFlyoutItem`、`IXamlCondition`、新的页面过渡。
- Windows AI API（`Microsoft.Windows.AI.Imaging.TextRecognizer`）随 Windows App SDK 提供，仅在有 NPU 的 Copilot+ PC 上可用，作为 OCR 的增强路径。

### 1.2 .NET

- 目标框架：`net10.0-windows10.0.26100.0`（官方 winapp CLI 文档使用的 TFM）。
- .NET 11 已到 RC2（2026-10），不作为初始目标。
- `DropWheel.Core` 多目标 `net10.0`，保证非 Windows 环境可构建与测试。

### 1.3 备选方案（已否决）

| 方案 | 否决原因 |
|---|---|
| Rust + Tauri 2 | Fluent 控件、Mica、无障碍树全部要自己搭；WebView2 覆盖层点击穿透与高 DPI 边界问题多 |
| WPF | 没有原生 Mica / Acrylic；Win11 风格要靠 WPF-UI 等三方库模拟 |
| C++ / WinUI 3 | 开发速度慢，托管生态（OCR、OpenXML、SharpCompress）用不上 |
| Electron | 体积、内存、离线承诺与「不上传」的信任感相悖 |

## 2. 核心难点：全局拖拽侦测与轮盘唤出

Windows **没有**「全局拖拽开始」事件。Microsoft Q&A 上的讨论给出三条线，全部需要组合使用：

| 手段 | 作用 | 注意 |
|---|---|---|
| `SetWindowsHookEx(WH_MOUSE_LL)` | 侦测「左键按下后移动超过阈值」= 可能在拖 | 只能知道鼠标状态，不知道是否 OLE 拖拽 |
| `SetWindowsHookEx(WH_KEYBOARD_LL)` | 侦测修饰键的「轻点」（按下→300 ms 内松开） | 低级钩子回调必须极快，否则系统会摘除钩子 |
| UI Automation 结构变化事件 → 根下出现类名 `SysDragImage` 的窗口 | 确认 Shell 拖拽正在进行 | 依赖未文档化的类名；每次事件要遍历桌面子窗口，需节流 |
| WinEvents（`EVENT_SYSTEM_DRAGDROPSTART`） | 文档列出但实测常不触发 | 作为辅助信号，不可依赖 |
| 透明、分层、全屏的放置目标窗口 `RegisterDragDrop` | **接收松手的那一刻与文件列表**（`CF_HDROP` / `IShellItemArray`） | 隐藏窗口收不到拖拽事件，须在轮盘出现时才显示；点击穿透用 `WS_EX_TRANSPARENT`，显示轮盘后关掉 |

设计落点（详见 [ADR-0002](adr/0002-global-drag-detection.md)）：

1. 鼠标钩子判定「正在拖」→ 启动 UIA / WinEvent 确认 → 进入 *Dragging* 状态。
2. *Dragging* 中键盘钩子收到修饰键轻点 → 显示覆盖层（放置目标窗口 + 轮盘 UI）。
3. 松手落在覆盖层：`IDropTarget::Drop` 拿到文件 → 入队。松手落在轮盘外或中心：覆盖层立刻隐藏并把 `DROPEFFECT_NONE` 返回，Shell 视为取消。
4. 没有轻点就松手：覆盖层从未显示，Shell 的 Ctrl/Shift/Alt 语义完全不受影响。

**键位冲突**：资源管理器的 Ctrl（复制）/ Shift（移动）/ Alt（快捷方式）只在**松手瞬间按住**时生效，因此「轻点」不冲突；「按住唤出」模式会在设置页标红冲突。`Ctrl+Alt` 在部分键盘布局等于 AltGr，切换布局时提示改键。`Win` 键在拖动期间需要吞掉以阻止开始菜单。

## 3. 资源管理器右键菜单

- Win11 新式右键菜单只接受 **`IExplorerCommand`**，且应用必须有**包标识**；`IContextMenu` 只会出现在「显示更多选项」里。
- 实现：原生 COM DLL（C++，`src/DropWheel.ContextMenu`），清单里注册 `com:SurrogateServer` + `desktop4:FileExplorerContextMenus`（`desktop5:ItemType Type="*"`）。DLL 架构必须与资源管理器一致 → x64 与 ARM64 各编译一份。
- 直装 MSI 用户：通过**稀疏包**（`uap10:AllowExternalContent`）获得包标识，`winapp pack ./sparse/appxmanifest.xml` 产出 identity-only msix，安装器用 `Add-AppxPackage -ExternalLocation` 注册。
- `GetTitle / GetIcon / GetState` 必须极快；真正工作放在 `Invoke` 之后（启动主程序并传文件列表）。

## 4. 转换引擎选型

### 4.1 图片

| 候选 | 许可证 | 评价 |
|---|---|---|
| **Magick.NET**（ImageMagick） | Apache-2.0 | 格式最全（含 HEIC / AVIF / PSD 读、RAW 读），有 ARM64 包；体积较大 |
| SkiaSharp | MIT | 快、小；HEIC/AVIF 需自行接 libheif/libavif |
| SixLabors.ImageSharp | Six Labors Split License | OSS 项目可免费用，但商业买断版本可能触发付费条款 → **不选** |
| Windows Imaging Component（WIC） | 系统 | 作为回退：HEIC 编码依赖系统「HEVC 视频扩展」是否安装 → 不可用时灰显并说明 |

决定：**Magick.NET 为主**，WIC 作系统编解码器探测与回退。

### 4.2 音视频

- **FFmpeg**，以 **sidecar 子进程**调用（`ffmpeg.exe` / `ffprobe.exe`），不在进程内链接 → 崩溃隔离 + 许可证边界清晰。
- 使用 **LGPL 构建**（无 libx264/x265/fdk-aac）。H.264 / HEVC 编码走 Windows **Media Foundation** 编码器（`h264_mf` / `hevc_mf`，硬件加速），AAC 走 `aac`（内置 LGPL）。
- NuGet 有打包好的 LGPL 二进制（如 `DevEnvy.FFmpeg.Binaries.LGPL`），也可自行从 BtbN 构建下载；ARM64 Windows 构建需单独确认。
- 若社区希望 GPL 全功能构建：作为**用户自行下载**的可选引擎，不随安装包分发。

### 4.3 PDF

| 任务 | 库 | 许可证 |
|---|---|---|
| 渲染 / 转图 / 页操作 | **PDFium**（`PDFtoImage` 或 `PDFiumCore` 封装，bblanchon 二进制） | BSD-3 / MIT |
| 文本与版式提取 | **PdfPig** | Apache-2.0 |
| 生成 PDF（图片→PDF、PDF/A） | PDFsharp 6（MIT）或 QuestPDF（社区许可） | — |
| PDF → DOCX | 两条路：① PdfPig 提取 + Open XML SDK 重建（质量「中」）；② **LibreOffice headless** sidecar（质量更稳，但 ~300 MB，作可选下载模块） | — |

「预期质量」规则初版：扫描页占比、是否含表格、是否多栏、字体是否嵌入 → 高 / 中 / 低 + 原因文案。

### 4.4 Office 文档 / 幻灯片 / 表格

- **Open XML SDK**（MIT）：DOCX / PPTX / XLSX 原生读写，TXT / MD / HTML / CSV 互转。
- **LibreOffice headless**（MPL-2.0）：ODT / ODP / ODS、DOC / PPT / XLS（仅输入）、文档→PDF 高保真。作为可选引擎模块，首次使用时下载（或企业离线包预置）。
- EPUB：自写生成器（EPUB 本质是 ZIP + XHTML）。

### 4.5 OCR（全部离线）

| 引擎 | 可用性 | 语言 |
|---|---|---|
| **`Windows.Media.Ocr.OcrEngine`** | 所有 Windows 10/11，零依赖 | 25 种，含简繁中、日、韩；需系统已装对应语言包 |
| `Microsoft.Windows.AI.Imaging.TextRecognizer` | 仅 Copilot+ PC（NPU） | 自动检测，含手写 |
| RapidOCR（ONNX Runtime）/ Tesseract | 任何机器，自带模型 | 补齐 Windows OCR 不支持的语言（如乌克兰语） |

策略：有 NPU 用 TextRecognizer → 否则系统 OcrEngine → 语言不支持时提示下载 RapidOCR 模型包。

### 4.6 压缩包

- **SharpCompress**（MIT）：读 RAR / 7z / ZIP / TAR / GZ，写 ZIP / TAR / GZ / BZ2。
- 写 7z：**7-Zip** sidecar（`7za.exe`，LGPL）。
- RAR 仅输入（解压算法授权限制）。

### 4.7 字幕

- SRT / VTT / ASS / SSA / LRC 文本格式自写解析器（规则简单、便于控制编码与时间轴偏移）。
- SUB/IDX 图形字幕仅输入（需 OCR，后续）。

## 5. 打包与分发

| 渠道 | 形态 | 工具 |
|---|---|---|
| Microsoft Store | 未签名 `.msixbundle`（x64 + ARM64） | `winapp pack ./publish/x64 ./publish/arm64` |
| winget | 指向 GitHub Release 的 MSIX（已签名）或 MSI | `installer/winget/` 清单模板 |
| 企业直装 | WiX v5 生成 `.msi`，内含稀疏包注册与 FFmpeg LGPL 二进制 | `installer/msi/` |

- `winapp` CLI（`winget install Microsoft.winappcli`）覆盖创建、带标识运行、打包、签名、多架构 bundle 全流程，可在 CI 使用。
- 单文件 EXE 仅支持「非打包 + 自包含」，与右键菜单（需包标识）冲突 → 不采用。
- ARM64：WinUI 3 原生支持；需逐一确认 Magick.NET、PDFium、FFmpeg LGPL、7-Zip 的 ARM64 二进制。

## 6. 许可证边界（MIT 主程序）

```
DropWheel.exe (MIT)
 ├─ 进程内：Magick.NET (Apache-2.0) · PdfPig (Apache-2.0) · PDFium (BSD) · Open XML SDK (MIT) · SharpCompress (MIT) · PDFsharp (MIT)
 └─ 子进程：ffmpeg.exe (LGPL-2.1+) · 7za.exe (LGPL) · soffice.exe (MPL-2.0，可选下载)
```

LGPL 组件以独立可执行文件随包分发并附带许可证文本与源码链接，用户可自行替换 → 满足 LGPL 对 MIT 主程序的要求。详见 `05-third-party-licenses.md`。

## 7. 待验证清单（M0 前）

- [ ] 低级鼠标 + 键盘钩子在拖拽期间的延迟与稳定性（Explorer 拖拽时消息泵是否会阻塞钩子回调）
- [ ] `SysDragImage` 侦测在 Win11 24H2+ 是否依旧有效；关闭拖拽相关视觉效果（「拖动时显示窗口内容」、动画、阴影）后的行为
- [ ] 透明分层窗口 `RegisterDragDrop` 在多显示器 / 不同 DPI 下的坐标映射
- [ ] `Win` 键在拖拽中被吞掉后，开始菜单是否仍会弹出
- [x] `SystemBackdropElement` 在透明窗口上的亚克力效果与 150 ms 弹出预算（GPU 开销仍待真机量化）
- [ ] Magick.NET / PDFium / FFmpeg LGPL 的 ARM64 包可用性

> 2026-10-24：用 [`spikes/DragSpike`](../spikes/DragSpike/README.md) 在 Win11 22H2 ARM64 上做了真实 Explorer 拖拽自测（6/6）：
> 第 1 项通过；第 2 项验证了 22H2 上 `SysDragImage` 会出现、关掉四项拖拽相关视觉效果后仍出现，24H2 未测，另外 `EVENT_SYSTEM_DRAGDROPSTART` 在该机上从不触发
> （原文写的「拖拽时显示图标」选项在 22621 的文件夹选项里不存在，已改写）；
> 第 3 项只验证了单屏 200% DPI；第 4 项通过——LL 键盘钩子吞掉 Win 键后开始菜单不弹出、拖拽与投放正常（放行时开始菜单会弹出并挡住投放）；
> 第 6 项由 [`spikes/MagickSpike`](../spikes/MagickSpike/README.md) 验证：Magick.NET 14.9.0 在 ARM64 原生加载并完成 JPG→PNG/WebP/AVIF/JXL（HEIC 只读不写，x64 亦然），
> PDFium 137 与 SkiaSharp 3.119 包内带 `win-arm64`，FFmpeg 有 BtbN 的 `winarm64-lgpl` 构建，7-Zip extra 包含 `arm64/7za.exe`；
> 第 5 项由 [`spikes/AcrylicSpike`](../spikes/AcrylicSpike/README.md)（WinUI 3 非打包、自包含）验证：预先创建的透明置顶不激活窗口 + `SystemBackdropElement` 圆盘，热身后 `Show()`→像素上屏中位 54 ms、最大 75 ms，
> 65 轮前台窗口均未改变，`DesktopAcrylicController` 状态 `Active`（VM 里也真的模糊）；但冷启动第 1 轮 48–219 ms 不稳定，M0 必须启动时预热；不激活窗口须把 `IsInputActive` 钉成 true 否则回退纯色；
> WinUI 3 窗口透明要走 DWM 配方（`DwmExtendFrameIntoClientArea` + `DwmEnableBlurBehindWindow` + `WM_ERASEBKGND` 填黑）而不是 `WS_EX_LAYERED`，与 `DropWheel.Shell/DropTargetHost.cs` 的 TODO 不一致，M0 开工前要定；GPU 开销在 VM 里读不到 dwm 计数，待真机。详见三个 README。

## 参考资料

- [Windows App SDK 发布频道](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)
- [Windows App SDK 2.1.3 讨论](https://github.com/microsoft/WindowsAppSDK/discussions/6494)
- [What's new: SDK, WinUI, tools](https://learn.microsoft.com/en-us/windows/apps/whats-new/whats-new-for-developers)
- [WinUI 3 Gallery 2.9 / Windows App SDK 2.0 新特性](https://visualstudiomagazine.com/articles/2026/05/01/winui-3-gallery-2-9-highlights-windows-app-sdk-2-0-for-app-developers.aspx)
- [Microsoft.WindowsAppSDK.WinUI on NuGet](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/)
- [How to Detect System-Wide Drag & Drop Start (Microsoft Q&A)](https://learn.microsoft.com/en-us/answers/a/2020151)
- [RegisterDragDrop](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-registerdragdrop) · [DoDragDrop](https://learn.microsoft.com/en-us/windows/desktop/api/ole2/nf-ole2-dodragdrop)
- [Add a File Explorer context menu command to a packaged desktop app](https://learn.microsoft.com/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer)
- [Windows app development best practices（Win11 右键菜单要求）](https://learn.microsoft.com/windows/apps/get-started/best-practices#user-experience-ux)
- [Create an unpackaged WinUI app](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)
- [winapp CLI](https://learn.microsoft.com/windows/apps/dev-tools/winapp-cli/) · [Sparse packaging](https://learn.microsoft.com/windows/apps/dev-tools/winapp-cli/guides/sparse) · [Using winapp CLI with WinUI](https://learn.microsoft.com/windows/apps/dev-tools/winapp-cli/guides/winui)
- [Windows AI Text Recognition (OCR)](https://learn.microsoft.com/windows/ai/apis/text-recognition) · [OcrEngine.AvailableRecognizerLanguages](https://learn.microsoft.com/uwp/api/windows.media.ocr.ocrengine.availablerecognizerlanguages)
- [Windows OCR 25 语种列表（Power Automate 文档）](https://learn.microsoft.com/power-automate/desktop-flows/actions-reference/ocr)
- [Windows OCR engine vs Tesseract](https://ironsoftware.com/csharp/ocr/blog/ocr-tools/windows-ocr-engine-vs-tesseract)
- [DevEnvy.FFmpeg.Binaries.LGPL](https://www.nuget.org/packages/DevEnvy.FFmpeg.Binaries.LGPL/) · [FFmpeg.AutoGen.Bindings.DynamicallyLinked](https://www.nuget.org/packages/FFmpeg.AutoGen.Bindings.DynamicallyLinked)
- [FFmpeg MediaFoundation wrapper (h264_mf)](https://ffmpeg.org/pipermail/ffmpeg-devel/2017-April/209557.html) · [Using Windows Media Foundation to encode h264 (Licensing)](https://learn.microsoft.com/en-gb/answers/questions/1060279/using-windows-media-foundation-to-encode-h264-(lic)
- [PDFtoImage](https://www.nuget.org/packages/PDFtoImage/) · [PDFiumCore](https://github.com/Richasy/PDFiumCore) · [Docnet](https://git.tdem.in/GowenGit/docnet)
- [ImageSharp vs SkiaSharp vs Magick.NET 对比](https://www.pistack.xyz/posts/2026-09-09-csharp-image-processing-imagesharp-skiasharp-magicknet-comparison/) · [SixLabors 许可证变更讨论](https://abp.io/support/questions/10663/Dependency---SixLabors-License-Change)
- [.NET 11 RC2 与 2026-10 安全更新](https://www.warp2search.net/story/net-11-rc2-and-october-2026-security-patches-cves-fix-across-net-8-9-and-10/)
