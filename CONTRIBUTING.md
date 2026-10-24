# 贡献指南

感谢你的兴趣。项目处于早期阶段，最有价值的贡献是 **M0 验证清单**（`docs/01-tech-stack.md §7`）里的实验结果与 Issue。

## 原则

- **文件不离开用户电脑**：任何 PR 不得引入默认开启的网络请求。
- **不可用就说清楚**：新增格式 / 引擎时，不可用路径必须给出用户可读原因（`Availability.Unavailable(reason)`）。
- **不抢资源管理器的键**：涉及键位的改动需同步更新 `ConflictChecker` 与设置页提示。
- **许可证边界**：进程内只允许 MIT / BSD / Apache-2.0 等宽松许可证；LGPL / MPL 一律 sidecar（ADR-0003）。
- 所有用户可见文案提供 zh-CN 与 en-US。

## 流程

1. 开 Issue 说明动机（功能 / Bug / ADR 修订）。
2. 分支命名 `feat/…`、`fix/…`、`adr/…`。
3. `dotnet test` 通过；WinUI 改动附截图（浅色 + 深色）。
4. 架构级决定写 ADR（`docs/adr/`）。

## 本地构建

见 [README](README.md#开发环境)。
