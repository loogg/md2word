import * as fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptsDirectory = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(scriptsDirectory, "..");
const manifestPath = path.join(projectRoot, "resources", "conversion", "capabilities.json");
const outputPath = path.join(projectRoot, "resources", "docs", "MD2Word-支持能力说明.md");
const packagePath = path.join(projectRoot, "package.json");
const checkOnly = process.argv.includes("--check");

const [manifest, packageMetadata] = await Promise.all([
  fs.readFile(manifestPath, "utf8").then(JSON.parse),
  fs.readFile(packagePath, "utf8").then(JSON.parse),
]);

function assertArray(value, label) {
  if (!Array.isArray(value) || value.length === 0) throw new Error(`${label} must be a non-empty array.`);
}

if (manifest.schemaVersion !== "1.1" || manifest.protocolVersion !== "1.0") {
  throw new Error("Capability manifest schema/protocol version is unsupported.");
}
if (manifest.productVersion !== packageMetadata.version) {
  throw new Error(`Capability productVersion ${manifest.productVersion} does not match package ${packageMetadata.version}.`);
}
assertArray(manifest.categories, "categories");
assertArray(manifest.frontMatter, "frontMatter");
assertArray(manifest.limitations, "limitations");

const ids = [
  ...manifest.categories.flatMap((category) => category.items.map((item) => item.id)),
  ...manifest.frontMatter.map((item) => item.id),
  ...manifest.limitations.map((item) => item.id),
];
if (new Set(ids).size !== ids.length || ids.some((id) => typeof id !== "string" || !id.length)) {
  throw new Error("Capability IDs must be non-empty and globally unique.");
}

const statusLabels = {
  supported: "支持",
  conditional: "条件支持",
  limited: "有限支持",
  unsupported: "未承诺",
};

const escapeCell = (value) => String(value).replaceAll("|", "\\|").replaceAll(/\r?\n/g, "<br>");
const bullets = (items) => items.map((item) => `- ${item}`).join("\n");
const codeBlock = (value, language = "text") => `\`\`\`${language}\n${value}\n\`\`\``;

const lines = [
  "<!-- 此文件由 scripts/generate-capability-docs.mjs 从 resources/conversion/capabilities.json 自动生成，请勿手工编辑。 -->",
  "",
  `# ${manifest.title}`,
  "",
  `> 产品版本：${manifest.productVersion} · 能力清单：${manifest.schemaVersion} · Worker 协议：${manifest.protocolVersion} · 语言：${manifest.locale}`,
  "",
  manifest.summary,
  "",
  "这份文件与桌面应用“能力说明”页面均来自同一份随 Worker 打包的机器可读清单。模板校验中的能力编号可以在本文中直接搜索。",
  "",
  "## 状态含义",
  "",
  "| 状态 | 含义 |",
  "|---|---|",
  "| 支持 | 已实现并具有仓库内合成自动化证据。 |",
  "| 条件支持 | 依赖特定语法、模板能力或可选工具；不满足条件时会警告或拒绝转换。 |",
  "| 有限支持 | 只覆盖清单明确列出的基础范围。 |",
  "| 未承诺 | 当前版本不作为可验收能力承诺。 |",
  "",
];

for (const category of manifest.categories) {
  lines.push(`## ${category.title}`, "", category.description, "");
  for (const item of category.items) {
    lines.push(
      `### ${item.title}`,
      "",
      `- 能力编号：\`${item.id}\``,
      `- 状态：${statusLabels[item.status] ?? item.status}`,
      `- 摘要：${item.summary}`,
    );
    if (item.details.length) lines.push("", bullets(item.details));
    if (item.syntax.length) lines.push("", "示例：", "", codeBlock(item.syntax.join("\n"), item.id.includes("HTML") ? "html" : "markdown"));
    if (item.relatedMetadata.length) lines.push("", `相关 Front Matter：${item.relatedMetadata.map((key) => `\`${key}\``).join("、")}`);
    lines.push("");
  }
}

lines.push(
  "## Front Matter 元数据",
  "",
  "| 键 | 类型 | 状态 | 默认行为 | 模板要求 |",
  "|---|---|---|---|---|",
  ...manifest.frontMatter.map((item) =>
    `| \`${escapeCell(item.key)}\` | ${escapeCell(item.type)} | ${statusLabels[item.status] ?? item.status} | ${escapeCell(item.defaultValue)} | ${escapeCell(item.requires.join("、") || "无")} |`,
  ),
  "",
);

