# MD2Word 模板制作指南

适用：0.6.0–0.6.1 的 DOCX/CSS 模板契约。

一个模板由两个文件组成：**DOCX 决定页面布局和 Word 样式，CSS 把 Markdown 内容映射到这些样式**。本指南先带你改出一个可用模板，再说明如何从空白文档开始。

> 应用接收 `.docx`，不是 Word 的 `.dotx` 模板格式。以下均为合成示例。自己的模板原件应保存在个人文档目录，不要放进源码仓库或重新打包会清理的 `release/`。

## 一、最快开始：修改公开参考模板

### 1. 复制一组文件

公开参考模板已经包含正文起止书签、封面标题/副标题书签，以及一组可用样式。

| 来源 | 参考模板位置 |
|---|---|
| 源码 | `resources/templates/reference/public-reference-template/` |
| 便携版 | EXE 同级 `templates/reference/public-reference-template/` |
| Setup 安装版 | “环境与设置 → 模板库 → 文件夹按钮”打开用户模板库，再进入 `reference/public-reference-template/` |

将 `template.docx` 和 `style.css` **一起复制**到自己的工作文件夹：

```text
我的模板/
  我的报告模板.docx
  style.css
```

安装版如果已删除参考模板，可以从安装目录同样的 `templates/reference/public-reference-template/` 复制原始文件；不要直接编辑安装目录里的文件。

### 2. 在 Word 中修改版式和样式

用桌面版 Word 打开副本，在“布局”中设置纸张、页边距和方向，按需设置页眉、页脚和固定封面。

正文的字体、字号和段距应修改**样式定义**，而不是仅选中示例文字后改字体：

1. 在“开始 → 样式”中找到“正文”（英文 Word 为 `Normal`）。
2. 右键样式，选择“修改”。
3. 设置字体、字号；在“格式 → 段落”中设置缩进、段距和行距。
4. 仅应用于当前文档，不修改全局 `Normal.dotm`。
5. 根据需要继续修改标题、列表、题注等样式，再保存 DOCX。

参考 CSS 使用的名称与中文 Word 的显示名称可能不同：

| 内容 | 参考 CSS 中的名称 | 常见中文显示名 |
|---|---|---|
| 正文、表格正文、图片段、行内代码 | `Normal` | 正文 |
| 一级至六级标题 | `Heading 1` 至 `Heading 6` | 标题 1 至标题 6 |
| 有序列表 | `List Number` | 编号 |
| 无序列表 | `List Paragraph` | 列表段落 |
| 图题、表题 | `Caption` | 题注／图注 |
| 代码块 | `No Spacing` | 无间隔 |
| 引用与提示 | `Intense Quote` | 明显引用 |

应用支持这些 Word 内建样式的中英文等价名称。只修改这些样式的格式时，通常不需要修改 CSS。

### 3. 保留书签，导入副本

参考模板正文中的英文说明是占位内容，转换时会被替换。不要删除正文边界书签。封面占位文字可以由 Markdown 的 `title` / `subtitle` 更新；手工修改封面文字后，要复查对应书签是否仍存在。

在 MD2Word 中打开“模板管理 → 添加模板”，填写新名称，选择 DOCX 副本，**选择“自定义 CSS”并选取复制的 `style.css`**，然后“校验配置 → 保存模板”。

不要随意改选“内置默认 CSS”：它显式引用 `CodeBlock` 等样式，而公开参考模板的代码块映射为 `No Spacing`。两套配置并不相同。

## 二、从空白 DOCX 开始制作

### 1. 安排固定区域和生成区域

先采用单栏、普通正文段落结构：

```text
封面、固定说明、版本表、目录（按需设置）
─────────────────────────────
正文开始位置  ← MANUAL_BODY_START
待替换的示例正文
正文结束位置  ← MANUAL_BODY_END
─────────────────────────────
固定尾页或其他保留内容（按需设置）
```

箭头和名称仅用于解释位置。**把书签名称当作普通文字打进文档，不等于创建书签。** 实际书签必须通过 Word 的“插入 → 书签”添加。

封面、目录、版本表及需要保留的分节边界应放在生成区域之外。正文会替换边界之间的内容，不会根据某段文字的外观判断它是否需要保留。

### 2. 创建两个正文边界书签

先在“开始”中打开 `¶` 显示段落标记，方便确认插入位置。

1. 在正文预期开始的位置建立普通段落，光标放在段首，**不要选中文字**。
2. 打开“插入 → 书签”，输入 `MANUAL_BODY_START`，点击“添加”。
3. 在下面放一段合成占位正文，再新建一个普通空段落。
4. 将光标放在后面这个空段落的开头，不选文字，添加 `MANUAL_BODY_END`。
5. 保存为 **Word 文档（`.docx`）**。

