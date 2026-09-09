import { DEMO_ENVIRONMENT } from "../data/demoData";
import { bundledCapabilityManifest } from "../data/capabilityManifest";
import type {
  AddTemplateInput,
  AppAdapter,
  ConversionEvent,
  ConversionResult,
  ConversionStage,
  EnvironmentStatus,
  PickedFile,
  PickedOutput,
  StartConversionInput,
  TemplateDraft,
  TemplateProfile,
  TemplateValidationReport,
  UpdateTemplateInput,
  ValidateTemplateInput,
  WordStyleMapping,
} from "../types";
import { loadTemplates, resetTemplates, saveTemplates } from "./storage";

const clone = <T,>(value: T): T => structuredClone(value);

const phaseMessages: Array<{ stage: ConversionStage; message: string }> = [
  { stage: "preparing", message: "演示：正在检查 Word、Pandoc 与模板快照…" },
  { stage: "metadata", message: "演示：正在读取 Markdown front matter…" },
  { stage: "pandoc", message: "演示：正在解析 Markdown 与本地资源…" },
  { stage: "mermaid", message: "演示：正在处理图表与图片资源…" },
  { stage: "word-import", message: "演示：正在导入 Word 目标 HTML…" },
  { stage: "template-assembly", message: "演示：正在写入模板书签区并更新目录…" },
  { stage: "word-finalize", message: "演示：正在收口 Word 样式、表格与字段…" },
  { stage: "openxml-finalize", message: "演示：正在执行 Open XML 后处理…" },
  { stage: "cleanup", message: "演示：临时资源清理完成" },
];

const delay = (milliseconds: number, signal: AbortSignal) =>
  new Promise<void>((resolve, reject) => {
    const timer = window.setTimeout(resolve, milliseconds);
    signal.addEventListener(
      "abort",
      () => {
        window.clearTimeout(timer);
        reject(new DOMException("Conversion cancelled", "AbortError"));
      },
      { once: true },
    );
  });

const unresolvedMappings = (): WordStyleMapping[] => [
  { role: "body", cssSelector: "p", requestedStyleName: "", status: "not-configured" },
  { role: "ordered-list", cssSelector: "ol > li", requestedStyleName: "", status: "not-configured" },
  { role: "unordered-list", cssSelector: "ul > li", requestedStyleName: "", status: "not-configured" },
  { role: "heading", headingLevel: 1, cssSelector: "h1", requestedStyleName: "", status: "not-configured" },
  { role: "caption", cssSelector: ".figure-caption", requestedStyleName: "", status: "not-configured" },
  { role: "code-block", cssSelector: "pre", requestedStyleName: "", status: "not-configured" },
  { role: "inline-code", cssSelector: "code.manual-inline-code", requestedStyleName: "", status: "not-configured" },
];

const resolvedMappings = (draft: TemplateDraft): WordStyleMapping[] => {
  const custom = draft.styleMode === "custom";
  const names = custom
    ? {
        body: "示例 正文",
        ordered: "示例 有序列项",
        unordered: "示例 正文",
        heading: "示例 标题1",
        caption: "示例 图示",
        code: "示例 代码",
        inlineCode: "示例 正文",
      }
    : {
        body: "正文",
        ordered: "正文",
        unordered: "正文",
        heading: "标题 1",
        caption: "图注",
        code: "CodeBlock",
        inlineCode: "正文",
      };
  return [
    { role: "body", cssSelector: "p", requestedStyleName: names.body, resolvedStyleId: names.body, resolvedStyleName: names.body, status: "resolved" },
    { role: "ordered-list", cssSelector: "ol > li", requestedStyleName: names.ordered, resolvedStyleId: names.ordered, resolvedStyleName: names.ordered, status: "resolved" },
    { role: "unordered-list", cssSelector: "ul > li", requestedStyleName: names.unordered, resolvedStyleId: names.unordered, resolvedStyleName: names.unordered, status: "resolved" },
    { role: "heading", headingLevel: 1, cssSelector: "h1", requestedStyleName: names.heading, resolvedStyleId: names.heading, resolvedStyleName: names.heading, status: "resolved" },
    { role: "caption", cssSelector: ".figure-caption", requestedStyleName: names.caption, resolvedStyleId: names.caption, resolvedStyleName: names.caption, status: "resolved" },
    { role: "code-block", cssSelector: "pre", requestedStyleName: names.code, resolvedStyleId: names.code, resolvedStyleName: names.code, status: "resolved" },
    { role: "inline-code", cssSelector: "code.manual-inline-code", requestedStyleName: names.inlineCode, resolvedStyleId: names.inlineCode, resolvedStyleName: names.inlineCode, status: "resolved" },
  ];
};

