import { randomBytes } from "node:crypto";
import path from "node:path";
import type { FileHandleKind } from "./contracts";
import { AppError } from "./errors";

interface HandleRecord {
  kind: FileHandleKind;
  absolutePath: string;
  ownerId: number;
  expiresAt: number;
  singleUse: boolean;
  outputTargetState?: OutputTargetState;
}

export interface OutputTargetState {
  existed: boolean;
  size?: number;
  mtimeMs?: number;
  ctimeMs?: number;
  ino?: number;
  dev?: number;
}

export interface ResolvedOutputGrant {
  absolutePath: string;
  expected: OutputTargetState;
}

export interface HandleRegistryOptions {
  now?: () => number;
  randomToken?: () => string;
  defaultTtlMs?: number;
}

export class HandleRegistry {
  readonly #records = new Map<string, HandleRecord>();
  readonly #now: () => number;
  readonly #randomToken: () => string;
  readonly #defaultTtlMs: number;

  constructor(options: HandleRegistryOptions = {}) {
    this.#now = options.now ?? Date.now;
    this.#randomToken = options.randomToken ?? (() => randomBytes(24).toString("base64url"));
    this.#defaultTtlMs = options.defaultTtlMs ?? 30 * 60_000;
  }

  register(
    kind: FileHandleKind,
    absolutePath: string,
    ownerId: number,
    options: { ttlMs?: number; singleUse?: boolean; outputTargetState?: OutputTargetState } = {},
  ): string {
    if (!path.isAbsolute(absolutePath)) throw new AppError("INTERNAL_ERROR", "文件授权路径无效。");
    const handle = `${kind}_${this.#randomToken()}`;
    this.#records.set(handle, {
      kind,
      absolutePath: path.resolve(absolutePath),
      ownerId,
      expiresAt: this.#now() + (options.ttlMs ?? this.#defaultTtlMs),
      singleUse: options.singleUse ?? kind === "output-docx",
      outputTargetState: options.outputTargetState ? { ...options.outputTargetState } : undefined,
    });
    return handle;
  }

  resolveOutput(handle: string, ownerId: number, options: { consume?: boolean } = {}): ResolvedOutputGrant {
    const absolutePath = this.resolve(handle, "output-docx", ownerId);
    const record = this.#records.get(handle);
    if (!record?.outputTargetState) {
      throw new AppError("INVALID_FILE_HANDLE", "输出授权缺少覆盖确认状态，请重新选择文件。", true);
    }
    if (options.consume && record.singleUse) this.#records.delete(handle);
    return { absolutePath, expected: { ...record.outputTargetState } };
  }

  resolve(
    handle: string,
    expectedKind: FileHandleKind,
    ownerId: number,
    options: { consume?: boolean } = {},
  ): string {
    const record = this.#records.get(handle);
    if (!record || record.kind !== expectedKind || record.ownerId !== ownerId || record.expiresAt < this.#now()) {
      this.#records.delete(handle);
      throw new AppError("INVALID_FILE_HANDLE", "文件授权已失效，请重新选择文件。", true);
    }
    if (options.consume && record.singleUse) this.#records.delete(handle);
    return record.absolutePath;
  }

  revoke(handle: string): void {
    this.#records.delete(handle);
  }

  revokeOwner(ownerId: number): void {
    for (const [handle, record] of this.#records) {
      if (record.ownerId === ownerId) this.#records.delete(handle);
    }
  }

  sweepExpired(): number {
    let removed = 0;
    const now = this.#now();
    for (const [handle, record] of this.#records) {
      if (record.expiresAt < now) {
        this.#records.delete(handle);
        removed += 1;
      }
    }
    return removed;
  }
}

interface OutputRecord {
  absolutePath: string;
  ownerId: number;
  createdAt: number;
}

export class JobResultRegistry {
  readonly #records = new Map<string, OutputRecord>();

  constructor(private readonly now: () => number = Date.now) {}

  register(jobId: string, absolutePath: string, ownerId: number): void {
    if (!path.isAbsolute(absolutePath)) throw new AppError("INTERNAL_ERROR", "输出路径无效。");
    this.#records.set(jobId, { absolutePath: path.resolve(absolutePath), ownerId, createdAt: this.now() });
  }

  resolve(jobId: string, ownerId: number): string {
    const record = this.#records.get(jobId);
    if (!record || record.ownerId !== ownerId) {
      throw new AppError("OUTPUT_NOT_AVAILABLE", "此任务没有可打开的输出文件。", false);
    }
    return record.absolutePath;
  }

  revokeOwner(ownerId: number): void {
    for (const [jobId, record] of this.#records) {
      if (record.ownerId === ownerId) this.#records.delete(jobId);
    }
  }
}
