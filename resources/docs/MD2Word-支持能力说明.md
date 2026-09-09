<!-- 此文件由 scripts/generate-capability-docs.mjs 从 resources/conversion/capabilities.json 自动生成，请勿手工编辑。 -->

# MD2Word 支持能力说明

> 产品版本：0.5.0 · 能力清单：1.1 · Worker 协议：1.0 · 语言：zh-CN

本清单是 Worker、上位机内置帮助和离线说明的单一能力真源；“支持”表示已有实现与合成证据，不等于所有私有模板和 Office 版本都已完成视觉一致性签收。

这份文件与桌面应用“能力说明”页面均来自同一份随 Worker 打包的机器可读清单。模板校验中的能力编号可以在本文中直接搜索。

## 状态含义

| 状态 | 含义 |
|---|---|
| 支持 | 已实现并具有仓库内合成自动化证据。 |
| 条件支持 | 依赖特定语法、模板能力或可选工具；不满足条件时会警告或拒绝转换。 |
| 有限支持 | 只覆盖清单明确列出的基础范围。 |
| 未承诺 | 当前版本不作为可验收能力承诺。 |

## Markdown 与内容结构

源 Markdown 经 Pandoc、Lua、Word COM 与 Open XML 共同转换。

### H1–H6 标题

- 能力编号：`CAP-MARKDOWN-HEADINGS`
- 状态：支持
- 摘要：支持 Markdown 标题、heading_base_level 层级重映射和模板 Word 标题样式。

- 模板校验会报告 H1–H6 的 CSS 映射或 Word 原生回退来源。
- 缺少未实际使用的高阶标题只产生条件性警告；实际使用时才阻止转换。

示例：

```markdown
# 一级标题
## 二级标题
```

相关 Front Matter：`heading_base_level`、`heading_numbering`、`heading_numbering_start_base`、`word_heading_numbering`

### 正文与常用行内语义

- 能力编号：`CAP-MARKDOWN-INLINE`
- 状态：支持
- 摘要：支持普通段落、粗体、斜体、删除线、外部超链接和行内代码。

- 行内代码使用 CSS 显式目标或正文样式回退，并清除 Word HTML Code 临时样式。
- 粗体、斜体和超链接保留 Word 字符级语义。

示例：

```markdown
**粗体**、*斜体*、~~删除线~~、`inline_code`
```

### 有序与无序列表

- 能力编号：`CAP-MARKDOWN-LISTS`
- 状态：支持
- 摘要：支持 Word 原生编号/项目符号、非 1 起始、分离重启、二至三级嵌套及混合列表。

- 保留 w:numPr、w:ilvl、w:numId 与目标段落样式。
- 支持 tight/loose、多段落条目，以及列表内代码和图片结构。

示例：

```markdown
1. 有序项
- 无序项
```

### 代码块

- 能力编号：`CAP-MARKDOWN-CODE`
- 状态：支持
- 摘要：普通 fenced 代码块映射模板代码样式，并保留硬换行、空行、缩进、连续空格和 tab 基线。

- 源代码在一个 Word 代码段落内使用手动换行。
- 代码块不会被普通 HTML 空白折叠压成一行。

示例：

```markdown
```text
const value = 1;
```
```

### 引用与语义提示

- 能力编号：`CAP-MARKDOWN-ADMONITION`
- 状态：支持
- 摘要：普通引用及 NOTE、CAUTION、WARNING、DANGER 与中英文别名具有固定背景和 2.25pt 左线。

- 保留 CSS/Word 解析出的段落样式，不改缩进、段距、行距、对齐、字体或字号。
- 嵌套列表、表格和代码继续使用自身语义角色。

示例：

```markdown
> 普通引用
> WARNING: 警告内容
> 注意: 注意内容
```

### 普通 Markdown 表格

- 能力编号：`CAP-MARKDOWN-TABLES`
- 状态：支持
- 摘要：普通无合并正文表格会收口为 Word 连续单线网格；两列表按 24%/76% 固定布局。

