import { spawn, type ChildProcessWithoutNullStreams, type SpawnOptionsWithoutStdio } from "node:child_process";
import path from "node:path";
import type {
  WorkerCancelFrame,
  WorkerConversionEvent,
  WorkerError,
  WorkerOutputFrame,
  WorkerRequestFrame,
} from "./contracts";
import { AppError } from "./errors";

export interface WorkerOperation<TResult> {
  result: Promise<TResult>;
  cancel(jobId: string): void;
  forceTerminate(): void;
}

export interface WorkerStartOptions {
  cwd?: string;
  timeoutMs?: number;
  maxLineBytes?: number;
  maxStderrBytes?: number;
  onEvent?: (event: WorkerConversionEvent) => void;
}

export interface WorkerJsonlClientOptions {
  executablePath: string;
  environment?: NodeJS.ProcessEnv;
  spawn?: (
    command: string,
    args: readonly string[],
    options: SpawnOptionsWithoutStdio & { stdio: ["pipe", "pipe", "pipe"] },
  ) => ChildProcessWithoutNullStreams;
  redact?: (text: string) => string;
}

function defaultRedact(text: string): string {
  return text
    .replace(/[A-Za-z]:\\(?:[^\\\r\n]+\\)*[^\s\r\n]*/g, "[本机路径]")
    .replace(/\\\\[^\\\s]+\\[^\s\r\n]+/g, "[网络路径]");
}

function isOutputFrame(value: unknown): value is WorkerOutputFrame {
  if (!value || typeof value !== "object") return false;
  const frame = value as Partial<WorkerOutputFrame>;
  return (
    frame.protocolVersion === "1.0" &&
    typeof frame.requestId === "string" &&
    (frame.type === "event" || frame.type === "result" || frame.type === "error")
  );
}

const EVENT_KINDS = new Set(["queued", "started", "stage", "log", "canceling", "completed", "failed", "canceled"]);
const CONVERSION_STAGES = new Set([
  "queued",
  "preparing",
  "metadata",
  "pandoc",
  "mermaid",
  "word-import",
  "template-assembly",
  "word-finalize",
  "openxml-finalize",
  "cleanup",
]);
const STYLE_ROLES = new Set([
  "body",
  "ordered-list",
  "unordered-list",
  "heading",
  "caption",
  "table-caption",
  "code-block",
  "inline-code",
  "table",
  "admonition",
  "figure-image",
]);
const STYLE_MAPPING_STATUSES = new Set(["resolved", "word-fallback", "missing", "ambiguous", "not-configured"]);
const CAPABILITY_STATUSES = new Set(["supported", "conditional", "limited", "unsupported"]);
const ISSUE_SEVERITIES = new Set(["info", "warning", "error"]);
const ISSUE_TARGETS = new Set(["docx", "css", "bookmark", "style", "security"]);

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function isOptionalString(value: unknown): boolean {
  return value === undefined || typeof value === "string";
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === "string");
}

function isValidCapabilityFeature(value: unknown, limitation = false): boolean {
  if (
    !isRecord(value)
    || typeof value.id !== "string"
    || typeof value.title !== "string"
    || typeof value.status !== "string"
    || !CAPABILITY_STATUSES.has(value.status)
    || typeof value.summary !== "string"
    || !isStringArray(value.details)
  ) {
    return false;
  }
  if (limitation) return value.status === "limited" || value.status === "unsupported";
  return isStringArray(value.syntax) && isStringArray(value.relatedMetadata);
}

