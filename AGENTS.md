# MD2Word 项目协作规则

## 项目定位

- 当前正式里程碑是 **Desktop MVP 0.7.0**：Windows 桌面应用；`v0.7.0` 已公开发布。当前开发分支新增 Browser Review Bridge，不属于已发布附件。
- 正式产品只面向 Windows，链路为 `React Renderer -> Electron Main (Node.js) -> C# Word Worker -> Word COM`。
- Renderer 通过统一 `AppAdapter` 访问后端；不得把独立 mock 的模拟转换、环境检测或文件选择描述成真实 Electron/C# 能力。

## 开始工作前

1. 阅读 [需求](doc/requirement/requirements.md) 与 [UI 规格](doc/requirement/ui-spec.md)。
2. 涉及进程、IPC、模板存储或转换协议时，阅读 [Electron/C# 架构](doc/architecture/electron-csharp.md)。
3. 涉及 WordDOM 兼容性时，阅读 [旧链路审计](doc/architecture/legacy-pipeline-audit.md)，不要只凭 Pandoc 或 Word 经验推断旧行为。
4. 涉及原型状态、截图或验收时，阅读 [UI 原型说明](doc/uiPrototype/README.md)。

## 界面设计与实操审查

- 涉及界面修改时采用 Windows 11 Fluent 风格：浅色 Surface、适度圆角与留白、中性色、系统风格强调色；保持桌面工程工具的信息密度，不套用 Web 仪表盘布局。
- UI 完成后启动 Browser Review Mode，使用内置浏览器逐页实际点击、输入、滚动并审查所有受影响的按钮、菜单、弹窗及 Empty、Loading、Error、Disabled、成功和取消状态。响应式覆盖 Electron 最小窗口 1100x720、目标 1280x800、典型 1440x900，以及受影响断点两侧。
- 默认 `npm run dev` 启动真实 Browser Review Bridge；只有难稳定复现的特殊状态才用 `npm run dev:mock` / Fixture，并在记录中标明。Browser Review 负责 Renderer 目视与交互；原生对话框、文件系统、更新外链与 Worker 仍须在真实 Electron 上验证。自动 E2E 不能代替目视和实操审查。
- 发现可见问题后直接修复并复审。功能和按钮检查结果、截图及未覆盖项写入 [UI 验收说明](doc/uiPrototype/README.md)，不可把未执行的项目记作通过。

## Word 成品审查

- 涉及转换、模板或 CSS 输出效果时，用公开合成夹具生成真实 DOCX；同时检查 Open XML 结构与 Word 导出的逐页 PDF/截图。关注标题、编号、图题、表格、图片、页眉页脚和分页，不凭单测通过宣称视觉正确。
- 使用真实用户模板或文档进行私有验收时，所有输入、DOCX、PDF 与截图仅留在仓库外或被忽略的 `output/`，不得提交或写入普通日志。

## Agent 协作

- 主 Agent 负责确认需求边界、集成改动、运行验收和审查 Word 成品。独立、边界清楚且不写同一文件的研究或审查任务才适合派发 subagent；交接时写明目标、文件范围、完成标准与证据。
- 跨 Renderer/Main/Worker 的契约、版本和发布安全由主 Agent 最终核对。协作实践与提示词范例见 [Agent 协作说明](doc/development/agent-collaboration.md)。

## 不可违反的规则

### 数据与文件安全

- **禁止提交真实 DOCX、真实 Markdown 正文、客户图片、转换产物或旧仓库业务资料。** 包括但不限于 `*.docx`、`*.doc`、`*.worddom.html`、`*.worddom.assets/`、`output/`、用户模板和用户源文档。
- 不从旧链路目录复制模板、源文档、图片或产物。[旧链路审计](doc/architecture/legacy-pipeline-audit.md) 只保留脱敏的通用行为结论；不得记录来源目录或让应用依赖旧路径。
- 如测试确实需要文档夹具，只能使用明确标注为合成、无业务内容、可公开的最小夹具，并先在需求/测试文档中说明用途；默认仍不提交 DOCX。
- 不记录密钥、令牌、用户名、私人绝对路径或文档正文到源码、日志、截图和普通文档中。公开源码、示例、测试夹具、截图及随包说明必须使用中性名称，不保留组织标识、业务来源路径或私有样例内容。

### 架构与安全边界

- Renderer 不得启用 Node 集成，不得直接访问文件系统、启动进程或操作 Word。
- Electron 必须启用 `contextIsolation` 与 `sandbox`，关闭 `nodeIntegration`；preload 只暴露窄接口，不得暴露原始 `ipcRenderer`。
- Browser Review Bridge 只在未打包开发态启动，绑定回环地址，经 Vite 同源代理、临时令牌和 Origin 校验调用与 IPC 相同的 Main 服务处理器；打包文件和生产 Renderer 不得包含 Bridge 实现或入口。Bridge 不接收浏览器提供的任意本机文件路径。
- 所有 IPC 入参必须在 Electron Main 再次校验；文件选择必须来自受控原生对话框或已登记模板记录。
- Node.js 只负责窗口、对话框、模板库、串行队列、日志和 Worker 生命周期，不直接使用 `winax` 等原生 COM 模块。
- Word COM 只在独立 C# Worker 的 STA 线程中运行。转换任务全局串行；成功、失败、超时和取消都必须关闭文档、退出 Word 并释放 COM 引用。
- 模板 DOCX 与 CSS 是一个不可拆分的配置单元。导入、编辑和转换前都要校验二者及其书签/样式契约。

### 浏览器审查与正式实现

- 正式 Renderer、真实 Browser Review 与特殊状态 mock 共用 `AppAdapter` 页面状态机：Electron 使用窄 preload/IPC，Browser Review 使用开发态 Bridge 访问同一 Main/Worker 服务；独立 mock 只维护合成数据和浏览器本地存储。
- 共用契约 `TemplateProfile`、`TemplateValidationReport`、`ConversionRequest`、`ConversionEvent`、`ConversionResult`、`EnvironmentStatus` 的语义应与架构文档一致。
- 原型中的延迟、进度、日志、路径和环境状态均须明确标注为“演示”或“模拟”。
- 接入或调整原生能力时先扩展共用契约与 Main 校验，再分别适配 IPC 和开发态 Bridge；不在页面中直接添加运行时访问。

## 文档必须实时同步

文档是交付物，不是事后补记。以下改动必须在同一提交中同步：

| 变更 | 必须同步的文档 |
|---|---|
| 产品范围、用户流程、验收口径 | `doc/requirement/requirements.md` |
| 页面结构、文案、交互或状态 | `doc/requirement/ui-spec.md`、`doc/uiPrototype/README.md` |
| IPC、共用类型、Worker 协议、存储或安全边界 | `doc/architecture/electron-csharp.md` |
| WordDOM 行为、模板书签、CSS 映射或兼容结论 | `doc/architecture/legacy-pipeline-audit.md` |
| 可见 UI 有实质变化 | 更新对应截图及截图清单 |
| 验证命令或结果变化 | `README.md` 与 `doc/uiPrototype/README.md` |

不得让文档继续声称已通过一个没有运行的检查，也不得把待办能力写成已完成能力。

## 版本与 Git

- `package.json` 是应用版本的单一真源；当前版本为 `0.7.0`。版本变化时同步 README 和里程碑说明。
- 使用语义化版本：修复为 patch，向后兼容功能为 minor，破坏性契约变化为 major；原型阶段仍需记录破坏性变更。
- 默认分支为 `master`，功能开发使用短期分支。提交信息采用 Conventional Commits，例如 `feat: initialize md2word UI prototype`。
- 提交前至少运行类型检查、Lint、单元测试和生产构建；UI 变化还要完成浏览器交互与目标尺寸视觉检查。
- GitHub 仓库按用户授权保持 **Public**。推送及权限变更后复核可见性，不得自行改变公开策略；源码、文档、截图和 Release 必须经过脱敏检查，不得发布真实业务模板、源文档或转换产物。
- 不强推 `master`，不重写已共享历史，不提交凭据或真实文档。发现疑似敏感文件时先停止提交并清理历史风险。

## 完成标准

- 需求范围内的行为可演示，错误、空态、取消和成功状态均有合理反馈。
- 相关检查通过，且没有把真实转换能力误报为已实现。
- 代码、契约、README、需求、UI 说明、架构和截图相互一致。
- 工作区中没有真实模板、转换产物、秘密信息或旧仓库绝对路径硬编码。
