# MD2Word 产品需求

## 1. 文档信息

| 项目 | 内容 |
|---|---|
| 产品名 | MD2Word 文档生成器 |
| 当前里程碑 | Desktop MVP 0.6.1 |
| 目标平台 | Windows |
| 当前实现 | React Renderer + Electron Main (Node.js) + 独立 .NET 8 C# Word Worker |
| 转换依赖 | 外部 Pandoc + Microsoft Word COM + Open XML |
| 文档状态 | 0.6.1 模板制作指南分发修复，2026-09-09；保留历史验收记录 |

## 2. 背景与目标

现有 WordDOM 工作流需要在命令行中手工选择 Markdown、Word 模板、CSS 和输出路径。默认样式链路 更接近统一默认样式，自定义样式链路 又按模板细分 CSS，长期使用容易选错目录或把模板与样式搭配混淆。

产品目标是把这组选择收敛到本地桌面界面：

1. 用户从已校验模板中选一个配置。
2. 用户选择或拖入一个 Markdown 文件。
3. 用户通过系统“另存为”选择 DOCX 输出路径。
4. 应用串行执行转换并持续显示阶段、日志和结果。
5. 用户可导入新的 Word 模板，并将 DOCX 与内置或自定义 CSS 绑定保存。

本产品延续 WordDOM 的自由度，不把“换成纯 Pandoc reference-doc”作为目标。Pandoc/Lua 仍负责 Markdown 结构转换，最终版式由模板、CSS、Word COM 与 Open XML 共同收口。

## 3. 用户与核心场景

主要用户是需要重复产出使用说明书、技术报告等规范 Word 文档的本机用户。用户熟悉文档内容，但不应再记忆脚本位置、模板路径、CSS 参数或命令行顺序。

核心场景：

- 日常生成：用默认模板把一个 Markdown 生成 DOCX。
- 模板切换：同一 Markdown 分别按“使用说明书”或“技术报告”规范生成。
- 模板维护：导入新 DOCX，选择默认 CSS 或专属 CSS，完成校验后加入模板库。
- 排障：转换前查看 Word、Pandoc、Worker、Mermaid 与模板状态；失败时获得可行动的错误信息。

“上传”仅表示把本机文件交给本地应用，不将文档发送到网络服务。

## 4. 当前里程碑范围

### 0.6.1 文档分发修复

新增随包 `MD2Word-模板制作指南.md`，覆盖从空白 DOCX 制作与修改公开参考模板的操作步骤。目录版、ZIP、Setup 中均在 EXE 同级提供，单文件 Portable 的配套文件放在 `release` 根。此 patch 不改变转换算法、IPC、模板存储或书签/样式契约；`v0.6.0` Release 保持不变，通过新标签发布 `v0.6.1`。

### 0.6.0 安装分发增量

0.6.0 继承 0.5.0 转换契约，新增 NSIS Setup 安装版。`package:win` 必须同时交付便携目录、Portable ZIP、单文件 Portable EXE 和 `MD2Word-<version>-win-x64-setup.exe`，不减少现有交付形式。安装向导默认面向当前用户，支持选择目录和桌面/开始菜单快捷方式，安装完成不自动启动。

Setup 的受管模板库位于 `userData/templates`；首次启动复制安装包内的公开模板包，已有包、用户导入、编辑和空索引不覆盖。复制单包必须原子发布并拒绝链接/特殊文件。升级与默认卸载保留用户模板。用户通过“打开模板库”添加其他完整包，不修改安装目录中的模板种子。安装身份由 NSIS 写入的标记确定，不接受 Renderer 提供路径或模式。用例与实际结果见 [Setup 验收](../testing/setup-installation.md)。下文标明 0.5.0 的记录为继承基线，0.6.0 验证单独记录。

### 4.1 包含

- 四个桌面导航页面：生成 Word、模板管理、能力说明、环境与设置；页面通过统一 adapter 同时支持 Electron 真实后端和浏览器 mock 预览。
- Electron 安全壳、受控 preload API、Windows 原生文件/另存为对话框、opaque 文件句柄、IPC 来源与 schema 再校验。
- Main 管理的模板库：DOCX/CSS 隔离复制、Worker 校验、fingerprint、原子索引更新、导入回滚、增删改和默认模板。
- Main 全局 FIFO 单任务队列、每任务模板快照与临时目录、阶段/日志/取消/结果事件、最终输出原子替换及受控打开/定位。
- .NET 8 Windows Worker 的 JSON/JSONL 协议、环境诊断、模板校验和真实转换；Word COM 只在专用 STA 线程中运行并显式清理专用 Word 实例。
- 版本化能力清单：同一份 JSON 描述语法/转换能力、Front Matter、模板书签与 CSS 角色、限制及工具依赖；Worker 可查询，桌面页面可搜索，模板校验问题可跳转到对应能力，且自动生成随包离线 Markdown。
- Pandoc standalone HTML 与四个版本锁定的 Lua filter：admonition 语义、表格列宽意图、标题层级、图号。
- admonition 基础 Word 视觉：保留 CSS/Word 解析出的段落样式，仅为普通引用及 note/caution/warning/danger 语义段落追加固定背景色和 2.25pt 左侧单线。
- 普通 fenced 代码块在 Word 中使用目标代码样式，并保留源代码的硬换行、空行、行首缩进、连续空格和 tab 基线。
- Mermaid fenced 代码块本地渲染为 PNG/SVG；相邻 `caption` / 历史 `cation` 注释绑定、失败模式、系统浏览器启动加固、受管浏览器回退和最终统一图号收口。
- 模板正文书签装配、成品正文书签契约验证、CSS `mso-style-name` 显式映射与 Word 原生样式回退、HTML 直接格式重置及 Open XML 段落/行内代码角色样式收口。
- Markdown 有序/无序列表的 Word 原生编号保留和样式重绑，包括常用符号、非 1 起始、分离重启、二至三级混合嵌套、tight/loose、多段落条目及列表内代码/图片结构。无序目标样式没有编号定义时，生成态项目符号和缩进必须与 Word 手工项目符号库一致，不得只落正文样式后保留 HTML 导入定义。
- 显式 `title`、`subtitle` 与历史键 `manul_version_tables` 的 front matter 读取和模板封面/版本表更新。
- Front Matter 的 Word 原生标题编号与表格表头控制：`heading_numbering`、`heading_numbering_start_base`、`word_heading_numbering.level1..4`、`word_repeat_table_headers`。
- 正文书签范围内无合并普通表格的版心宽度、两列 24%/76% 固定网格，以及规则矩形 HTML `rowspan` / `colspan` 表格的合并结构保留、版心宽度和 AutoFit 收口；两类表格均使用连续单线边框、单元格内边距/垂直对齐、紧凑段落和表头视觉收口；相邻独立表格之间的结构性空段落使用 CSS 解析后的正文样式，不创建专用分隔样式。
- 相对本地图片嵌入（包括 Word 对非 ASCII 文件名 URI 的二次转义恢复），以及正文书签范围内随文图片按 section 版心和正段落缩进进行的等比缩小、自动扩展行距收口。
- Markdown/HTML 内部链接的 ASCII 安全 Word 书签生成、同目标链接归并重写及成品链接—书签唯一匹配校验。
- 浏览器 mock 仍保留两个合成演示模板、本地存储、模拟任务和模拟环境，且与真实桌面能力明确区分。
- Windows x64 候选直接构建到 `release`，交付未压缩 Portable 目录、单文件 Portable EXE、Portable ZIP、Setup EXE 与便携 EXE 同级 `templates/reference`，文件名包含版本和 `x64`。自定义模板分发复用同一可执行文件，把仓库外完整私有模板包目录手工复制为 EXE 同级 `templates/<package-name>`，包名可自定义；私有模板不参与构建，也不得进入基线 ZIP 或 Git。

