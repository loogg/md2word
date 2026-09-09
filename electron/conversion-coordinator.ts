import { randomUUID } from "node:crypto";
import { constants as fsConstants } from "node:fs";
import * as fs from "node:fs/promises";
import path from "node:path";
import type {
  ConversionEvent,
  ConversionRequest,
  ConversionResult,
  EnvironmentStatus,
  StartConversionInput,
  WorkerConversionResult,
  WorkerRequestFrame,
} from "./contracts";
import { ConversionQueue } from "./conversion-queue";
import { AppError, toPublicError } from "./errors";
import { HandleRegistry, JobResultRegistry, type OutputTargetState } from "./handle-registry";
import type { TemplateStore } from "./template-store";
import type { WorkerJsonlClient } from "./worker-jsonl-client";

export type ConversionCoordinatorFileSystem = Pick<
  typeof fs,
  "mkdir" | "copyFile" | "rename" | "link" | "rm" | "stat" | "lstat" | "unlink" | "access" | "realpath"
>;

export interface ConversionCoordinatorOptions {
  jobsRoot: string;
  handles: HandleRegistry;
  results: JobResultRegistry;
  templates: TemplateStore;
  queue: ConversionQueue;
  worker: WorkerJsonlClient;
  checkEnvironment: () => Promise<EnvironmentStatus>;
  tools: { pandocPath: string; npxPath?: string; mermaidBrowserPath?: string };
  forbiddenOutputRoots?: string[];
  emit: (ownerId: number, event: ConversionEvent) => void;
  fs?: ConversionCoordinatorFileSystem;
  idFactory?: () => string;
  requestIdFactory?: () => string;
  now?: () => Date;
}

interface ActiveRecord {
  ownerId: number;
  finalOutputPath: string;
  workerOutputPath: string;
  jobDirectory: string;
  expectedOutputState: OutputTargetState;
}

function sameWindowsPath(left: string, right: string): boolean {
  return path.resolve(left).toLocaleLowerCase("en-US") === path.resolve(right).toLocaleLowerCase("en-US");
}

function isNodeError(error: unknown, code: string): boolean {
  return error instanceof Error && "code" in error && (error as NodeJS.ErrnoException).code === code;
}

function isUnsupportedHardLinkError(error: unknown): boolean {
  return ["EPERM", "ENOTSUP", "EOPNOTSUPP", "ENOSYS", "EXDEV"].some((code) => isNodeError(error, code));
}

export class ConversionCoordinator {
  readonly #jobsRoot: string;
  readonly #handles: HandleRegistry;
  readonly #results: JobResultRegistry;
  readonly #templates: TemplateStore;
  readonly #queue: ConversionQueue;
  readonly #worker: WorkerJsonlClient;
  readonly #checkEnvironment: () => Promise<EnvironmentStatus>;
  readonly #tools: ConversionCoordinatorOptions["tools"];
  readonly #forbiddenOutputRoots: string[];
  readonly #emit: ConversionCoordinatorOptions["emit"];
  readonly #fs: ConversionCoordinatorFileSystem;
  readonly #idFactory: () => string;
  readonly #requestIdFactory: () => string;
  readonly #now: () => Date;
  readonly #active = new Map<string, ActiveRecord>();
  readonly #settling = new Set<Promise<void>>();
  #shuttingDown = false;

  constructor(options: ConversionCoordinatorOptions) {
    if (!path.isAbsolute(options.jobsRoot) || !path.isAbsolute(options.tools.pandocPath)) {
      throw new AppError("INTERNAL_ERROR", "转换运行路径必须是绝对路径。");
    }
    this.#jobsRoot = path.resolve(options.jobsRoot);
    this.#handles = options.handles;
    this.#results = options.results;
    this.#templates = options.templates;
    this.#queue = options.queue;
    this.#worker = options.worker;
    this.#checkEnvironment = options.checkEnvironment;
    this.#tools = { ...options.tools };
    this.#forbiddenOutputRoots = (options.forbiddenOutputRoots ?? [this.#jobsRoot, options.templates.rootPath])
      .map((root) => path.resolve(root));
    this.#emit = options.emit;
    this.#fs = options.fs ?? fs;
    this.#idFactory = options.idFactory ?? (() => `job-${randomUUID()}`);
    this.#requestIdFactory = options.requestIdFactory ?? (() => `req-${randomUUID()}`);
    this.#now = options.now ?? (() => new Date());
  }

