# DropWheel.Engines

每个子目录一个引擎适配器，实现 `DropWheel.Core.Conversion.IConverter`。

| 目录 | 引擎 | 里程碑 | 形态 |
|---|---|---|---|
| `Image/` | Magick.NET | M0 | 进程内 |
| `Media/` | FFmpeg (LGPL 构建) · `h264_mf` | M1 | sidecar |
| `Pdf/` | PDFium (PDFtoImage) + PdfPig + SkiaSharp PDF 后端；DOCX 手写 OOXML | M1 | 进程内 |
| `Archive/` | SharpCompress | M1 | 进程内 |
| `Subtitle/` | 自写解析器 | M1 | 进程内 |
| `Tools/` | 复用上面的 Magick.NET、SharpCompress、SkiaSharp；去元数据按 JPEG / PNG / WebP 的段结构直接删，不重新编码 | M1 | 进程内 |
| `Document/` | Open XML SDK · LibreOffice headless（可选模块） | M2 | 进程内 + sidecar |
| `Ocr/` | Windows.Media.Ocr → TextRecognizer → RapidOCR | M2 | 进程内 |

`Tools/` 里的是工具轮盘上的操作，实现 `DropWheel.Core.Wheel.IWheelOperation`，不是转换。图片工具输出原格式，文件名加后缀（`-压缩`、`-320x200`）；合成 PDF 输出 `第一张-合并.pdf`，解压输出同名文件夹。都不套用设置里的命名模板。`EngineTools.Create` 按轮盘上的顺序列出全部工具。

规则：
- `Check()` 必须便宜、同步；不可用时给出用户可读原因并设置 `Cause`。
- `Estimate()` 只对复杂转换返回；原因文案要能直接显示在轮盘下方。
- sidecar 二进制放 `tools/sidecars/<name>/<arch>/`（不入库），由 `scripts/fetch-sidecars.ps1` / `.sh` 按 `scripts/sidecars.lock` 下载；App 项目把它复制到输出的 `sidecars/<name>/`。
- 音视频测试调用 PATH 上的 ffmpeg 和 ffprobe（Mac 上 `brew install ffmpeg`，Windows 上把 `tools/sidecars/ffmpeg/<arch>` 加进 PATH）。