### 4.2 不包含

- 旧 WordDOM 的多列复杂比例、不规则网格、合并表格高级固定列宽及跨 Office 复杂表视觉，浮动/文本框/多栏/复杂表格单元格图片布局，admonition 的图标/文字色/内边距/圆角/连续多段盒子等丰富布局及完整视觉一致性。
- 全部 Mermaid 图型、复杂 SVG 和不同 Office 版本的逐页视觉一致性签收；当前正式契约只承诺通过输出安全校验的 PNG/SVG、图注/图号结构与失败语义。
- 使用真实业务模板/文档的公开夹具；旧链路最终一致性只能用仓库外的私有基准样本验收。
- 已签名的正式发布包；0.5.0 x64 Portable 候选已完成三种交付形态的打包，目录版已通过能力查询与资源 packaged E2E，但当前 EXE 的 Authenticode 状态仍为 `NotSigned`。
- 批量生成、历史任务、云同步、账号、团队模板库与跨平台支持。

## 5. 功能需求

### 5.1 全局框架

| ID | 需求 |
|---|---|
| FR-G01 | 左侧固定导航显示产品标识和四个页面入口；Electron 显示“桌面版/桌面运行”，浏览器 mock 显示“原型/交互原型”。 |
| FR-G02 | 页面切换不丢失当前选择与任务结果；Electron 模板由 Main 的模板索引恢复，浏览器演示模板由本地存储恢复。 |
| FR-G03 | 所有 mock 能力必须标注“模拟”或“演示”；真实 Worker 也必须展示尚未完成的兼容警告，不得把生成成功等同于完整 WordDOM 一致。 |
| FR-G04 | 破坏性操作有确认或明确撤销边界；按钮在不可执行时禁用并说明原因。 |
| FR-G05 | “能力说明”只展示当前安装 Worker 对应的版本化清单；浏览器 mock 导入同一清单。UI 与离线说明不得各自维护另一份能力事实。 |

### 5.2 生成 Word

