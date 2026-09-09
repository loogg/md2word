import path from "node:path";
import type { ConversionEvent, WorkerConversionEvent } from "./contracts";

export function redactUserText(value: string | undefined): string | undefined {
  if (!value) return value;
  return value
    .replace(/[A-Za-z]:\\(?:[^\\\r\n]+\\)*[^\s\r\n]*/g, "[本机路径]")
    .replace(/\\\\[^\\\s]+\\[^\s\r\n]+/g, "[网络路径]")
    .slice(0, 2000);
}

export function sanitizeWorkerEvent(event: WorkerConversionEvent): ConversionEvent {
  const result = event.result
    ? {
        jobId: event.result.jobId,
        status: event.result.status,
        outputFileName: event.result.outputPath ? path.basename(event.result.outputPath) : undefined,
        outputDisplayPath: event.result.outputPath ? `已保存\\${path.basename(event.result.outputPath)}` : undefined,
        diagnosticAvailable: Boolean(event.result.diagnosticPath),
        durationMs: event.result.durationMs,
        warnings: event.result.warnings.map((warning) => redactUserText(warning) ?? ""),
      }
    : undefined;
  return {
    jobId: event.jobId,
    timestamp: event.timestamp,
    kind: event.kind,
    stage: event.stage,
    level: event.level,
    progress: event.progress,
    message: redactUserText(event.message),
    result,
    error: event.error
      ? {
          code: event.error.code,
          message: redactUserText(event.error.message) ?? "转换失败。",
          stage: event.error.stage,
          retryable: event.error.retryable,
        }
      : undefined,
  };
}
