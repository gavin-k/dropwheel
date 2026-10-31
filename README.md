# DropWheel

> 拖一下，松手，转好了。
> A local, offline, drag-to-convert file converter for Windows 11.

[![Build](https://github.com/gavin-k/dropwheel/actions/workflows/build.yml/badge.svg)](https://github.com/gavin-k/dropwheel/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-orange.svg)](LICENSE)
![Status: pre-alpha](https://img.shields.io/badge/status-pre--alpha-lightgrey)

> **状态：pre-alpha，M1 开发中。** 已能在资源管理器里拖拽转换图片、字幕、压缩包、音视频和 PDF；x64 / ARM64 的 MSIX 和 MSI 由 `release.yml` 产出、用 Azure Trusted Signing 签名，发布在 GitHub Releases。设置窗口能改唤出键、输出位置、文件名和重名策略（自动编号、跳过、替换或每次询问），能看到网络请求次数，打开或清除日志；第一次启动有入门指南。界面有简体中文和英文，默认跟随 Windows 的显示语言，也能在设置里指定，重启后生效。工具轮盘（Ctrl+Alt）有压缩图片、缩小、合成 / 合并 / 拆分 PDF、识别文字、压缩视频、提取音频、解压等十二项；动作轮盘（Win）有复制路径、移动到、重命名、压缩等六项。转换轮盘上直接打字，可以搜索全部可转格式；指针停在扇区上，圆心会显示预期质量，灰掉的格式会说明原因。触屏或笔拖动时停住不动，也能唤出转换轮盘（只用模拟输入测过）。拖动时把鼠标推到屏幕左右边缘会弹出停靠栏，文件可以先放着，从几个文件夹收齐了再一起拖走。产品名「DropWheel」为工作代号，随时可能更改。

## 它是什么

拖动任意文件，在拖动过程中**轻点一下修饰键**（默认 `Shift`），屏幕上弹出一个 6 扇区的**转换轮盘**；移到目标格式上松手，文件就在本机转换完成，输出到同一文件夹。

- **本地离线**：读取、转换、写入全部在本机完成，文件不上传，也不联网检查更新；设置页显示这次启动以来的网络请求次数。你自己装的脚本工具是另一个进程，它上不上网 DropWheel 看不见，也不在这个计数里。
- **多种唤出方式**：修饰键轻点 / 资源管理器右键 / 触屏长按 / 拖到屏幕边缘停靠栏 / 全局快捷键。
- **三种轮盘**：`Shift` 转换 · `Ctrl+Alt` 工具（压缩、缩放、OCR、拆分…）· `Win` 动作（复制路径、移动…）。转换轮盘上直接打字，搜索全部格式。
- **一等公民**：任务队列、撤销（输出移入回收站）、命名规则模板、重名策略。
- **松手前看到预期质量**：PDF → DOCX 等复杂转换先告诉你结果大概会怎样、为什么。
- **不抢资源管理器的键**：默认「轻点唤出」，按住 Ctrl / Shift / Alt 松手仍是系统原生的复制 / 移动 / 快捷方式。全部键位可自定义。
- **Fluent**：WinUI 3，Mica / 亚克力，浅色 + 深色，WCAG AA，支持 `prefers-reduced-motion`，所有轮盘操作均有键盘等价操作。

## 用你自己的 AI 造工具

DropWheel 不内置模型。把 [`skills/dropwheel-tool`](skills/dropwheel-tool/SKILL.md) 交给你在用的 agent（Claude Code、Codex、Cursor 都行），说一句「做个去 EXIF 再复制一份的工具」，它会把 `tool.json` 和脚本写进 `%LOCALAPPDATA%\DropWheel\tools\<slug>\`，再用 `DropWheel.exe --validate-tool` 自检；DropWheel 正在运行时会自动加载。新工具先是灰的，在 设置 › 我的工具 里看过命令行、打开开关才会跑；命令一改又会灰掉。工具轮盘和动作轮盘每个扇区放什么都记在 `layout.json` 里，扇区可以是一个分组，指针停上去在外环展开；想换位置可以让 agent 改这个文件。清单格式见 [`docs/tool-manifest.schema.json`](docs/tool-manifest.schema.json)，取舍见 [ADR-0005](docs/adr/0005-user-tools-manifest-and-trust.md)。这部分还没有在 Windows 上实测，见路线图 M1.5。

## 设计稿

落地页（桌面 + 手机）、转换轮盘覆盖层、任务队列、设置页的高保真设计稿见 [`docs/design/`](docs/design/README.md)。

## 技术栈（摘要）

| 层 | 选型 | 说明 |
|---|---|---|
| UI | **C# / WinUI 3** (Windows App SDK 2.x, .NET 10) | 原生 Fluent：Mica / Acrylic / `SystemBackdropElement`；x64 + ARM64 |
| 全局拖拽侦测 | Win32：`SetWindowsHookEx(WH_MOUSE_LL / WH_KEYBOARD_LL)` + UIA 侦测 `SysDragImage` + 透明 `RegisterDragDrop` 覆盖窗 | Windows 没有「全局拖拽事件」API，需要组合方案，见 [ADR-0002](docs/adr/0002-global-drag-detection.md) |
| 资源管理器右键 | `IExplorerCommand`（C++ COM DLL）+ MSIX 清单 / 稀疏包 | Win11 新式右键菜单只接受 `IExplorerCommand` 且要求包标识 |
| 图片 | Magick.NET (ImageMagick) 或 SkiaSharp + libheif/libavif | 许可证友好；HEIC 编码依赖系统 HEVC 扩展时需灰显说明 |
| 音视频 | **FFmpeg LGPL 构建，独立进程（sidecar）** | MIT 主程序 + LGPL 进程边界；H.264 编码走 `h264_mf`（Media Foundation），WebM 编成 AV1 + Opus |
| PDF | PDFium（渲染 / 转图）+ PdfPig（文本提取）+ SkiaSharp（图片→PDF） | PDF → DOCX 用 PdfPig 提取后按段落重建；LibreOffice sidecar 留作以后的可选模块 |
| Office 文档 | 手写 OOXML 读写 + LibreOffice headless（可选下载模块，还没做） | Open XML SDK 只在测试里校验输出；旧版 DOC / PPT / XLS 要等 LibreOffice 模块 |
| OCR | `Windows.Media.Ocr`（系统内置，25 语种）→ Windows AI `TextRecognizer`（Copilot+ PC）→ RapidOCR / Tesseract 回退 | 全部离线 |
| 压缩包 | SharpCompress（读 ZIP / 7z / TAR / TAR.GZ / RAR，写 ZIP / 7z / TAR / TAR.GZ） | RAR 仅输入 |
| 打包分发 | MSIX（Store + winget）· WiX v5 MSI（企业直装，稀疏包未做） | `winapp` CLI 产出 x64 / ARM64 msixbundle，WiX 产出各架构的 .msi |

完整调研与替代方案对比见 [`docs/01-tech-stack.md`](docs/01-tech-stack.md)。

## 目前能转换

| 类别 | 输入 | 输出 |
|---|---|---|
| 图片 | JPG、PNG、WebP、AVIF、GIF、BMP、TIFF，另可读 HEIC、PSD（取合成后的图像）、ICO（取最大的一张）、SVG（按标明的尺寸画成位图，引用了外部文件的不转） | JPG、PNG、WebP、AVIF、GIF、BMP、TIFF（转 JPG、BMP 时透明部分填白），另有 ICO（最大 256×256，不是正方形时补透明边）和 PDF（一张图一页，多页 TIFF 每页一页）。HEIC 还不能输出，轮盘上灰显 |
| PDF | PDF | DOCX（按段落重建文字和图片，保留字号、粗斜体、颜色）、PNG、JPG（150 DPI，每页一张，多页放进同名文件夹）、TXT（只取文字层） |
| Word 文档 | DOCX | TXT（只留文字，表格的单元格用 Tab 隔开）、Markdown 和 HTML（保留标题、列表、粗体、斜体、链接和表格）。修订按全部接受，图片、页眉页脚和脚注不保留 |
| 演示文稿 | PPTX | TXT（只留文字）、Markdown 和 HTML（每张幻灯片的标题成一级标题，保留项目符号、编号、粗体、斜体、链接和表格，SmartArt 只取文字，备注放在每张幻灯片最后）。隐藏的幻灯片、图片、图表、动画和版式不保留 |
| 纯文本 | TXT（UTF-8、GBK，或带 BOM 的 UTF-16） | DOCX（每行一段） |
| 表格 | XLSX（只取看得见的工作表里的值）、CSV（逗号或分号分隔，认 Excel 的 `sep=` 行）、TSV；CSV 和 TSV 认 UTF-8、GBK 和带 BOM 的 UTF-16 | CSV、TSV（XLSX 每个工作表一个文件，有几个就放进同名文件夹；公式取上次算好的结果，日期写成 2026-10-25，小数位数照格式）、XLSX（一个工作表；数字写成数字，小数位数照原样显示，以 0 开头的编号、超过 15 位的数字和日期按文字保留） |
| 视频 | MP4、MOV、MKV、WebM、AVI、RMVB | MP4、MOV、MKV、WebM、AVI、GIF，以及下面的音频格式 |
| 音频 | MP3、AAC、M4A、WAV、FLAC、OGG、Opus、AIFF、AMR | MP3、AAC、M4A、WAV、FLAC、OGG、Opus、AIFF |
| 字幕 | SRT、VTT、ASS、SSA、LRC | 同左 |
| 压缩包 | ZIP、7z、TAR、TAR.GZ、RAR | ZIP、7z、TAR、TAR.GZ |

## 仓库结构

```
dropwheel/
├─ docs/                  调研、架构、ADR、设计稿索引
├─ src/
│  ├─ DropWheel.App/         WinUI 3 主程序（轮盘覆盖层、队列、设置、引导）
│  ├─ DropWheel.Core/        领域模型：转换图、任务队列、命名规则、键位、质量预估
│  ├─ DropWheel.Shell/       Win32 集成：钩子、拖拽侦测、OLE 放置目标、托盘、全局快捷键
│  ├─ DropWheel.Engines/     各类转换引擎适配器（图片 / 媒体 / PDF / 文档 / 压缩 / 字幕 / OCR）
│  └─ DropWheel.ContextMenu/ IExplorerCommand COM DLL（C++）
├─ skills/dropwheel-tool/  给 agent 的 skill：清单字段、脚本协议、两个样例工具
├─ tests/
├─ installer/             MSIX 清单、winget 清单模板、WiX MSI
└─ .github/workflows/     CI
```

## 开发环境

日常在 macOS 上写 Core 与测试，WinUI 部分在 Windows（Parallels 虚拟机或物理机）里构建与打包。详细说明见 [`scripts/dev-mac.md`](scripts/dev-mac.md)。

### macOS

- .NET 10 SDK（`~/.dotnet`，`dotnet-install.sh --channel 10.0`），`.zshrc` 里加入 PATH 与 `DOTNET_CLI_TELEMETRY_OPTOUT=1`
- `./build.sh` 构建并测试 Core 与 Engines；Shell / App 依赖 Windows SDK
- 音视频测试要 PATH 上有 ffmpeg 和 ffprobe：`brew install ffmpeg`

```bash
git clone https://github.com/gavin-k/dropwheel.git
cd dropwheel
./build.sh            # restore + build + test（Core）
```

### Windows

- Windows 11 (22H2+)，开发者模式
- .NET 10 SDK、`winapp` CLI；可选 Visual Studio 2022 17.14+（「WinUI 应用程序开发」+「.NET 桌面开发」工作负载）
- 一键安装以上全部（幂等，可重复执行；需管理员 PowerShell）：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\setup-windows.ps1       # 加 -SkipVisualStudio 只装 CLI 工具链
.\scripts\build-windows.ps1                                                # 下载 ffmpeg + build + test + 未签名 x64 .msix -> .\artifacts
.\scripts\build-windows.ps1 -Arch arm64
```

不经过脚本直接 `dotnet build` App 时，先运行一次 `.\scripts\fetch-sidecars.ps1`，否则输出里没有 ffmpeg，音视频目标会灰显。

`build.sh` 在 Windows 的 Git Bash 下会改为构建整个 `DropWheel.slnx`（该分支尚未在 Windows 上实测，Windows 日常请用上面的 PowerShell 脚本）。CI 见 `.github/workflows/build.yml`（每次 push / PR）与 `release.yml`（`v*` 标签或手动触发，产出签名的 x64 + ARM64 `.msixbundle`、单架构 `.msix` 和各架构一个 `.msi`，推标签时发成 GitHub Release；签名和 MSI 的做法见 `installer/README.md`）。

## 路线图

见 [`docs/04-roadmap.md`](docs/04-roadmap.md)。M0 目标：**一张图片拖进来、轻点 Shift、选 PNG、松手，队列里能撤销。**

## 许可证

[MIT](LICENSE) © 2026 Gavin Lee。第三方组件许可证见 [`docs/05-third-party-licenses.md`](docs/05-third-party-licenses.md)。
