# 05 · 第三方组件许可证

> 随安装包分发的组件在此登记。新增依赖时同步更新。

| 组件 | 许可证 | 链接方式 | 备注 |
|---|---|---|---|
| Windows App SDK / WinUI 3 | MIT (SDK) | NuGet | 运行时随 MSIX 框架包 |
| Magick.NET / ImageMagick | Apache-2.0 / ImageMagick License | 进程内 | 含 libheif、libavif、libraw 等子依赖，见其 NOTICE |
| PDFium (bblanchon 二进制) | BSD-3-Clause | 进程内 | |
| PdfPig | Apache-2.0 | 进程内 | |
| PDFsharp | MIT | 进程内 | |
| Open XML SDK | MIT | 进程内 | |
| SharpCompress 0.50.5 | MIT | 进程内 | 读 ZIP / 7z / TAR / TAR.GZ / RAR，写 ZIP / 7z / TAR / TAR.GZ。包内另含 Bouncy Castle 代码的 MIT 声明（见包内 README 的 LICENSE 段） |
| FFmpeg n8.1.2-50-g1a748fe2cd（BtbN LGPL shared 构建） | LGPL-3.0-or-later（构建带 `--enable-version3`） | **子进程** | 来源与 SHA-256 钉在 `scripts/sidecars.lock`（`autobuild-2026-08-31-13-27` 的 `win64-lgpl-shared-8.1` 与 `winarm64-lgpl-shared-8.1`）。随包附 `LICENSE.txt` 和 `SOURCE.txt`（下载地址、SHA-256、FFmpeg 提交）。不含 GPL 组件（`--disable-libx264 --disable-libx265 --disable-libxvid`）；avcodec 里静态链入了 libopenh264、opencore-amr 等外部库，其中 H.264 / HEVC / AAC 的专利授权尚未评估。解包后 x64 137 MB、arm64 79 MB。没用 2026-09-30 的构建：它的 winarm64 版在 Win11 ARM64 上一启动就崩（0xC0000005） |
| LibreOffice (可选模块) | MPL-2.0 | **子进程**，用户按需下载 | 不随安装包分发 |
| RapidOCR 模型 (可选) | Apache-2.0 | 用户按需下载 | |
| Inter 字体（官网） | OFL-1.1 | 网页 | 应用内使用系统 Segoe UI Variable |