- 表格宽度限制到当前 section 版心。
- 支持重复表头、内边距、垂直居中和紧凑单元格段落。

示例：

```markdown
| 字段 | 说明 |
|---|---|
| alpha | 合成内容 |
```

相关 Front Matter：`word_repeat_table_headers`

### 规则 HTML 合并单元格

- 能力编号：`CAP-MARKDOWN-HTML-MERGED-TABLES`
- 状态：条件支持
- 摘要：支持逻辑网格规则的矩形 rowspan/colspan，保留 Word 纵横合并并以 AutoFit 比例收口。

- 要求每行逻辑列数一致、纵向合并连续，且不使用 gridBefore/gridAfter。
- 不规则网格会保留原结构，但跳过高级宽度重写。

示例：

```html
<td rowspan="2">字段</td>
<td colspan="2">属性</td>
```

相关 Front Matter：`word_repeat_table_headers`

### 相对本地图片

- 能力编号：`CAP-MARKDOWN-IMAGES`
- 状态：条件支持
- 摘要：支持相对本地图片嵌入、非 ASCII 文件名恢复和正文随文图片版心宽度约束。

- 资源以 Markdown 文件所在目录为基准解析，远程或不可读目标不会被伪装为本地图片。
- 只缩小超宽随文图片，不放大小图或修改模板固定区域图片。

示例：

```markdown
![图示](./images/example.png)
```

相关 Front Matter：`figure_captions`

### 文档内部链接

- 能力编号：`CAP-MARKDOWN-INTERNAL-LINKS`
- 状态：支持
- 摘要：内部链接重写为 ASCII 安全 Word 书签，并在成品中验证链接和书签唯一匹配。

- 多个链接指向同一目标时共享同一书签。
- 缺失目标保留源语义，不创建虚假书签。

示例：

```markdown
[跳到章节](#目标章节)
## 目标章节
```

### Mermaid PNG/SVG

- 能力编号：`CAP-MARKDOWN-MERMAID`
- 状态：条件支持
- 摘要：支持固定版本 Mermaid CLI、本机或受管浏览器回退，以及严格相邻 caption 图注绑定。

- auto 单图失败保留代码并告警；off 不渲染；required 任一失败终止任务。
- 只承诺通过安全校验的 PNG/SVG；全部图型和跨 Office 视觉仍需单独验收。

示例：

```markdown
```mermaid
flowchart LR
```
<!-- caption: 合成流程图 -->
```

相关 Front Matter：`figure_captions`

## Word 模板与 CSS 契约

模板 DOCX 与 CSS 是不可拆分的配置单元，导入和转换前都会校验。

### 可读 Open XML DOCX

- 能力编号：`CAP-TEMPLATE-DOCX-PACKAGE`
- 状态：支持
- 摘要：模板必须是可读的 .docx Open XML 包，并且输出不能覆盖模板本身。

- 模板始终作为只读输入，Worker 只修改任务临时副本。
- 模板与 CSS 指纹变化会使旧校验失效。

### 正文书签装配

- 能力编号：`CAP-TEMPLATE-BODY-RANGE`
- 状态：支持
- 摘要：模板必须包含唯一、有序的 MANUAL_BODY_START 与 MANUAL_BODY_END。

- 生成正文只写入两书签之间，封面、页眉页脚和模板固定区域保持由模板主导。
- 发布前再次验证成品正文书签唯一、成对且顺序正确。

### 能力型可选书签

- 能力编号：`CAP-TEMPLATE-OPTIONAL-BOOKMARKS`
- 状态：条件支持
- 摘要：封面标题、副标题和版本表书签只在对应 Front Matter 被显式使用时成为必需。

- 缺少可选书签会在模板预检中报告警告，而不是无条件阻止保存。
- 显式使用缺失能力时转换会失败并给出稳定错误。

相关 Front Matter：`title`、`subtitle`、`manul_version_tables`

### CSS 到 Word 样式映射

