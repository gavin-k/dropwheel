# DropWheel.Engines

每个子目录一个引擎适配器，实现 `DropWheel.Core.Conversion.IConverter`。

| 目录 | 引擎 | 里程碑 | 形态 |
|---|---|---|---|
| `Image/` | Magick.NET | M0 | 进程内 |
| `Media/` | FFmpeg (LGPL 构建) · `h264_mf` / `hevc_mf` | M1 | sidecar |
| `Pdf/` | PDFium (PDFtoImage) + PdfPig + PDFsharp | M1 | 进程内 |
| `Archive/` | SharpCompress · 7za sidecar | M1 | 进程内 + sidecar |
| `Subtitle/` | 自写解析器 | M1 | 进程内 |
| `Document/` | Open XML SDK · LibreOffice headless（可选模块） | M2 | 进程内 + sidecar |
| `Ocr/` | Windows.Media.Ocr → TextRecognizer → RapidOCR | M2 | 进程内 |

规则：
- `Check()` 必须便宜、同步；不可用时给出用户可读原因并设置 `Cause`。
- `Estimate()` 只对复杂转换返回；原因文案要能直接显示在轮盘下方。
- sidecar 二进制放 `tools/sidecars/<name>/<arch>/`（不入库），由 `installer/` 打包。
