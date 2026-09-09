# 复杂合成文档专项验收

## 目的与安全边界

本专项用于验证 Desktop MVP 0.5.0 的真实 `Pandoc -> HTML DOM 归一化 -> Word COM 导入 -> 模板装配 -> Open XML 收口` 链路，重点回答：

1. DOM/HTML 导入是否给正文、标题、列表、代码、引用、表格、图片或图题引入模板之外的段前、段后、行距或空段落。
2. Word 原生显示与 Word 原生导出的 PDF 是否保持相同的页面节奏、分页、表格间距、图片尺寸和图题位置。
3. 成品正文是否泄漏 HTML 标签/属性、Pandoc/HTML 临时样式、内部角色标记或 `HTML Code` / `HTML 代码` 字符样式。
4. 复杂但明确处于 0.5.0 契约内的结构是否同时通过 Open XML、Word COM 和逐页 PDF 检查。

所有输入均由 `scripts/New-SyntheticAcceptanceFixture.ps1` 在运行时生成，只包含明确标注的合成文字、程序生成图片和公开的最小结构，不含业务模板、客户正文、客户图片、用户名或私人路径。模板、Markdown、转换成品、PDF、页面 PNG、日志和审计 JSON 只允许写入 Git 忽略的 `output/` / `tmp/`，不得提交。

## 合成夹具覆盖

### 模板

- A4 页面、固定页边距、页眉页脚和模板固定内容。
- `MANUAL_COVER_TITLE`、`MANUAL_COVER_SUBTITLE`、`MANUAL_TABLE_VERSION_HISTORY`、`MANUAL_BODY_START`、`MANUAL_BODY_END`。
- 正文、H1-H6、有序列表、无序列表、图题、表题、代码、表格正文、admonition 和图片段的独立 Word 段落样式。
- 所有验收样式显式编码字体、字号、段前、段后、行距、对齐和必要的 keep 规则；正文基线为段前 `0pt`、段后 `6pt`、多倍行距 `1.15`。
- 有序列表包含模板原生编号定义；表格使用明确边框和单元格内边距。

### Markdown

- 显式 title/subtitle、历史版本表键、标题编号/起点/格式、重复表头和图注开关。
- H1-H6、正文粗体/斜体/行内代码、重复内部链接和非 ASCII 标题锚点。
- `-` / `*` / `+`、非 1 起始有序列表、二至三级混合嵌套、tight/loose、多段 continuation、分离重启、列表内代码。
- 多行 fenced 代码、空行、四空格缩进、内部双空格和 tab。
- 普通引用及 note/caution/warning/danger、中英文标签、多段引用和嵌套列表/代码。
- 两列表、长表、连续三张独立表、规则 `rowspan` / `colspan` HTML 表格。
- 程序生成的普通图、超宽图和非 ASCII 文件名本地图；相邻图题。
- Mermaid 正常图、`caption` / `cation` 兼容、无图题图和失败降级图。

## 验收口径

### 间距与视觉

- CSS 映射到合成正文样式的普通正文，Word COM 计算格式必须为段前 `0pt`、段后 `6pt`、多倍行距 `1.15`；成品 `document.xml` 不得为这些段落残留覆盖样式的直接 `w:spacing`、`w:ind` 或 `w:jc`。
- 标题、代码、图题、图片段和表题使用各自模板样式的明确节奏；admonition 只增加约定底纹和 2.25pt 左线，不得新增段前、段后、行距、缩进、对齐或字体覆盖。
- 正文书签内不得出现来源不明的空顶层段落。相邻顶层表格之间允许保留 Word 所需的结构性空段落，但它只能引用最终正文样式，且不得含 `w:vanish`、直接格式、空 run 或新分隔样式。
- Word COM 页数与 Word 原生 PDF 页数必须一致。相同锚点在 Word 页面坐标与 PDF 文字坐标中的相邻垂直距离允许最多 `2pt` 的渲染/字体度量差异；分页、图题归属和表格间可见间距必须一致。
- PDF 每页渲染为 PNG 并逐页检查，不允许文字/图片裁切、重叠、表格越界、图题跑到图片右侧或异常大空白。

### HTML/临时内容残留

