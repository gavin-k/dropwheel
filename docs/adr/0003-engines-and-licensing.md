# ADR-0003 · 转换引擎分层与许可证边界

- 状态：已接受（2026-10-24）

## 决定

- 主程序 **MIT**。
- 进程内只链接宽松许可证库：Magick.NET (Apache-2.0)、PdfPig (Apache-2.0)、PDFium (BSD)、Open XML SDK (MIT)、SharpCompress (MIT)、PDFsharp (MIT)。
- **LGPL / MPL 重型引擎一律 sidecar 子进程**：ffmpeg (LGPL 构建)、7za (LGPL)、soffice (MPL-2.0，可选下载)。随包附带许可证全文与源码地址，用户可替换二进制。
- 不随安装包分发任何 GPL 构建；GPL 全功能 FFmpeg 作为用户自行指定路径的「自定义引擎」。
- H.264 / HEVC 编码使用 Media Foundation（`h264_mf` / `hevc_mf`），专利授权随 Windows 系统。

## 后果

- 安装包体积：核心 ~60 MB + ffmpeg LGPL ~50 MB；LibreOffice 模块 ~300 MB 按需下载。
- 每个 sidecar 需要 x64 与 ARM64 两份二进制。
- 「不可用并说明原因」机制必须覆盖：缺少系统编解码器（HEIC）、缺少可选模块（DOC→DOCX 需 LibreOffice）、许可证限制（RAR 仅输入）。

## 更新（2026-10-25）

- SharpCompress 0.50 已能写 7z（LZMA2，非 solid），压缩包引擎全部在进程内完成，不再分发 7za sidecar。sidecar 只剩 ffmpeg 和可选的 soffice。
- RAR 仍然只读：RAR 格式只有 WinRAR 能创建，SharpCompress 只实现了解压。
