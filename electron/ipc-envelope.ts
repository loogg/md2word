import type { ConversionError } from "./contracts";

export type IpcEnvelope<T> =
  | { ok: true; value: T }
  | { ok: false; error: ConversionError };

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function isConversionError(value: unknown): value is ConversionError {
  if (!isRecord(value)) return false;
  return (
    typeof value.code === "string"
    && typeof value.message === "string"
    && typeof value.retryable === "boolean"
    && (value.stage === undefined || typeof value.stage === "string")
  );
}

export function isIpcEnvelope<T>(value: unknown): value is IpcEnvelope<T> {
  if (!isRecord(value) || typeof value.ok !== "boolean") return false;
  return value.ok ? Object.hasOwn(value, "value") : isConversionError(value.error);
}
