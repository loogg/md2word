import { describe, expect, it } from "vitest";
import { isIpcEnvelope } from "../ipc-envelope";

describe("IPC response envelope", () => {
  it("accepts success and structured failure responses", () => {
    expect(isIpcEnvelope({ ok: true, value: undefined })).toBe(true);
    expect(isIpcEnvelope({
      ok: false,
      error: { code: "ENVIRONMENT_BLOCKED", message: "blocked", retryable: true, stage: "preparing" },
    })).toBe(true);
  });

  it("rejects malformed responses", () => {
    expect(isIpcEnvelope({ ok: true })).toBe(false);
    expect(isIpcEnvelope({ ok: false, error: { message: "missing code", retryable: false } })).toBe(false);
  });
});
