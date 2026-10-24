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

`build.sh` 在 macOS / Linux 上只处理 Core 与测试项目；在 Windows 的 Git Bash 里运行时会改为构建整个 `DropWheel.slnx`（VM 里没有装 Git for Windows，这个分支还没实测；Windows 上以 `scripts/build-windows.ps1` 为准）。

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

- Parallels 里已有「Windows 11」VM（ARM64，之前处于挂起状态），直接 `prlctl start` 恢复使用，没有新建 VM。
- VM 内原本只有 VS Build Tools 17.14（`C:\BuildTools`），没有 .NET 10 SDK、没有 `winapp`。
- 所有操作通过 `prlctl exec`（SYSTEM 身份）执行。SYSTEM 账户下 `winget` 会挂起，`setup-windows.ps1` 检测到后自动走直连下载：`dotnet-install.ps1` 装 SDK 10.0.402 到 `C:\Program Files\dotnet`，GitHub Release 的 `winappcli-arm64.zip` 解压到 `C:\Program Files\winapp`，开发者模式写注册表，`winapp cert generate` 生成 `C:\src\dropwheel\devcert.pfx`（git 忽略，未提交）。
- 代码用 `robocopy` 同步到 `C:\src\dropwheel`（排除 `.git`、`bin`、`obj`、`artifacts`、`devcert.pfx`）。
- 为了让 `dotnet build DropWheel.slnx -p:Platform=x64` 成功，给 `.slnx` 加了 x64/ARM64 平台映射，补了 App 的占位图标与 `win-x64/win-arm64` 发布配置（见提交 `6260710`）。没有改 `src/` 下的业务代码。
- `build-windows.ps1` 运行结果（VM 内，Release）：

  | 步骤 | x64 | ARM64 |
  |---|---|---|
  | `dotnet build DropWheel.slnx` | 0 错误 | 0 错误（9 个既有 PInvoke004 警告） |
  | `dotnet test`（Core） | 21/21 通过 | 跳过（AnyCPU，同一套） |
  | `winapp pack --no-sign` | `DropWheel.App_0.1.0.0_x64.msix` 38.3 MB | `DropWheel.App_0.1.0.0_arm64.msix` 36.7 MB |

  另验证 `winapp pack <csproj> --arch x64 --arch arm64 --no-sign` 一步生成 75 MB 的 `.msixbundle`，`release.yml` 即用这条命令。
- Visual Studio 2022 Community：`vs_community.exe --quiet --wait --norestart` 由脚本在 SYSTEM 下静默安装（Universal + ManagedDesktop 工作负载）。安装结果见下一条。
- VS 安装结果：19:38 开始、20:22 结束（约 44 分钟），退出码 0，无需重启；`vswhere` 确认 Community 17.14.42 含 Universal + ManagedDesktop 两个工作负载。随后再次完整运行 `setup-windows.ps1`，所有步骤走「已安装」分支，1 秒结束，幂等性成立。
- 未验证的路径：`setup-windows.ps1` 的 winget 分支（整个过程都在 SYSTEM 下，winget 不可用，只跑过直连下载回退）；`build.sh` 的 Windows/Git Bash 分支（VM 里没有 Git for Windows）。
- 占位图标（`src/DropWheel.App/Assets/*.png`、`DropWheel.ico`）是脚本生成的橙色圆环，仅为让 MSIX 打包通过，正式发布前要替换。