- 能力编号：`CAP-TEMPLATE-CSS-MAPPING`
- 状态：支持
- 摘要：CSS 使用 mso-style-name 把语义角色映射到模板现有 Word 段落样式。

- 先按 style ID、名称和 alias 精确解析，精确候选不存在时再尝试 Word 内建样式中英文等价名。
- 显式样式缺失或歧义会阻止模板保存，不能静默改成正文。

示例：

```markdown
p.manual-table-paragraph { mso-style-name: "示例表格"; }
```

### Word 原生样式回退与重绑定

- 能力编号：`CAP-TEMPLATE-WORD-STYLE-FALLBACK`
- 状态：支持
- 摘要：CSS 未配置角色时使用模板 Word 原生回退；Word 保存后重新绑定当前 styles.xml 中的样式 ID。

- 校验报告区分 CSS 已解析与 Word 原生来源。
- 无法恢复或有歧义时拒绝发布，避免悬空 w:pStyle 显示成默认正文。

### 模板与输出离线安全

- 能力编号：`CAP-TEMPLATE-OFFLINE-SAFETY`
- 状态：支持
- 摘要：模板和成品会阻止可能自动加载网络或本地外部内容的关系与危险字段。

- 普通超链接允许保留，但自动加载内容的外部关系会被阻止。
- 生成文档的本地图片在发布前嵌入包内。

## Word 成品收口

Word COM 负责模板语义，Open XML 负责可验证的最终结构。

### Word 原生四级标题编号

- 能力编号：`CAP-WORD-HEADING-NUMBERING`
- 状态：支持
- 摘要：支持统一 numId、编号起点、四级编号样式与格式，并在目录/字段更新前应用。

- 编号样式支持十进制、大小写罗马数字和大小写字母。
- 标题直接格式会被清理，让模板标题样式主导字体与段落格式。

相关 Front Matter：`heading_numbering`、`heading_numbering_start_base`、`word_heading_numbering`

### 正文表格基础收口

- 能力编号：`CAP-WORD-TABLE-FINALIZATION`
- 状态：支持
- 摘要：支持普通表格和规则矩形合并表格的版心宽度、边框、内边距、表头与单元格段落收口。

- 相邻顶层表格之间保留 Word 必需空段落，并恢复 CSS 正文样式。
- 不规则网格和高级固定列宽仍属于限制项。

相关 Front Matter：`word_repeat_table_headers`

### 正文随文图片宽度与行距

- 能力编号：`CAP-WORD-IMAGE-FINALIZATION`
- 状态：支持
- 摘要：正文随文图片按当前 section 版心和段落缩进等比缩小，并使用可自动扩展行距。

- 尺寸触发和结果验收使用相同的 1pt Word 布局容差。
- 浮动、文本框、多栏及复杂表格单元格图片不在当前契约内。

### 原子输出与清理

- 能力编号：`CAP-WORD-ATOMIC-OUTPUT`
- 状态：支持
- 摘要：Worker 验证临时 DOCX 后交 Main 原子发布；失败、取消和超时不会保留部分成品。

- 每个任务使用独立 Worker 和独立 Word 实例，全局串行执行。
- 成功、失败和取消都会关闭文档、退出 Word 并清理临时目录。

## Front Matter 元数据

| 键 | 类型 | 状态 | 默认行为 | 模板要求 |
|---|---|---|---|---|
| `title` | string | 条件支持 | 缺失时保留模板原文 | MANUAL_COVER_TITLE |
| `subtitle` | string | 条件支持 | 缺失时保留模板原文 | MANUAL_COVER_SUBTITLE |
| `heading_base_level` | integer | 支持 | 由标题层级 Lua filter 使用默认层级 | 无 |
| `heading_numbering` | boolean | 支持 | true | 无 |
| `heading_numbering_start_base` | integer | 支持 | 1 | 无 |
| `word_heading_numbering` | map(level1..level4) | 支持 | 四级十进制：%1. / %1.%2 / %1.%2.%3 / %1.%2.%3.%4 | 无 |
| `word_repeat_table_headers` | boolean | 支持 | true | 无 |
| `figure_captions` | boolean | 支持 | true | 无 |
| `manul_version_tables` | array | 条件支持 | 缺失时不更新版本表 | 对应的 MANUAL_TABLE_* 书签 |