| ID | 需求 |
|---|---|
| FR-C01 | 默认选中当前默认且校验可用的模板。生成页只常驻展示当前模板的紧凑摘要，通过“更换模板”打开选择弹层，不铺开整个模板库。 |
| FR-C02 | 支持拖入或选择单个 `.md` / `.markdown` 文件；其他类型给出明确错误。 |
| FR-C03 | 文件卡显示文件名、大小和可移除操作。Electron Main 保留源文件绝对路径以解析相对资源，Renderer 只接收 handle 和必要元信息。 |
| FR-C04 | 紧凑摘要显示名称、默认标识、校验状态、DOCX 与 CSS 模式；右侧“当前配置”显示最近校验时间、能力和问题摘要。无效模板可选中查看原因，但不能启动转换。 |
| FR-C05 | 每次点击“生成 Word”都先打开“另存为”；取消对话框不得创建任务、日志或临时文件。Electron 使用 Windows 原生窗口，浏览器 mock 使用明确标注的模拟窗口。 |
| FR-C06 | 输出扩展名固定为 `.docx`；覆盖已有文件必须由系统对话框确认。输入、模板和输出不得是同一文件。 |
| FR-C07 | 任务按阶段显示：排队、准备、元数据、Pandoc、Mermaid/资源（如需要）、Word 导入与模板装配、Word/Open XML 收口、清理、完成。 |
| FR-C08 | 显示有时间顺序的日志；普通用户看到可读信息，诊断详情可展开但不得包含文档正文或秘密信息。 |
| FR-C09 | 运行中可请求取消。Main 先向 Worker 发送优雅取消并等待清理；只有超时后才终止该 Worker，并提示检查 Word 进程。Worker 只兜底结束能确认由本任务创建的 Word PID。 |
| FR-C10 | 成功结果显示输出文件名与“打开文件”“在文件夹中显示”；失败显示错误原因、失败阶段和建议动作。 |
| FR-C11 | Electron Main 维护全局单任务队列；同时提交的任务排队，不允许并行操作 Word。 |
| FR-C12 | 模板选择弹层支持按名称、用途、DOCX 或 CSS 搜索，列表具有固定最大高度并内部滚动；模板数量不得持续推低 Markdown、生成按钮和任务状态。运行中或“另存为”打开时禁止切换模板。 |
| FR-C13 | fenced `mermaid` 代码块支持按模板默认值或任务选项生成 `png` / `svg`。只把代码块后下一个非空白节点为 `<!-- caption: ... -->` 的注释绑定为图注，兼容历史拼写 `cation`；正文、元素或其他注释会中断绑定。渲染成功且带绑定图注的图参与普通带图注图片共用的最终按章图号序列；无图注 Mermaid 只生成图片，失败降级代码块不占图号。生成的 Mermaid 图片必须以 `600px` 作为 Word 导入显示宽度并带 `max-width: 100%`、`height: auto` 约束，避免高倍率 PNG 按自然像素宽度溢出版心。 |
| FR-C14 | Mermaid `off` 不启动渲染并保留代码；`auto` 逐图渲染，单图失败保留对应代码和原相邻注释、返回稳定告警并继续其他图；`required` 在任一图失败或环境缺失时终止任务，且不得发布部分 DOCX。 |
| FR-C15 | Worker 读取显式 Front Matter 的标题编号与重复表头配置。标题编号必须使用 Word 原生多级列表，在目录/字段更新前应用；重复表头只作用于正文书签内的表格，不能改动封面、页眉页脚或模板固定表格。字段缺失时保持兼容默认值，类型、编号样式或格式非法时以 `FRONT_MATTER_INVALID` 阻止转换。 |
| FR-C16 | 普通 fenced 代码块必须映射为模板代码段落；每个源换行（包括空行）在同一段落内生成 Word 手动换行，行首空格、内部连续空格和 tab 展开不得被 HTML/Word 空白折叠。代码专项验收不能只检查段落样式名。 |
| FR-C17 | Markdown 行内代码必须作为独立 run 角色参与 CSS/Word 映射，不得保留 Word HTML 导入器生成的 `HTML Code`/`HTML 代码` 字符样式。CSS 未显式声明时继承已解析的正文角色；合成示例因此映射为 `示例 正文`。正文/表格中应直接继承该段落样式，有序列表等其他段落中应投影目标正文样式的计算字体属性，且不得改写整个段落样式。 |
| FR-C18 | 正文书签内无合并普通表格必须使用连续 Word 原生网格，不得残留 HTML 的表级或行级 `tblCellSpacing` 双线间隙；两列保持 24%/76%。逻辑网格规则的 HTML `rowspan` / `colspan` 表格必须保留 `w:vMerge` / `w:gridSpan`，使用 section 版心宽度和 Word AutoFit 列比例，并应用相同连续网格；两类表格的单元格均使用 4pt 上下/6pt 左右内边距、垂直居中、0 首行缩进和单倍行距，表头居中加粗、正文左对齐。不规则网格不得被强行改写。 |
| FR-C19 | Pandoc/Lua 完成标题层级重映射后，内部 `href="#..."` 必须只对实际存在且被引用的 HTML `id` 生成确定性 ASCII 安全 Word 书签；多个链接指向同一目标时共享一个书签，缺失目标保持源链接语义。Word 保存和 Open XML 收口后，每个生成链接必须唯一命中成品书签，否则以 `INTERNAL_LINK_FINALIZATION_FAILED` 阻止发布。 |
| FR-C20 | 每个普通 admonition 段落必须保留 CSS/Word 最终解析出的 `w:pStyle`，并按 `generic`、`note`、`caution`、`warning`、`danger` 仅追加固定 `w:shd` 与 `w:pBdr/w:left`。左线为 `single`、2.25pt、`space=0`；不得新增上/右/下边框，不得写入缩进、段前段后、行距、对齐、字体或字号。多段普通引用分别着同色；嵌套列表、表格和代码保持各自角色。只有开头合法“标签 + 冒号”进入语义配色，否则为普通灰色引用。 |

admonition 固定色板如下，Worker 不提供 CSS 覆盖项：

| 类型 | 背景色 | 左线颜色 |
|---|---|---|
| 普通引用 | `F8FAFC` | `64748B` |
| 说明/提示/NOTE | `EFF6FF` | `2563EB` |
| 注意/CAUTION | `FFFBEB` | `B45309` |
| 警告/WARNING | `FEF2F2` | `B91C1C` |
| 危险/DANGER | `FEE2E2` | `7F1D1D` |

### 5.3 模板管理