function isValidCapabilityManifest(value: unknown): boolean {
  if (
    !isRecord(value)
    || value.schemaVersion !== "1.1"
    || typeof value.productVersion !== "string"
    || value.protocolVersion !== "1.0"
    || value.locale !== "zh-CN"
    || typeof value.title !== "string"
    || typeof value.summary !== "string"
    || !Array.isArray(value.categories)
    || value.categories.length === 0
    || !Array.isArray(value.frontMatter)
    || value.frontMatter.length === 0
    || !isRecord(value.templateContract)
    || !Array.isArray(value.limitations)
    || value.limitations.length === 0
    || !isRecord(value.tooling)
    || !isStringArray(value.implemented)
    || !isStringArray(value.pendingLegacyParity)
  ) {
    return false;
  }

  const ids = new Set<string>();
  const registerId = (item: Record<string, unknown>) => {
    if (typeof item.id !== "string" || ids.has(item.id)) return false;
    ids.add(item.id);
    return true;
  };
  for (const category of value.categories) {
    if (
      !isRecord(category)
      || typeof category.id !== "string"
      || typeof category.title !== "string"
      || typeof category.description !== "string"
      || !Array.isArray(category.items)
      || category.items.length === 0
    ) {
      return false;
    }
    for (const item of category.items) {
      if (!isRecord(item) || !isValidCapabilityFeature(item) || !registerId(item)) return false;
    }
  }
  for (const metadata of value.frontMatter) {
    if (
      !isRecord(metadata)
      || !registerId(metadata)
      || typeof metadata.key !== "string"
      || typeof metadata.type !== "string"
      || typeof metadata.defaultValue !== "string"
      || typeof metadata.status !== "string"
      || !CAPABILITY_STATUSES.has(metadata.status)
      || typeof metadata.description !== "string"
      || typeof metadata.example !== "string"
      || !isStringArray(metadata.allowedValues)
      || !isStringArray(metadata.requires)
    ) {
      return false;
    }
  }
  for (const limitation of value.limitations) {
    if (!isRecord(limitation) || !isValidCapabilityFeature(limitation, true) || !registerId(limitation)) return false;
  }

  const bookmarkIsValid = (bookmark: unknown) =>
    isRecord(bookmark) && typeof bookmark.name === "string" && typeof bookmark.description === "string";
  const contract = value.templateContract;
  if (
    !Array.isArray(contract.requiredBookmarks)
    || contract.requiredBookmarks.length === 0
    || !contract.requiredBookmarks.every(bookmarkIsValid)
    || !Array.isArray(contract.optionalBookmarks)
    || !contract.optionalBookmarks.every(bookmarkIsValid)
    || !Array.isArray(contract.cssRoles)
    || contract.cssRoles.length === 0
    || !contract.cssRoles.every((role) =>
      isRecord(role)
      && typeof role.role === "string"
      && STYLE_ROLES.has(role.role)
      && typeof role.label === "string"
      && isStringArray(role.selectors)
      && typeof role.fallback === "string")
    || !isStringArray(contract.validationNotes)
  ) {
    return false;
  }

  const tooling = value.tooling;
  return (
    typeof tooling.platform === "string"
    && isStringArray(tooling.required)
    && isStringArray(tooling.optional)
    && isRecord(tooling.pinned)
    && Object.values(tooling.pinned).every((item) => typeof item === "string")
  );
}

function isValidValidationIssue(value: unknown): boolean {
  return (
    isRecord(value)
    && typeof value.code === "string"
    && typeof value.severity === "string"
    && ISSUE_SEVERITIES.has(value.severity)
    && typeof value.target === "string"
    && ISSUE_TARGETS.has(value.target)
    && typeof value.message === "string"
    && isOptionalString(value.capabilityId)
  );
}

function isValidStyleMapping(value: unknown): boolean {
  if (!isRecord(value) || typeof value.role !== "string" || !STYLE_ROLES.has(value.role)) return false;
  const headingLevelIsValid =
    typeof value.headingLevel === "number" &&
    Number.isInteger(value.headingLevel) &&
    value.headingLevel >= 1 &&
    value.headingLevel <= 6;
  if ((value.role === "heading" && !headingLevelIsValid) || (value.role !== "heading" && value.headingLevel !== undefined)) {
    return false;
  }
  return (
    typeof value.cssSelector === "string" &&
    typeof value.requestedStyleName === "string" &&
    isOptionalString(value.resolvedStyleId) &&
    isOptionalString(value.resolvedStyleName) &&
    typeof value.status === "string" &&
    STYLE_MAPPING_STATUSES.has(value.status) &&
    isOptionalString(value.message)
  );
}

function isValidWorkerError(value: unknown): value is WorkerError {
  if (!isRecord(value) || typeof value.code !== "string" || typeof value.message !== "string") return false;
  if (value.retryable !== undefined && typeof value.retryable !== "boolean") return false;
  return value.stage === undefined || (typeof value.stage === "string" && CONVERSION_STAGES.has(value.stage));
}

function isValidEvent(value: unknown, request: WorkerRequestFrame): boolean {
  if (!isRecord(value) || typeof value.jobId !== "string" || typeof value.timestamp !== "string") return false;
  if (typeof value.kind !== "string" || !EVENT_KINDS.has(value.kind)) return false;
  if (request.command === "convert" && value.jobId !== request.request.jobId) return false;
  return true;
}

