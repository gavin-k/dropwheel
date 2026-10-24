# 打包与分发

| 目录 | 产物 | 渠道 |
|---|---|---|
| `msix/` | `DropWheel_<ver>_x64_arm64.msixbundle` | Microsoft Store（未签名）/ GitHub Release + winget（已签名） |
| `winget/` | `GavinLee.DropWheel` 清单模板 | winget-pkgs PR |
| `msi/` | WiX v5 `.msi`（含稀疏包注册 + sidecar 二进制） | 企业直装、静默安装 |

## MSIX（winapp CLI）

```powershell
winget install Microsoft.winappcli --source winget
dotnet publish src/DropWheel.App -c Release -r win-x64   -o publish/x64
dotnet publish src/DropWheel.App -c Release -r win-arm64 -o publish/arm64
winapp pack ./publish/x64 ./publish/arm64 --manifest src/DropWheel.App/Package.appxmanifest            # Store：不签名
winapp pack ./publish/x64 ./publish/arm64 --manifest src/DropWheel.App/Package.appxmanifest --cert cert.pfx  # 侧载：签名
```

## 稀疏包（MSI 用户获得包标识 → 右键菜单）

```powershell
winapp pack ./installer/msix/sparse/appxmanifest.xml --cert cert.pfx   # identity-only msix
winapp embed-identity ./publish/x64/DropWheel.exe
# MSI 自定义操作：Add-AppxPackage -Path DropWheel.identity.msix -ExternalLocation "<InstallDir>"
```

## sidecar 二进制

放在 `tools/sidecars/<name>/<arch>/`（不入库，由 CI 下载并校验 SHA-256）：

- `ffmpeg/`：FFmpeg **LGPL** 构建（x64、arm64）+ `LICENSE.LGPL` + 构建配置与源码链接
- `7za/`：7-Zip 命令行版 + `License.txt`
- LibreOffice 不随包分发，运行时按需下载到 `%LOCALAPPDATA%\DropWheel\modules\`
