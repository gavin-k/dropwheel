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
- [ ] 任务队列窗口（进度 / 取消 / 撤销 / 打开位置 / 重试）
- [ ] 设置页：键位与冲突检查、输出位置、命名规则、重名策略、文件动作、隐私与离线
- [ ] 首次启动 20 秒引导
- [ ] 引擎：Subtitle、Archive、Media（已完成）、Pdf (PDFium + PdfPig)
- [ ] 预期质量：PDF→DOCX / TXT、有损→无损图片
- [ ] 边缘停靠托盘、触屏长按

进展（每条注明证据）：

- 2026-10-25 设置与最近使用落盘：`%LOCALAPPDATA%\DropWheel\settings.json` 和 `recent.json`，先写临时文件再替换；读不了的文件改名为 `.bad` 并用默认值。转换请求的输出目录、命名模板、重名策略都取自设置。`--data-dir <目录>` 换一个配置目录，验收每次用全新的配置。证据：Mac 上 Core 测试 83 个通过；VM 上 `app --explorer` 验收 PASS。
- 2026-10-25 常驻托盘：通知区域图标（左键打开任务窗口，右键菜单「打开任务窗口 / 退出 DropWheel」，资源管理器重启后重新添加），关闭任务窗口只隐藏。单实例：再启动一次会唤出已运行实例的任务窗口，`--quit` 让它退出。还没完成首次引导的用户启动时直接看到任务窗口。Win11 把新图标放进溢出区，用户要自己拖到任务栏上。证据：VM 上 `app --explorer` 验收 PASS，新增「关窗后进程仍在且窗口隐藏」「再次启动唤出任务窗口」「`--quit` 结束进程」三项；截图确认图标正常显示（截图前在测试机上把图标提到了任务栏）。
- 2026-10-25 贴边修复：轮盘因贴边被挪开、指针不在圆心区时，扇区要等指针进过圆心区才响应，此前松手一律取消。证据：Mac 上 Core 测试 86 个通过（新增 3 个）；VM 上 `app --explorer` 验收 PASS，新增两项：指针在工作区顶边下 60 DIP 处轻点，轮盘中心被下移 102 DIP，指针正落在可用的 PNG 扇区上，原地松手记为取消且没有新文件；同样位置先移到圆心再到 PNG 扇区，正常转换。
- 2026-10-25 字幕引擎：SRT、VTT、ASS、SSA、LRC 两两互转，进程内自写解析器。读入依次认 BOM、严格 UTF-8、GB18030（覆盖无 BOM 的 GBK 字幕），写出一律带 BOM 的 UTF-8 和 CRLF。只保留时间轴和文字，样式只留斜体、粗体、下划线；从 ASS/SSA 转出、从 LRC 转出、转成 LRC 时，轮盘给出「中」和原因。LRC 没有结束时间，取下一行的开始时间，最后一行显示 5 秒。各引擎共用的输出写法抽到 `OutputFiles`：先写临时文件再不覆盖地移入，一批要么全成要么全不成。证据：Mac 上 Core 测试 92 个、Engines 测试 33 个通过（字幕 17 个，含 GBK 输入、UTF-16 输入、经 ASS 往返、LRC 多时间标签与 offset）；VM 上 `app --explorer` 验收 PASS，新增三项：拖 .srt 时轮盘给出 vtt、ass、lrc、ssa，落到 VTT 扇区后 89 ms 完成，输出是带 BOM 的 WebVTT 且中文和斜体保留。
- 2026-10-25 压缩包引擎：SharpCompress 0.50.5 进程内完成，读 ZIP、7z、TAR、TAR.GZ、RAR，写 ZIP、7z、TAR、TAR.GZ，16 条边。逐条目重新打包，保留路径、内容、修改时间和空目录；符号链接、Unix 权限、注释不保留（源是 tar / tgz 时轮盘给出原因），转成 tar 时提示文件会变大。写 zip 一律设 UTF-8 标志；读到没有标志的旧 zip，文件名先试 UTF-8，不行再按系统 OEM 代码页解码（中文系统即 GBK）。加密的包直接拒绝并说明原因；backup.tar.gz 转出 backup.zip，不是 backup.tar.zip。证据：Mac 上 Core 测试 93 个、Engines 测试 45 个通过（压缩包 12 个，含四种格式往返、GBK 文件名回退、加密拒绝、损坏包让整批回滚）；用 SharpCompress 仓库的样本手动验证 RAR4 solid、RAR5、RAR5 solid 都能转成 zip，加密的 RAR5 给出提示，样本不入库；VM 上 `app --explorer` 验收 PASS，新增一项：拖 .zip 时轮盘给出 7z、tgz、tar，落到 7Z 扇区后 163 ms 完成，输出在源文件旁且以 7z 签名开头。

- 2026-10-25 音视频引擎：ffmpeg sidecar，用 BtbN 的 LGPL shared 构建 n8.1.2-50，版本和 SHA-256 钉在 `scripts/sidecars.lock`，`scripts/fetch-sidecars.ps1` / `.sh` 下载校验，App 构建时复制到输出的 `sidecars/ffmpeg/`。视频 MP4、MOV、MKV、WebM、AVI 互转，另读 RMVB；音频 MP3、AAC、M4A、WAV、FLAC、OGG、Opus、AIFF 互转，另读 AMR；视频还能转 GIF 和各音频格式，共 143 条边。先用 ffprobe 读流信息：编码能放进目标容器就只换容器，否则 H.264 走 `h264_mf`，WebM 编成 AV1 + Opus（arm64 构建没有 libvpx），AVI 用 MPEG-4 + MP3。奇数宽高缩成偶数，多声道转 MP3 / Opus 降为双声道，WAV / FLAC / AIFF 保留 24 位。进度读 `-progress pipe:1`；取消时结束整个进程树，不留临时文件。转 GIF、AVI 时轮盘给出「中」和原因，有损转无损、MKV 转出、转 WebM 附提示。找不到 sidecar 时音视频目标灰显并说明。CI 在 Windows 上用同一份 sidecar 跑测试，release 流程打包后检查每个 MSIX 里 ffmpeg.exe 的架构，手动触发的一次运行里两个包都对得上（x64 MSIX 99.2 MB，arm64 76.7 MB，bundle 175.9 MB）。证据：Mac 上 Core 测试 95 个、Engines 测试 88 个通过（音视频 43 个：31 个测参数生成，12 个调用真实 ffmpeg，含中文文件名、24 位、6 声道、奇数尺寸、损坏文件让整批回滚、中途取消不留文件）；VM（ARM64）与 Win10 x64 真机上 `app --explorer` 验收 PASS（各 36 项），新增一项：拖 .wav 时轮盘给出 mp3、m4a、flac、aac、ogg、opus，落到 MP3 扇区后分别 277 ms、256 ms 完成，输出以 ID3 开头；两台机器上用 sidecar 把 5 秒 720p 测试画面编成 H.264（`h264_mf`），分别 716 ms、609 ms；`fetch-sidecars.ps1` 在 Windows PowerShell 5.1 下首次 11 秒，再跑直接跳过。

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
