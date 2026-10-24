# ADR-0004 · 放置目标与轮盘分成两个窗口

- 状态：已接受，第 1 条风险待 M0 验收驱动验证（2026-10-24）

## 背景

- ADR-0002 第 3 条：轮盘出现时显示一个透明全屏窗口并 `RegisterDragDrop`，松手在中心或轮盘外返回 `DROPEFFECT_NONE`。
- `DropWheel.Shell/DropTargetHost.cs` 的 TODO 写的是 `WS_EX_LAYERED`；[AcrylicSpike](../../spikes/AcrylicSpike/README.md) 证明 WinUI 3 窗口透明要走 DWM 配方，`WS_EX_LAYERED` 用在 WinUI 窗口上会和合成器冲突（该 README 发现 5，推断）。两者必须定一个。
- [DragSpike](../../spikes/DragSpike/README.md) 用一个 WinForms 窗口同时当轮盘和放置目标，窗口只有轮盘大小。松手在轮盘外会落到下面的真实窗口，比如资源管理器的文件夹，触发真的复制或移动，不符合 ADR-0002。
- 隐藏的窗口收不到拖拽事件。分层窗口里 alpha 为 0 的像素不参与命中测试（Win32 文档），OLE 用命中测试找放置目标。

## 决定

1. **两个窗口，各管一件事。**
   - **放置宿主**（`DropWheel.Shell.DropTargetHost`）：纯 Win32 弹出窗口，`WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`，`SetLayeredWindowAttributes(LWA_ALPHA, 1)`，覆盖整个虚拟屏。alpha=1 肉眼不可见，但参与命中测试。它跑在自己的 STA 线程上：`OleInitialize`，窗口创建一次并 `RegisterDragDrop` 一次，之后只 Show / Hide。
   - **轮盘**（`DropWheel.App.OverlayWindow`）：WinUI 3 窗口，DWM 透明配方加 `SystemBackdropElement` 亚克力圆盘，`NOACTIVATE | TOOLWINDOW | TOPMOST`，只有轮盘大小。它在放置宿主下面，不接收输入，只负责画。
2. **扇区判定在宿主里做。** App 把轮盘中心（物理像素）和所在显示器的缩放交给宿主，宿主用 Core 的 `WheelGeometry`（DIP）算扇区。宿主在自己的线程上抛出 `Entered(paths)`、`Hovered(sector)`、`Dropped(paths, sector)`、`Canceled`，App 用 `DispatcherQueue` 转到 UI 线程。
3. **两阶段填充。** 轻点修饰键时先显示轮盘，扇区为空、中心显示提示；宿主的 `DragEnter` 从 `IDataObject` 的 `CF_HDROP` 取到路径后，App 再构建 `WheelModel` 填扇区。`DragOver` 落在可用扇区返回 `DROPEFFECT_COPY`，其余返回 `DROPEFFECT_NONE`。返回 COPY 而不是 MOVE，资源管理器就不会删除源文件。
4. **隐藏有三重保证。** `Drop` / `DragLeave`；LL 鼠标钩子看到左键抬起；看门狗（显示后一段时间内既没有 `DragOver` 左键也已抬起）。
5. **显示顺序。** 先显示轮盘，再显示宿主。两者都是 TOPMOST，后显示的在上。

## 备选

- **轮盘窗口自己当放置目标，只覆盖轮盘**：松手在轮盘外会落到下面的真实窗口，违反 ADR-0002 的「外部松手等于取消」。放弃。
- **WinUI 窗口铺满虚拟屏**：常驻一块全屏透明交换链，开销和 AcrylicSpike 量过的条件不同；WinUI 拖放事件在不激活窗口上的行为也没验证过。放弃。
- **WinUI 窗口加 `WS_EX_LAYERED`**：见背景。放弃。
- **宿主 alpha=0**：不参与命中测试，收不到 `DragEnter`。放弃。

## 风险

1. alpha=1 的分层窗口能不能被 OLE 选为放置目标，只有文档依据，M0 验收驱动要先验证。
2. `DragEnter` 要等宿主出现后的下一次鼠标移动或按键变化，在此之前扇区是空的。用户本来就要移动鼠标去选扇区，所以先接受。
3. 多显示器、混合 DPI 没测过（`docs/01-tech-stack.md` §7 第 3 项）。宿主覆盖虚拟屏，OLE 给的是物理像素坐标，缩放取轮盘所在显示器的。
