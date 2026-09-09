import { randomUUID } from "node:crypto";
import path from "node:path";
import type { TemplateValidationReport, ValidateTemplateInput, WorkerRequestFrame } from "./contracts";
import { AppError } from "./errors";
import type { HandleRegistry } from "./handle-registry";
import type { TemplateStore, TemplateValidator } from "./template-store";
import type { WorkerJsonlClient } from "./worker-jsonl-client";

export class WorkerTemplateValidator implements TemplateValidator {
  constructor(
    private readonly worker: WorkerJsonlClient,
    private readonly requestIdFactory: () => string = () => `req-${randomUUID()}`,
  ) {}

  validate(docxPath: string, cssPath: string): Promise<TemplateValidationReport> {
    const request: WorkerRequestFrame = {
      protocolVersion: "1.0",
      requestId: this.requestIdFactory(),
      command: "validate-template",
      docxPath,
      cssPath,
    };
    return this.worker.start<TemplateValidationReport>(request, { timeoutMs: 60_000 }).result;
  }
}

export interface TemplateDraftValidationServiceOptions {
  handles: HandleRegistry;
  templates: TemplateStore;
  validator: TemplateValidator;
  builtinCssPath: string;
  now?: () => number;
}

export class TemplateDraftValidationService {
  readonly #handles: HandleRegistry;
  readonly #templates: TemplateStore;
  readonly #validator: TemplateValidator;
  readonly #builtinCssPath: string;
  readonly #now: () => number;
  readonly #warningApprovals = new Map<number, Map<string, { expiresAt: number; fingerprint: string }>>();

  constructor(options: TemplateDraftValidationServiceOptions) {
    if (!path.isAbsolute(options.builtinCssPath)) throw new AppError("INTERNAL_ERROR", "内置 CSS 路径无效。");
    this.#handles = options.handles;
    this.#templates = options.templates;
    this.#validator = options.validator;
    this.#builtinCssPath = path.resolve(options.builtinCssPath);
    this.#now = options.now ?? Date.now;
  }

  async validate(ownerId: number, input: ValidateTemplateInput): Promise<TemplateValidationReport> {
    const validatePair = async (existing?: Awaited<ReturnType<TemplateStore["getSnapshot"]>>) => {
      const docxPath = input.templateFile
        ? this.#handles.resolve(input.templateFile.handle, "template-docx", ownerId)
        : existing?.docxPath;
      if (!docxPath) throw new AppError("TEMPLATE_FILE_REQUIRED", "请选择 Word 模板。");

      let cssPath: string;
      if (input.css.mode === "builtin") {
        cssPath = this.#builtinCssPath;
      } else if (input.css.file) {
        cssPath = this.#handles.resolve(input.css.file.handle, "css", ownerId);
      } else if (existing?.profile.css.mode === "custom") {
        cssPath = existing.cssPath;
      } else {
        throw new AppError("CSS_FILE_REQUIRED", "自定义模式必须选择 CSS 文件。");
      }
      return this.#validator.validate(docxPath, cssPath);
    };
    const report = input.templateId
      ? await this.#templates.withSnapshot(input.templateId, validatePair)
      : await validatePair();
    const key = this.#approvalKey(input);
    const approvals = this.#warningApprovals.get(ownerId) ?? new Map<string, { expiresAt: number; fingerprint: string }>();
    if (report.status === "warning") {
      approvals.set(key, { expiresAt: this.#now() + 5 * 60_000, fingerprint: report.contentFingerprint });
    }
    else approvals.delete(key);
    this.#warningApprovals.set(ownerId, approvals);
    return report;
  }

  /** A warning may be saved only after the exact file pair was validated and shown by the UI. */
  consumeWarningApproval(ownerId: number, input: ValidateTemplateInput): string | undefined {
    const approvals = this.#warningApprovals.get(ownerId);
    if (!approvals) return undefined;
    const key = this.#approvalKey(input);
    const approval = approvals.get(key);
    approvals.delete(key);
    return approval && approval.expiresAt >= this.#now() ? approval.fingerprint : undefined;
  }

  revokeOwner(ownerId: number): void {
    this.#warningApprovals.delete(ownerId);
  }

  #approvalKey(input: ValidateTemplateInput): string {
    return JSON.stringify({
      templateId: input.templateId ?? null,
      templateHandle: input.templateFile?.handle ?? null,
      cssMode: input.css.mode,
      cssHandle: input.css.file?.handle ?? null,
    });
  }
}