两者必须处于文档正文中不同的位置，开始在前、结束在后。初次制作不要把边界放在页眉、文本框或复杂表格单元格里。

这里推荐的是**插入点书签**。转换实际替换“开始书签范围的末尾”到“结束书签范围的开头”；如果让开始书签包住一段文字，那段文字可能留在替换范围之外。

### 3. 核对书签

在“插入 → 书签”中分别选择两个名称并点击“定位”。使用按**位置**排序检查先后；按名称排序时 `END` 可能排在 `START` 前面，不代表文档位置错误。

需要在页面中看到边界时，使用“文件 → 选项 → 高级 → 显示文档内容 → 显示书签”。仅开启 `¶` 不会显示书签边界。

必需名称应逐字一致，使用上面的大写字母与下划线。删除占位段落或调整封面后，务必复查书签。

### 4. 创建并保存段落样式

Word 样式库中能看到某个样式，并不总代表它的定义已经写入当前 DOCX。首次制作时，应实际应用并保存需要的样式。

可以使用参考模板那套内建样式，或创建自己的样式：

1. 打开“开始 → 样式”窗格，选择新建样式。
2. 输入名称，例如 `示例 正文`，样式类型选择**段落**。
3. 设置字体、字号、段距等，保存到当前文档。
4. 给生成区域内的一段合成占位文字应用该样式，再保存 DOCX。
5. 对需要的标题、列表、题注和代码样式重复操作；标题还应设置对应的大纲级别。

首次建议把使用了各样式的占位段落保留在正文边界内。生成时占位内容会被替换，样式定义仍可供生成内容使用。

多个角色可以共用一个样式。当前行内代码映射也引用段落样式，再将相应格式应用到文字片段；不要把字符样式名作为 CSS 映射目标。

## 三、编写配套 CSS

CSS 用 UTF-8 保存为 `.css` 文件。`mso-style-name` 指定**模板中已经存在的 Word 样式名**。

下面是公开参考模板的完整基础映射。使用自定义名称时，替换右侧引号里的名称，并确认相应样式已在 DOCX 中创建、应用并保存。

```css
p.manual-body-paragraph,
p.manual-table-paragraph,
p.manual-figure-image-paragraph {
  mso-style-name: "Normal";
}
ol > li.manual-body-list-item { mso-style-name: "List Number"; }
ul > li.manual-body-list-item { mso-style-name: "List Paragraph"; }
h1 { mso-style-name: "Heading 1"; }
h2 { mso-style-name: "Heading 2"; }
h3 { mso-style-name: "Heading 3"; }
h4 { mso-style-name: "Heading 4"; }
h5 { mso-style-name: "Heading 5"; }
h6 { mso-style-name: "Heading 6"; }
p.manual-figure-caption,
p.manual-table-caption { mso-style-name: "Caption"; }
p.manual-code-block-paragraph { mso-style-name: "No Spacing"; }
code.manual-inline-code { mso-style-name: "Normal"; }
p.manual-admonition-paragraph { mso-style-name: "Intense Quote"; }
```

例如只新增了 `示例 正文` 样式，就把上例两处 `"Normal"` 改为 `"示例 正文"`，其他名称先保持不变。这会同时改变正文、表格正文、图片段和行内代码的目标样式。

只在 CSS 中写一个新名称，不会自动创建 Word 样式。字体和段距等主要在 Word 样式中调整；网页 CSS 的视觉效果不等于最终 Word 版式，正文表格、图片和引用框的部分格式还会受转换规则约束。

“内置默认 CSS”也需要匹配的 Word 样式，不等于跳过校验。空 CSS 可触发 Word 原生回退，但缺失必要样式仍可能失败；第一次制作更适合完整显式映射。

## 四、按需添加封面、目录和版本表

### 封面标题与副标题

在正文开始书签之前创建两个独立段落，分别应用 Word 的标题/副标题段落样式。仅选中占位文字，添加：

- `MANUAL_COVER_TITLE`：封面标题。
- `MANUAL_COVER_SUBTITLE`：封面副标题。

它们与正文边界不同，应**包住要替换的文字**。不要跨段落，不要包含段落末尾标记、嵌套书签或图片。封面外观应定义在段落样式中，不依赖占位文字的直接格式。

Markdown 可以写：

```yaml
---
title: 合成报告标题
subtitle: 合成副标题
heading_base_level: 1
---
```

缺少可选封面书签时，不使用对应元数据仍可转换；实际填写 `title` / `subtitle` 后，对应书签就成为必需项。