const report = (
  draft: TemplateDraft,
  patch: Partial<TemplateValidationReport> & Pick<TemplateValidationReport, "status" | "summary">,
): TemplateValidationReport => ({
  status: patch.status,
  checkedAt: new Date().toISOString(),
  contentFingerprint: `demo:${draft.templateFileName}:${draft.styleMode}:${draft.styleCssFileName}:${patch.status}`,
  summary: patch.summary,
  issues: patch.issues ?? [],
  capabilities: patch.capabilities ?? {
    bodyRange: true,
    coverTitle: true,
    coverSubtitle: draft.name.includes("说明"),
    versionTables: draft.name.includes("说明") ? ["MANUAL_TABLE_VERSION_HISTORY"] : [],
    codeBlockStyle: true,
  },
  styleMappings: patch.styleMappings ?? resolvedMappings(draft),
});

export function validateTemplateDraft(draft: TemplateDraft): TemplateValidationReport {
  const templateName = draft.templateFileName.toLocaleLowerCase("zh-CN");
  const cssName = draft.styleCssFileName.toLocaleLowerCase("zh-CN");

  if (!templateName.endsWith(".docx")) {
    return report(draft, {
      status: "invalid",
      summary: "请选择有效的 DOCX 模板",
      issues: [{ code: "TEMPLATE_FILE_UNREADABLE", severity: "error", target: "docx", message: "模板文件必须为 .docx 格式。", capabilityId: "CAP-TEMPLATE-DOCX-PACKAGE" }],
      capabilities: { bodyRange: false, coverTitle: false, coverSubtitle: false, versionTables: [], codeBlockStyle: false },
      styleMappings: unresolvedMappings(),
    });
  }

  if (templateName.includes("不可读") || templateName.includes("unreadable") || templateName.includes("损坏")) {
    return report(draft, {
      status: "invalid",
      summary: "模板文件不可读取",
      issues: [{ code: "TEMPLATE_FILE_UNREADABLE", severity: "error", target: "docx", message: "无法读取 DOCX 文件，请确认文件未损坏且当前用户有访问权限。", capabilityId: "CAP-TEMPLATE-DOCX-PACKAGE" }],
      capabilities: { bodyRange: false, coverTitle: false, coverSubtitle: false, versionTables: [], codeBlockStyle: false },
      styleMappings: unresolvedMappings(),
    });
  }

  if (templateName.includes("无书签") || templateName.includes("invalid") || templateName.includes("旧版")) {
    return report(draft, {
      status: "invalid",
      summary: "缺少正文范围书签",
      issues: [{ code: "BODY_BOOKMARK_MISSING", severity: "error", target: "bookmark", message: "未找到 MANUAL_BODY_START / MANUAL_BODY_END，不能添加此模板。", capabilityId: "CAP-TEMPLATE-BODY-RANGE" }],
      capabilities: { bodyRange: false, coverTitle: true, coverSubtitle: false, versionTables: [], codeBlockStyle: true },
      styleMappings: resolvedMappings(draft),
    });
  }

  if (draft.styleMode === "custom" && !cssName.endsWith(".css")) {
    return report(draft, {
      status: "invalid",
      summary: "自定义样式文件无效",
      issues: [{ code: "CSS_FILE_UNREADABLE", severity: "error", target: "css", message: "自定义样式必须选择可读的 .css 文件。", capabilityId: "CAP-TEMPLATE-CSS-MAPPING" }],
      capabilities: { bodyRange: true, coverTitle: true, coverSubtitle: false, versionTables: [], codeBlockStyle: false },
      styleMappings: unresolvedMappings(),
    });
  }

  if (cssName.includes("缺失") || cssName.includes("missing")) {
    const mappings = resolvedMappings(draft).map((mapping) =>
      mapping.role === "code-block"
        ? { ...mapping, resolvedStyleId: undefined, resolvedStyleName: undefined, status: "missing" as const, message: "模板中未解析到该代码块样式。" }
        : mapping,
    );
    return report(draft, {
      status: "invalid",
      summary: "CSS 要求的 Word 样式不存在",
      issues: [{ code: "WORD_STYLE_MISSING", severity: "error", target: "style", message: "CSS 引用的代码块样式未在模板中解析到，不能保存此配置。", capabilityId: "CAP-TEMPLATE-CSS-MAPPING" }],
      capabilities: { bodyRange: true, coverTitle: true, coverSubtitle: draft.name.includes("说明"), versionTables: [], codeBlockStyle: false },
      styleMappings: mappings,
    });
  }

  if (templateName.includes("告警") || templateName.includes("warning")) {
    return report(draft, {
      status: "warning",
      summary: "模板可用，但部分可选书签缺失",
      issues: [{ code: "OPTIONAL_BOOKMARK_MISSING", severity: "warning", target: "bookmark", message: "未找到可选的封面副标题书签。", capabilityId: "CAP-TEMPLATE-OPTIONAL-BOOKMARKS" }],
    });
  }

  return report(draft, { status: "valid", summary: "正文书签与样式映射检查通过" });
}

