# 04 · 路线图

## M0 · 原型验证（目标：2 周）

**一句话验收**：在资源管理器里拖一张 JPG，轻点 Shift，轮盘弹出，选 PNG，松手，同目录出现 PNG；在队列里点撤销，PNG 进回收站。

- [x] `DropWheel.Shell`：鼠标 / 键盘低级钩子 + `SysDragImage` 侦测 + 透明放置目标窗口（放置目标是纯 Win32 窗口，见 [ADR-0004](adr/0004-drop-target-host.md)）
- [x] `DropWheel.App`：透明覆盖窗口 + `SystemBackdropElement` 亚克力轮盘（6 扇区、中心文件信息、按键提示）
- [x] `DropWheel.Engines.Image`：Magick.NET JPG/PNG/WEBP 互转
- [x] `DropWheel.Core`：JobQueue、UndoLedger（回收站）、NamingTemplate 最小实现
- [ ] 验证 `docs/01-tech-stack.md §7` 待验证清单（第 2 项的 24H2 与第 3 项的多显示器 / 不同 DPI 还没测，手头没有这样的机器）

> 2026-10-24：一句话验收由 `tests/DropWheel.Acceptance` 的 `app --explorer` 模式自动跑通，对象是真实的 `DropWheel.exe` 和真实的资源管理器窗口：
> Win11 22H2 ARM64 VM（200%）上轮盘在钩子看到 Shift 后 22.0 ms 显示、再过 13 ms 出首帧，JPG→PNG 141 ms；
> Win10 21H2 x64 真机（125%）上是 7.7 ms + 6 ms，JPG→PNG 349 ms。两台机器上撤销后 PNG 都进了回收站，轮盘外松手不转换，日志无错误。
> 真机系统「透明效果」没开时圆盘是纯色回退 `#262626`；临时打开后控制器为 `Active`，跑完恢复原状。
> 诊断开关：`DropWheel.exe --wheel-preview [host]` 在主屏中央显示轮盘后退出，用于截图。

### M0 之后的 TODO

- [x] 托盘图标。关掉任务窗口只是隐藏，托盘菜单「退出 DropWheel」或 `DropWheel.exe --quit` 才退出。
- [ ] Tools / Actions 轮盘。M0 只注册了 Convert 的 Shift 绑定，所以拖拽中不吞 Win 键；加上 Actions（Win）后要重新验证吞键。
- [ ] 启动预热时自检白块（[AcrylicSpike 发现 9](../spikes/AcrylicSpike/README.md)）：显示一帧后抓自己的窗口，整块单色就记日志并降级。
- [ ] 混合 DPI 跨屏：轮盘按指针所在显示器的缩放放置，没在两块不同缩放的屏上测过。
- [ ] `Styles/Tokens.xaml` 的轮盘尺寸（直径 320、外径 156、内径 58）与 `WheelGeometry`（外径 146、内径 54）不一致，以 `WheelGeometry` 为准，统一一处。
- [ ] 系统「透明效果」关闭或注册表值缺失时是纯色回退，考虑在设置页提示用户。
- [x] 贴边时轮盘被挪回工作区，中心不再在指针下。指针离边缘不到约 108 DIP 时，一弹出就落在某个扇区里，轻点后直接松手会转换成没选过的格式（验收时在 VM 上见过：指针 2966、中心 2700，200% 下偏 133 DIP）。已改为：弹出时指针不在圆心区，扇区先不响应，圆心提示「先移到圆心」，指针进过圆心区后才开始判定扇区。

## M1 · 可用的 Alpha

- [ ] 三种轮盘 + 搜索全部格式 + 不可用灰显与原因
- [x] 任务队列窗口（进度 / 取消 / 撤销 / 打开位置 / 重试）
- [ ] 设置页：键位与冲突检查、输出位置、命名规则、重名策略、文件动作、隐私与离线
- [x] 首次启动引导（计划 20 秒内读完，没有计时）
- [x] 引擎：Subtitle、Archive、Media、Pdf (PDFium + PdfPig，转图、转文字、转 DOCX、图片转 PDF)
- [x] 预期质量：PDF→DOCX / TXT、有损→无损图片
- [ ] 边缘停靠托盘、触屏长按

进展（每条注明证据）：

