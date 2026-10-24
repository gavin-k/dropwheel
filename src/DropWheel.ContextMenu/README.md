# DropWheel.ContextMenu（M2）

Win11 新式右键菜单只接受 `IExplorerCommand` 且要求**包标识**。此目录将包含一个 **C++ COM DLL**：

- 实现 `IExplorerCommand`（标题「用 DropWheel 转换」、图标、`GetState`）。
- `Invoke(IShellItemArray*)`：收集路径 → 通过 `AppLifecycle` 激活协议 / 命名管道 把文件列表交给主程序，然后立刻返回。
- `GetTitle / GetIcon / GetState` 不做任何 I/O（在 Explorer 进程内运行）。
- CLSID `7D1E9A4C-5B2F-4E7A-9C3D-1F2E3D4C5B6A`（与 `Package.appxmanifest` 中注释掉的注册一致）。
- x64 与 ARM64 各编译一份；MSI 直装用户通过稀疏包获得包标识。

参考：
- https://learn.microsoft.com/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer
- https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/Win7Samples/winui/shell/appshellintegration/ExplorerCommandVerb
- PowerToys 的 `ImageResizer` / `PowerRename` 右键菜单实现是很好的现代参考。
