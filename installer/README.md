# 打包与分发

| 目录 | 产物 | 渠道 |
|---|---|---|
| `msix/` | `DropWheel_<ver>_x64_arm64.msixbundle` | Microsoft Store（未签名）/ GitHub Release + winget（已签名） |
| `winget/` | `GavinLee.DropWheel` 清单模板 | winget-pkgs PR |
| `msi/` | `DropWheel-<ver>-<x64\|ARM64>.msi`（WiX v5，未打包的自包含 publish，含 sidecar） | 企业直装、静默安装、用不了 MSIX 的机器 |

## MSIX（winapp CLI）

```powershell
winget install Microsoft.winappcli --source winget
# 每个架构在自己的空目录里打，winapp 自己跑 dotnet publish；和 release.yml 的 msix 任务一样
mkdir artifacts/x64;   cd artifacts/x64;   winapp pack ../../src/DropWheel.App/DropWheel.App.csproj -c Release --arch x64   --no-sign; cd ../..
mkdir artifacts/arm64; cd artifacts/arm64; winapp pack ../../src/DropWheel.App/DropWheel.App.csproj -c Release --arch arm64 --no-sign; cd ../..
# 多架构 bundle：要么让 winapp 一次打两个架构（release.yml 这么做，2026-10-31 在 GitHub 上跑通），要么拿上面两个 .msix 用 makeappx 合（2026-10-30 本机验证过的路子）
mkdir artifacts/bundle; cd artifacts/bundle; winapp pack ../../src/DropWheel.App/DropWheel.App.csproj -c Release --arch x64 --arch arm64 --no-sign; cd ../..
makeappx bundle /o /d <只放这两个 .msix 的文件夹> /p DropWheel.App_<ver>.msixbundle
# 侧载：去掉 --no-sign 换成 --cert cert.pfx，或者事后 signtool sign /fd SHA256 /f cert.pfx <msix>
```

- **包里必须带 .NET 运行时。** `winapp pack` 调 `dotnet publish -r win-x64` 时不传 `Platform`，所以 `Properties/PublishProfiles/win-x64.pubxml` 里的 `SelfContained` 根本没被读到；csproj 里在 `_IsPublishing` 时强制 `SelfContained=true` 补这个洞。看大小就知道对不对：x64 约 145 MiB、ARM64 约 120 MiB 是对的；87 MiB 左右的是框架依赖版，没装 .NET 10 的机器上一启动就死（「You must install .NET to run this application」）。release.yml 的原生文件检查里 `coreclr.dll` / `hostfxr.dll` 就是防这个。
- **侧载机器要先有 Windows App SDK 运行时。** 清单声明依赖框架包 `Microsoft.WindowsAppRuntime.2`（≥ 2.5.1）。Store 安装会自动带上；侧载（GitHub Release / winget）的用户得先装微软的 Windows App SDK 2.5 运行时安装器，或者 `Add-AppxPackage` 对应架构的 `Microsoft.WindowsAppRuntime.2` 框架包。缺了会在 `Add-AppxPackage` 时被拒（具体报错文字还没抓到，2026-10-30 在测试机上没能把框架包卸干净）。
- `unvirtualizedResources` 是受限能力（原因见 ADR-0005）：侧载不受影响，上架 Store 时审核可能问用途。

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
- 本地打的 MSI 不签名，下载后会被 SmartScreen 拦一下；`release.yml` 打的会签，见下一节。
- Release 工作流（`release.yml` 的 `msi` 任务）出 x64 和 ARM64 两个 MSI，先检查 publish 里的原生文件是不是本架构的，再签 publish 里的 `DropWheel.exe`，编完 MSI 再签 `.msi`（ffmpeg 是第三方二进制，不用我们的证书签）。

## 签名与 GitHub Release（release.yml）

签名走 Azure Trusted Signing，做法和 shhield-ai 一样，仓库里没有任何证书：

- GitHub 环境 `signing`（只允许 `main` 分支和 `v*` 标签部署）里放机密 `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` / `AZURE_SIGNING_ENDPOINT` / `AZURE_SIGNING_ACCOUNT_NAME` / `AZURE_CERTIFICATE_PROFILE_NAME`，和变量 `MSIX_PUBLISHER`（证书主题）。作业用 `id-token: write` 经 OIDC 登录（`azure/login`），Azure 那边的应用注册要有一条联合凭据，subject 是 `repo:gavin-k/dropwheel:environment:signing`（GitHub 现在发的 subject 带 owner / repo 的数字 id，所以 id 形式那条也要有），并在证书配置上有「Artifact Signing Certificate Profile Signer」角色。
- MSIX 的 `Identity/Publisher` 必须和证书主题逐字相同，signtool 才肯签。源文件里保留 `CN=Gavin Lee` 给本地 devcert 用，`release.yml` 只在签名的构建里把它改成 `MSIX_PUBLISHER`。后果：发布包的包家族名和本地开发包不同，两者可以并存、不会互相升级；`PublisherDisplayName` 不受影响。
- signtool 签 `.msixbundle` 时会连里面的包一起签，所以单架构 `.msix` 和 bundle 各签一次就够；签完用 `Get-AuthenticodeSignature` 核对 `Status` 是 `Valid`。
- 手动触发（`gh workflow run release.yml --ref main -f signing=false`）可以拿未签名的包；推送 `v*` 标签时总是签名，并由 `release` 作业把产物改成 `DropWheel-<ver>-x64.msix` / `-arm64.msix` / `.msixbundle` / `-x64.msi` / `-arm64.msi` 加 `SHA256SUMS.txt` 发成 GitHub（预）发布版。标签必须等于 `Directory.Build.props` 的 `VersionPrefix`，并且 `Package.appxmanifest` 的 `Version` 是它加 `.0`，否则 `check` 作业直接失败。
- 侧载 MSIX 的机器要能验到「Microsoft Identity Verification Root CA 2020」这条链，Windows 通过自动根证书更新拿到它；长期离线的机器可能报 0x800B0109。

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
