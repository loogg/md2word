# MD2Word 项目协作规则

## 项目定位

- 当前里程碑是 **Desktop MVP 0.6.0**：Windows 桌面应用，同时保留浏览器交互预览。
- 正式产品只面向 Windows，链路为 `React Renderer -> Electron Main (Node.js) -> C# Word Worker -> Word COM`。
- 不得把浏览器模拟转换、模拟环境检测或文件选择描述成真实 Electron/C# 能力。

## 开始工作前

1. 阅读 [需求](doc/requirement/requirements.md) 与 [UI 规格](doc/requirement/ui-spec.md)。
2. 涉及进程、IPC、模板存储或转换协议时，阅读 [Electron/C# 架构](doc/architecture/electron-csharp.md)。
3. 涉及 WordDOM 兼容性时，阅读 [旧链路审计](doc/architecture/legacy-pipeline-audit.md)，不要只凭 Pandoc 或 Word 经验推断旧行为。
4. 涉及原型状态、截图或验收时，阅读 [UI 原型说明](doc/uiPrototype/README.md)。

## 不可违反的规则

### 数据与文件安全

- **禁止提交真实 DOCX、真实 Markdown 正文、客户图片、转换产物或旧仓库业务资料。** 包括但不限于 `*.docx`、`*.doc`、`*.worddom.html`、`*.worddom.assets/`、`output/`、用户模板和用户源文档。
- 不从旧链路目录复制模板、源文档、图片或产物。[旧链路审计](doc/architecture/legacy-pipeline-audit.md) 只保留脱敏的通用行为结论；不得记录来源目录或让应用依赖旧路径。
- 如测试确实需要文档夹具，只能使用明确标注为合成、无业务内容、可公开的最小夹具，并先在需求/测试文档中说明用途；默认仍不提交 DOCX。
- 不记录密钥、令牌、用户名、私人绝对路径或文档正文到源码、日志、截图和普通文档中。公开源码、示例、测试夹具、截图及随包说明必须使用中性名称，不保留组织标识、业务来源路径或私有样例内容。

### 架构与安全边界

- Renderer 不得启用 Node 集成，不得直接访问文件系统、启动进程或操作 Word。
- Electron 必须启用 `contextIsolation` 与 `sandbox`，关闭 `nodeIntegration`；preload 只暴露窄接口，不得暴露原始 `ipcRenderer`。
- 所有 IPC 入参必须在 Electron Main 再次校验；文件选择必须来自受控原生对话框或已登记模板记录。
- Node.js 只负责窗口、对话框、模板库、串行队列、日志和 Worker 生命周期，不直接使用 `winax` 等原生 COM 模块。
- Word COM 只在独立 C# Worker 的 STA 线程中运行。转换任务全局串行；成功、失败、超时和取消都必须关闭文档、退出 Word 并释放 COM 引用。
- 模板 DOCX 与 CSS 是一个不可拆分的配置单元。导入、编辑和转换前都要校验二者及其书签/样式契约。

### 原型与正式实现

- 原型数据通过 mock adapter 与浏览器本地存储维护；页面不得直接耦合未来 Electron IPC。
- 共用契约 `TemplateProfile`、`TemplateValidationReport`、`ConversionRequest`、`ConversionEvent`、`ConversionResult`、`EnvironmentStatus` 的语义应与架构文档一致。
- 原型中的延迟、进度、日志、路径和环境状态均须明确标注为“演示”或“模拟”。
- 后续接入 Electron 时优先替换 adapter，不重写页面业务状态机。

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

- `package.json` 是应用版本的单一真源；当前版本为 `0.6.0`。版本变化时同步 README 和里程碑说明。
- 使用语义化版本：修复为 patch，向后兼容功能为 minor，破坏性契约变化为 major；原型阶段仍需记录破坏性变更。
- 默认分支为 `master`，功能开发使用短期分支。提交信息采用 Conventional Commits，例如 `feat: initialize md2word UI prototype`。
- 提交前至少运行类型检查、Lint、单元测试和生产构建；UI 变化还要完成浏览器交互与目标尺寸视觉检查。
- GitHub 仓库必须保持 **Private**。首次推送及权限变更后都要复核可见性；禁止为了方便分享而改成 Public。
- 不强推 `master`，不重写已共享历史，不提交凭据或真实文档。发现疑似敏感文件时先停止提交并清理历史风险。

## 完成标准

- 需求范围内的行为可演示，错误、空态、取消和成功状态均有合理反馈。
- 相关检查通过，且没有把真实转换能力误报为已实现。
- 代码、契约、README、需求、UI 说明、架构和截图相互一致。
- 工作区中没有真实模板、转换产物、秘密信息或旧仓库绝对路径硬编码。