### `title`

- 能力编号：`CAP-METADATA-TITLE`
- 说明：显式值写入 MANUAL_COVER_TITLE。

```yaml
title: 合成技术报告
```

### `subtitle`

- 能力编号：`CAP-METADATA-SUBTITLE`
- 说明：显式值写入 MANUAL_COVER_SUBTITLE。

```yaml
subtitle: 合成验收说明
```

### `heading_base_level`

- 能力编号：`CAP-METADATA-HEADING-BASE`
- 说明：把源标题层级统一重映射后再应用 H1–H6 样式和编号。
- 可选值：`正整数`

```yaml
heading_base_level: 2
```

### `heading_numbering`

- 能力编号：`CAP-METADATA-HEADING-NUMBERING`
- 说明：控制正文标题是否使用 Word 原生多级编号。
- 可选值：`true`、`false`

```yaml
heading_numbering: false
```

### `heading_numbering_start_base`

- 能力编号：`CAP-METADATA-HEADING-START`
- 说明：从第 N 个映射后一级标题开始编号，并跳过此前一级标题下的子标题。
- 可选值：`大于等于 1 的整数`

```yaml
heading_numbering_start_base: 2
```

### `word_heading_numbering`

- 能力编号：`CAP-METADATA-WORD-HEADING`
- 说明：分别配置四级 Word 编号的 number_style 与非空 format。
- 可选值：`decimal`、`upper_roman`、`lower_roman`、`upper_letter`、`lower_letter`

```yaml
word_heading_numbering:
  level1:
    number_style: upper_roman
    format: "%1."
```

### `word_repeat_table_headers`

- 能力编号：`CAP-METADATA-REPEAT-HEADERS`
- 说明：控制正文书签范围内每张表格的首行是否作为 Word 重复表头。
- 可选值：`true`、`false`

```yaml
word_repeat_table_headers: false
```

### `figure_captions`

- 能力编号：`CAP-METADATA-FIGURE-CAPTIONS`
- 说明：控制普通图片和成功 Mermaid 图的图注与按章图号。
- 可选值：`true`、`false`

```yaml
figure_captions: false
```

### `manul_version_tables`

- 能力编号：`CAP-METADATA-VERSION-TABLES`
- 说明：按 bookmark、column_keys 和 rows 更新模板版本表；历史拼写必须保留。

```yaml
manul_version_tables:
  - bookmark: MANUAL_TABLE_VERSION_HISTORY
    column_keys: [version, description]
    rows:
      - version: "1.0"
        description: 合成记录
```

## 模板契约

### 书签

| 类型 | 名称 | 用途 |
|---|---|---|
| 必需 | `MANUAL_BODY_START` | 正文写入范围的起点。 |
| 必需 | `MANUAL_BODY_END` | 正文写入范围的终点，必须位于起点之后。 |
| 按需 | `MANUAL_COVER_TITLE` | 显式 title 元数据的写入目标。 |
| 按需 | `MANUAL_COVER_SUBTITLE` | 显式 subtitle 元数据的写入目标。 |
| 按需 | `MANUAL_TABLE_*` | 显式 manul_version_tables 条目的写入目标。 |

### CSS 语义角色

