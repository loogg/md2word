# GitHub Windows 发布

工作流：`.github/workflows/windows-release.yml`。

## 触发与产物

- 推送与 `package.json` 一致的版本标签，例如 `v0.6.0`，在 GitHub 托管的 `windows-2022` 上执行检查和构建，并创建 Release。
- 针对分支（通常为 `master`）手动运行 `Windows release` 时只生成 Actions artifact；针对匹配版本的标签运行时，同样创建 Release。
- Actions artifact 和 Release 附件只包含 `MD2Word-<version>-win-x64-portable.zip` 与 `MD2Word-<version>-win-x64-setup.exe`。本机 `package:win` 仍保留四种交付形式。
- Release 先建立草稿，两个附件上传成功后再发布。已发布的版本不覆盖，应修改版本后使用新标签。
- 仓库保持 Private，Release 与 Actions 产物继承仓库访问边界；工作流不修改仓库可见性。无需保存个人令牌，发布使用该次任务的 GitHub token。

## 构建环境与检查边界

GitHub 的 Windows 镜像带 Visual Studio Office 开发工作负载。工作流从 Visual Studio 的官方 PIA 共享目录定位 Word 15 程序集，检查名称、版本及 Microsoft 公钥标记，再用 `OfficeInteropWordPath` 环境属性交给现有 MSBuild 项目；不下载第三方 NuGet 重打包的 Office Interop，也不上传本机 PIA。

参考：[微软 PIA 构建目录说明](https://learn.microsoft.com/en-us/visualstudio/vsto/office-primary-interop-assemblies?view=visualstudio)、[Windows 2022 runner 镜像清单](https://github.com/actions/runner-images/blob/main/images/windows/Windows2022-Readme.md)。

该程序集可用于编译，不代表构建机安装了 Word。CI 执行类型检查、Lint、前端/存储测试、Worker 常规测试、公开模板校验、打包和协议 smoke；Word/Mermaid 真实环境门控不启用，不能把 CI 通过记作真实 Word 转换或安装生命周期通过。真实转换、安装、覆盖安装和卸载继续按照 [Setup 验收](../testing/setup-installation.md) 在本机完成。

Lua 资源包含字节指纹，checkout 前关闭自动换行转换，避免构建机改写受版本控制的 filter 文件。Actions 固定到已核对的提交 SHA；构建只使用源码中的公开合成模板包，上传路径明确限定为上述两个文件。

## 发布操作

先完成并提交代码、文档与本机验收，再执行：

```powershell
git push origin master
git tag v0.6.0
git push origin v0.6.0
```

标签必须与 `package.json` 一致。查看 Actions 的 `Windows release` 运行，成功后在 GitHub Releases 下载附件。CI 失败时应修复原因并重新运行失败任务；不要把本机构建结果冒充 GitHub 构建结果。

首次切换默认分支等情况下，如果标签推送后没有产生任务，可以明确对该标签触发：

```powershell
gh workflow run windows-release.yml --ref v0.6.0
```

## 当前状态

2026-09-09，`v0.6.0` 首次 GitHub 构建成功，Actions run 为 `34316945129`，构建提交为 `0531eb4`。首次建立默认分支后针对标签手动触发，完整任务耗时约 8 分钟。

类型检查、Lint、24 个前端/存储测试文件、Worker 常规测试（126 通过、11 跳过）、打包和协议 smoke 均通过。两个 Release 附件均为 `uploaded`，Release 已从草稿发布：

| 附件 | 字节数 |
|---|---:|
| `MD2Word-0.6.0-win-x64-portable.zip` | 170,510,284 |
| `MD2Word-0.6.0-win-x64-setup.exe` | 112,846,559 |

Actions 同时保留包含这两个文件的下载包，保留期为 7 天。仓库仍为 Private，默认且唯一分支为 `master`。安装包当前未签名，真实 Word/安装生命周期的本机结果与 CI 结果分开记录。
