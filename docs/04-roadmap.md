# 04 · 路线图

## M0 · 原型验证（目标：2 周）

**一句话验收**：在资源管理器里拖一张 JPG，轻点 Shift，轮盘弹出，选 PNG，松手，同目录出现 PNG；在队列里点撤销，PNG 进回收站。

- [ ] `DropWheel.Shell`：鼠标 / 键盘低级钩子 + `SysDragImage` 侦测 + 透明放置目标窗口（Spike，可先用 WinForms 验证再迁 WinUI）
- [ ] `DropWheel.App`：透明覆盖窗口 + `SystemBackdropElement` 亚克力轮盘（6 扇区、中心文件信息、按键提示）
- [ ] `DropWheel.Engines.Image`：Magick.NET JPG/PNG/WEBP 互转
- [ ] `DropWheel.Core`：JobQueue、UndoLedger（回收站）、NamingTemplate 最小实现
- [ ] 验证 `docs/01-tech-stack.md §7` 待验证清单

## M1 · 可用的 Alpha

- [ ] 三种轮盘 + 搜索全部格式 + 不可用灰显与原因
- [ ] 任务队列窗口（进度 / 取消 / 撤销 / 打开位置 / 重试）
- [ ] 设置页：键位与冲突检查、输出位置、命名规则、重名策略、文件动作、隐私与离线
- [ ] 首次启动 20 秒引导
- [ ] 引擎：Media (ffmpeg LGPL)、Pdf (PDFium + PdfPig)、Archive (SharpCompress)
- [ ] 预期质量：PDF→DOCX / TXT、有损→无损图片
- [ ] 边缘停靠托盘、触屏长按

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