| ID | 需求 |
|---|---|
| FR-T01 | 模板列表展示名称、说明、DOCX 文件名、CSS 模式、默认标识、校验状态和最后更新时间。 |
| FR-T02 | 添加模板至少需要名称和 `.docx` 文件。CSS 可选“内置默认”或“自定义”；自定义模式必须选择可读的 `.css` 文件。 |
| FR-T03 | DOCX 与 CSS 作为一个 `TemplateProfile` 保存，不能独立替换后继续沿用旧校验结果。 |
| FR-T04 | 添加/编辑时检查重名、文件扩展名、可读性、正文书签顺序、CSS 映射样式和模板能力。CSS 样式名先按 style ID、名称与 alias 精确解析；精确候选不存在时，再按 Word 内建样式的中英文等价名解析（例如 `正文` ↔ `Normal`），且仍必须唯一命中。错误阻止保存，警告允许用户确认后保存。 |
| FR-T05 | 可编辑名称、说明、DOCX、CSS 与 Mermaid 默认值。DOCX/CSS 改变后必须立即使旧校验失效并重新校验。 |
| FR-T06 | 可将一个有效模板设为默认；任一时刻最多一个默认模板。删除默认模板后优先把第一个有效模板设为默认，无有效模板则进入无默认状态。 |
| FR-T07 | 删除前显示确认，说明只删除应用管理目录中的副本，不删除用户原文件。最后一个模板允许删除，但生成页进入空态。 |
| FR-T08 | Electron 导入时把 DOCX/CSS 规范化复制为 Main 管理的 `templates/user/<template-id>/template.docx` 与 `style.css`，不继续引用原位置。每个模板包只保留一个精简 `index.json`，不保存完整 validation、styleMappings 或稳定 `profile.json`；Main 加载时重新校验文件并在内存恢复 Renderer 所需完整结果。开发运行与 Setup 安装版的模板包容器位于应用 `userData/templates`；打包后的 Windows Portable 形态统一使用 `MD2Word.exe` 同级 `templates`。Main 必须遍历该容器的直接子目录并合并其中 `index.json`，同时拒绝跨包重复 ID 或多个默认模板；旧版 version 1 根级/子包索引只读兼容。 |
| FR-T09 | 校验结果至少区分 `valid`、`warning`、`invalid`，并为每个问题提供稳定错误码、目标、严重级别和说明；可解释问题应携带稳定 `capabilityId`，页面提供到对应能力条目的入口。 |
| FR-T10 | Windows 打包只提供 `package:win` 基线命令：不读取私有路径，把 `resources/templates` 作为完整公开模板包容器，遍历每个带 `index.json` 的直接子目录，经 Worker 校验索引登记的每一组 DOCX/CSS，并只把索引及登记文件原样复制到 `release/templates`，不得临时生成或改写索引。可手工增加明确无业务内容的完整公开包。自定义模板分发由操作人把仓库外完整模板包目录复制为 EXE 同级 `templates/<package-name>`；应用启动时自动发现，无需重新打包，包名可自定义。所有暂存、私有包和 release 均不得进入 Git；源码构建完成时成品 `templates` 必须只包含已审核的源码公开包，之后手工加入的私有包不得上传。 |

公开示例与合成测试只使用中性样式名；这些名称不是模板契约。既有用户 DOCX/CSS 继续按原样式名动态解析，脱敏不迁移或修改受管模板、用户原件及转换协议。

模板使用说明必须提供可操作的制作教程：从公开参考模板修改和从空白 DOCX 制作两条路径，覆盖插入点正文书签、段落样式、CSS 显式映射、可选封面/版本表及合成试转。教程随应用作为离线 Markdown 分发，不改变既有模板契约。教程示例只使用公开参考 DOCX 和运行时提取的合成 Markdown/CSS，产物留在忽略目录中。

最低模板契约：