- 2026-10-25 设置与最近使用落盘：`%LOCALAPPDATA%\DropWheel\settings.json` 和 `recent.json`，先写临时文件再替换；读不了的文件改名为 `.bad` 并用默认值。转换请求的输出目录、命名模板、重名策略都取自设置。`--data-dir <目录>` 换一个配置目录，验收每次用全新的配置。证据：Mac 上 Core 测试 83 个通过；VM 上 `app --explorer` 验收 PASS。
- 2026-10-25 常驻托盘：通知区域图标（左键打开任务窗口，右键菜单「打开任务窗口 / 退出 DropWheel」，资源管理器重启后重新添加），关闭任务窗口只隐藏。单实例：再启动一次会唤出已运行实例的任务窗口，`--quit` 让它退出。当时还没有首次引导，新用户启动时直接看到任务窗口；现在改为看到入门指南，见下面「首次引导」一条。Win11 把新图标放进溢出区，用户要自己拖到任务栏上。证据：VM 上 `app --explorer` 验收 PASS，新增「关窗后进程仍在且窗口隐藏」「再次启动唤出任务窗口」「`--quit` 结束进程」三项；截图确认图标正常显示（截图前在测试机上把图标提到了任务栏）。
- 2026-10-25 贴边修复：轮盘因贴边被挪开、指针不在圆心区时，扇区要等指针进过圆心区才响应，此前松手一律取消。证据：Mac 上 Core 测试 86 个通过（新增 3 个）；VM 上 `app --explorer` 验收 PASS，新增两项：指针在工作区顶边下 60 DIP 处轻点，轮盘中心被下移 102 DIP，指针正落在可用的 PNG 扇区上，原地松手记为取消且没有新文件；同样位置先移到圆心再到 PNG 扇区，正常转换。
- 2026-10-25 字幕引擎：SRT、VTT、ASS、SSA、LRC 两两互转，进程内自写解析器。读入依次认 BOM、严格 UTF-8、GB18030（覆盖无 BOM 的 GBK 字幕），写出一律带 BOM 的 UTF-8 和 CRLF。只保留时间轴和文字，样式只留斜体、粗体、下划线；从 ASS/SSA 转出、从 LRC 转出、转成 LRC 时，轮盘给出「中」和原因。LRC 没有结束时间，取下一行的开始时间，最后一行显示 5 秒。各引擎共用的输出写法抽到 `OutputFiles`：先写临时文件再不覆盖地移入，一批要么全成要么全不成。证据：Mac 上 Core 测试 92 个、Engines 测试 33 个通过（字幕 17 个，含 GBK 输入、UTF-16 输入、经 ASS 往返、LRC 多时间标签与 offset）；VM 上 `app --explorer` 验收 PASS，新增三项：拖 .srt 时轮盘给出 vtt、ass、lrc、ssa，落到 VTT 扇区后 89 ms 完成，输出是带 BOM 的 WebVTT 且中文和斜体保留。
- 2026-10-25 压缩包引擎：SharpCompress 0.50.5 进程内完成，读 ZIP、7z、TAR、TAR.GZ、RAR，写 ZIP、7z、TAR、TAR.GZ，16 条边。逐条目重新打包，保留路径、内容、修改时间和空目录；符号链接、Unix 权限、注释不保留（源是 tar / tgz 时轮盘给出原因），转成 tar 时提示文件会变大。写 zip 一律设 UTF-8 标志；读到没有标志的旧 zip，文件名先试 UTF-8，不行再按系统 OEM 代码页解码（中文系统即 GBK）。加密的包直接拒绝并说明原因；backup.tar.gz 转出 backup.zip，不是 backup.tar.zip。证据：Mac 上 Core 测试 93 个、Engines 测试 45 个通过（压缩包 12 个，含四种格式往返、GBK 文件名回退、加密拒绝、损坏包让整批回滚）；用 SharpCompress 仓库的样本手动验证 RAR4 solid、RAR5、RAR5 solid 都能转成 zip，加密的 RAR5 给出提示，样本不入库；VM 上 `app --explorer` 验收 PASS，新增一项：拖 .zip 时轮盘给出 7z、tgz、tar，落到 7Z 扇区后 163 ms 完成，输出在源文件旁且以 7z 签名开头。