export function profileFromDraft(draft: TemplateDraft, current?: TemplateProfile): TemplateProfile {
  const now = new Date().toISOString();
  return {
    id: current?.id ?? `template-${crypto.randomUUID()}`,
    name: draft.name.trim(),
    description: draft.description.trim(),
    templateFileName: draft.templateFileName,
    css: { mode: draft.styleMode, fileName: draft.styleMode === "builtin" ? "内置默认样式" : draft.styleCssFileName },
    isDefault: current?.isDefault ?? false,
    mermaidDefaults: { mode: draft.mermaidMode, format: draft.mermaidFormat },
    validation: validateTemplateDraft(draft),
    createdAt: current?.createdAt ?? now,
    updatedAt: now,
  };
}

const pickedFile = (kind: string, fileName: string, size = 0, lastModified = Date.now()): PickedFile => ({
  handle: `mock:${kind}:${crypto.randomUUID()}`,
  fileName,
  size,
  lastModified,
});

const draftFromInput = (
  input: ValidateTemplateInput | AddTemplateInput | UpdateTemplateInput,
  current?: TemplateProfile,
): TemplateDraft => ({
  name: input.name,
  description: input.description,
  templateFileName: input.templateFile?.fileName ?? current?.templateFileName ?? "",
  templateFileHandle: input.templateFile?.handle ?? null,
  styleMode: input.css.mode,
  styleCssFileName: input.css.mode === "builtin" ? "" : input.css.file?.fileName ?? (current?.css.mode === "custom" ? current.css.fileName : ""),
  styleCssHandle: input.css.file?.handle ?? null,
  mermaidMode: input.mermaidDefaults.mode,
  mermaidFormat: input.mermaidDefaults.format,
});

const recomputeOverall = (environment: EnvironmentStatus): EnvironmentStatus["overall"] => {
  if (environment.items.some((item) => item.required && item.status !== "ready")) return "blocked";
  if (environment.items.some((item) => item.status !== "ready")) return "degraded";
  return "ready";
};

