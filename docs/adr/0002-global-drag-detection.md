# ADR-0002 · 全局拖拽侦测与「轻点唤出」

- 状态：已接受（2026-10-24），第 1、3、4 条按 DragSpike 结果修订，见文末

## 背景

Windows 没有「全局拖拽开始」API。同时资源管理器在**松手瞬间**按住 Ctrl / Shift / Alt 分别表示复制 / 移动 / 创建快捷方式，产品不能破坏这些语义。

## 决定

1. **侦测拖拽**：`WH_MOUSE_LL` 判定「按下并移动」→ UI Automation 结构变化事件确认桌面根下出现 `SysDragImage` 窗口（WinEvents 作辅助信号）。
2. **唤出轮盘**：`WH_KEYBOARD_LL` 监听修饰键；默认 **轻点**（按下 → ≤300 ms 松开）才唤出。按住到松手 → 什么都不做，交给 Shell。
3. **接收放置**：轮盘显示的同时显示一个透明分层全屏窗口并 `RegisterDragDrop`；`Drop` 时读 `IShellItemArray`；松手在中心 / 外部返回 `DROPEFFECT_NONE`。
4. **默认键位**：`Shift` 转换 · `Ctrl+Alt` 工具 · `Win` 动作（拖拽期间吞掉 Win 以阻止开始菜单）。全部可改；设置页用 `ConflictChecker` 标出与 Shell / AltGr 的冲突。
5. **非修饰键入口**：资源管理器右键（`IExplorerCommand`）、触屏/笔长按 450 ms、拖到屏幕边缘停靠托盘、全局快捷键。

## 备选

- 只做右键菜单，不做全局拖拽：体验退化为普通工具，放弃。
- 用 Shell 扩展（`IDropTarget` 注册到桌面）：只覆盖桌面，不覆盖任意窗口，放弃。
- 注入 Explorer 进程 hook `DoDragDrop`：越界、不安全、Store 不允许，放弃。

## 风险

- `SysDragImage` 为未文档化类名，Windows 更新可能失效 → 保留 WinEvents 与「仅鼠标判定」降级路径（降级时轮盘仍能弹，只是无法区分 OLE 拖拽与普通拖动）。
- 低级钩子回调超时会被系统摘除 → 回调只投递消息，不做任何工作。
- 覆盖层显示时若用户的拖拽源窗口已 Capture 鼠标，窗口 Z 序与 DPI 需验证。

## 修订（2026-10-24，DragSpike 之后）

- 第 1 条：`SysDragImage` 改为左键按下期间每 25 ms 用 `FindWindowEx` 轮询，不用 UI Automation 事件。`EVENT_SYSTEM_DRAGDROPSTART` 在 Win11 22H2 ARM64 和 Win10 21H2 x64 上都从未触发，只记日志。拖拽结束只认 LL 鼠标的左键抬起。见 [DragSpike](../../spikes/DragSpike/README.md)。
- 第 3 条：放置目标和轮盘分成两个窗口，见 [ADR-0004](0004-drop-target-host.md)。
- 第 4 条：吞 Win 键要同时吞按下和抬起，而且只吞「自己吞过按下」的那次抬起，否则系统会一直认为 Win 被按住。拖拽中不能替用户发 Esc，Esc 会取消 OLE 拖拽。