function isValidResult(value: unknown, request: WorkerRequestFrame): boolean {
  if (!isRecord(value)) return false;
  if (request.command === "describe-capabilities") {
    return isValidCapabilityManifest(value);
  }
  if (request.command === "convert") {
    return (
      value.jobId === request.request.jobId &&
      typeof value.status === "string" &&
      ["succeeded", "failed", "canceled"].includes(value.status) &&
      typeof value.durationMs === "number" &&
      Array.isArray(value.warnings)
    );
  }
  if (request.command === "validate-template") {
    return (
      typeof value.status === "string" &&
      ["valid", "warning", "invalid"].includes(value.status) &&
      typeof value.contentFingerprint === "string" &&
      Array.isArray(value.issues) &&
      value.issues.every(isValidValidationIssue) &&
      Array.isArray(value.styleMappings) &&
      value.styleMappings.every(isValidStyleMapping)
    );
  }
  return (
    typeof value.checkedAt === "string" &&
    typeof value.overall === "string" &&
    ["ready", "degraded", "blocked"].includes(value.overall) &&
    Array.isArray(value.items)
  );
}

export class WorkerJsonlClient {
  readonly #executablePath: string;
  readonly #spawn: NonNullable<WorkerJsonlClientOptions["spawn"]>;
  readonly #redact: (text: string) => string;
  readonly #environment: NodeJS.ProcessEnv | undefined;
  readonly #activeOperations = new Set<WorkerOperation<unknown>>();
  #accepting = true;

  constructor(options: WorkerJsonlClientOptions) {
    if (!path.isAbsolute(options.executablePath)) {
      throw new AppError("INTERNAL_ERROR", "Worker 路径必须来自应用资源目录。");
    }
    this.#executablePath = path.resolve(options.executablePath);
    this.#spawn = options.spawn ?? ((command, args, spawnOptions) => spawn(command, args, spawnOptions));
    this.#redact = options.redact ?? defaultRedact;
    this.#environment = options.environment ? { ...options.environment } : undefined;
  }

