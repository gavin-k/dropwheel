# 打包与分发

| 目录 | 产物 | 渠道 |
|---|---|---|
| `msix/` | `DropWheel_<ver>_x64_arm64.msixbundle` | Microsoft Store（未签名）/ GitHub Release + winget（已签名） |
| `winget/` | `GavinLee.DropWheel` 清单模板 | winget-pkgs PR |
| `msi/` | `DropWheel-<ver>-<x64\|ARM64>.msi`（WiX v5，未打包的自包含 publish，含 sidecar） | 企业直装、静默安装、用不了 MSIX 的机器 |

## MSIX（winapp CLI）

```powershell
winget install Microsoft.winappcli --source winget
dotnet publish src/DropWheel.App -c Release -r win-x64   -o publish/x64
dotnet publish src/DropWheel.App -c Release -r win-arm64 -o publish/arm64
winapp pack ./publish/x64 ./publish/arm64 --manifest src/DropWheel.App/Package.appxmanifest            # Store：不签名
winapp pack ./publish/x64 ./publish/arm64 --manifest src/DropWheel.App/Package.appxmanifest --cert cert.pfx  # 侧载：签名
```

## MSI（WiX v5）

```powershell
dotnet publish src/DropWheel.App -c Release -r win-x64 -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:SelfContained=true -p:PublishProfile= -o publish/msi-x64
dotnet build installer/msi/DropWheel.Msi.wixproj -c Release -p:Platform=x64 -p:PublishDir=$PWD\publish\msi-x64\ -o artifacts/msi
msiexec /i artifacts\msi\DropWheel-0.1.0-x64.msi /qn    # 静默安装；/x 静默卸载
```

- 按机器装到 `Program Files\DropWheel`，开始菜单有快捷方式。publish 文件夹里的文件全部收进去，不用逐个列。
- 用户自己升级或卸载时，安装器先运行 `DropWheel.exe --quit` 让它自己退出。以 SYSTEM 身份运行时（部署工具常这样），`--quit` 碰不到用户会话里的 DropWheel，由 Windows 重启管理器关掉它，DropWheel 来不及写退出日志。还没退出的，安装器等 5 秒后结束进程；测试里程序都在这之前退出了，没走到这一步。都不用重启。
- 没有包标识，所以没有 StartupTask：设置里的「登录 Windows 时启动」写当前用户 Run 键下的 `DropWheel` 值。完全卸载时删掉执行卸载的那个用户的这个值；别的用户自己打开过的删不到，会留下一个指向已删文件的启动项。
- 卸载不动 `%LOCALAPPDATA%\DropWheel`，设置、最近使用和日志都留着，重装后接着用。
- 用 WiX v5 是有意的：从 v6 起 WiX 的二进制受 Open Source Maintenance Fee EULA 约束，年收入超过 1 万美元的组织要付费赞助。升级 WiX 之前先看许可。
- 不签名。没签名的 MSI 下载后会被 SmartScreen 拦一下。
- Release 工作流（`release.yml` 的 `msi` 任务）出 x64 和 ARM64 两个 MSI，先检查 publish 里的原生文件是不是本架构的。

## 稀疏包（MSI 用户获得包标识 → 右键菜单，未做，需要签名证书）

```powershell
winapp pack ./installer/msix/sparse/appxmanifest.xml --cert cert.pfx   # identity-only msix
winapp embed-identity ./publish/x64/DropWheel.exe
# MSI 自定义操作：Add-AppxPackage -Path DropWheel.identity.msix -ExternalLocation "<InstallDir>"
```

## sidecar 二进制

放在 `tools/sidecars/<name>/<arch>/`（不入库，由 CI 下载并校验 SHA-256）：

- `ffmpeg/`：FFmpeg **LGPL** shared 构建（x64、arm64），含 `ffmpeg.exe`、`ffprobe.exe`、DLL、`LICENSE.txt`、`SOURCE.txt`。运行 `scripts/fetch-sidecars.ps1`（或 `.sh`）按 `scripts/sidecars.lock` 下载并校验 SHA-256，可重复执行。App 项目构建时把对应架构的目录复制到输出的 `sidecars/ffmpeg/`；目录不存在就不复制，轮盘上的音视频目标灰显。`build-windows.ps1` 和 CI 会先自动下载。
- LibreOffice 不随包分发，运行时按需下载到 `%LOCALAPPDATA%\DropWheel\modules\`