- 正文可见文字中不得出现非测试代码内容的 `<html>`、`<body>`、`<p>`、`<div>`、`<table>`、`class=`、`style=`、`<!DOCTYPE` 或 HTML 注释。
- 不得残留 `__MD2WORD_ROLE_`、`md2word-role-marker`、`manual-*` 临时段落样式引用/定义、`HTML Code` / `HTML 代码` 字符样式引用。
- 所有生成 `w:pStyle` 必须命中成品当前 `styles.xml`；正文外模板固定区域不得被正文样式收口污染。
- 成品不得含外部图片/超链接关系；内部链接必须使用唯一、可解析的 ASCII 安全书签。

### 其他结构

- `MANUAL_BODY_START` / `MANUAL_BODY_END` 唯一、成对、有序；封面和版本表书签能力正常。
- 标题编号共享一个 Word 原生 `numId`；普通列表保留起始号、重启、层级、编号格式和模板样式。
- 代码块的 Word 手动换行数、空行、缩进、连续空格与源 Markdown 一致；行内代码只修改对应 run。
- 普通表和规则合并表满足当前版心、24%/76%、AutoFit、连续边框、cell spacing、内边距、垂直对齐和单元格段落契约；长表续页表头开启。
- 正文图片全部内嵌，超宽图片等比缩小到可用版心，小图不放大，非 ASCII 文件名不留下外链或恢复书签，图片段行距可自动扩展。
- Mermaid `auto` 允许单图失败并保留对应代码且返回稳定告警；另行验证 `required` 失败不产生部分成品、`off` 不启动渲染。
- 转换和导出结束后不得留下本任务新建的 WINWORD、Mermaid CLI 或浏览器子进程。

## 可复现命令

生成夹具：

```powershell
powershell -NoProfile -NonInteractive -File scripts/New-SyntheticAcceptanceFixture.ps1 `
  -Scenario Comprehensive `
  -OutputDirectory output/comprehensive-acceptance
```

Word 原生 PDF 与 Word 计算布局：

```powershell
dotnet run --project scripts/WordAcceptanceExporter/WordAcceptanceExporter.csproj -c Release -- `
  --docx output/comprehensive-acceptance/synthetic-comprehensive-output.docx `
  --pdf output/pdf/synthetic-comprehensive-word.pdf `
  --metrics output/comprehensive-acceptance/word-layout-metrics.json
```

DOCX/PDF 结构与间距审计：

```powershell
python scripts/audit-comprehensive-acceptance.py `
  --docx output/comprehensive-acceptance/synthetic-comprehensive-output.docx `
  --template output/comprehensive-acceptance/synthetic-comprehensive-template.docx `
  --expectations output/comprehensive-acceptance/synthetic-expectations.json `
  --pdf output/pdf/synthetic-comprehensive-word.pdf `
  --out-json output/comprehensive-acceptance/acceptance-report.json `
  --out-md output/comprehensive-acceptance/acceptance-report.md