  async start(ownerId: number, input: StartConversionInput): Promise<{ jobId: string }> {
    if (this.#shuttingDown) throw new AppError("APP_SHUTTING_DOWN", "应用正在退出，不能开始新的转换任务。", true);
    const environment = await this.#checkEnvironment();
    if (this.#shuttingDown) throw new AppError("APP_SHUTTING_DOWN", "应用正在退出，不能开始新的转换任务。", true);
    if (environment.overall === "blocked") {
      throw new AppError("ENVIRONMENT_BLOCKED", "Word、Pandoc 或 Worker 尚未就绪，不能开始转换。", true);
    }
    if (
      input.options.mermaidMode === "required"
      && environment.items.find((item) => item.id === "mermaid")?.status !== "ready"
    ) {
      throw new AppError(
        "MERMAID_REQUIRED_UNAVAILABLE",
        "当前模板要求 Mermaid，但本机未检测到可用的 npx 与 Edge/Chrome 渲染环境。请完成环境配置后重新检测，或改用 auto/off。",
        true,
      );
    }
    const sourcePath = this.#handles.resolve(input.sourceHandle, "markdown", ownerId);
    const outputGrant = this.#handles.resolveOutput(input.outputHandle, ownerId);
    const finalOutputPath = outputGrant.absolutePath;
    await this.#assertOutputPathAllowed(finalOutputPath);
    await this.#assertOutputTargetUnchanged(finalOutputPath, outputGrant.expected);
    if (sameWindowsPath(sourcePath, finalOutputPath)) {
      throw new AppError("OUTPUT_PATH_CONFLICT", "输出文件不能覆盖源文件、模板或 CSS。");
    }
    await this.#fs.access(sourcePath);

    const jobId = this.#idFactory();
    if (!/^job-[A-Za-z0-9_-]{8,128}$/.test(jobId) || this.#active.has(jobId)) {
      throw new AppError("INTERNAL_ERROR", "无法创建转换任务标识。");
    }
    const jobDirectory = path.join(this.#jobsRoot, jobId);
    const snapshotDirectory = path.join(jobDirectory, "template");
    const snapshotDocxPath = path.join(snapshotDirectory, "template.docx");
    const snapshotCssPath = path.join(snapshotDirectory, "style.css");
    const workerOutputPath = path.join(jobDirectory, "result.docx");
    let validationFingerprint: string;
    try {
      validationFingerprint = await this.#templates.withSnapshot(input.templateId, async (snapshot) => {
        if (sameWindowsPath(snapshot.docxPath, finalOutputPath) || sameWindowsPath(snapshot.cssPath, finalOutputPath)) {
          throw new AppError("OUTPUT_PATH_CONFLICT", "输出文件不能覆盖源文件、模板或 CSS。");
        }
        await this.#fs.mkdir(snapshotDirectory, { recursive: true });
        try {
          await Promise.all([
            this.#fs.copyFile(snapshot.docxPath, snapshotDocxPath),
            this.#fs.copyFile(snapshot.cssPath, snapshotCssPath),
          ]);
        } catch (error) {
          throw new AppError("TEMPLATE_SNAPSHOT_FAILED", "无法创建不可变模板快照。", true, { cause: error });
        }
        return snapshot.profile.validation.contentFingerprint;
      });
    } catch (error) {
      await this.#safeCleanup(jobDirectory);
      throw error;
    }

    // Output grants are single-use only after all preflight validation and snapshot creation succeed.
    this.#handles.resolveOutput(input.outputHandle, ownerId, { consume: true });
    const request: ConversionRequest = {
      jobId,
      templateId: input.templateId,
      sourcePath,
      outputPath: workerOutputPath,
      templateSnapshot: {
        docxPath: snapshotDocxPath,
        cssPath: snapshotCssPath,
        validationFingerprint,
      },
      tools: { ...this.#tools },
      options: { ...input.options },
    };
    const frame: WorkerRequestFrame = {
      protocolVersion: "1.0",
      requestId: this.#requestIdFactory(),
      command: "convert",
      request,
    };
    const record: ActiveRecord = {
      ownerId,
      finalOutputPath,
      workerOutputPath,
      jobDirectory,
      expectedOutputState: outputGrant.expected,
    };
    this.#active.set(jobId, record);
    let queued: Promise<WorkerConversionResult>;
    try {
      queued = this.#queue.enqueue({
        jobId,
        ownerId,
        start: (onEvent) => this.#worker.start<WorkerConversionResult>(frame, { cwd: jobDirectory, onEvent }),
      });
    } catch (error) {
      this.#active.delete(jobId);
      await this.#safeCleanup(jobDirectory);
      throw error;
    }
    const settling = queued.then(
      (result) => this.#finish(record, result),
      (error) => this.#fail(jobId, record, error),
    );
    this.#settling.add(settling);
    void settling.then(
      () => this.#settling.delete(settling),
      () => this.#settling.delete(settling),
    );
    return { jobId };
  }