- 2026-10-25 音视频引擎：ffmpeg sidecar，用 BtbN 的 LGPL shared 构建 n8.1.2-50，版本和 SHA-256 钉在 `scripts/sidecars.lock`，`scripts/fetch-sidecars.ps1` / `.sh` 下载校验，App 构建时复制到输出的 `sidecars/ffmpeg/`。视频 MP4、MOV、MKV、WebM、AVI 互转，另读 RMVB；音频 MP3、AAC、M4A、WAV、FLAC、OGG、Opus、AIFF 互转，另读 AMR；视频还能转 GIF 和各音频格式，共 143 条边。先用 ffprobe 读流信息：编码能放进目标容器就只换容器，否则 H.264 走 `h264_mf`，WebM 编成 AV1 + Opus（arm64 构建没有 libvpx），AVI 用 MPEG-4 + MP3。奇数宽高缩成偶数，多声道转 MP3 / Opus 降为双声道，WAV / FLAC / AIFF 保留 24 位。进度读 `-progress pipe:1`；取消时结束整个进程树，不留临时文件。转 GIF、AVI 时轮盘给出「中」和原因，有损转无损、MKV 转出、转 WebM 附提示。找不到 sidecar 时音视频目标灰显并说明。CI 在 Windows 上用同一份 sidecar 跑测试，release 流程打包后检查每个 MSIX 里 ffmpeg.exe 的架构，手动触发的一次运行里两个包都对得上（x64 MSIX 99.2 MB，arm64 76.7 MB，bundle 175.9 MB）。证据：Mac 上 Core 测试 95 个、Engines 测试 88 个通过（音视频 43 个：31 个测参数生成，12 个调用真实 ffmpeg，含中文文件名、24 位、6 声道、奇数尺寸、损坏文件让整批回滚、中途取消不留文件）；VM（ARM64）与 Win10 x64 真机上 `app --explorer` 验收 PASS（各 36 项），新增一项：拖 .wav 时轮盘给出 mp3、m4a、flac、aac、ogg、opus，落到 MP3 扇区后分别 277 ms、256 ms 完成，输出以 ID3 开头；两台机器上用 sidecar 把 5 秒 720p 测试画面编成 H.264（`h264_mf`），分别 716 ms、609 ms；`fetch-sidecars.ps1` 在 Windows PowerShell 5.1 下首次 11 秒，再跑直接跳过。
- 2026-10-25 PDF 引擎：进程内完成。PDFium 152（PDFtoImage 5.4 封装）渲染页面，PdfPig 0.1.16 取文字，SkiaSharp 4.150.1 的 PDF 后端把图片写成 PDF；没用 PDFsharp，它 6.5 起改成了 AGPL。PDF 转 PNG / JPG：白底，带注释和表单，150 DPI；像素尺寸按页自己算并四舍五入（交给 PDFtoImage 按 DPI 算会截断，240 pt 得 499 px），最长边超过 10000 px 的页单独降低 DPI。一页的 PDF 输出放在它旁边，多页的放进同名文件夹（report/report-01.png 起），文件夹先在临时名下写满再改名，重名时同样自动编号。PDF 转 TXT：逐页取文字，写成带 BOM 的 UTF-8 和 CRLF；没有文字层就报错，提示可能是扫描件。图片转 PDF：JPG、PNG、WebP、AVIF、GIF、BMP、TIFF 都能转，一张图一页，页面大小按图片的 DPI 算，没有 DPI 就按 96；JPG 去掉 EXIF 段后原样嵌入、按 EXIF 方向自己旋转，不重新压缩；多页 TIFF 每页一页，GIF 只取第一帧。有密码的 PDF 直接拒绝并说明。轮盘上图片的推荐里 PDF 排第三，PDF 推荐 PNG、JPG、TXT；转图和图片转 PDF 给「高」加原因，转 TXT 给「中」。release 流程的架构检查加上 pdfium.dll 和 libSkiaSharp.dll。SkiaSharp 原生包自带的 libSkiaSharp.pdb（x64 89 MB）不再复制进输出。x64 发布目录因此多了 25.8 MB（libSkiaSharp 12.3、pdfium 7.2、PdfPig 5.7 MB），zip 压缩后 10.7 MB，MSIX 增量应与此相近（推断，未打包量）。证据：Mac 上 Core 测试 102 个、Engines 测试 108 个通过（PDF 20 个，含多页进文件夹、重名文件夹编号、超大页降 DPI、取整、加密拒绝、损坏 PDF 让整批回滚、JPG 带 EXIF 方向时原样嵌入且方向正确、多页 TIFF）；VM（ARM64）与 Win10 x64 真机上 `app --explorer` 验收 PASS（各 44 项），新增两组：拖 .jpg 时轮盘给出 pdf，落到 PDF 扇区后分别 85 ms、427 ms 完成，输出在源文件旁且以 `%PDF-` 开头；把它复制成另一个名字再拖，轮盘给出 png、jpg、txt，落到 PNG 扇区后分别 105 ms、174 ms 完成，得到 500 × 313 的 PNG。
- 2026-10-25 PDF 转 DOCX：PdfPig 取词，Docstrum 分块，再按阅读顺序排好，每块写成段落；块内换行处字号变化超过 10% 就另起一段，标题因此不会和正文粘在一起。保留字号、粗体、斜体和颜色（黑色和接近白色的不写），跨行时英文补空格，中日韩文字和行尾连字符不补。短而居中的段落居中。每个 PDF 页从新页开始，纸张大小和方向取第一页。图片插在它下方的第一段之前；JPG 原样放入，其他 PdfPig 能解码的转成 PNG，JPEG 2000、JBIG2、CCITT 丢掉；有文字的页上盖住 80% 以上页面的图当作扫描底图，不放入。没有文字层就报错，提示可能是扫描件。DOCX 手写，没用 Open XML SDK（约 8.5 MB），SDK 只在测试里校验输出。分栏、表格、页眉页脚不还原，预期质量给「中」并写明。轮盘上 PDF 的推荐改成 DOCX、PNG、JPG、TXT。证据：Mac 上 Core 测试 102 个、Engines 测试 115 个通过（PDF 27 个，新增 DOCX 的段落与分页、字号粗体颜色、图片位置且 JPG 字节不变、中文换行不补空格和连字符换行，每个都过 Office 2019 架构校验；加密、损坏、无文字三种失败也覆盖 DOCX）；linux/amd64 容器里 PDF 27 个通过。Mac 上 200 页 Letter、1.0 MB、带 67 张图的 PDF 转 DOCX：首次 2.9 到 3.3 秒，同一进程第二次 1.6 秒，输出 2.4 MB，进程内存峰值约 125 MB；macOS 的 textutil 能读出全部 467 段。还没在 Windows 上验收，也没用 Word 打开过。
- 2026-10-25 图片的预期质量补全，并修了一个透明度问题：带透明的 PNG 转 JPG、BMP，透明部分原来会变黑（JPEG 没有透明通道，多数 BMP 阅读器忽略它），现在先铺白底。JPG、HEIC、WebP、AVIF 转 PNG、BMP、TIFF 给「高」，说明无损保存但原有压缩痕迹不会消失、文件会变大；PNG 等转 JPG 给「中」，说明边缘杂点和透明变白；转 BMP 给「高」并说明透明变白；转 GIF 仍是「中」（256 色）；从 GIF 转出一律提示只取第一帧（这里不读是否为动图）。JPG 转 WebP 这类有损到有损不给估计。证据：Mac 上 Engines 测试 126 个通过，新增透明 PNG 转 JPG、BMP 后透明处为白且红色不变、转 WebP 保留透明，以及 8 组估计。
- 2026-10-25 设置窗口：托盘菜单和任务窗口都能打开。可以改唤出转换轮盘的修饰键（Shift、Ctrl、Alt、Win 任意组合）和轻点时限；输出位置（源文件旁边，或统一放到一个文件夹，用系统对话框选）；文件名模板，带实时预览，停止输入 0.6 秒后保存；同名文件已存在时自动编号还是跳过。每项改动立即写入 settings.json，新键位马上交给钩子，不用重启，任务窗口的提示语也跟着换键名。统一输出的文件夹不存在时，转换前先建好。冲突检查判为错误的键位（比如一个键都不选）不保存，窗口里写明原因；按住唤出和 AltGr 的问题只提示。文件动作、隐私与离线、Tools / Actions 轮盘的键位还没进窗口。证据：Mac 上 Core 测试 110 个通过；`app --explorer` 验收在 Win11 ARM64 VM 和 Win10 x64 真机上都是 57 项全过。新增的设置场景：取消唯一勾选的 Shift，窗口说明原因，settings.json 不变；勾选 Ctrl、改为统一文件夹、文件名改成 `{name}-dw` 后逐项写盘；同一次拖拽里轻点 Shift，700 ms 内不出轮盘，再轻点 Ctrl，钩子看到后 11.4 ms（真机 9.2 ms）显示轮盘；转出的 PNG 落进验收前删掉的那个文件夹，名为 `…-dw.png`。同一轮还加了 PDF 转 DOCX 的验收：DOCX 在 PDF 旁边，`word/document.xml` 里有页面上的文字。
- 2026-10-25 登录 Windows 时启动：MSIX 清单声明 StartupTask，默认打开，用户第一次手动启动之后生效。由 Windows 在登录时拉起时只进托盘，不弹任务窗口，日志记一行。设置窗口里有开关；用户在任务管理器或「设置 › 应用 › 启动」里关掉后，开关变灰，并说明只能在那里重新打开；组织策略打开或关闭时同样变灰。没有包标识（开发构建、验收）时这一节不显示。证据：VM 和 Win10 x64 真机上 `app --explorer` 验收 57 项全过，日志无错误（未打包，这一节不显示）。打包后在 ARM64 VM 上用自签名 MSIX 验证：安装后、第一次启动前，Windows 里没有这个启动任务的记录；第一次手动启动后记为已启用。设置窗口里显示这一节，把开关关掉再打开，Windows 记录的状态随之变为已禁用、已启用，日志各记一行。开着时重启，开机后 28 秒由 Windows 拉起，日志记 started by Windows at sign-in，没有弹出任务窗口；关掉后重启，开机后 59 秒仍没有 DropWheel 进程。Win10 x64 真机上没有测打包版。
- 2026-10-25 任务窗口：每行显示状态和进度条，引擎报不出进度时进度条来回滚动。按钮随状态出现：等待或转换中有「取消」；失败或已取消有「重试」，用同样的文件和目标新建一个任务，替换原来那一行；完成后有「打开位置」，在资源管理器里打开文件夹并选中输出，还有「撤销」。每个按钮的无障碍名称带上任务名，例如「重试 a.jpg → PNG」。证据：Mac 上 Core 测试 112 个通过（新增 2 个）；VM 和 Win10 x64 真机上 `app --explorer` 验收 65 项全过，日志无错误。被别的程序独占打开的 JPG 转换失败（51 / 35 ms），解锁后按「重试」完成（25 / 43 ms）；按「打开位置」后资源管理器里输出处于选中状态；30 秒 720p 视频转 WebM 时按「取消」，226 / 227 ms 后进入已取消，ffmpeg 退出，没有留下输出。进度条没有自动检查。
- 2026-10-25 首次引导：第一次启动（settings.json 里没有 onboardingDone）弹出「DropWheel 入门」窗口，不再直接打开任务窗口；由 Windows 在登录时拉起的除外，仍只进托盘。三步说明里的唤出键和输出位置取自当前设置，改了设置跟着变；一个键都没绑定时提示去「更改按键」。「试一试」在「图片」文件夹的「DropWheel 示例」里生成一张示例照片（已有就不重写），并在资源管理器里选中它。引导开着时，转换完成或失败的结果显示在窗口里。按「开始使用」或关掉窗口都记为看过；托盘菜单里的「入门指南」可以再打开。没看过引导时再启动一次唤出引导，看过之后唤出任务窗口。在资源管理器里选中文件的代码由任务窗口的「打开位置」和「试一试」共用。验收里的「日志无错误」改为检查整份日志，原来只看最后一个标记之后的部分；改后没有查出新的错误。证据：VM 和 Win10 x64 真机上 `app --explorer` 验收 72 项全过，日志无错误。新增的首次启动场景用全新的配置目录：启动后引导显示、任务窗口不显示；按「试一试」后示例照片在「图片」里且在资源管理器中处于选中状态；把它拖到 PNG 扇区，分别 285 / 256 ms 完成，引导里显示「转换成功」和输出路径；按「开始使用」后窗口隐藏，settings.json 写入 onboardingDone。场景结束时删掉示例文件夹。新用户读完引导要多久没有计时。

## M2 · Beta 与分发

- [ ] `IExplorerCommand` 右键菜单（C++），稀疏包
- [ ] OCR：Windows.Media.Ocr → TextRecognizer → RapidOCR 回退
- [ ] Document 引擎：Open XML SDK + 可选 LibreOffice 模块下载
- [ ] MSIX bundle (x64 + ARM64)、winget 清单、WiX MSI
- [ ] 严格离线模式 + 网络请求日志
- [ ] 本地化：zh-CN / en-US

## M3 · 1.0

- [ ] 免费额度 / Pro / 买断 的许可证实现（离线密钥）
- [ ] Store 提交
- [ ] 官网落地页上线（设计稿已完成）
