# 在 macOS 上开发 DropWheel

DropWheel 是 Windows 11 应用，但仓库被切成两半：一半是纯 .NET，可以在 Mac 上日常开发；另一半依赖 WinUI 3 / Windows App SDK，只能在 Windows 上构建。本页说明 Mac 上能做什么、不能做什么，以及如何用 Parallels 里的 Windows 11 虚拟机补上另一半。

## Mac 上能做什么

| 项目 | TFM | Mac 可构建 | 说明 |
|---|---|---|---|
| `src/DropWheel.Core` | `net10.0` | ✅ | 领域模型：转换图、轮盘、任务队列、命名模板、设置。无 Windows 依赖 |
| `tests/DropWheel.Core.Tests` | `net10.0` | ✅ | xUnit，`dotnet test` 直接跑 |
| `src/DropWheel.Shell` | `net10.0-windows10.0.26100.0` | ❌ | CsWin32 生成的 Win32 互操作 |
| `src/DropWheel.Engines` | `net10.0-windows10.0.26100.0` | ❌ | Magick.NET 等引擎适配器 |
| `src/DropWheel.App` | `net10.0-windows10.0.26100.0` | ❌ | WinUI 3 主程序 + MSIX 打包 |

能做：写 Core 的业务逻辑和单测、改文档、改 CI、改脚本。
不能做：编译 / 运行 WinUI 应用、生成 MSIX、调试钩子与拖拽。这些放到 Windows 里做。

## 一次性准备

1. .NET 10 SDK 安装在 `~/.dotnet`（通过 `dotnet-install.sh`）。`~/.zshrc` 里已加入：

   ```zsh
   export DOTNET_ROOT="$HOME/.dotnet"
   export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
   export DOTNET_CLI_TELEMETRY_OPTOUT=1
   ```

   新开终端或 `source ~/.zshrc` 后 `dotnet --version` 应输出 `10.0.x`。
2. 仓库根目录的 `build.sh` 会在没有 PATH 的情况下自动回退到 `~/.dotnet`。

## 日常命令

```bash
./build.sh              # restore + build + test Core 与测试项目（Release）
./build.sh -c Debug     # Debug 配置
./build.sh --no-test    # 只构建
```

`build.sh` 在 macOS / Linux 上只处理 Core 与测试项目；在 Windows 的 Git Bash 里运行时会改为构建整个 `DropWheel.slnx`。

## 用 Parallels 里的 Windows 构建另一半

### 虚拟机

- 本机 Parallels Desktop 里有一台名为 **「Windows 11」** 的 Windows 11 ARM 虚拟机（4 vCPU / 6 GB）。
- 启停：

  ```bash
  prlctl list -a                 # 查看状态
  prlctl start "Windows 11"      # 启动 / 从挂起恢复
  prlctl suspend "Windows 11"    # 用完挂起
  ```

- 虚拟机里以 SYSTEM 身份执行命令（无需登录、无 UAC 弹窗）：

  ```bash
  prlctl exec "Windows 11" cmd /c "echo hi"
  prlctl exec "Windows 11" --current-user cmd /c "echo as logged-in user"
  ```

  注意：`prlctl exec` 对带反斜杠的参数转义很脆弱，长脚本请先复制进 VM 的 `C:\temp` 再执行，或用 `powershell -EncodedCommand`。

### 共享文件夹：在 Windows 里打开同一份代码

Parallels 已开启「共享用户主目录」，Mac 上的 `~/Projects/dropwheel` 在 Windows 里就是：

- UNC 路径：`\\Mac\Home\Projects\dropwheel`
- 登录用户看到的映射盘：`Z:\Projects\dropwheel`（`Z:` = `\\Mac\Home`）

用 VS / VS Code 打开这个路径即可编辑同一份源码，Mac 上 `git commit`，Windows 上只管编译。

**不要直接在共享路径上 `dotnet build`**：UNC/网络文件系统上 NuGet 还原和 MSIX 打包都很慢，而且部分 Windows App SDK 任务在 UNC 路径上会出错。推荐把代码同步到 VM 本地盘再构建：

```powershell
# 在 Windows 里（PowerShell）
robocopy \\Mac\Home\Projects\dropwheel C:\src\dropwheel /MIR /XD .git bin obj artifacts .vs /NFL /NDL /NJH /NJS
cd C:\src\dropwheel
pwsh .\scripts\build-windows.ps1          # 或 powershell -File .\scripts\build-windows.ps1
```

或者从 Mac 一键触发：

```bash
prlctl exec "Windows 11" cmd /c "robocopy \\Mac\Home\Projects\dropwheel C:\src\dropwheel /MIR /XD .git bin obj artifacts .vs /NFL /NDL /NJH /NJS & powershell -NoProfile -ExecutionPolicy Bypass -File C:\src\dropwheel\scripts\build-windows.ps1"
```

产物在 `C:\src\dropwheel\artifacts\`（未签名 x64 `.msix`）。

### Windows 侧工具链

首次使用在 Windows 里跑一次（幂等，可重复）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File \\Mac\Home\Projects\dropwheel\scripts\setup-windows.ps1
```

它会安装 .NET 10 SDK、`winapp` CLI、Visual Studio 2022 Community（WinUI 工作负载），开启开发者模式，并用 `winapp cert generate --if-exists skip` 生成本地开发证书（`devcert.pfx`，已在 `.gitignore` 中，不要提交）。细节见脚本头部注释。

## 本次搭建实际做了什么（2026-10-24）

- Parallels 里已有「Windows 11」VM（之前处于挂起状态），直接 `prlctl start` 恢复使用，没有新建 VM。
- VM 内原本只有 VS Build Tools 17.14（`C:\BuildTools`），没有 .NET 10 SDK、没有 `winapp`。
- 通过 `prlctl exec`（SYSTEM 身份）把 `scripts/setup-windows.ps1` 复制到 `C:\temp` 执行：SYSTEM 账户下 `winget` 不可用，脚本自动走直连下载路径（`dotnet-install.ps1`、GitHub Release 的 `winappcli-arm64.zip`、`vs_community.exe` 引导程序）。
- 代码用 `robocopy` 同步到 `C:\src\dropwheel`，再运行 `scripts/build-windows.ps1`。结果见仓库 README 与 PR / 提交说明。
