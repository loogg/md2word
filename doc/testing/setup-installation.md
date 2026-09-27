# Setup 安装版验收（0.6.0 / 0.7.0 / 0.8.0）

## 范围与数据

`npm run package:win` 同时生成便携目录、ZIP、单文件 Portable 和 NSIS Setup。Setup 使用当前用户安装、可选安装目录、桌面/开始菜单快捷方式；安装完成不自动运行。默认卸载保留用户数据。程序仍需本机 Word 和 Pandoc。

安装脚本只在安装目录的 `resources/md2word-installed` 写入 `setup` 标记；原始便携目录和 Portable 解压负载不包含该标记。Main 根据它选择用户数据目录中的模板库，首次启动按包复制随包公开模板。已有包（包括空索引）不覆盖，升级后保留用户编辑、导入与删除状态；新模板包只在不存在时复制。源链接/特殊文件拒绝，单包复制失败不发布半成品。

单元测试只在系统临时目录生成带明确合成占位字节的最小 DOCX/CSS 文件，用于验证文件复制、原子性、已有用户包保留、编辑保留和删除后不复活；这些字节不用于 Word 转换、不提交。桌面测试只导入随包公开参考 DOCX/CSS，独立数据目录固定为 `output/e2e-setup-user-data`。

## 命令与检查

先确认本机没有需要保留的同产品安装。安装生命周期验收使用独立目录，不对用户已有安装运行静默覆盖或卸载。

```powershell
$ErrorActionPreference = 'Stop'
npm run package:win
$version = (Get-Content package.json | ConvertFrom-Json).version
$installer = (Resolve-Path "release/MD2Word-$version-win-x64-setup.exe").Path
$installDir = [IO.Path]::GetFullPath((Join-Path (Get-Location) "output/setup-install-test-$version"))
$install = Start-Process -FilePath $installer -ArgumentList "/S /D=$installDir" -WindowStyle Hidden -Wait -PassThru
if ($install.ExitCode -ne 0) { throw "Setup 安装失败：$($install.ExitCode)" }

$env:MD2WORD_RUN_SETUP_E2E = '1'
$env:MD2WORD_SETUP_EXE = Join-Path $installDir 'MD2Word.exe'
$env:MD2WORD_SETUP_USER_DATA = [IO.Path]::GetFullPath((Join-Path (Get-Location) "output/e2e-setup-$version-user-data"))
npx playwright test --config playwright.electron.config.ts e2e/electron-installed.spec.ts
```

桌面用例在任何模板写入前确认 Electron 的 `userData` 与隔离目录完全相同；检查首次复制参考模板、实际导入后的文件位置、安装目录模板不变、删除参考模板并设置默认项后重启保持状态。

随后关闭应用、记录隔离模板库文件哈希，再重复安装同一 Setup，检查文件哈希不变。最后通过该测试安装目录中的卸载器静默卸载，检查安装注册和快捷方式被清理、用户模板数据保留。所有删除/移动目标必须先核实位于上述专用测试目录。

另外执行便携目录版 packaged E2E、真实 Word 转换和截图检查，确保增加安装版不改变便携版的 EXE 同级模板路径。

## 结果

### 0.8.0 本机验收（2026-09-27）

- `npm run package:win` 成功生成 0.8.0 便携目录、Portable ZIP、单文件 EXE 与 Setup EXE；最终 `release` 只有四种交付物、公开模板容器和两份离线说明。原有 0.7.0 本地产物移入被忽略的 `output/release-v0.7.0-local`，未混入新包。`check:bridge-production` 通过，便携包的 `app.asar` 仅有 10 个白名单条目，不含 Bridge 脚本或前端适配器。
- 便携目录 `e2e/electron-packaged.spec.ts` 通过，内置 Worker 能力版本 0.8.0、公开参考模板、窄 preload 与必需环境均正常；同一便携目录的 `e2e/electron-smoke.spec.ts` 两项均通过，其中一项实际经 Electron Main、C# Worker 与 Word 转换运行时合成 Markdown，检查列表、Mermaid 和图题阶段。
- 在没有现有 MD2Word 安装或运行进程时，0.8.0 Setup 首次静默安装到 `output/setup-install-test-0.8.0` 返回 0；安装版 E2E 在隔离 `userData` 中导入公开合成模板、删除参考模板、设默认并重启，状态正常。同版本覆盖安装返回 0，四个模板文件 SHA-256 不变，retention E2E 通过。专用安装卸载返回 0，应用 EXE 移除，四个模板文件仍在且哈希不变。
- 跨版本验收使用先前下载、SHA-256 与 GitHub digest `261ef7c344e7ca38941a5c2fb27e42664401cc2b90271ee5654a44275f5362da` 一致的公开 0.7.0 Setup。将其安装到另一个 `output` 专用目录后，`e2e/electron-upgrade.spec.ts` 的 seed 阶段导入合成模板、删除参考模板并设默认；0.8.0 Setup 覆盖返回 0，四个模板文件 SHA-256 不变。verify 阶段确认版本 0.8.0、模板校验通过、唯一导入模板仍为默认且删除的参考模板未恢复。专用卸载成功，模板哈希仍不变。没有覆盖用户已有安装或删除用户数据。
- 本地 ZIP 为 170,636,932 字节、Setup 为 112,947,572 字节；本地 SHA-256 分别为 `c628f4b7230882ec2fc163bf875a99ce2e68f60ad9932e8a2c017adddb6795ce`、`db52ac2f3ffb50d866f385fa8a58c258cc7045bac9b8c29211b9f3ac6abf214c`。这些是本机构建值，不冒充 GitHub 标签工作流产物或 digest。安装包仍未签名，构建报告既有 `AngleSharp 1.3.0` 的 `NU1902` 告警。
- `v0.8.0` GitHub 标签工作流和公开 Release 后续均通过；已分别下载远端 ZIP/Setup，比对其 GitHub digest。远端附件与本地构建大小、SHA 不必相同；远端结果与本地 Word/安装生命周期验收分开记录，详见 [发布记录](../development/github-release.md)。

