# ADR-0005 · 用户工具：清单格式与信任模型

- 状态：已接受（2026-10-29）

## 背景

DropWheel 的差异化方向是「AI 造工具」：用户在自己的 agent（Claude Code、Codex、Cursor）里装上 `skills/dropwheel-tool`，说一句想要什么，agent 把工具写进 `%LOCALAPPDATA%\DropWheel\tools\<slug>\`，DropWheel 热加载后它出现在工具或动作轮盘上。DropWheel 本身不内置模型、不要 API key，运行时照旧离线。

## 决定

- **清单是公开契约，从第一天带版本号。** `tool.json` 的 `schema` 固定为 `1`，JSON Schema 发布在 `docs/tool-manifest.schema.json`（`$id` 以 `/v1` 结尾）。字段增减只能向后兼容；不兼容的改动升 `schema`，旧值继续能读。
- **id 规则 `user.<slug>`**，并校验它不是任何格式的 id：操作 id 会原样当作任务的 `TargetFormatId` 记录，和内置的 `tool.` / `action.` 前缀一样不能撞上格式表。
- **工具分两档。** 脚本型（`kind: script`，本 ADR）跑外部进程；配方型（`kind: recipe`，只拼内置引擎，M1.5 ④）以后加，清单上 `kind` 已经留位。
- **脚本型默认不可信。** 新工具装上后在轮盘上灰显，原因写「在 设置 › 我的工具 里启用后可用」；用户在设置页看到完整命令行后打开开关才会跑。设置里记的是 `(id → SHA-256(command + args))`，清单的命令或参数一改，哈希对不上，工具重新灰显。信任的是「这条命令」，不是「这个文件夹」。
- **脚本不自报输出路径。** DropWheel 在目标目录旁开一个 `.{guid}.dropwheel-tmp` 临时文件夹，把带最终扩展名的 `{output}` 路径交给脚本；脚本写完，DropWheel 用 `OutputFiles.WriteAsync` 搬进位。这样命名模板、重名策略、撤销、批量回滚全部沿用，资源管理器也永远看不到半成品。`output.mode: none` 表示没有要跟踪的产物；`undo: none` 表示不能撤销，改写或删除输入的脚本必须声明它。
- **stdout 协议只有三行**：`progress <0..1>`、`detail <文字>`、`skip <原因>`。退出码非 0 算失败，stderr 末尾几行成为用户看到的原因。超时和取消都结束整个进程树（Job Object，`KILL_ON_JOB_CLOSE`），DropWheel 退出时子进程一起结束。
- **命令解析**：绝对路径原样；相对路径在工具文件夹下；裸名先在工具文件夹、再沿 PATH × PATHEXT 找。`pwsh` 不存在时回退到 Windows PowerShell 5.1，并在问题列表里警告编码；skill 要求 PowerShell 脚本第一条语句设 `[Console]::OutputEncoding` 为 UTF-8，Python 由环境变量 `PYTHONIOENCODING` / `PYTHONUTF8` 兜底。命令找不到是**警告**不是错误：清单仍然加载，轮盘上灰显并说明。
- **agent 能自检。** `DropWheel.exe --validate-tool <folder>` 和 `--list-tools` 在 WinUI 初始化前处理，输出 JSON，退出码 0 / 1（有错误）/ 2（用法），不写任何文件。热加载时每个工具文件夹旁写一份 `.problems.json`，agent 不跑命令也能看到问题。
- **`placement` 只是首次安装的建议。** 工具第一次被看到时，按清单的 `group` / `index` 放进 `layout.json`，之后布局归用户；轮盘满了就把目标扇区折成一个分组（≤ 6 项，不嵌套），所以图片的工具轮盘即使六格已满，新工具也会出现。

## 隐私

脚本型工具是 DropWheel 之外的另一个进程，用用户的权限跑用户电脑上的 pwsh、python 或任何可执行文件。**它读了什么、上不上网，DropWheel 看不见；设置页的网络请求计数只计 DropWheel 自己进程里 .NET 发出的请求，不包括它。** 这句话写在 ADR、设置页启用开关旁、README 的「本地离线」段和 skill 里。信任开关解决的是「用户知道自己在跑什么」，不是沙箱。

## 后果

- `Package.appxmanifest` 关闭文件系统写虚拟化（`desktop6:FileSystemWriteVirtualization = disabled`），否则打包版把 `.problems.json` 和 `layout.json` 写到 `LocalCache`，agent 看不到；这一点只能在 MSIX 安装上实测。
- 工具的名字和提示是数据，不经本地化目录；清单里 `name` / `hint` 可以给 `{zh, en}`。
- `--validate-tool` 的问题码（`json schema id name wheel appliesTo kind command args output timeout placement undo`）从此稳定，是 agent 和测试依赖的接口。
- 没做：`output.mode: replace`（原地替换 + 可撤销，需要新的 UndoPlan）、嵌套分组、让 agent 反过来调用 DropWheel 引擎的 MCP / CLI。
