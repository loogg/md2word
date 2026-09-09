import type { WorkerConversionEvent, WorkerConversionResult } from "./contracts";
import { AppError } from "./errors";
import type { WorkerOperation } from "./worker-jsonl-client";

export interface ConversionQueueJob {
  jobId: string;
  ownerId: number;
  timeoutMs?: number;
  start(onEvent: (event: WorkerConversionEvent) => void): WorkerOperation<WorkerConversionResult>;
}

export interface ConversionQueueOptions {
  graceCancelMs?: number;
  defaultTimeoutMs?: number;
  now?: () => Date;
  onEvent?: (ownerId: number, event: WorkerConversionEvent) => void;
}

interface PendingJob {
  job: ConversionQueueJob;
  resolve: (result: WorkerConversionResult) => void;
  reject: (error: unknown) => void;
  enqueuedAt: number;
}

interface ActiveJob extends PendingJob {
  operation: WorkerOperation<WorkerConversionResult>;
  startedAt: number;
  cancellationReason?: "user" | "timeout" | "shutdown";
  timeoutTimer?: ReturnType<typeof setTimeout>;
  forceTimer?: ReturnType<typeof setTimeout>;
}

export class ConversionQueue {
  readonly #pending: PendingJob[] = [];
  readonly #graceCancelMs: number;
  readonly #defaultTimeoutMs: number;
  readonly #now: () => Date;
  readonly #onEvent?: ConversionQueueOptions["onEvent"];
  readonly #idleWaiters = new Set<() => void>();
  #active?: ActiveJob;
  #accepting = true;

  constructor(options: ConversionQueueOptions = {}) {
    this.#graceCancelMs = options.graceCancelMs ?? 15_000;
    this.#defaultTimeoutMs = options.defaultTimeoutMs ?? 15 * 60_000;
    this.#now = options.now ?? (() => new Date());
    this.#onEvent = options.onEvent;
  }

  get activeJobId(): string | undefined {
    return this.#active?.job.jobId;
  }

  get size(): number {
    return this.#pending.length + (this.#active ? 1 : 0);
  }