- 必须有按顺序出现的 `MANUAL_BODY_START` 与 `MANUAL_BODY_END`。
- `MANUAL_COVER_TITLE`、`MANUAL_COVER_SUBTITLE` 和版本表书签是能力型契约：Markdown 显式使用相应 front matter 时才变成必需。
- CSS 中出现的 `mso-style-name` 是显式覆盖，必须能在模板中唯一解析。校验先尊重 style ID、名称与 alias 的精确候选；只有精确候选不存在时，才允许按 Word 内建样式的中英文等价名解析，例如 CSS `正文` 唯一解析到包内 `Normal`。自定义 `正文1` 等近似名称不属于内建等价名；显式引用不存在或歧义的 Word 段落样式应阻止转换，不能静默改用其他样式。
- CSS 未配置某个语义角色时，Worker 必须从模板中解析 Word 原生回退，不得把 Pandoc/HTML 导入段落样式当作成品样式。覆盖角色至少包括正文、有序/无序列表、H1-H6、图题、表题、代码块、行内代码、表格单元格、admonition 和图片段；回退来源在协议中标记为 `word-fallback`。行内代码无显式映射时继承已解析的正文角色。模板校验阶段尚不知道源文档会使用哪些标题级别：若 CSS 未映射某级标题且模板缺少该级 Word 原生样式，报告条件性 warning 并允许确认保存；Pandoc 完成 `heading_base_level` 重映射后，转换必须扫描实际 HTML 标题级别，只有实际使用缺失级别时才以 `WORD_NATIVE_STYLE_FALLBACK_MISSING` 阻止发布。CSS 显式引用不存在/歧义样式、非条件性角色缺少回退或实际使用的标题级别缺少回退仍须阻止转换。合成示例 CSS 可显式覆盖为 `示例 正文`、`示例 有序列项`、`示例 标题*`、`示例 图示` 和 `示例 代码`。
- Word COM 保存模板装配结果后可能重编号、合并或本地化段落 style ID。Open XML 收口前必须把预检得到的每个角色样式重新绑定到成品当前 `styles.xml`：依次按原 style ID、名称、alias 和等价的中英文 Word 内建名称解析，最终写入的每个 `w:pStyle` 必须存在对应样式定义。无法恢复时以 `WORD_STYLE_REBIND_FAILED` 阻止发布，多个候选时以 `WORD_STYLE_REBIND_AMBIGUOUS` 阻止发布，不能继续写入悬空 style ID 后让 Word 显示成默认正文。
- 成品正文范围不得残留角色标记、标记字符样式引用、`manual-*` 等转换临时段落样式或覆盖目标样式的 HTML 直接段落格式。Word 可能在跨页表格中把一个标记拆到多个 `w:t` / `w:r`，并在其间插入 `w:lastRenderedPageBreak`；清理必须按段落拼接可见文本后跨 run 定位，仅移除标记及其字符样式，保留分页提示。收口后仍有可见标记或标记字符样式引用时，必须以 `ROLE_MARKER_FINALIZATION_FAILED` 阻止发布。H1-H6 与代码块还必须清除导入字体、字号和字距；普通角色应清除 Pandoc/HTML 导入的字体提示。粗体、斜体和超链接保留 Word 字符级语义；行内代码只对自身 run 应用解析后的目标格式，不得错误提升或改写所在段落样式，也不得保留 `HTML Code`/`HTML 代码`。
- 内部链接目标不能依赖 Word 对非 ASCII Pandoc `id` 的隐式转换。HTML 归一化必须插入 ASCII 安全命名锚点并重写相应 `href`；成品中生成链接数量不得少于 Word 导入前的已重写链接，生成书签名称必须唯一，所有生成超链接都必须具有唯一对应书签。
- Markdown `1.` 和 `-` 生成的条目必须保持为 Word 原生编号/项目符号列表：目标段落带解析后的 `w:pStyle`，同时保留 `w:numPr` 与嵌套层级，不得把 `1.` 或 `-` 写成普通正文字符。有序目标样式若自带 Word 编号定义，成品必须采用该定义对应层级的编号文本、字体与缩进，同时保留 Markdown 起始号、分离重启及有序/无序混合层级；不能只满足样式名相同。派生编号定义必须具有独立 `w:nsid` / `w:tmpl` 身份，避免桌面 Word 复用 HTML 导入编号外观；套用有序样式的 loose continuation 必须显式抑制伪编号。
- 无序目标样式没有编号定义时，Word 原生项目符号必须使用 `Wingdings F06C / F06E / F075` 循环、440 twip 悬挂和 220 twip 级间距，并把目标段落样式的正首行缩进作为一级标记基点。目标样式正首行缩进为 21pt 时，三级验收值为标记 21/32/43pt、文字 43/54/65pt，不能沿用 Pandoc/HTML 的 0/36/72pt 级差。
- `heading_numbering` 必须是布尔值，默认 `true`；设为 `false` 时正文标题不应用 Word 多级编号。
- `heading_numbering_start_base` 必须是正整数，默认 `1`；它按层级映射后的一级标题计数，从第 N 个一级标题开始编号，并同时跳过此前一级标题下的二至四级标题。
- `word_heading_numbering` 可按 `level1` 至 `level4` 配置 `number_style` 与非空 `format`。`number_style` 支持 `decimal`、`upper_roman`、`lower_roman`、`upper_letter`、`lower_letter`；未提供的层级分别回退到 `%1.`、`%1.%2`、`%1.%2.%3`、`%1.%2.%3.%4` 的十进制默认值。
- 标题应用 CSS 显式映射或 Word 原生回退样式时必须清除会覆盖模板字体、字号、字距及段落格式的 HTML 导入直接格式；参与编号的标题在成品中必须共享同一 `numId`，编号规范化完成后才可更新目录/字段。
- `word_repeat_table_headers` 必须是布尔值，默认 `true`。开启时正文每个表格首行规范化为唯一重复表头；关闭时移除正文表格各行的 `w:tblHeader`，模板正文书签外的表格保持不变。
- 无合并的普通正文表格必须固定到当前 section 页面宽度减左右页边距的版心；两列表格按 Lua 审计意图写入 24%/76% 的 `w:tblGrid`、单元格宽度和 fixed layout，避免 Word AutoFit 把中文键列压成单字竖排。逻辑列数一致、纵向合并连续且不使用 `gridBefore` / `gridAfter` 的规则矩形 HTML 合并表格，必须保留 Word 已导入的 `w:vMerge` / `w:gridSpan` 和 AutoFit 列比例，同时把 `w:tblW` 固定到版心。两类表格都必须清除表级及逐行 `w:tblCellSpacing` 和会覆盖统一网格的单元格直接边框，写入六向 0.5pt 黑色单线边框、4pt 上下/6pt 左右内边距和垂直居中；非列表单元格段落覆盖为 0 首行缩进、单倍行距，表头居中加粗、正文左对齐，同时保留映射后的 `w:pStyle`。正文书签内两个顶层表格之间若只有 Word HTML 导入产生的结构性空段落，Worker 必须保留该必要段落、清除 `w:vanish` 及导入直接格式，并把 `w:pStyle` 设置为 Word 保存后重新绑定的 CSS 正文目标样式；不得新增 `TableSeparator` 一类自定义样式，含文字、书签、分页符、域或图片的段落不得改写。逻辑列数不一致、纵向合并断裂或有行级网格偏移的不规则表格继续保留原结构并进入高级表格验收范围。
- 成品正文随文图片只在正文书签范围内按当前 section 页面宽度减左右页边距和正段落缩进执行等比缩小；不得放大小图或修改模板固定区域图片。是否需要缩小与缩小后的验收必须统一使用 1pt Word 布局容差：宽度不超过可用宽度 1pt 的图片保持原尺寸，超过该容差的图片仍须等比缩到可用宽度，防止 COM 浮点差异触发近似 `ScaleWidth` 百分比回写后反向放大。Word 对已经 URL 编码的非 ASCII 本地文件名再次转义时，Worker 必须只在目标通过本地文件、大小和格式校验后恢复一层编码，用 Word 原生内嵌图片替换不可缩放的链接占位图，保留替代文本/标题并在发布前清除恢复标记。图片段必须使用可随图片高度扩展的行距，不能继承会裁图的固定正文行距。浮动图片、文本框、多栏和复杂表格单元格宽度不在当前契约内。
- 转换完成后成品必须仍包含唯一、成对、有序的 `MANUAL_BODY_START` / `MANUAL_BODY_END`；契约失效时不得发布 DOCX。
- Mermaid 成功渲染且绑定图注时必须生成统一的 figure/caption 语义；Word 导入前必须规范化为相邻的独立图片段与独立图题段，图片段居中且不得包含图题文本，图题段不得包含 drawing，避免图号停留在图片右侧。图题经 CSS `figcaption` / `p.manual-figure-caption` 的 `mso-style-name` 解析到模板已有 Caption/“图注”等段落样式。默认开启 `figure_captions` 时按章生成 `图 X.Y <标题>`；显式设为 `false` 时关闭图注和图号。
- `MANUAL_TABLE_VERSION_HISTORY` 是现有版本表实例；元数据键 `manul_version_tables` 的历史拼写必须兼容，迁移期不得擅自更名。

