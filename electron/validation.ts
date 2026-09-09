import path from "node:path";
import type {
  AddTemplateInput,
  PickedFile,
  StartConversionInput,
  UpdateTemplateInput,
  ValidateTemplateInput,
} from "./contracts";
import { AppError } from "./errors";

const HANDLE_PATTERN = /^(markdown|template-docx|css|output-docx)_[A-Za-z0-9_-]{16,128}$/;
const ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/;

function hasForbiddenControl(value: string, allowWhitespace = false): boolean {
  return [...value].some((character) => {
    const code = character.charCodeAt(0);
    return code < 32 && !(allowWhitespace && (code === 9 || code === 10 || code === 13));
  });
}

function requireRecord(value: unknown, label: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new AppError("INVALID_INPUT", `${label}格式无效。`);
  }
  return value as Record<string, unknown>;
}

function requireString(value: unknown, label: string, maximum = 256): string {
  if (typeof value !== "string") throw new AppError("INVALID_INPUT", `${label}必须是文本。`);
  const normalized = value.trim();
  if (!normalized || normalized.length > maximum || hasForbiddenControl(normalized)) {
    throw new AppError("INVALID_INPUT", `${label}不能为空或过长。`);
  }
  return normalized;
}

function boundedString(value: unknown, label: string, maximum: number): string {
  if (typeof value !== "string" || value.length > maximum || hasForbiddenControl(value, true)) {
    throw new AppError("INVALID_INPUT", `${label}格式无效。`);
  }
  return value.trim();
}

export function assertOpaqueHandle(value: unknown, expectedPrefix?: string): string {
  const handle = requireString(value, "文件句柄", 160);
  if (!HANDLE_PATTERN.test(handle) || (expectedPrefix && !handle.startsWith(`${expectedPrefix}_`))) {
    throw new AppError("INVALID_FILE_HANDLE", "文件授权已失效，请重新选择文件。", true);
  }
  return handle;
}

export function assertId(value: unknown, label = "标识"): string {
  const id = requireString(value, label, 128);
  if (!ID_PATTERN.test(id)) throw new AppError("INVALID_INPUT", `${label}格式无效。`);
  return id;
}

function parsePickedFile(value: unknown, expectedPrefix: "markdown" | "template-docx" | "css"): PickedFile {
  const record = requireRecord(value, "所选文件");
  const size = record.size;
  const lastModified = record.lastModified;
  if (typeof size !== "number" || !Number.isFinite(size) || size < 0) {
    throw new AppError("INVALID_INPUT", "文件大小无效。");
  }
  if (typeof lastModified !== "number" || !Number.isFinite(lastModified) || lastModified < 0) {
    throw new AppError("INVALID_INPUT", "文件时间无效。");
  }
  return {
    handle: assertOpaqueHandle(record.handle, expectedPrefix),
    fileName: requireString(record.fileName, "文件名", 260),
    size,
    lastModified,
  };
}

function parseMermaid(value: unknown): UpdateTemplateInput["mermaidDefaults"] {
  const record = requireRecord(value, "Mermaid 配置");
  if (!(["auto", "off", "required"] as unknown[]).includes(record.mode)) {
    throw new AppError("INVALID_INPUT", "Mermaid 模式无效。");
  }
  if (record.format !== "png" && record.format !== "svg") {
    throw new AppError("INVALID_INPUT", "Mermaid 格式无效。");
  }
  return {
    mode: record.mode as "auto" | "off" | "required",
    format: record.format,
  };
}

function parseTemplateUpdateBase(value: unknown): UpdateTemplateInput {
  const record = requireRecord(value, "模板配置");
  const css = requireRecord(record.css, "CSS 配置");
  if (css.mode !== "builtin" && css.mode !== "custom") throw new AppError("INVALID_INPUT", "CSS 模式无效。");
  return {
    name: requireString(record.name, "模板名称", 100),
    description: boundedString(record.description, "模板说明", 500),
    templateFile:
      record.templateFile === undefined ? undefined : parsePickedFile(record.templateFile, "template-docx"),
    css: {
      mode: css.mode,
      file: css.file === undefined ? undefined : parsePickedFile(css.file, "css"),
    },
    mermaidDefaults: parseMermaid(record.mermaidDefaults),
  };
}

export function parseAddTemplateInput(value: unknown): AddTemplateInput {
  const parsed = parseTemplateUpdateBase(value);
  if (!parsed.templateFile) throw new AppError("INVALID_INPUT", "请选择 Word 模板。");
  if (parsed.css.mode === "custom" && !parsed.css.file) {
    throw new AppError("INVALID_INPUT", "自定义模式必须选择 CSS 文件。");
  }
  return parsed as AddTemplateInput;
}

export function parseUpdateTemplateInput(value: unknown): UpdateTemplateInput {
  return parseTemplateUpdateBase(value);
}

export function parseValidateTemplateInput(value: unknown): ValidateTemplateInput {
  const record = requireRecord(value, "模板校验配置");
  return {
    ...parseTemplateUpdateBase(record),
    templateId: record.templateId === undefined ? undefined : assertId(record.templateId, "模板 ID"),
  };
}

export function parseStartConversionInput(value: unknown): StartConversionInput {
  const record = requireRecord(value, "转换请求");
  const options = requireRecord(record.options, "转换选项");
  if (!Number.isInteger(options.tocDepth) || Number(options.tocDepth) < 1 || Number(options.tocDepth) > 6) {
    throw new AppError("INVALID_INPUT", "目录深度必须在 1 到 6 之间。");
  }
  if (!["auto", "off", "required"].includes(String(options.mermaidMode))) {
    throw new AppError("INVALID_INPUT", "Mermaid 模式无效。");
  }
  if (options.mermaidFormat !== "png" && options.mermaidFormat !== "svg") {
    throw new AppError("INVALID_INPUT", "Mermaid 格式无效。");
  }
  return {
    templateId: assertId(record.templateId, "模板 ID"),
    sourceHandle: assertOpaqueHandle(record.sourceHandle, "markdown"),
    outputHandle: assertOpaqueHandle(record.outputHandle, "output-docx"),
    options: {
      tocDepth: Number(options.tocDepth),
      mermaidMode: options.mermaidMode as "auto" | "off" | "required",
      mermaidFormat: options.mermaidFormat,
    },
  };
}

export function normalizeSuggestedDocxName(value: unknown): string {
  const suggested = requireString(value, "建议文件名", 180);
  const fileName = path.basename(suggested).replace(/[<>:"/\\|?*]/g, "_");
  if (fileName === "." || fileName === "..") throw new AppError("INVALID_INPUT", "建议文件名无效。");
  return fileName.toLowerCase().endsWith(".docx") ? fileName : `${fileName}.docx`;
}

export function assertExtension(filePath: string, allowed: readonly string[], label: string): void {
  const extension = path.extname(filePath).toLowerCase();
  if (!allowed.includes(extension)) throw new AppError("INVALID_FILE_TYPE", `${label}文件类型不受支持。`);
}
