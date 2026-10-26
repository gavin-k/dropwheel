# DropWheel.Engines

每个子目录一个引擎适配器，实现 `DropWheel.Core.Conversion.IConverter`。

| 目录 | 引擎 | 里程碑 | 形态 |
|---|---|---|---|
| `Image/` | Magick.NET | M0 | 进程内 |
| `Media/` | FFmpeg (LGPL 构建) · `h264_mf` | M1 | sidecar |
| `Pdf/` | PDFium (PDFtoImage) + PdfPig + SkiaSharp PDF 后端；DOCX 手写 OOXML | M1 | 进程内 |
| `Archive/` | SharpCompress | M1 | 进程内 |
| `Subtitle/` | 自写解析器 | M1 | 进程内 |
| `Tools/` | 复用上面的 Magick.NET、SharpCompress、SkiaSharp、PdfPig、FFmpeg；去元数据按 JPEG / PNG / WebP 的段结构直接删，不重新编码 | M1 | 进程内 + sidecar |
| `Document/` | 手写 OOXML：DOCX 读成标题、列表、段落和表格，写成 TXT、Markdown、HTML；TXT 写成 DOCX。Open XML SDK 只在测试里用。LibreOffice headless（可选模块）还没做 | M1 · LibreOffice 在 M2 | 进程内 + sidecar |
| `Sheet/` | 手写 OOXML：XLSX 读出每个看得见的工作表的值，写成 CSV、TSV；CSV、TSV 写成只有一个工作表的 XLSX，也互相转换。Open XML SDK 只在测试里用 | M1 | 进程内 |
| `Ocr/` | 只有 `ITextRecognizer` 接口；Windows.Media.Ocr 的实现是 App 里的 `WindowsTextRecognizer`。TextRecognizer、RapidOCR 还没做 | M2 | 进程内 |

`Tools/` 里的是工具轮盘上的操作，实现 `DropWheel.Core.Wheel.IWheelOperation`，不是转换。图片工具输出原格式，文件名加后缀（`-压缩`、`-320x200`）；合成 PDF 和合并 PDF 输出 `第一个-合并.pdf`；拆分 PDF 输出同名文件夹，里面是 `名字-01.pdf` 这样的单页 PDF；压缩视频输出 `-压缩.mp4`，去掉声音输出原容器加 `-无声`，提取音频按音轨的编码选容器（AAC 是 `.m4a`，MP3 是 `.mp3`），解压输出同名文件夹，识别文字输出同名 `.txt`（UTF-8 带 BOM，CRLF 换行，PDF 每页按 300 DPI 渲染再识别，超过识别器的最大边长就缩小，页之间空一行）。视频工具共用 `MediaTool`：先用 ffprobe 读信息，再让 `FfmpegPlan` 出参数，没有可做的（没有音轨、压完没变小）记为跳过。都不套用设置里的命名模板。`EngineTools.Create` 按轮盘上的顺序列出全部工具。

`Document/` 和 `Pdf/` 的 DOCX 共用 `DocxPackage` 里的部件写法。DOCX 转出的 TXT 和其他 TXT 输出一样是带 BOM 的 UTF-8、CRLF 换行；Markdown 和 HTML 是不带 BOM 的 UTF-8、LF 换行。读 TXT 时按 `TextFiles.Decode` 判断编码（BOM，否则 UTF-8，否则 GB18030），字幕也用它。加密的 DOCX、旧版 DOC、Strict Open XML 和损坏的文件都报错说明原因，不猜。`Document/` 和 `Sheet/` 都经 `OoxmlPackage` 打开文件：它先认出加密的或改了扩展名的旧版文件，读 XML 时不处理 DTD。

`Sheet/` 写的 CSV、TSV 是带 BOM 的 UTF-8、CRLF 换行，含分隔符、引号或换行的字段按 RFC 4180 加引号，TSV 也一样。读 CSV、TSV 也用 `TextFiles.Decode`；CSV 第一行分号比逗号多就按分号分，第一行是 `sep=;` 这样的就按它说的分。XLSX 按单元格的数字格式认日期和时间（内置的日期格式，和含 y、d、h、s 的自定义格式），写成 `2026-10-25`、`14:30:00`，1900 和 1904 两种日期系统都认；其他数字按 15 位有效数字写；`0.00`、`#,##0.00` 这类定了小数位数的格式照样补 0，但不为此四舍五入；百分比、货币符号和千位分隔符不套。写 XLSX 时小数配上同样位数的格式，`12.50` 在 Excel 里仍显示 12.50。整张工作表先读进内存再写出。

规则：
- `Check()` 必须便宜、同步；不可用时给出用户可读原因并设置 `Cause`。
- `Estimate()` 只对复杂转换返回；原因文案要能直接显示在轮盘下方。
- sidecar 二进制放 `tools/sidecars/<name>/<arch>/`（不入库），由 `scripts/fetch-sidecars.ps1` / `.sh` 按 `scripts/sidecars.lock` 下载；App 项目把它复制到输出的 `sidecars/<name>/`。
- 音视频测试调用 PATH 上的 ffmpeg 和 ffprobe（Mac 上 `brew install ffmpeg`，Windows 上把 `tools/sidecars/ffmpeg/<arch>` 加进 PATH）。