  enqueue(job: ConversionQueueJob): Promise<WorkerConversionResult> {
    if (!this.#accepting) {
      throw new AppError("APP_SHUTTING_DOWN", "应用正在退出，不能开始新的转换任务。", true);
    }
    if (this.#active?.job.jobId === job.jobId || this.#pending.some((item) => item.job.jobId === job.jobId)) {
      throw new AppError("DUPLICATE_JOB_ID", "任务标识重复。");
    }
    const result = new Promise<WorkerConversionResult>((resolve, reject) => {
      this.#pending.push({ job, resolve, reject, enqueuedAt: Date.now() });
    });
    this.#emit(job.ownerId, {
      jobId: job.jobId,
      timestamp: this.#now().toISOString(),
      kind: "queued",
      stage: "queued",
      level: "info",
      message: `任务已进入队列，前方有 ${this.#pending.length - 1 + (this.#active ? 1 : 0)} 个任务。`,
    });
    queueMicrotask(() => this.#drain());
    return result;
  }

  cancel(jobId: string, ownerId: number): void {
    const pendingIndex = this.#pending.findIndex((item) => item.job.jobId === jobId && item.job.ownerId === ownerId);
    if (pendingIndex >= 0) {
      const [pending] = this.#pending.splice(pendingIndex, 1);
      const result: WorkerConversionResult = {
        jobId,
        status: "canceled",
        durationMs: 0,
        warnings: [],
      };
      pending.resolve(result);
      return;
    }

    const active = this.#active;
    if (!active || active.job.jobId !== jobId || active.job.ownerId !== ownerId) {
      throw new AppError("JOB_NOT_FOUND", "任务不存在或已经结束。");
    }
    if (active.cancellationReason) return;
    active.cancellationReason = "user";
    this.#beginGracefulCancellation(active, "正在安全取消并清理 Word 资源…");
  }

  async shutdown(): Promise<void> {
    this.#accepting = false;
    while (this.#pending.length > 0) {
      const pending = this.#pending.shift()!;
      pending.resolve({
        jobId: pending.job.jobId,
        status: "canceled",
        durationMs: 0,
        warnings: [],
      });
    }

    const active = this.#active;
    if (active && !active.cancellationReason) {
      active.cancellationReason = "shutdown";
      this.#beginGracefulCancellation(active, "应用正在退出，正在安全停止并清理 Word 资源…");
    }
    if (!this.#active) return;
    await new Promise<void>((resolve) => this.#idleWaiters.add(resolve));
  }

  #drain(): void {
    if (!this.#accepting || this.#active || this.#pending.length === 0) {
      this.#notifyIdle();
      return;
    }
    const pending = this.#pending.shift()!;
    let operation: WorkerOperation<WorkerConversionResult>;
    try {
      operation = pending.job.start((event) => {
        if (!(["canceling", "completed", "failed", "canceled"] as string[]).includes(event.kind)) {
          this.#emit(pending.job.ownerId, event);
        }
      });
    } catch (error) {
      pending.reject(error);
      queueMicrotask(() => this.#drain());
      return;
    }
    const active: ActiveJob = {
      ...pending,
      operation,
      startedAt: Date.now(),
    };
    this.#active = active;
    // Main owns queued/canceling events. Worker is the sole source of started/stage/log events;
    // terminal events are filtered above and published by ConversionCoordinator after cleanup.
    const timeoutMs = active.job.timeoutMs ?? this.#defaultTimeoutMs;
    active.timeoutTimer = setTimeout(() => {
      if (this.#active !== active || active.cancellationReason) return;
      active.cancellationReason = "timeout";
      this.#beginGracefulCancellation(active, "任务已超时，正在停止并清理 Word 资源…");
    }, timeoutMs);

    operation.result.then(
      (result) => this.#finish(active, undefined, result),
      (error) => this.#finish(active, error),
    );
  }

  #beginGracefulCancellation(active: ActiveJob, message: string): void {
    this.#emit(active.job.ownerId, {
      jobId: active.job.jobId,
      timestamp: this.#now().toISOString(),
      kind: "canceling",
      level: active.cancellationReason === "timeout" ? "warning" : "info",
      message,
    });
    active.operation.cancel(active.job.jobId);
    active.forceTimer = setTimeout(() => {
      if (this.#active === active) active.operation.forceTerminate();
    }, this.#graceCancelMs);
  }

  #finish(active: ActiveJob, error?: unknown, result?: WorkerConversionResult): void {
    if (this.#active !== active) return;
    if (active.timeoutTimer) clearTimeout(active.timeoutTimer);
    if (active.forceTimer) clearTimeout(active.forceTimer);
    this.#active = undefined;

    if (active.cancellationReason === "timeout") {
      active.reject(new AppError("CONVERSION_TIMEOUT", "转换超时，已停止 Worker；请检查是否残留 Word 进程。", true));
    } else if ((active.cancellationReason === "user" || active.cancellationReason === "shutdown") && error) {
      active.reject(new AppError("CANCEL_CLEANUP_TIMEOUT", "无法在期限内安全取消；请检查 Word 进程。", true, { cause: error }));
    } else if (error) {
      active.reject(error);
    } else if (result) {
      active.resolve(result);
    } else {
      active.reject(new AppError("WORKER_RESULT_MISSING", "转换任务没有返回结果。", true));
    }
    this.#notifyIdle();
    queueMicrotask(() => this.#drain());
  }

  #emit(ownerId: number, event: WorkerConversionEvent): void {
    this.#onEvent?.(ownerId, event);
  }

  #notifyIdle(): void {
    if (this.#active || this.#pending.length > 0) return;
    for (const resolve of this.#idleWaiters) resolve();
    this.#idleWaiters.clear();
  }
}