export function createBrowserMockAdapter(): AppAdapter {
  let templates = loadTemplates();
  let environment = clone(DEMO_ENVIRONMENT);
  let nextConversionShouldFail = false;
  const outputs = new Map<string, PickedOutput>();
  const listeners = new Set<(event: ConversionEvent) => void>();
  const activeJobs = new Map<string, { controller: AbortController; startedAt: number; output: PickedOutput; shouldFail: boolean }>();

  const emit = (event: ConversionEvent) => listeners.forEach((listener) => listener(clone(event)));
  const persist = () => saveTemplates(templates);
  const templateList = () => clone(templates);

  const runConversion = async (jobId: string) => {
    const job = activeJobs.get(jobId);
    if (!job) return;
    const timestamp = () => new Date().toISOString();
    emit({ jobId, timestamp: timestamp(), kind: "queued", stage: "queued", level: "info", message: "演示：任务已进入单任务队列" });
    try {
      await delay(80, job.controller.signal);
      emit({ jobId, timestamp: timestamp(), kind: "started", stage: "preparing", level: "info", message: "演示：任务开始执行" });
      for (const phase of phaseMessages) {
        await delay(phase.stage === "preparing" ? 170 : 260, job.controller.signal);
        if (job.shouldFail && phase.stage === "template-assembly") {
          const result: ConversionResult = {
            jobId,
            status: "failed",
            diagnosticAvailable: true,
            durationMs: Math.round(performance.now() - job.startedAt),
            warnings: [],
          };
          emit({
            jobId,
            timestamp: timestamp(),
            kind: "failed",
            stage: phase.stage,
            level: "error",
            message: "演示：Word 无法写入目标文件",
            error: { code: "OUTPUT_FILE_LOCKED", message: "目标 Word 文件已打开，请关闭文件后重试。", stage: phase.stage, retryable: true },
            result,
          });
          return;
        }
        emit({ jobId, timestamp: timestamp(), kind: "stage", stage: phase.stage, level: "info", message: phase.message });
      }
      const result: ConversionResult = {
        jobId,
        status: "succeeded",
        outputFileName: job.output.fileName,
        outputDisplayPath: job.output.displayPath,
        durationMs: Math.round(performance.now() - job.startedAt),
        warnings: [],
      };
      emit({ jobId, timestamp: timestamp(), kind: "completed", stage: "cleanup", level: "info", message: "演示生成完成，未写入真实文件", result });
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        await new Promise<void>((resolve) => window.setTimeout(resolve, 120));
        const result: ConversionResult = {
          jobId,
          status: "canceled",
          durationMs: Math.round(performance.now() - job.startedAt),
          warnings: [],
        };
        emit({ jobId, timestamp: timestamp(), kind: "canceled", stage: "cleanup", level: "info", message: "演示：安全取消与清理已完成", result });
      } else {
        emit({
          jobId,
          timestamp: timestamp(),
          kind: "failed",
          level: "error",
          message: "演示任务异常",
          error: { code: "MOCK_CONVERSION_FAILED", message: error instanceof Error ? error.message : "未知演示错误", retryable: true },
        });
      }
    } finally {
      activeJobs.delete(jobId);
    }
  };

  const adapter: AppAdapter = {
    runtimeCapabilities: {
      backend: "browser-mock",
      fileDialogs: "mock",
      templateStorage: "local-storage",
      templateValidation: "mock",
      conversion: "mock",
      environment: "mock",
      shell: "mock",
    },
    initialState: {
      templates: templateList(),
      environment: clone(environment),
      capabilityManifest: clone(bundledCapabilityManifest),
    },
    templates: {
      async list() {
        return templateList();
      },
      async add(input) {
        const draft = draftFromInput(input);
        const profile = profileFromDraft(draft);
        templates = [...templates, profile];
        persist();
        return clone(profile);
      },
      async update(id, input) {
        const current = templates.find((template) => template.id === id);
        if (!current) throw new Error("模板不存在或已被删除。");
        const profile = profileFromDraft(draftFromInput(input, current), current);
        templates = templates.map((template) => (template.id === id ? profile : template));
        persist();
        return clone(profile);
      },
      async remove(id) {
        const removed = templates.find((template) => template.id === id);
        templates = templates.filter((template) => template.id !== id);
        if (removed?.isDefault) {
          const fallbackId = templates.find((template) => template.validation.status !== "invalid")?.id;
          templates = templates.map((template) => ({ ...template, isDefault: template.id === fallbackId }));
        }
        persist();
        return templateList();
      },
      async setDefault(id) {
        const target = templates.find((template) => template.id === id);
        if (!target) throw new Error("模板不存在或已被删除。");
        if (target.validation.status === "invalid") throw new Error("校验失败的模板不能设为默认。");
        templates = templates.map((template) => ({ ...template, isDefault: template.id === id }));
        persist();
        return templateList();
      },
      async validate(id) {
        const current = templates.find((template) => template.id === id);
        if (!current) throw new Error("模板不存在或已被删除。");
        const draft: TemplateDraft = {
          name: current.name,
          description: current.description,
          templateFileName: current.templateFileName,
          templateFileHandle: null,
          styleMode: current.css.mode,
          styleCssFileName: current.css.mode === "custom" ? current.css.fileName : "",
          styleCssHandle: null,
          mermaidMode: current.mermaidDefaults.mode,
          mermaidFormat: current.mermaidDefaults.format,
        };
        const profile = { ...current, validation: validateTemplateDraft(draft), updatedAt: new Date().toISOString() };
        templates = templates.map((template) => (template.id === id ? profile : template));
        persist();
        return clone(profile);
      },
      async validateDraft(input) {
        const current = input.templateId ? templates.find((template) => template.id === input.templateId) : undefined;
        return validateTemplateDraft(draftFromInput(input, current));
      },
    },
    files: {
      async pickMarkdown() {
        return null;
      },
      async registerMarkdown(file) {
        return pickedFile("markdown", file.name, file.size, file.lastModified);
      },
      async pickTemplateDocx() {
        return null;
      },
      async pickCss() {
        return null;
      },
      async pickOutput() {
        return null;
      },
    },
    conversions: {
      async start(input: StartConversionInput) {
        const output = outputs.get(input.outputHandle);
        if (!output) throw new Error("输出选择已失效，请重新选择保存位置。");
        const jobId = `demo-${crypto.randomUUID()}`;
        activeJobs.set(jobId, {
          controller: new AbortController(),
          startedAt: performance.now(),
          output,
          shouldFail: nextConversionShouldFail,
        });
        nextConversionShouldFail = false;
        window.setTimeout(() => void runConversion(jobId), 0);
        return { jobId };
      },
      async cancel(jobId) {
        const job = activeJobs.get(jobId);
        if (!job) return;
        emit({ jobId, timestamp: new Date().toISOString(), kind: "canceling", stage: "cleanup", level: "info", message: "演示：正在安全取消并清理临时资源…" });
        job.controller.abort();
      },
      onEvent(listener) {
        listeners.add(listener);
        return () => listeners.delete(listener);
      },
    },
    environment: {
      async check() {
        environment = { ...environment, checkedAt: new Date().toISOString() };
        return clone(environment);
      },
    },
    capabilities: {
      async describe() {
        return clone(bundledCapabilityManifest);
      },
    },
    shell: {
      async openOutput() {},
      async revealOutput() {},
      async openTemplateLibrary() {},
    },
    demo: {
      async registerFile(kind, file) {
        return pickedFile(kind, file.name, file.size, file.lastModified);
      },
      registerNamedFile(kind, fileName) {
        return pickedFile(kind, fileName);
      },
      async createOutput(directoryLabel, fileName) {
        const normalizedFileName = fileName.toLocaleLowerCase("en-US").endsWith(".docx") ? fileName : `${fileName}.docx`;
        const output: PickedOutput = {
          handle: `mock:output:${crypto.randomUUID()}`,
          fileName: normalizedFileName,
          displayPath: `${directoryLabel || "演示位置"} / ${normalizedFileName}`,
        };
        outputs.set(output.handle, output);
        return clone(output);
      },
      setNextConversionFailure(shouldFail) {
        nextConversionShouldFail = shouldFail;
      },
      async setWordAvailable(available) {
        environment = {
          ...environment,
          checkedAt: new Date().toISOString(),
          items: environment.items.map((item) =>
            item.id === "word"
              ? { ...item, status: available ? "ready" : "blocked", version: available ? "Office 16 · 模拟就绪" : "演示：未检测到 Word.Application" }
              : item,
          ),
        };
        environment = { ...environment, overall: recomputeOverall(environment) };
        return clone(environment);
      },
      async reset() {
        templates = resetTemplates();
        environment = clone(DEMO_ENVIRONMENT);
        return { templates: templateList(), environment: clone(environment) };
      },
    },
  };

  return adapter;
}