for (const item of manifest.frontMatter) {
  lines.push(
    `### \`${item.key}\``,
    "",
    `- 能力编号：\`${item.id}\``,
    `- 说明：${item.description}`,
  );
  if (item.allowedValues.length) lines.push(`- 可选值：${item.allowedValues.map((value) => `\`${value}\``).join("、")}`);
  lines.push("", codeBlock(item.example, "yaml"), "");
}

lines.push(
  "## 模板契约",
  "",
  "### 书签",
  "",
  "| 类型 | 名称 | 用途 |",
  "|---|---|---|",
  ...manifest.templateContract.requiredBookmarks.map((bookmark) => `| 必需 | \`${escapeCell(bookmark.name)}\` | ${escapeCell(bookmark.description)} |`),
  ...manifest.templateContract.optionalBookmarks.map((bookmark) => `| 按需 | \`${escapeCell(bookmark.name)}\` | ${escapeCell(bookmark.description)} |`),
  "",
  "### CSS 语义角色",
  "",
  "| 角色 | 选择器 | 回退 |",
  "|---|---|---|",
  ...manifest.templateContract.cssRoles.map((role) =>
    `| ${escapeCell(role.label)}（\`${role.role}\`） | ${escapeCell(role.selectors.map((selector) => `\`${selector}\``).join("、"))} | ${escapeCell(role.fallback)} |`,
  ),
  "",
  "### 校验约定",
  "",
  bullets(manifest.templateContract.validationNotes),
  "",
  "## 已知边界",
  "",
);

for (const item of manifest.limitations) {
  lines.push(
    `### ${item.title}`,
    "",
    `- 能力编号：\`${item.id}\``,
    `- 状态：${statusLabels[item.status] ?? item.status}`,
    `- 摘要：${item.summary}`,
    "",
    bullets(item.details),
    "",
  );
}

lines.push(
  "## 运行环境与工具",
  "",
  `平台：${manifest.tooling.platform}`,
  "",
  "必需：",
  "",
  bullets(manifest.tooling.required),
  "",
  "可选：",
  "",
  bullets(manifest.tooling.optional),
  "",
  "固定版本：",
  "",
  ...Object.entries(manifest.tooling.pinned).map(([name, value]) => `- \`${name}\`：\`${value}\``),
  "",
  "## 兼容性声明",
  "",
  `当前仍待旧链路完整验收：${manifest.pendingLegacyParity.map((item) => `\`${item}\``).join("、")}。机器清单中的 implemented/pendingLegacyParity 字段供审计和自动化使用，不替代上面的用户能力说明。`,
  "",
);

const generated = `${lines.join("\n").replaceAll(/\n{3,}/g, "\n\n").trimEnd()}\n`;

if (checkOnly) {
  let existing;
  try {
    existing = await fs.readFile(outputPath, "utf8");
  } catch (error) {
    if (error?.code === "ENOENT") {
      throw new Error(`Generated capability document is missing: ${path.relative(projectRoot, outputPath)}`, { cause: error });
    }
    throw error;
  }
  if (existing !== generated) {
    throw new Error("Generated capability document is stale. Run npm run capabilities:generate.");
  }
  process.stdout.write("Capability manifest and generated offline document are synchronized.\n");
} else {
  await fs.mkdir(path.dirname(outputPath), { recursive: true });
  await fs.writeFile(outputPath, generated, "utf8");
  process.stdout.write(`Generated ${path.relative(projectRoot, outputPath)}.\n`);
}
