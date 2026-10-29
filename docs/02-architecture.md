# 02 · 架构

## 进程与模块

```
┌─────────────────────────────────────────────────────────────────┐
│ DropWheel.App (WinUI 3, 单实例, 常驻托盘)                         │
│  ├─ OverlayWindow     轮盘窗（DWM 透明 + 亚克力），不接收输入       │
│  ├─ QueueWindow       任务队列 / 详情                              │
│  ├─ SettingsWindow    键位 · 输出 · 命名 · 重名 · 动作 · 我的工具 · 隐私 │
│  ├─ OnboardingWindow  20 秒引导                                    │
│  └─ DockWindow        屏幕边缘停靠栏，WinUI 拖动源拖出（CanDrag）     │
├─────────────────────────────────────────────────────────────────┤
│ DropWheel.Shell (Win32 互操作)                                    │
│  ├─ DragMonitor       唯一的钩子线程：WH_MOUSE_LL + WH_KEYBOARD_LL │
│  │                    + SysDragImage 轮询，按键交给 Core.Input     │
│  ├─ DropTargetHost    全屏 alpha=1 分层窗 · RegisterDragDrop       │
│  ├─ GlobalHotkey      RegisterHotKey                               │
│  └─ ShellFileOperations IFileOperation（移动/回收站）              │
├─────────────────────────────────────────────────────────────────┤
│ DropWheel.Core (纯 .NET, 可在任意平台测试)                          │
│  ├─ Conversion/   ConversionGraph · FormatRegistry · Availability  │
│  ├─ Wheel/        WheelModel (6 槽: 3 最近 + 3 推荐) · Ranking     │
│  │                WheelLayout / LayoutStore（layout.json，分组外环） │
│  ├─ Tools/        ToolManifest · UserToolCatalog · ScriptTool      │
│  │                CommandResolver · ToolCommandLine（--validate-tool）│
│  ├─ Jobs/         JobQueue · Job · UndoLedger · NamingTemplate     │
│  ├─ Input/        ModifierTapper（轻点 / 按住判定）· WinKeyGate     │
│  ├─ Quality/      QualityEstimator (高/中/低 + 原因)                │
│  └─ Settings/     KeyBinding · ConflictChecker · Policies          │
├─────────────────────────────────────────────────────────────────┤
│ DropWheel.Engines (IConverter 实现)                               │
│  Image(Magick.NET) · Media(ffmpeg sidecar) · Pdf(PDFium+PdfPig)   │
│  Document(OpenXML / soffice sidecar) · Archive(SharpCompress)     │
│  Subtitle(自写) · Ocr(Windows.Media.Ocr / TextRecognizer)          │
└─────────────────────────────────────────────────────────────────┘
          │ COM (IExplorerCommand → 启动参数 / 命名管道)
┌─────────────────────────────────────────────────────────────────┐
│ DropWheel.ContextMenu.dll (C++, 随 Explorer 进程加载, 必须极轻)     │
└─────────────────────────────────────────────────────────────────┘
```

## 核心流程：拖拽 → 轮盘 → 入队

```
Idle ──(鼠标按下+移动>8px)──▶ MaybeDragging ──(轮询见 SysDragImage)──▶ Dragging
MaybeDragging | Dragging ──(修饰键轻点 ≤300ms)──▶ WheelOpen(mode)   // WinForms 等不产生 SysDragImage 的拖拽停在 MaybeDragging
WheelOpen ──(停在分组扇区 200 ms)──▶ 外环展开 ──(移到别的顶层扇区 150 ms | 回到圆心)──▶ 外环收起
WheelOpen ──(松手在扇区)──▶ IDropTarget::Drop → Enqueue(job) → Idle
WheelOpen ──(松手在中心/外部 | Esc)──▶ DROPEFFECT_NONE → Idle
Dragging ──(松手, 未轻点)──▶ Idle   // 覆盖层从未显示, Shell 原生语义
Dragging ──(推到屏幕左右外缘)──▶ DockWindow 显示 ──(松手在停靠栏)──▶ 记下路径 → Idle   // 没放下就隐藏；拖出用 WinUI 的拖动，不经 Shell 的 DoDragDrop
```

## 领域模型（Core）