### 5.4 能力说明

| ID | 需求 |
|---|---|
| FR-H01 | `resources/conversion/capabilities.json` 是能力元数据单一真源，包含清单 schema、产品版本、Worker 协议版本、简体中文标题/摘要、分类能力、Front Matter、模板契约、限制、工具依赖及内部迁移审计字段。所有公开 ID 在清单内全局唯一。 |
| FR-H02 | Worker 提供只读 `describe-capabilities` 命令并深校验清单；Main 缓存当前安装 Worker 返回值，通过 `capabilities.describe()` 暴露给 Renderer。请求不接收路径或任意参数，Renderer 不直接读取文件系统。 |
| FR-H03 | 页面显示当前产品/清单/协议版本、能力数量、Front Matter 数量和限制数量；可在“语法与转换”“Front Matter”“模板契约”“边界与环境”间切换，并按名称、ID、语法或元数据搜索。状态必须区分支持、条件支持、有限支持和未承诺。 |
| FR-H04 | 从模板校验问题进入能力页时，关闭编辑窗口、切换到正确类别、聚焦并高亮对应条目；没有 `capabilityId` 的旧问题仍正常显示。 |
| FR-H05 | `scripts/generate-capability-docs.mjs` 从同一清单生成 `resources/docs/MD2Word-支持能力说明.md`；产品版本必须与 `package.json` 一致，生成文档漂移时构建失败。目录版/ZIP 将文档放在 `MD2Word.exe` 同级，单文件版在 `release` 根提供并列说明。 |

### 5.5 环境与设置

| ID | 需求 |
|---|---|
| FR-E01 | 展示 Windows、Microsoft Word、Pandoc、C# Worker 和 Mermaid 的可用性与版本/路径摘要；Mermaid 可用需同时具备 npx 与本机 Microsoft Edge 或 Google Chrome。 |
| FR-E02 | 环境状态区分正常、可选缺失、阻塞和检查失败；Word、Pandoc 或 Worker 缺失会阻止真实转换。 |
| FR-E03 | Mermaid 为可选能力；模式为 `off` 时不阻塞，`required` 时缺少 npx/本地浏览器或渲染失败会阻止任务，`auto` 时产生告警并按单图降级策略继续。渲染固定使用 `@mermaid-js/mermaid-cli@11.16.0` 与 `puppeteer@25.3.0`，清除继承的 Windows 兼容层标记后优先复用本机浏览器；已检测浏览器启动失败时，显式准备对应 `chrome-headless-shell` 到 `userData` 私有持久缓存并重试一次。安装失败和 15 分钟安装超时分别使用稳定错误码。首次运行可能获取固定 CLI 包和受管浏览器二进制，但不得上传 Markdown、模板、图片或 Mermaid 图源。 |
| FR-E04 | 提供“重新检测”；检测有超时且不能冻结 Renderer。 |
| FR-E05 | 显示模板库位置，并在 Electron 中提供受控“打开目录”。Renderer 不获得任意路径读写权限。 |
| FR-E06 | 浏览器 mock 提供“重置演示数据”，仅清除本应用的 mock 存储并恢复初始模板；Electron 不显示该动作。 |

## 6. 模板校验问题码基线

| 错误码 | 级别 | 含义 |
|---|---|---|
| `TEMPLATE_FILE_UNREADABLE` | error | DOCX 不存在、不可读或不是有效 Open XML 包 |
| `CSS_FILE_UNREADABLE` | error | 自定义 CSS 不存在或不可读 |
| `BODY_BOOKMARK_MISSING` | error | 缺少正文起始或结束书签 |
| `BODY_BOOKMARK_ORDER_INVALID` | error | 正文结束书签早于起始书签 |
| `WORD_STYLE_MISSING` | error | CSS 要求的 Word 样式无法在模板中解析 |
| `WORD_NATIVE_STYLE_FALLBACK_MISSING` | warning / error | 模板预检发现未映射标题级别缺少 Word 原生回退时为条件性 warning；Pandoc 重映射后的实际 HTML 使用该级别时为转换 error。其他非条件性必需回退缺失仍为 error |
| `WORD_NATIVE_STYLE_FALLBACK_AMBIGUOUS` | error | CSS 未配置角色，但模板中有多个无法唯一确定的 Word 原生回退 |
| `WORD_STYLE_REBIND_FAILED` | error | Word 保存后预检样式的 ID/名称已变化，且无法在成品当前样式表中恢复唯一目标 |
| `WORD_STYLE_REBIND_AMBIGUOUS` | error | Word 保存后有多个样式可对应同一预检角色，禁止写入不确定的 `w:pStyle` |
| `ROLE_MARKER_FINALIZATION_FAILED` | error | Open XML 收口后仍有内部角色标记或标记字符样式引用，禁止发布成品 |
| `OPTIONAL_BOOKMARK_MISSING` | warning | 封面或版本表等可选能力不可用 |
| `DUPLICATE_TEMPLATE_NAME` | error | 模板名称与已有配置冲突 |
| `VALIDATION_STALE` | warning | DOCX/CSS 已变化，校验结果需要刷新 |

## 7. 非功能需求

