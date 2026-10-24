# MagickSpike — §7 #6：Magick.NET 在 Windows ARM64 上能不能真的跑

一次性控制台程序，回答 `docs/01-tech-stack.md` §7 第 6 项的 Magick.NET 部分：原生库能否在 ARM64 进程内加载并完成 M0 需要的图片互转。
**不加入 `DropWheel.slnx`**，也不会被移植进 `src/`。

## 运行

```powershell
dotnet build spikes/MagickSpike/MagickSpike.csproj -c Release -r win-arm64
.\spikes\MagickSpike\bin\Release\net10.0\win-arm64\MagickSpike.exe      # 退出码 0 = 全部往返成功
```

生成一张 1920×1080 的 JPG，转成 PNG / WebP / AVIF / JXL / HEIC 再读回，打印本构建声明的读写能力表与耗时。
构建不声明可写的格式（HEIC）失败只记 SKIP，不算失败。

## 结果（2026-10-24，Parallels Windows 11 22H2 22621 ARM64，.NET 10.0.13，Magick.NET-Q16-AnyCPU 14.9.0）

| | ARM64 原生（`-r win-arm64`） | x64 在同一 VM 上模拟运行（`-r win-x64 --self-contained`） |
| --- | --- | --- |
| 加载的原生库 | `Magick.Native-Q16-arm64.dll` | `Magick.Native-Q16-x64.dll` |
| 原生初始化 | 24–121 ms（2 次） | 166 ms（1 次） |
| JPG→PNG 写 / 读回 | 97–108 / 33–35 ms | 381 / 96 ms |
| JPG→WebP | 101–115 / 27 ms | 237 / 73 ms |
| JPG→AVIF | 161–171 / 44 ms | 812 / 230 ms |
| JPG→JXL | 3440 / 123 ms（1 次） | 未测 |
| JPG→HEIC | 无编码器（`no encode delegate`） | 同样无编码器 |

- 包内 `runtimes/win-arm64/native/` 确实带了 ARM64 原生库（同时查过 `bblanchon.PDFium.Win32` 137.0.7149 与 `SkiaSharp.NativeAssets.Win32` 3.119.0，都有 `win-arm64`）。
- **HEIC/HEIF 只读不写**是 Magick.NET 的构建选择（不带 x265），x64 上一样，与 ARM64 无关；与 ADR-0003「HEIC 编码走系统编解码器」的决定一致。
- JXL 编码 3.4 s 偏慢，M0 不涉及，但做格式清单时要留意。
- x64 模拟运行是对照组，不能替代真实 Intel 机器上的结果（本项目没有 Intel 硬件，见 `README.md`）。
