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
| SharpCompress | MIT | 进程内 | |
| FFmpeg (LGPL 构建) | LGPL-2.1-or-later | **子进程** | 附带源码地址与构建配置；不含 GPL 组件。ARM64 来源：BtbN/FFmpeg-Builds 的 `winarm64-lgpl` 资产（2026-10-24 核对，master 与 n8.1/n9.0 均有） |
| 7-Zip (7za) | LGPL-2.1 + unRAR 限制 | **子进程** | 仅用于写 7z。`7z2605-extra.7z` 内含 `arm64/7za.exe` 与 `x64/7za.exe`（2026-10-24 核对） |
| LibreOffice (可选模块) | MPL-2.0 | **子进程**，用户按需下载 | 不随安装包分发 |
| RapidOCR 模型 (可选) | Apache-2.0 | 用户按需下载 | |
| Inter 字体（官网） | OFL-1.1 | 网页 | 应用内使用系统 Segoe UI Variable |