- **本地优先**：Markdown、模板、图片、Mermaid 图源和输出文档只在本机处理，不上传到转换服务。首次 Mermaid 渲染可能由 npx 获取固定版本 CLI；系统浏览器启动失败时还可能显式获取固定 Puppeteer 对应的受管浏览器二进制。两类下载均不得携带文档内容；离线使用需有可启动的本地浏览器，或提前预热 CLI 与浏览器缓存。
- **安全**：Electron 使用沙箱、上下文隔离、严格 preload API、IPC schema 校验和 CSP；不在 Renderer 暴露 Node。
- **Mermaid 安全**：只通过参数数组调用固定的 CLI/Puppeteer；禁用 npm/Puppeteer 的隐式浏览器下载，仅在系统浏览器启动失败后显式安装受管 `chrome-headless-shell`。普通渲染启用 Mermaid strict 安全级别和不可达本地代理，限制图数量、图源、图注、输出大小与 180 秒渲染时间，校验 PNG/SVG 后才原子采用，拒绝 SVG 活动内容、事件处理器、外部引用和外部样式。
- **可靠性**：每个转换使用独立临时目录与独立 Worker；Word 任务全局串行；清理失败可诊断。
- **可恢复性**：模板库元数据原子写入；导入失败不留下半成品配置；输出先写临时文件，成功后再原子移动到目标。
- **可观察性**：每个任务拥有 `jobId`，日志带时间、阶段与级别；协议日志不混入 Worker 标准输出。
- **性能**：普通页面操作在本机即时响应；环境检查和文件解析不阻塞 UI；进度可以是阶段级，不能伪造精确百分比。
- **可访问性**：键盘可完成主要操作，焦点清晰，状态不只依赖颜色，交互控件有可读名称。

## 8. 0.5.0 Desktop MVP 验收标准

1. 1280x800 与 1440x900 下四个页面可用，无关键控件被遮挡；多模板数据不会撑高生成页模板区域或产生横向滚动。
2. 模板选择弹层可搜索、切换、显示无结果、固定高度滚动，并支持方向键、Enter、Escape 与关闭后焦点恢复。
3. Electron Renderer 无 Node/`require` 权限；preload 只暴露登记 API，文件和输出路径不跨越到 Renderer，Main 拒绝非法来源、句柄和路径组合。
4. 模板添加/编辑/删除/设默认、内置/自定义 CSS、真实 Worker 校验、warning 二次确认、索引原子写入和失败回滚可验证。
5. Markdown 选择与拖放均通过 Main 登记；不支持的文件显示错误。原生“另存为”取消不创建任务，确认后进入全局串行队列。
6. 环境缺失、模板无效和 fingerprint 过期会阻止生成；取消、超时、失败与成功均产生稳定终态并清理任务目录和专用 Word 进程。
7. 合成真实 Word 用例生成可读 DOCX；空 CSS 时所有实际使用的段落角色应用模板 Word 原生回退，显式 CSS 时应用其解析后的覆盖。缺少未使用的高阶标题样式只产生条件性模板 warning；测试必须证明 `heading_base_level` 重映射后不使用该级别时可转换，而实际使用时稳定失败。Word 保存后必须重新解析样式 ID；结构断言须证明正文、列表、图注等每个生成 `w:pStyle` 都存在对应的当前样式定义，且显式“图注”样式即使被 Word 重编号也不会显示成正文，无法恢复/歧义时稳定失败。正文范围不得残留角色标记、标记字符样式引用或 HTML/Pandoc 临时段落样式；测试须覆盖角色标记被 `w:lastRenderedPageBreak` 拆分的跨页表格，确认清理后单元格正文不变、分页提示不丢失，残留时稳定失败。行内代码的结构断言须同时检查无 `HTML Code`/`HTML 代码` run 样式、正文/表格中的 Word 实际样式和其他段落中的目标字体计算值。`-` / `*` / `+` 与有序列表在 `numbering.xml` 中保持正确 `w:numFmt`、`w:numPr`、`w:ilvl`、`w:numId` 和目标 `w:pStyle`。对于自带编号的有序目标样式，还须在桌面 Word 中比较生成态与重新应用同名样式后的 `ListString`、字体、字号、缩进、段距和行距；除 Markdown 明确控制的起始号/层级外应一致，不能以 XML 样式名作为唯一证据。
8. 标题编号默认开启，可全局关闭、按第 N 个一级标题起算，并按 `level1..4` 生成共享 `numId`、正确 `w:ilvl` / `w:numFmt` / 格式文本；标题不得残留覆盖模板字体、字号和字距的直接格式，目录在编号规范化后更新。成品正文书签必须唯一、成对、有序。重复表头开关须在正文范围内分别验证启用、关闭和模板固定表格不受影响，并用长表逐页确认续页表头。
9. 合成 Markdown 中 fenced Mermaid 可按 `png` / `svg` 生成本地图片；只有紧邻 `caption` / `cation` 注释绑定并落到模板 Caption 样式，成功且带图注的图与普通带图注图片统一编号。Word/PDF 视觉验收还须确认完整 `图 X.Y 标题` 位于图片正下方的独立居中段，不得只检查 Caption 样式名。`auto` 单图降级、`off` 保留代码、`required` 失败无成品，以及超时/取消/不安全 SVG/越限输出均有稳定结果；还须覆盖浏览器启动失败分类、受管浏览器准备/缓存/单次重试，以及安装失败和安装超时稳定码。
10. 超宽正文随文图片按 section 版心和正段落缩进等比缩小，小图不放大，模板固定图片尺寸不变；边界测试须覆盖“宽度只比可用宽度大不到 1pt 时不重写缩放百分比”和“超过 1pt 时仍缩小”两种情况。非 ASCII 本地文件名的二次转义链接可恢复并内嵌，图片替代文本/标题不丢失，临时恢复书签不得出现在成品中。结构断言还须确认图片段为自动扩展行距，并用 Word/PDF 视觉复核图片未被固定正文行距裁切；浮动、文本框、多栏和复杂表格单元格图片继续作为明确限制。HTML `rowspan` / `colspan` 表格专项须同时断言合并标记不变、逻辑网格规则、版心宽度、AutoFit、正式六向边框、无表/行级 cell spacing、内边距/垂直对齐/单元格段落，并逐页确认不再只显示灰色编辑网格线；不规则网格必须保持原结构且明确跳过高级宽度重写。
11. 能力清单的 schema、产品/协议版本、唯一 ID、深层项目结构、模板契约和工具字段由 Worker 与 Main 双重验证；协议 smoke 必须实际调用 `describe-capabilities`。UI 搜索、分类、深链和失败重试有自动化覆盖，生成离线说明通过漂移检查。
12. 类型检查、Lint、TypeScript/C# 单元测试、Worker 协议 smoke、生产构建和 Electron E2E 在发布候选上通过；真实 Word 用例需显式开启且不得使用业务文档。
13. 关键页面沿用已完成桌面验收的 1440x900 与 1280x800 布局；有可见 UI 变化时必须重拍截图。八张桌面截图已于 2026-07-20 重新生成并完成视觉复核，其中能力页展示 Front Matter，添加模板窗口已在 1440x900 及 Electron 最小窗口 1100x720 下断言完整位于应用视口内。
14. 仓库中的 DOCX 只能位于 `resources/templates/<package>/<template-id>/template.docx`，且必须是明确合成、可公开、经 Worker 校验的模板源码；当前只有 `resources/templates/reference/public-reference-template/template.docx`。仓库不得包含真实业务 Markdown、转换产物、凭据或新增旧目录硬编码。
15. `release` 直接产出带版本/架构的未压缩 Portable 目录、Portable EXE、Portable ZIP、Setup EXE、离线能力说明及单文件 EXE 同级 `templates`，不生成额外校验清单，且不得残留 `public`、`win-unpacked` 或 builder 元数据。目录版和 ZIP 内的离线说明均非空；模板根不得有 `index.json`，基线 `reference/index.json` 必须只有 `public-reference-template`。目录版按 1 个模板通过 packaged E2E；任意完整私有模板包可在构建后直接复制为 `templates/<package-name>`，但不得被 Git 跟踪或上传 GitHub。