### 0.7.0 本机验收（2026-09-27）

- 安装前确认没有已登记的 MD2Word 安装或运行进程，使用 `output/setup-install-test-0.7.0` 与 `output/e2e-setup-0.7.0-user-data`，未覆盖用户安装。
- `npm run package:win` 完成四种产物，最终 `release` 只有便携目录、Portable EXE/ZIP、Setup EXE、公开模板容器和两份离线说明；无 `win-unpacked` 或 builder 元数据。此前本机的 `win-unpacked.tmp` 重命名 `EPERM` 由已安装 Electron 分发目录复制规避，最终便携目录的同类 `EPERM` 由复验后复制回退解决。Electron 43 在干净 `npm ci` 后尚无 `dist` 时，打包先用包内安装脚本准备官方二进制；已用无 `dist` 的隔离副本实测。旧本地 0.6.1 产物保存在忽略目录。
- 便携目录 `e2e/electron-packaged.spec.ts` 通过：内置 Worker、能力版本 0.7.0、窄 preload API、参考模板校验与必需环境就绪。
- Setup 首次静默安装返回 0，安装标记存在；`e2e/electron-installed.spec.ts` 通过：首次种子、合成模板导入、安装目录不变、删除参考模板后重启不恢复且导入模板保持默认。
- 对同一专用目录覆盖安装返回 0，隔离用户模板库 4 个文件的 SHA-256 全部不变；`e2e/electron-installed-retention.spec.ts` 再次启动并确认唯一导入模板仍为默认。
- 使用保留的公开 0.6.1 Setup 在另一专用目录安装，`e2e/electron-upgrade.spec.ts` 的 seed 阶段导入合成模板、设为默认并删除参考模板；随后用 0.7.0 Setup 覆盖安装返回 0，4 个模板库文件 SHA-256 不变。verify 阶段确认应用版本为 0.7.0、模板校验通过且唯一导入模板仍为默认，已删除的参考模板未恢复。
- 专用卸载器 `/currentuser /S` 返回 0，安装 EXE、注册项、桌面/开始菜单快捷方式均不存在；4 个用户模板文件仍存在且 SHA-256 未变。
- 两组专用安装均已卸载且用户模板数据保留。本节签收本机 0.6.1→0.7.0 跨版本验收；0.7.0 GitHub 公开发布另见 [发布记录](../development/github-release.md)，不能用远端 CI 代替本机安装/Word 验收。安装包仍未完成代码签名，构建仍报告既有 `AngleSharp 1.3.0` 的 `NU1902` 告警。

### 0.6.0 历史验收（2026-09-09）

2026-09-09，0.6.0 实际结果：

- `npm run package:win` 成功生成便携目录、ZIP、Portable EXE、Setup EXE 及配套模板/离线说明。最终包关闭未配置的自动更新服务，不含 `app-update.yml` 或多余 blockmap。
- 首次静默安装返回 `0`，安装标记正确，桌面与开始菜单快捷方式均创建。
- 安装版 E2E 通过：用户数据目录隔离、首次种子复制、实际模板导入、安装目录不变、删除参考模板后重启不恢复、默认模板保持。
- 安装版的四页/安全边界与真实 Word/Mermaid 转换两项 E2E 通过；便携目录版的启动/资源/唯一参考模板 E2E 通过。
- 使用最终 Setup 对测试安装执行同版本覆盖安装，返回 `0`；隔离模板库 4 个文件的 SHA-256 全部不变，升级后启动仍保持唯一导入模板和默认状态，安装后的应用归档与最终构建一致。这是同版本覆盖安装验证，尚无旧版 Setup 可供跨版本升级验证。
- 通过测试目录自身的卸载器执行 `/currentuser /S`，返回 `0`；应用、安装注册与测试快捷方式已清理，4 个用户模板文件哈希仍不变。仅操作本次专用安装，未清除用户其他数据。
- 类型检查、Lint、101 项 Vitest（24 个文件）、Worker 常规测试 126 通过/11 跳过、协议 smoke 和生产构建通过。开发桌面的真实转换、九张截图生成及目标尺寸检查通过，截图已逐张复核。

Setup 与 Portable EXE 仍为 `NotSigned`；Worker 构建仍报告既有 `AngleSharp 1.3.0` 的 `NU1902` 告警。本次未升级依赖，未重跑全量 Word/Mermaid 双门控或复杂 DOCX/PDF 专项，不把此前 0.5.0 的完整专项结果记作 0.6.0 新结果。
