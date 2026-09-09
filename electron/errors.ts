import type { ConversionError, ConversionStage } from "./contracts";

export interface AppErrorOptions extends ErrorOptions {
  stage?: ConversionStage;
}

export class AppError extends Error {
  readonly code: string;
  readonly retryable: boolean;
  readonly stage?: ConversionStage;

  constructor(code: string, message: string, retryable = false, options?: AppErrorOptions) {
    super(message, options);
    this.name = "AppError";
    this.code = code;
    this.retryable = retryable;
    this.stage = options?.stage;
  }
}

export function toPublicError(error: unknown): ConversionError {
  if (error instanceof AppError) {
    return {
      code: error.code,
      message: error.message,
      stage: error.stage,
      retryable: error.retryable,
    };
  }
  return {
    code: "INTERNAL_ERROR",
    message: "操作未完成，请重试；如果问题持续存在，请查看诊断日志。",
    retryable: true,
  };
}