  cancel(ownerId: number, jobId: string): void {
    const record = this.#active.get(jobId);
    if (!record || record.ownerId !== ownerId) throw new AppError("JOB_NOT_FOUND", "任务不存在或已经结束。");
    this.#queue.cancel(jobId, ownerId);
  }

  async shutdown(): Promise<void> {
    this.#shuttingDown = true;
    await this.#queue.shutdown();
    await Promise.allSettled([...this.#settling]);
  }

  async #finish(record: ActiveRecord, workerResult: WorkerConversionResult): Promise<void> {
    const jobId = workerResult.jobId;
    try {
      if (workerResult.status === "succeeded") {
        await this.#publishOutput(record.workerOutputPath, record.finalOutputPath, record.expectedOutputState);
        this.#results.register(jobId, record.finalOutputPath, record.ownerId);
        const result = this.#publicResult(workerResult, record.finalOutputPath);
        this.#emit(record.ownerId, {
          jobId,
          timestamp: this.#now().toISOString(),
          kind: "completed",
          stage: "cleanup",
          level: "info",
          message: "Word 文档已生成。",
          result,
        });
      } else if (workerResult.status === "canceled") {
        this.#emit(record.ownerId, {
          jobId,
          timestamp: this.#now().toISOString(),
          kind: "canceled",
          stage: "cleanup",
          level: "info",
          message: "任务已取消，未保留输出文件。",
          result: this.#publicResult(workerResult),
        });
      } else {
        this.#emit(record.ownerId, {
          jobId,
          timestamp: this.#now().toISOString(),
          kind: "failed",
          level: "error",
          message: "转换未完成。",
          result: this.#publicResult(workerResult),
          error: { code: "CONVERSION_FAILED", message: "转换未完成，请查看任务日志。", retryable: true },
        });
      }
    } catch (error) {
      await this.#fail(jobId, record, error);
      return;
    } finally {
      this.#active.delete(jobId);
      await this.#safeCleanup(record.jobDirectory);
    }
  }

  async #fail(jobId: string, record: ActiveRecord, error: unknown): Promise<void> {
    const publicError = toPublicError(error);
    this.#emit(record.ownerId, {
      jobId,
      timestamp: this.#now().toISOString(),
      kind: "failed",
      level: "error",
      message: publicError.message,
      error: publicError,
    });
    this.#active.delete(jobId);
    await this.#safeCleanup(record.jobDirectory);
  }

  #publicResult(workerResult: WorkerConversionResult, outputPath?: string): ConversionResult {
    return {
      jobId: workerResult.jobId,
      status: workerResult.status,
      outputFileName: outputPath ? path.basename(outputPath) : undefined,
      outputDisplayPath: outputPath ? `已保存\\${path.basename(outputPath)}` : undefined,
      diagnosticAvailable: Boolean(workerResult.diagnosticPath),
      durationMs: workerResult.durationMs,
      warnings: workerResult.warnings,
    };
  }

  async #publishOutput(
    workerOutputPath: string,
    finalOutputPath: string,
    expected: OutputTargetState,
  ): Promise<void> {
    const stats = await this.#fs.stat(workerOutputPath);
    if (!stats.isFile() || stats.size === 0) throw new AppError("OUTPUT_INVALID", "Worker 没有生成有效 DOCX。", true);
    await this.#assertOutputPathAllowed(finalOutputPath);
    await this.#assertOutputTargetUnchanged(finalOutputPath, expected);
    const siblingTemporary = path.join(
      path.dirname(finalOutputPath),
      `.${path.basename(finalOutputPath)}.${randomUUID()}.tmp`,
    );
    let siblingTemporaryCreated = false;
    try {
      await this.#fs.copyFile(workerOutputPath, siblingTemporary, fsConstants.COPYFILE_EXCL);
      siblingTemporaryCreated = true;
      await this.#assertOutputPathAllowed(finalOutputPath);
      await this.#assertOutputTargetUnchanged(finalOutputPath, expected);
      if (!expected.existed) {
        try {
          await this.#fs.link(siblingTemporary, finalOutputPath);
        } catch (error) {
          if (!isUnsupportedHardLinkError(error)) throw error;
          // Some Windows volumes and network shares do not support hard links. COPYFILE_EXCL
          // preserves the no-overwrite guarantee, though an interrupted copy can leave a
          // partial newly-created file for the user to inspect or remove.
          await this.#fs.copyFile(siblingTemporary, finalOutputPath, fsConstants.COPYFILE_EXCL);
        }
        await this.#safeUnlink(siblingTemporary);
        siblingTemporaryCreated = false;
        return;
      }

      await this.#fs.rename(siblingTemporary, finalOutputPath);
      siblingTemporaryCreated = false;
    } catch (error) {
      if (siblingTemporaryCreated) await this.#safeUnlink(siblingTemporary);
      if (error instanceof AppError) throw error;
      throw new AppError("OUTPUT_COMMIT_FAILED", "无法原子保存到所选位置；原文件未被有意删除。", true, { cause: error });
    }
  }

  async #assertOutputTargetUnchanged(target: string, expected: OutputTargetState): Promise<void> {
    try {
      const stats = await this.#fs.lstat(target);
      if (!expected.existed || !stats.isFile() || stats.isSymbolicLink()) {
        throw new AppError("OUTPUT_TARGET_CHANGED", "输出位置在确认后发生变化，请重新选择并确认。", true);
      }
      if (
        (expected.size !== undefined && stats.size !== expected.size)
        || (expected.mtimeMs !== undefined && stats.mtimeMs !== expected.mtimeMs)
        || (expected.ctimeMs !== undefined && stats.ctimeMs !== expected.ctimeMs)
        || (expected.ino !== undefined && stats.ino !== expected.ino)
        || (expected.dev !== undefined && stats.dev !== expected.dev)
      ) {
        throw new AppError("OUTPUT_TARGET_CHANGED", "输出文件在确认后已被修改，请重新选择并确认。", true);
      }
    } catch (error) {
      if (isNodeError(error, "ENOENT") && !expected.existed) return;
      if (error instanceof AppError) throw error;
      throw new AppError("OUTPUT_TARGET_CHANGED", "无法确认输出文件仍与选择时一致。", true, { cause: error });
    }
  }

  async #assertOutputPathAllowed(outputPath: string): Promise<void> {
    const candidate = path.resolve(outputPath);
    const isInside = (root: string, value: string) => {
      const relative = path.relative(root, value);
      return relative === "" || (!relative.startsWith(`..${path.sep}`) && relative !== ".." && !path.isAbsolute(relative));
    };
    if (this.#forbiddenOutputRoots.some((root) => isInside(root, candidate))) {
      throw new AppError("OUTPUT_PATH_FORBIDDEN", "输出文件不能保存到应用管理目录、模板库或安装资源中。");
    }

    let realParent: string;
    try {
      realParent = await this.#fs.realpath(path.dirname(candidate));
    } catch (error) {
      throw new AppError("OUTPUT_DIRECTORY_UNREADABLE", "无法确认输出目录。", true, { cause: error });
    }
    const realCandidate = path.join(realParent, path.basename(candidate));
    for (const root of this.#forbiddenOutputRoots) {
      let realRoot = root;
      try {
        realRoot = await this.#fs.realpath(root);
      } catch {
        // A not-yet-created managed directory is still covered by the lexical check above.
      }
      if (isInside(realRoot, realCandidate)) {
        throw new AppError("OUTPUT_PATH_FORBIDDEN", "输出文件不能保存到应用管理目录、模板库或安装资源中。");
      }
    }
  }

  async #safeCleanup(target: string): Promise<void> {
    try {
      await this.#fs.rm(target, { recursive: true, force: true });
    } catch {
      // Startup cleanup and diagnostics handle leftovers without masking the task outcome.
    }
  }

  async #safeUnlink(target: string): Promise<void> {
    try {
      await this.#fs.unlink(target);
    } catch {
      // Never recurse into a path derived from a user-selected output target.
    }
  }
}