- `FileSignature`：扩展名 + 魔数嗅探 → `MediaKind`（Image/Video/Audio/Pdf/Document/Slides/Sheet/Subtitle/Archive）。
- `ConversionGraph`：`(sourceFormat, targetFormat) → IConverter + Availability`。`Availability` 为 `Available | Unavailable(reason)`，原因是用户可读文案（例：「还不能写出 HEIC，体积差不多的可以选 AVIF」）。
- `WheelModel.Build(files, mode)`：6 个槽位 = 3 个最近使用（按 MediaKind 分桶的 LRU）+ 3 个智能推荐（规则：体积、来源目录、同类文件历史）；不可用项保留并灰显。
- `Job`：`Pending → Running(progress) → Done | Failed(reason) | Canceled | Undone`。每个 Job 记录 `UndoPlan`（删除输出到回收站 / 把移动的文件移回）。
- `NamingTemplate`：`{name} {fmt} {ext} {date} {time} {n} {w}x{h}`；`ConflictPolicy = AutoNumber | Overwrite(ToRecycleBin) | Skip | Ask`。
- `QualityEstimator.Estimate(source, target) → (Level, reasons[])`，M0 仅覆盖 PDF→DOCX/TXT 与图片有损→无损。
- `QuotaCounter`：本地计数；仅成功输出计次；失败/取消/撤销/文件动作不计次；本地 0 点重置。
- `ToolManifest` / `UserToolCatalog` / `ScriptTool`（`Core.Tools`）：`%LOCALAPPDATA%\DropWheel\tools\<slug>\tool.json` 描述一个用户工具，目录热加载，脚本型工具装上后灰显、用户在设置里启用过的命令哈希才会跑。输出走 `OutputFiles`，撤销 / 重名策略沿用。见 ADR-0005 和 `skills/dropwheel-tool/SKILL.md`。
- `WheelLayout` / `LayoutStore`：`layout.json` 记工具轮盘（按文件类型）和动作轮盘每个扇区放什么；扇区可以是一个分组（≤ 6 项，不嵌套），指针停在分组上 200 ms 时在外环展开。没有布局的轮盘保持注册顺序。

## 引擎接口

```csharp
public interface IConverter
{
    string Id { get; }                         // "magick", "ffmpeg", ...
    IEnumerable<ConversionEdge> Edges { get; } // 支持的 (from, to)
    Availability Check(ConversionRequest r);   // 可用性 + 原因
    QualityEstimate? Estimate(ConversionRequest r);
    Task<ConversionResult> RunAsync(ConversionRequest r, IProgress<double> p, CancellationToken ct);
}
```

sidecar 引擎（ffmpeg / soffice）通过 `SidecarProcess` 统一启动：低于正常优先级，工作目录为系统临时目录，stdout 逐行回调（ffmpeg 用 `-progress pipe:1` 报进度），stderr 只留最后 20 行用于报错。取消时结束整个进程树，`OutputFiles` 在提交前检查取消，并清理临时文件；批量回滚只删除指纹仍匹配的输出。指纹包含时间、文件内容及整个目录树，生成后被替换、修改或加入新文件的输出保留；撤销同样先核对指纹，变化或无法核实时拒绝并提示。

## 隐私与离线

- 没有联网代码：不检查更新，不收集使用数据。许可证计划用离线密钥（M3），更新计划交给 Microsoft Store 和 winget（M2）。
- `NetworkWatch`（`DropWheel.Core.Privacy`）是一个 `EventListener`，订阅运行时自带的 `System.Net.Sockets` 和 `System.Net.NameResolution` 事件，每次建立连接（ConnectStart）和每次解析域名（ResolutionStart）记一次，计的是本进程经 .NET 发出的请求。设置页「隐私与离线」显示次数，每次请求记一行 `network:` 日志。原生代码（PDFium、SkiaSharp、Windows 组件）自己开的套接字看不到；ffmpeg sidecar 是另一个进程，也不在内。
- 日志在 `%LOCALAPPDATA%\DropWheel\logs`，每天一个文件，记着转换过的文件路径。启动时按 `LogRetention` 删掉 7 天前的；设置页能打开日志文件夹，也能全部清除。日志不会离开这台电脑。
- `StrictOfflineMode` 等第一处联网功能（M2 的 LibreOffice 模块下载）出现时一起做：全局 `HttpClient` 工厂返回拒绝一切请求的 handler，下载入口灰显并说明原因。现在没有可阻断的请求，开关只会是摆设。

## 无障碍

- 覆盖层是真正的窗口，轮盘扇区是 `Button`，`AutomationProperties.Name = "{格式}，{最近/推荐}{，不可用：原因}"`。
- 键盘：Esc 取消。方向键环绕选择和 Enter 确认还没有做，另立项。拖动中轮盘不抢焦点，焦点留在拖动来源，所以转换轮盘上的搜索靠键盘钩子：不按 Ctrl / Alt / Win 时吞掉字母、数字和退格，Esc 放行给系统取消拖动。
- 所有状态文字 + 图标；对比度 ≥ 4.5:1。轮盘目前没有任何动效；动效系统和 `UISettings.AnimationsEnabled == false` 时的关闭是 M1.5 ②。
