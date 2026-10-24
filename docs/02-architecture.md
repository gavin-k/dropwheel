# 02 · 架构

## 进程与模块

```
┌─────────────────────────────────────────────────────────────────┐
│ DropWheel.App (WinUI 3, 单实例, 常驻托盘)                         │
│  ├─ OverlayWindow     轮盘窗（DWM 透明 + 亚克力），不接收输入       │
│  ├─ QueueWindow       任务队列 / 详情                              │
│  ├─ SettingsWindow    键位 · 输出 · 命名 · 重名 · 动作 · 隐私        │
│  ├─ OnboardingWindow  20 秒引导                                    │
│  └─ TrayDock          屏幕边缘停靠托盘                              │
├─────────────────────────────────────────────────────────────────┤
│ DropWheel.Shell (Win32 互操作)                                    │
│  ├─ DragMonitor       WH_MOUSE_LL + SysDragImage 轮询             │
│  ├─ ModifierTapper    WH_KEYBOARD_LL：轻点 / 按住 判定               │
│  ├─ DropTargetHost    全屏 alpha=1 分层窗 · RegisterDragDrop       │
│  ├─ GlobalHotkey      RegisterHotKey                               │
│  └─ ShellOps          IFileOperation（移动/回收站）· 打开位置        │
├─────────────────────────────────────────────────────────────────┤
│ DropWheel.Core (纯 .NET, 可在任意平台测试)                          │
│  ├─ Conversion/   ConversionGraph · FormatRegistry · Availability  │
│  ├─ Wheel/        WheelModel (6 槽: 3 最近 + 3 推荐) · Ranking     │
│  ├─ Jobs/         JobQueue · Job · UndoLedger · NamingTemplate     │
│  ├─ Quality/      QualityEstimator (高/中/低 + 原因)                │
│  └─ Settings/     KeyBinding · ConflictChecker · Policies          │
├─────────────────────────────────────────────────────────────────┤
│ DropWheel.Engines (IConverter 实现)                               │
│  Image(Magick.NET) · Media(ffmpeg sidecar) · Pdf(PDFium+PdfPig)   │
│  Document(OpenXML / soffice sidecar) · Archive(SharpCompress/7za) │
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
Dragging ──(修饰键轻点 ≤300ms)──▶ WheelOpen(mode)
WheelOpen ──(修饰键再轻点)──▶ WheelOpen(其他 mode)
WheelOpen ──(松手在扇区)──▶ IDropTarget::Drop → Enqueue(job) → Idle
WheelOpen ──(松手在中心/外部 | Esc)──▶ DROPEFFECT_NONE → Idle
Dragging ──(松手, 未轻点)──▶ Idle   // 覆盖层从未显示, Shell 原生语义
Dragging ──(松手在屏幕边缘停靠区)──▶ TrayDock.Add(files) → Idle
```

## 领域模型（Core）

- `FileSignature`：扩展名 + 魔数嗅探 → `MediaKind`（Image/Video/Audio/Pdf/Document/Slides/Sheet/Subtitle/Archive）。
- `ConversionGraph`：`(sourceFormat, targetFormat) → IConverter + Availability`。`Availability` 为 `Available | Unavailable(reason)`，原因是用户可读文案（例：「HEIC 编码需要系统已安装 HEVC 视频扩展」）。
- `WheelModel.Build(files, mode)`：6 个槽位 = 3 个最近使用（按 MediaKind 分桶的 LRU）+ 3 个智能推荐（规则：体积、来源目录、同类文件历史）；不可用项保留并灰显。
- `Job`：`Pending → Running(progress) → Done | Failed(reason) | Canceled | Undone`。每个 Job 记录 `UndoPlan`（删除输出到回收站 / 把移动的文件移回）。
- `NamingTemplate`：`{name} {fmt} {ext} {date} {time} {n} {w}x{h}`；`ConflictPolicy = AutoNumber | Overwrite(ToRecycleBin) | Skip | Ask`。
- `QualityEstimator.Estimate(source, target) → (Level, reasons[])`，M0 仅覆盖 PDF→DOCX/TXT 与图片有损→无损。
- `QuotaCounter`：本地计数；仅成功输出计次；失败/取消/撤销/文件动作不计次；本地 0 点重置。

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

sidecar 引擎（ffmpeg / 7za / soffice）通过 `SidecarProcess` 统一启动：工作目录为 `%TEMP%\DropWheel\<jobId>`，进度解析 stderr，取消 = 结束进程 + 清理临时目录。

## 隐私与离线

- 默认无网络请求，仅「检查更新」一项且可关。
- `StrictOfflineMode`：全局 `HttpClient` 工厂返回拒绝一切请求的 handler；更新检查、许可证校验停用。
- 网络请求计数显示在设置页与队列页脚，可导出日志。

## 无障碍

- 覆盖层是真正的窗口，轮盘扇区是 `Button`，`AutomationProperties.Name = "{格式}，{最近/推荐}{，不可用：原因}"`。
- 键盘：方向键环绕选择，Enter 确认，Esc 取消，`/` 聚焦搜索；覆盖层打开时焦点进入，关闭后焦点还给来源窗口。
- 所有状态文字 + 图标；对比度 ≥ 4.5:1；`UISettings.AnimationsEnabled == false` 时关闭全部动效。
