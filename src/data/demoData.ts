import type { EnvironmentStatus, TemplateProfile, WordStyleMapping } from "../types";

const createdAt = "2026-07-15T09:00:00.000Z";

const styleMappings = (names: {
  body: string;
  orderedList: string;
  unorderedList: string;
  heading: string;
  caption: string;
  codeBlock: string;
  inlineCode: string;
}): WordStyleMapping[] => [
  { role: "body", cssSelector: "p", requestedStyleName: names.body, resolvedStyleId: names.body, resolvedStyleName: names.body, status: "resolved" },
  { role: "ordered-list", cssSelector: "ol > li", requestedStyleName: names.orderedList, resolvedStyleId: names.orderedList, resolvedStyleName: names.orderedList, status: "resolved" },
  { role: "unordered-list", cssSelector: "ul > li", requestedStyleName: names.unorderedList, resolvedStyleId: names.unorderedList, resolvedStyleName: names.unorderedList, status: "resolved" },
  { role: "heading", headingLevel: 1, cssSelector: "h1", requestedStyleName: names.heading, resolvedStyleId: names.heading, resolvedStyleName: names.heading, status: "resolved" },
  { role: "caption", cssSelector: ".figure-caption", requestedStyleName: names.caption, resolvedStyleId: names.caption, resolvedStyleName: names.caption, status: "resolved" },
  { role: "code-block", cssSelector: "pre", requestedStyleName: names.codeBlock, resolvedStyleId: names.codeBlock, resolvedStyleName: names.codeBlock, status: "resolved" },
  { role: "inline-code", cssSelector: "code.manual-inline-code", requestedStyleName: names.inlineCode, resolvedStyleId: names.inlineCode, resolvedStyleName: names.inlineCode, status: "resolved" },
];

export const DEMO_TEMPLATES: TemplateProfile[] = [
  {
    id: "manual-template",
    name: "使用说明书",
    description: "适用于产品使用说明、操作手册与标准交付文档。",
    templateFileName: "使用说明书模板.docx",
    css: { mode: "builtin", fileName: "内置默认样式" },
    isDefault: true,
    mermaidDefaults: { mode: "auto", format: "png" },
    validation: {
      status: "valid",
      checkedAt: createdAt,
      contentFingerprint: "demo:manual-template",
      summary: "正文书签与默认样式可用",
      issues: [],
      capabilities: {
        bodyRange: true,
        coverTitle: true,
        coverSubtitle: true,
        versionTables: ["MANUAL_TABLE_VERSION_HISTORY"],
        codeBlockStyle: true,
      },
      styleMappings: styleMappings({
        body: "正文",
        orderedList: "正文",
        unorderedList: "正文",
        heading: "标题 1",
        caption: "图注",
        codeBlock: "CodeBlock",
        inlineCode: "正文",
      }),
    },
    createdAt,
    updatedAt: createdAt,
  },
  {
    id: "tech-report-template",
    name: "技术报告",
    description: "适用于技术分析、问题定位、方案评审与验证报告。",
    templateFileName: "技术报告模板.docx",
    css: { mode: "custom", fileName: "docx-worddom-style.css" },
    isDefault: false,
    mermaidDefaults: { mode: "auto", format: "png" },
    validation: {
      status: "valid",
      checkedAt: createdAt,
      contentFingerprint: "demo:tech-report-template",
      summary: "DOCX 与 CSS 样式映射完整",
      issues: [],
      capabilities: {
        bodyRange: true,
        coverTitle: true,
        coverSubtitle: false,
        versionTables: [],
        codeBlockStyle: true,
      },
      styleMappings: styleMappings({
        body: "示例 正文",
        orderedList: "示例 有序列项",
        unorderedList: "示例 正文",
        heading: "示例 标题1",
        caption: "示例 图示",
        codeBlock: "示例 代码",
        inlineCode: "示例 正文",
      }),
    },
    createdAt,
    updatedAt: createdAt,
  },
];

export const DEMO_ENVIRONMENT: EnvironmentStatus = {
  checkedAt: createdAt,
  overall: "degraded",
  items: [
    {
      id: "windows",
      name: "Windows",
      version: "Windows 11 · x64",
      detail: "Word DOM 自动化仅面向 Windows 桌面环境。",
      status: "ready",
      required: true,
    },
    {
      id: "word",
      name: "Microsoft Word",
      version: "Office 16 · 模拟就绪",
      detail: "演示状态；正式版由 Worker 诊断 Word.Application。",
      status: "ready",
      required: true,
    },
    {
      id: "pandoc",
      name: "Pandoc",
      version: "3.9 · 模拟就绪",
      detail: "演示状态；正式版负责 Markdown 语义解析与 Lua 过滤器处理。",
      status: "ready",
      required: true,
    },
    {
      id: "worker",
      name: "C# Word Worker",
      version: "浏览器 mock · 模拟就绪",
      detail: "演示状态；不会启动真实 C# 进程或 Word COM。",
      status: "ready",
      required: true,
    },
    {
      id: "mermaid",
      name: "Mermaid CLI",
      version: "可选组件",
      detail: "Markdown 含 Mermaid 图时用于渲染 PNG/SVG。",
      status: "optional-missing",
      required: false,
    },
  ],
};