### 目录

在正文开始书签之前使用 Word“引用 → 目录”插入目录域。不要把它放入待替换的正文范围。标题样式应有正确的大纲级别，生成后打开 Word 检查目录和页码。

### 版本记录表

公开参考模板**不自带版本表**。需要时，在正文开始书签之前插入普通表格，例如两列、两行：

| 版本 | 说明 |
|---|---|
| 合成占位值 | 合成占位说明 |

第一行是表头，第二行用作数据行样式示例。选择整张表并添加 `MANUAL_TABLE_VERSION_HISTORY` 书签；一个版本表书签只关联一张表，初次使用避免合并单元格和嵌套表格。

在 Markdown 顶部的同一个 Front Matter 区域内添加：

```yaml
manul_version_tables:
  - bookmark: MANUAL_TABLE_VERSION_HISTORY
    column_keys: [version, description]
    rows:
      - version: "1.0"
        description: 合成首次记录
      - version: "1.1"
        description: 合成修订记录
```

`manul_version_tables` 是当前固定拼写，不要改成 `manual_version_tables`。`column_keys` 顺序对应从左到右的列，不能多于表格列数。生成时保留表头，按 `rows` 重建数据行。

## 五、导入并试转

保存 DOCX/CSS 后，在“模板管理”中选择这一组文件，点击“校验配置”，逐项查看正文、标题、列表、题注、代码等目标样式。校验失败应先修正；警告需结合实际元数据和标题级别判断。

先使用最小合成 Markdown 试转，基本内容正确后再增加图片、目录和版本表。下面的样例不依赖可选封面或版本表书签：

````markdown
---
heading_base_level: 1
heading_numbering: false
---
# 合成一级标题

用于检查正文、**粗体**和 `行内代码` 的合成文字。

## 合成二级标题

1. 合成有序项
2. 第二项

- 合成无序项
- 第二项

> NOTE: 合成提示文字。

| 项目 | 说明 |
| --- | --- |
| 合成项 | 合成说明 |

```text
first line
    indented line
```
````

打开生成的 Word，检查标题和正文是否使用预期样式、列表是否保留编号、代码换行与缩进是否正确、表格是否可读、固定封面和尾页是否保留。通过 Word 样式窗格查看实际样式，比仅看字体外观更可靠。

这个最小样例只使用 H1/H2，不证明其他标题级别、可选封面或版本表已验收。应继续加入实际会使用的各级标题、图片、Mermaid 和表格结构做检查。

应用导入后使用的是**管理目录中的副本**。修改原 DOCX/CSS 不会自动更新应用里的模板；应在“编辑模板”中重新选择文件、重新校验并保存，再试转。

## 六、常见问题

| 现象 | 检查与处理 |
|---|---|
| `BODY_BOOKMARK_MISSING` | 在 Word 书签对话框检查必需名称；仅输入同名文字或把书签放在页眉中都不符合要求 |
| `BODY_BOOKMARK_ORDER_INVALID` | 用“定位”和按位置排序确认开始在前、结束在后，且不是同一个插入点 |
| `WORD_STYLE_MISSING` | CSS 指定样式不存在；在 Word 中创建并应用后保存，或改为实际存在的名称 |
| `WORD_STYLE_AMBIGUOUS` | 名称、ID 或别名产生歧义；给自定义样式使用明确且不重复的名称 |
| `WORD_NATIVE_STYLE_FALLBACK_MISSING` | 补齐所需原生样式或显式 CSS 映射；未使用的高阶标题可只是警告，实际使用时仍会阻止转换 |
| 模板能保存，但转换失败 | 检查实际输入是否使用缺失的封面/版本表书签或标题样式；模板校验不是全部实际内容的最终验收 |
| 字体或段距没有变化 | 修改样式定义本身，而非仅对占位文字做直接格式设置；确认已更新应用里的文件副本 |
| 正文还留有占位文字 | 检查文字是否在边界之外或被开始书签包住；正文边界建议使用插入点书签 |
| `.dotx` 无法选取 | 另存为 `.docx` 再导入 |

完整语法和限制见同目录的 **MD2Word-支持能力说明.md**。Word 菜单可能随版本变化，基础操作可参考微软的 [书签操作说明](https://support.microsoft.com/zh-cn/word/add-or-delete-bookmarks-in-a-word-document-or-outlook-message)、[样式修改说明](https://support.microsoft.com/zh-CN/Word/customize-or-create-new-styles) 和 [显示书签说明](https://support.microsoft.com/en-us/word/troubleshoot-bookmarks)。