  start<TResult>(request: WorkerRequestFrame, options: WorkerStartOptions = {}): WorkerOperation<TResult> {
    if (!this.#accepting) {
      throw new AppError("APP_SHUTTING_DOWN", "应用正在退出，不能启动 Worker。", true);
    }
    const child = this.#spawn(this.#executablePath, [], {
      cwd: options.cwd,
      windowsHide: true,
      shell: false,
      stdio: ["pipe", "pipe", "pipe"],
      env: this.#environment,
    });
    const maxLineBytes = options.maxLineBytes ?? 1024 * 1024;
    const maxStderrBytes = options.maxStderrBytes ?? 64 * 1024;
    let stdoutBuffer = "";
    let stderrBuffer = "";
    let terminal: { kind: "result"; value: TResult } | { kind: "error"; value: WorkerError } | undefined;
    let fatalError: Error | undefined;
    let settled = false;
    let cancelSent = false;
    let timeout: ReturnType<typeof setTimeout> | undefined;
    let resolveResult!: (value: TResult) => void;
    let rejectResult!: (reason: unknown) => void;
    const result = new Promise<TResult>((resolve, reject) => {
      resolveResult = resolve;
      rejectResult = reject;
    });

    const fail = (error: Error, terminate = true) => {
      if (fatalError || settled) return;
      fatalError = error;
      if (terminate) child.kill();
    };

    const parseLine = (line: string) => {
      if (!line.trim()) return;
      let parsed: unknown;
      try {
        parsed = JSON.parse(line);
      } catch (error) {
        fail(new AppError("WORKER_PROTOCOL_INVALID_JSON", "Worker 返回了无效协议帧。", false, { cause: error }));
        return;
      }
      if (!isOutputFrame(parsed)) {
        fail(new AppError("WORKER_PROTOCOL_INVALID_FRAME", "Worker 返回了未知协议帧。"));
        return;
      }
      if (parsed.requestId !== request.requestId) {
        fail(new AppError("WORKER_PROTOCOL_REQUEST_MISMATCH", "Worker 响应与请求不匹配。"));
        return;
      }
      if (parsed.type === "event") {
        if (!isValidEvent(parsed.event, request)) {
          fail(new AppError("WORKER_PROTOCOL_INVALID_EVENT", "Worker 事件格式无效。"));
          return;
        }
        options.onEvent?.(parsed.event);
        return;
      }
      if (terminal) {
        fail(new AppError("WORKER_PROTOCOL_MULTIPLE_TERMINALS", "Worker 返回了多个终态帧。"));
        return;
      }
      if (parsed.type === "result") {
        if (!isValidResult(parsed.result, request)) {
          fail(new AppError("WORKER_PROTOCOL_INVALID_RESULT", "Worker 结果格式无效或任务标识不匹配。"));
          return;
        }
        terminal = { kind: "result", value: parsed.result as TResult };
      } else {
        if (!isValidWorkerError(parsed.error)) {
          fail(new AppError("WORKER_PROTOCOL_INVALID_ERROR", "Worker 错误帧格式无效。"));
          return;
        }
        terminal = { kind: "error", value: parsed.error };
      }
    };

    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => {
      if (fatalError) return;
      stdoutBuffer += chunk;
      if (Buffer.byteLength(stdoutBuffer, "utf8") > maxLineBytes && !stdoutBuffer.includes("\n")) {
        fail(new AppError("WORKER_PROTOCOL_LINE_TOO_LARGE", "Worker 协议帧超过限制。"));
        return;
      }
      let newline = stdoutBuffer.indexOf("\n");
      while (newline >= 0) {
        const line = stdoutBuffer.slice(0, newline).replace(/\r$/, "");
        stdoutBuffer = stdoutBuffer.slice(newline + 1);
        if (Buffer.byteLength(line, "utf8") > maxLineBytes) {
          fail(new AppError("WORKER_PROTOCOL_LINE_TOO_LARGE", "Worker 协议帧超过限制。"));
          return;
        }
        parseLine(line);
        newline = stdoutBuffer.indexOf("\n");
      }
    });
    child.stderr.on("data", (chunk: string) => {
      if (Buffer.byteLength(stderrBuffer, "utf8") >= maxStderrBytes) return;
      stderrBuffer = this.#redact(`${stderrBuffer}${chunk}`).slice(0, maxStderrBytes);
    });
    child.stdin.on("error", (error) => {
      fail(new AppError("WORKER_STDIN_FAILED", "无法向 Worker 发送控制指令。", true, { cause: error }));
    });
    child.on("error", (error) => {
      fail(new AppError("WORKER_START_FAILED", "无法启动 C# Word Worker。", true, { cause: error }), false);
    });
    child.on("close", (code) => {
      if (settled) return;
      if (timeout) clearTimeout(timeout);
      if (stdoutBuffer.trim() && !fatalError) parseLine(stdoutBuffer.replace(/\r$/, ""));
      settled = true;
      if (fatalError) {
        rejectResult(fatalError);
        return;
      }
      if (!terminal) {
        rejectResult(
          new AppError(
            "WORKER_PROTOCOL_MISSING_TERMINAL",
            stderrBuffer ? "Worker 异常退出且未返回结果。" : "Worker 未返回终态结果。",
            true,
          ),
        );
        return;
      }
      if (terminal.kind === "error") {
        rejectResult(
          new AppError(terminal.value.code, terminal.value.message, terminal.value.retryable ?? false, {
            stage: terminal.value.stage,
          }),
        );
        return;
      }
      const resultValue = terminal.value as { status?: string };
      const acceptableExit = code === 0 || (resultValue?.status === "canceled" && code === 5);
      if (!acceptableExit) {
        rejectResult(new AppError("WORKER_EXIT_CODE_MISMATCH", "Worker 结果与退出状态不一致。", true));
        return;
      }
      resolveResult(terminal.value);
    });

    const initialFrame = `${JSON.stringify(request)}\n`;
    if (request.command === "convert") {
      child.stdin.write(initialFrame, "utf8", (error) => {
        if (error) fail(new AppError("WORKER_STDIN_FAILED", "无法向 Worker 发送转换请求。", true, { cause: error }));
      });
    }
    else child.stdin.end(initialFrame, "utf8");

    if (options.timeoutMs && options.timeoutMs > 0) {
      timeout = setTimeout(() => {
        fail(new AppError("WORKER_TIMEOUT", "Worker 执行超时。", true));
      }, options.timeoutMs);
    }

    const operation: WorkerOperation<TResult> = {
      result,
      cancel: (jobId: string) => {
        if (request.command !== "convert" || cancelSent || settled) return;
        cancelSent = true;
        const frame: WorkerCancelFrame = {
          protocolVersion: "1.0",
          requestId: request.requestId,
          command: "cancel",
          jobId,
        };
        child.stdin.write(`${JSON.stringify(frame)}\n`, "utf8", (error) => {
          if (error) fail(new AppError("WORKER_STDIN_FAILED", "无法向 Worker 发送取消指令。", true, { cause: error }));
        });
      },
      forceTerminate: () => {
        if (!settled) child.kill();
      },
    };
    this.#activeOperations.add(operation as WorkerOperation<unknown>);
    void result.then(
      () => this.#activeOperations.delete(operation as WorkerOperation<unknown>),
      () => this.#activeOperations.delete(operation as WorkerOperation<unknown>),
    );
    return operation;
  }

  async shutdown(): Promise<void> {
    this.#accepting = false;
    const operations = [...this.#activeOperations];
    for (const operation of operations) operation.forceTerminate();
    await Promise.allSettled(operations.map((operation) => operation.result));
  }

  forceTerminateAll(): void {
    this.#accepting = false;
    for (const operation of this.#activeOperations) operation.forceTerminate();
  }
}