| 角色 | 选择器 | 回退 |
|---|---|---|
| 正文（`body`） | `p.manual-body-paragraph` | 模板默认/正文段落样式 |
| 有序列表（`ordered-list`） | `ol > li.manual-body-list-item`、`li.manual-body-ordered-item` | 模板正文段落样式 |
| 无序列表（`unordered-list`） | `ul > li.manual-body-list-item`、`li.manual-body-unordered-item` | 模板正文段落样式 |
| H1–H6（`heading`） | `h1`、`h2`、`h3`、`h4`、`h5`、`h6` | Word 原生 Heading 1–6 / 标题 1–6 |
| 图注（`caption`） | `p.manual-figure-caption`、`figcaption` | Word Caption / 图注 |
| 表题（`table-caption`） | `p.manual-table-caption` | Word Caption / 图注 |
| 代码块（`code-block`） | `p.manual-code-block-paragraph` | 模板代码段落样式或正文回退 |
| 行内代码（`inline-code`） | `code.manual-inline-code` | 已解析正文角色 |
| 表格单元格正文（`table`） | `p.manual-table-paragraph` | 模板默认/正文段落样式 |
| 引用与提示（`admonition`） | `p.manual-admonition-paragraph` | 模板默认/正文段落样式 |
| 图片段落（`figure-image`） | `p.manual-figure-image-paragraph` | 模板默认/正文段落样式 |

### 校验约定

- DOCX 与 CSS 任一变化都会使旧 fingerprint 失效并要求重新校验。
- CSS 显式样式引用缺失或歧义时阻止保存；未映射标题级别缺少回退时先报告条件性警告。
- Word 保存后重新绑定当前样式 ID，最终 w:pStyle 必须指向 styles.xml 中的现存定义。

## 已知边界

### 不规则网格与高级合并表格宽度

- 能力编号：`LIMIT-TABLE-ADVANCED`
- 状态：有限支持
- 摘要：不规则网格会保留 Word 导入结构，但不承诺高级固定列宽和跨 Office 复杂视觉一致性。

- 多列复杂比例、纵向合并断裂、gridBefore/gridAfter 和高级固定列宽需单独验收。

### 高级图片布局

- 能力编号：`LIMIT-IMAGE-ADVANCED`
- 状态：有限支持
- 摘要：浮动图片、文本框、多栏、复杂表格单元格宽度、裁剪、环绕和边框不在当前基础收口契约内。

- 当前正式能力集中于正文书签范围内的随文图片嵌入、宽度限制和自动行距。

### 丰富 admonition 视觉

- 能力编号：`LIMIT-ADMONITION-RICH`
- 状态：有限支持
- 摘要：只实现固定背景和左线，不包含图标、文字色、内边距、圆角、整框和连续多段盒子。

- 完整旧 WordDOM admonition 视觉和跨 Office 一致性仍需私有基准证据。

### 全部 Mermaid 图型与跨 Office 视觉

- 能力编号：`LIMIT-MERMAID-VISUAL`
- 状态：有限支持
- 摘要：PNG/SVG 安全输出和结构已实现，但全部图型、复杂 SVG 与不同 Office 版本逐页视觉未完成签收。

- required 模式保证失败不发布部分成品，不代表每种图型都已完成人工视觉验收。

### 完整 WordDOM 视觉一致性

- 能力编号：`LIMIT-WORDDOM-PARITY`
- 状态：未承诺
- 摘要：当前版本是可运行迁移基线，不宣称所有旧模板、字段、目录和 Office 版本已达到完整视觉一致。

- 最终签收仍需仓库外私有模板的 Open XML、Word COM 和逐页 Word/PDF 对比。

## 运行环境与工具

平台：Windows x64

必需：

- Microsoft Word 桌面版
- Pandoc
- .NET 8 self-contained Worker（随应用打包）

可选：

- npx 与 Microsoft Edge/Google Chrome（Mermaid auto/required）

固定版本：

- `mermaidCli`：`@mermaid-js/mermaid-cli@11.16.0`
- `puppeteer`：`puppeteer@25.3.0`
- `proxyAgent`：`proxy-agent@6.5.0`

## 兼容性声明

当前仍待旧链路完整验收：`advanced-table-width-finalization`、`advanced-image-layout-finalization`、`full-worddom-visual-parity`。机器清单中的 implemented/pendingLegacyParity 字段供审计和自动化使用，不替代上面的用户能力说明。