2026-07-20 的 0.5.0 候选已通过能力清单/生成文档同步检查、95 项 Vitest（23 个测试文件，新增能力清单深校验、搜索/深链和重复 ID 拒绝）、包含 `describe-capabilities` 的协议 smoke、生产构建、四页 Electron E2E、8 张截图生成/视觉复核、三种 Windows x64 打包及目录版 packaged E2E。同日追加的复杂合成专项已实际重跑 Word/Mermaid 双门控，结果为 137/137 通过、0 跳过；9 页 Word 原生 PDF 与最终 DOCX 的结构/视觉审计 30/30 通过，模板原生正文对和 DOM 导入正文对的可见间距差为 0.12pt，未发现可见 HTML、`w:altChunk`、临时样式、外部关系、浮动/文本框或非契约空段残留。重复转换结构指标一致，Mermaid `auto/off/required` 分支和真实 Electron -> Main -> Worker -> Word 用例均通过，最终无本次新建的 WINWORD 残留。专项证据见 `doc/testing/comprehensive-synthetic-acceptance.md`。

2026-07-20 的 0.4.0 候选满足当前合成验收基线：91 项 Vitest（含弹窗根层挂载、多模板包遍历、精简单索引、旧版索引兼容、公开参考模板组装、跨包 ID/默认冲突与模板根路径）、Worker 常规门控 125 通过/11 跳过、136/136 项 Word/Mermaid 双门控测试、Worker 协议和生产构建均通过。空 CSS 真实 Word 用例证明公开角色会走模板 Word 原生回退，但自定义样式视觉验收必须使用模板配套 CSS。五类 admonition 合成用例确认 Word 再保存后仍保留 CSS/Word 目标样式、准确底纹与 2.25pt 左线，且没有新增几何/字体格式或内部标记。历史自定义模板检查的通用结论是：显式样式需与配套 CSS 成组验证，样式引用、原生列表、代码换行、图片、图题及表格结构必须保持一致。公开记录不保留私有文档内容、页码或样本统计。尚未完成 Authenticode 签名，以及不规则网格、高级图片布局、丰富 admonition 布局和完整 WordDOM 视觉一致性验收。

## 9. 完整 WordDOM 兼容验收原则

- 先用旧 Python WordDOM 生成经人工确认的基准输出，再迁移 C#；不以“命令执行成功”替代版式一致性验收。
- 必须覆盖封面/正文/版本表书签、标题与编号、目录字段、正文与列表、代码块、admonition、表格、图片、图注、内部链接、Mermaid、front matter 和本地资源嵌入。
- 列表专项至少覆盖 `-` / `*` / `+`、`1.`、非 1 起始号、tight/loose、多段落条目、二至三级嵌套、有序与无序混合、分离列表重启及列表内代码/图片；结构检查需同时核对 `w:pStyle`、`w:numFmt`、`w:lvlText`、符号字体、`w:numPr`、`w:ilvl`、`w:numId`、缩进与派生编号身份，并用桌面 Word 比较有序列表生成态/重新应用目标样式，以及无序列表生成态/手工项目符号库的可见编号和计算格式。
- 代码块专项至少覆盖多行、空行、四空格缩进、内部双空格及 tab；同时核对 Word 手动换行数量、逐行文本和 Word/PDF 可见结果，不以代码样式名存在替代内容语义验收。
- 行内代码专项至少覆盖正文、有序列表和表格单元格；核对 CSS/正文回退报告、run 样式清理、目标字体投影与 Word 计算结果，不能仅看所在段落样式名。
- 普通表格专项同时核对版心宽度、固定列宽、表/行级单元格间距、六向边框、内边距、垂直对齐、单元格段落缩进/行距/对齐、表头加粗与逐页连续单线视觉。
- 兼容差异必须记录在旧链路审计中，并由用户明确接受后才能改变既有行为。