```

真实 Word/Mermaid Worker 门控：

```powershell
$env:MD2WORD_RUN_WORD_E2E = '1'
$env:MD2WORD_RUN_MERMAID_E2E = '1'
dotnet test worker/Md2Word.Worker.sln -c Release
```

本次专项的转换、Word 原生 PDF 导出、结构审计、页面渲染与视觉结论记录在 `output/comprehensive-acceptance*/` 的 `acceptance-report.md` / `acceptance-report.json` 及 `output/pdf/` 中。该目录是本地验收证据，不是可提交源码。

## 本次结果

执行日期：2026-07-20。

环境：

- Windows NT `10.0.26200.0`。
- Microsoft Word `16.0`，build `16.0.20131`。
- Pandoc `3.9`，固定 Mermaid CLI `11.16.0`，Microsoft Edge `150.0.4078.83`。
- .NET SDK `8.0.204`，Node.js `24.13.1`，npm `11.8.0`。
- MD2Word `0.5.0`，协议 `1.0`，测试基线提交 `3fda939` 加本次未提交的验收脚本改动。

最终采用 `output/comprehensive-acceptance-v2/` 的运行时合成夹具。v1 逐页复核时发现“小图”标签在源 PNG 内已被夹具自身裁切；图片生成器改为按可用宽度缩小字体后生成 v2。v2 的第 1–7、9 页与 v1 渲染 PNG 逐文件 SHA-256 相同，第 8 页仅包含修正后的小图，确认标签完整。

### 核心结论

1. **未发现 DOM 导入引入额外正文间距。**
   - 模板原生 `[CTRL-BODY-1/2]` 与 DOM 导入 `[DOM-BODY-1/2]` 都使用同一最终 Word 样式，Word COM 计算结果均为段前 `0pt`、段后 `6pt`、行距 `13.8pt` / 多倍行距规则；两组页内纵坐标差均为 `26.70pt`。
   - Word 原生 PDF 中，模板控制组间距为 `26.76pt`，DOM 导入组为 `26.64pt`，差值 `0.12pt`，小于 `2pt` 验收容差。
   - 导入正文没有直接 `w:spacing`、`w:ind` 或 `w:jc`。正文顶层空段仅有相邻三张独立表所需的 `2` 个结构分隔段，均只引用最终正文样式，没有 run、隐藏文字或直接格式。

2. **未发现 HTML 落入 Word。**
   - 可见文本中的 HTML 标签/属性、DOCTYPE、内部角色标记和占位文字均为 `0`。
   - `w:altChunk` / `afchunk` 部件与元素为 `0`，外部关系为 `0`。
   - `HTML Code` / `HTML 代码`、`manual-*`、角色标记临时样式的引用与定义为 `0`；`289` 个样式引用全部能在最终 `styles.xml` 中解析。
   - 浮动 anchor、文本框和旧式 `w:pict` 为 `0`，没有发现 HTML 导入额外生成的浮动/文本框内容。

3. **DOCX/PDF 视觉与结构通过。**
   - 最终成品为 `9` 页 A4、`282` 个段落、`8` 张表、`4` 个内嵌 drawing、`26` 个样式定义。
   - 结构 + PDF 审计 `30/30` 通过；所有 9 页均以 150 DPI 渲染并逐页检查，未见文字/图片越界、重叠、图题错位或异常大空白。
   - 56 行长表跨第 4–7 页，四页均有重复表头；规则 HTML 表保留纵向/横向合并；图片全部在版心内，图题均为独立紧邻段落。
   - fenced 代码的空行、双空格、四空格缩进和 tab 展开保持一致。Word 使用不换行空格保存不可折叠空白，规范化为空格后逐行与 Markdown 相同。

4. **分支、重复性与清理通过。**
   - `auto`：2 张有效 Mermaid 生成图片，1 张故意无效的图保留为代码并返回 `MERMAID_SOURCE_INVALID` 告警，不产生孤立图题。
   - `off`：3 段 Mermaid 源码均保留，只存在 2 张普通图片，Mermaid 图题标记为 `0`。
   - `required`：在故意无效图处以 `MERMAID_SOURCE_INVALID` 失败，退出码 `4`，不产生部分 DOCX。
   - 第二次 `auto` 转换的结构审计 `25/25` 通过，段落、表格、drawing、样式和空段指标与第一次完全一致。
   - 转换、PDF 导出、完整门控和桌面 E2E 结束后新建 WINWORD 进程为 `0`。

### 仓库门控

- Word/Mermaid 双门控：`137/137` 通过，`0` 跳过，耗时 `2m38s`。
- Vitest：23 个测试文件、`95/95` 通过。
- TypeScript、ESLint、能力清单漂移检查、Worker 协议 smoke、Renderer/Electron/Worker 生产构建均通过。
- 真实 Electron -> Main -> Worker -> Word E2E 最终为 `2` 通过、`2` 个专用开关用例跳过；跳过项是本次未要求重打包的 packaged 启动和无 UI 变化时不应更新的截图集。
- 桌面真实转换首次运行发现基础 smoke 夹具未显式映射 `table-caption` / `admonition` / `figure-image`，Word 保存后 `Normal` 同名候选触发 `WORD_STYLE_REBIND_AMBIGUOUS`。已补齐夹具 CSS 映射，预检不再含这三个 `Normal` 回退，原 E2E 复跑通过。

### 保留风险与边界

- `dotnet restore/publish` 报告 `AngleSharp 1.3.0` 存在已知中等级漏洞 `GHSA-pgww-w46g-26qg`；本次只记录风险，未在测试任务中升级依赖。
- 本机没有 LibreOffice，因此没有把 LibreOffice 作为第二排版引擎；版式判定以正式依赖的 Microsoft Word 与 Word 原生 PDF 为准。
- 本专项证明当前合成矩阵内未见额外间距或 HTML 泄漏，不等于不规则网格、高级合并列宽、浮动/文本框/多栏图片、丰富 admonition 或完整旧 WordDOM 视觉已经签收。
