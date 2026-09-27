# GitHub Windows 发布

工作流：`.github/workflows/windows-release.yml`。

## 触发与产物

- 推送与 `package.json` 一致、尚未发布的新版本标签，在 GitHub 托管的 `windows-2022` 上执行检查和构建，并创建 Release。
- 针对分支（通常为 `master`）手动运行 `Windows release` 时只生成 Actions artifact；针对匹配版本的标签运行时，同样创建 Release。
- Actions artifact 和 Release 附件只包含 `MD2Word-<version>-win-x64-portable.zip` 与 `MD2Word-<version>-win-x64-setup.exe`。本机 `package:win` 仍保留四种交付形式。
- Release 先建立草稿，两个附件上传成功后再发布。0.7.0 使用已审核的 `doc/development/release-notes-0.7.0.md`；其他版本使用工作流内的通用说明。已发布的版本不覆盖，应修改版本后使用新标签。
- 仓库按用户授权保持 Public，源码、Release 与 Actions 产物公开；发布内容仅限脱敏源码和公开合成模板，不包含用户私有文档。工作流不修改仓库可见性。无需保存个人令牌，发布使用该次任务的 GitHub token。

## 构建环境与检查边界

GitHub 的 Windows 镜像带 Visual Studio Office 开发工作负载。工作流从 Visual Studio 的官方 PIA 共享目录定位 Word 15 程序集，检查名称、版本及 Microsoft 公钥标记，再用 `OfficeInteropWordPath` 环境属性交给现有 MSBuild 项目；不下载第三方 NuGet 重打包的 Office Interop，也不上传本机 PIA。

参考：[微软 PIA 构建目录说明](https://learn.microsoft.com/en-us/visualstudio/vsto/office-primary-interop-assemblies?view=visualstudio)、[Windows 2022 runner 镜像清单](https://github.com/actions/runner-images/blob/main/images/windows/Windows2022-Readme.md)。

该程序集可用于编译，不代表构建机安装了 Word。CI 执行类型检查、Lint、前端/存储测试、Worker 常规测试、公开模板校验、打包和协议 smoke；Word/Mermaid 真实环境门控不启用，不能把 CI 通过记作真实 Word 转换或安装生命周期通过。真实转换、安装、覆盖安装和卸载继续按照 [Setup 验收](../testing/setup-installation.md) 在本机完成。

Lua 资源包含字节指纹，checkout 前关闭自动换行转换，避免构建机改写受版本控制的 filter 文件。Actions 固定到已核对的提交 SHA；构建只使用源码中的公开合成模板包，上传路径明确限定为上述两个文件。

## 发布操作

下一次发布须先将 `package.json` 更新为尚未使用的新版本，并完成代码、文档与本机验收，再执行：

```powershell
$version = (Get-Content package.json -Raw | ConvertFrom-Json).version
git push origin master
git tag "v$version"
git push origin "v$version"
```

标签必须与 `package.json` 一致。查看 Actions 的 `Windows release` 运行，成功后在 GitHub Releases 下载附件。CI 失败时应修复原因并重新运行失败任务；不要把本机构建结果冒充 GitHub 构建结果。

首次切换默认分支等情况下，如果标签推送后没有产生任务，可以明确对该标签触发：

```powershell
$version = (Get-Content package.json -Raw | ConvertFrom-Json).version
gh workflow run windows-release.yml --ref "v$version"
```

## 当前状态

2026-09-27，`v0.7.0` 已公开发布：[Release 页面](https://github.com/loogg/md2word/releases/tag/v0.7.0)。PR #1 合并提交与通过分支预演的提交具有相同 Git 树；标签指向 `5e37676`。标签 Actions run `36314275643` 的类型检查、Lint、111 项前端/存储测试、Worker 常规测试、打包、协议 smoke、上传和发布均通过。仓库维持 Public；`v0.6.1` 与 `v0.6.0` 未覆盖。

| 0.7.0 附件 | 字节数 | GitHub SHA-256 digest |
|---|---:|---|
| `MD2Word-0.7.0-win-x64-portable.zip` | 170,605,941 | `37dbe266e6e871db9cf1cdc9c188f84077713a156fcb516f0ff0bbf4587a8662` |
| `MD2Word-0.7.0-win-x64-setup.exe` | 112,941,447 | `261ef7c344e7ca38941a5c2fb27e42664401cc2b90271ee5654a44275f5362da` |

Release 非草稿、非预发布；两个附件均为 `uploaded`。已下载两份公开附件，比对文件 SHA-256 与 GitHub digest 一致；ZIP 内能力版本为 0.7.0，只含公开参考模板，离线模板指南与源码 SHA-256 一致。托管 CI 不运行 Word COM 或安装生命周期；这些已在本机单独验收。[0.6.1](https://github.com/loogg/md2word/releases/tag/v0.6.1) 以下为历史发布记录。

2026-09-09，`v0.6.1` 已公开发布。标签推送自动触发 Actions run `34324497576`，构建提交为 `ee7bf61`。类型检查、Lint、101 项前端/存储测试、Worker 常规测试（126 通过、11 跳过）、打包、协议 smoke、上传和发布全部通过。

| 0.6.1 附件 | 字节数 |
|---|---:|
| `MD2Word-0.6.1-win-x64-portable.zip` | 170,516,266 |
| `MD2Word-0.6.1-win-x64-setup.exe` | 112,860,968 |

两个附件均已上传完成，Release 非草稿。已实际下载公开 ZIP，确认文件 SHA-256 与 GitHub digest 一致，包内能力版本为 0.6.1，`MD2Word-模板制作指南.md` 与源码 SHA-256 一致。仓库保持用户授权的 Public，默认分支为 `master`。旧 `v0.6.0` 未覆盖。

### 0.6.0 历史记录

2026-09-09，`v0.6.0` 首次 GitHub 构建成功，Actions run 为 `34316945129`，构建提交为 `0531eb4`。首次建立默认分支后针对标签手动触发，完整任务耗时约 8 分钟。

类型检查、Lint、24 个前端/存储测试文件、Worker 常规测试（126 通过、11 跳过）、打包和协议 smoke 均通过。两个 Release 附件均为 `uploaded`，Release 已从草稿发布：

| 附件 | 字节数 |
|---|---:|
| `MD2Word-0.6.0-win-x64-portable.zip` | 170,510,284 |
| `MD2Word-0.6.0-win-x64-setup.exe` | 112,846,559 |

Actions 同时保留包含这两个文件的下载包，保留期为 7 天。首次发布时仓库为 Private；2026-09-09 用户已明确授权保持 Public 并继续发布。默认且唯一分支为 `master`。安装包当前未签名，真实 Word/安装生命周期的本机结果与 CI 结果分开记录。
