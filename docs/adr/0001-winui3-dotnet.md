# ADR-0001 · 采用 C# / WinUI 3（Windows App SDK 2.x）

- 状态：已接受（2026-10-24）

## 背景

产品定位是 Windows 11 原生体验：Mica / 亚克力、Fluent 控件、系统级无障碍、ARM64 原生。需要在拖拽过程中 150 ms 内弹出覆盖层。

## 决定

- UI：WinUI 3，Windows App SDK 2.x（Stable 频道，当前 2.5.1）。
- 语言：C# / .NET 10 LTS。
- Core 库多目标 `net10.0`，不依赖 Windows，以便在 Linux CI 跑单测。

## 备选

Rust + Tauri 2、WPF、C++ / WinUI 3、Electron —— 见 `docs/01-tech-stack.md §1.3`。

## 后果

- 需要 Windows 开发机 / Windows CI runner 构建 App 项目。
- Windows App SDK 的 Stable 大版本约每半年一次，依赖升级节奏需跟随。
- `SystemBackdropElement`（2.0+）让「透明窗口 + 局部亚克力」成为可能，是选择 2.x 而非 1.x 的直接原因。
