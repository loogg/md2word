import type { ChildProcessWithoutNullStreams } from "node:child_process";
import { EventEmitter } from "node:events";
import path from "node:path";
import { PassThrough } from "node:stream";
import { describe, expect, it, vi } from "vitest";
import capabilityManifest from "../../resources/conversion/capabilities.json";
import { WorkerJsonlClient } from "../worker-jsonl-client";

type FakeChild = ChildProcessWithoutNullStreams & {
  stdin: PassThrough;
  stdout: PassThrough;
  stderr: PassThrough;
};

function fakeChild(): FakeChild {
  const child = new EventEmitter() as FakeChild;
  Object.assign(child, {
    stdin: new PassThrough(),
    stdout: new PassThrough(),
    stderr: new PassThrough(),
    kill: vi.fn(() => true),
  });
  return child;
}

function templateResultFrame(styleMapping: Record<string, unknown>) {
  return JSON.stringify({
    protocolVersion: "1.0",
    requestId: "req-template",
    type: "result",
    result: {
      status: "valid",
      checkedAt: "2026-07-16T00:00:00Z",
      contentFingerprint: "sha256:synthetic",
      summary: "synthetic",
      issues: [],
      capabilities: {
        bodyRange: true,
        coverTitle: false,
        coverSubtitle: false,
        versionTables: [],
        codeBlockStyle: true,
      },
      styleMappings: [styleMapping],
    },
  });
}

const validBodyMapping = {
  role: "body",
  cssSelector: "p",
  requestedStyleName: "正文",
  resolvedStyleId: "Body",
  resolvedStyleName: "正文",
  status: "resolved",
};

const validInlineCodeMapping = {
  role: "inline-code",
  cssSelector: "code.manual-inline-code",
  requestedStyleName: "示例 正文",
  resolvedStyleId: "Example0",
  resolvedStyleName: "示例 正文",
  status: "resolved",
};

describe("WorkerJsonlClient", () => {
  it("accepts the bundled versioned capability catalog", async () => {
    const child = fakeChild();
    const client = new WorkerJsonlClient({
      executablePath: path.resolve("synthetic-worker.exe"),
      spawn: () => child,
    });
    const operation = client.start({
      protocolVersion: "1.0",
      requestId: "req-capabilities",
      command: "describe-capabilities",
    });
    child.stdout.write(`${JSON.stringify({
      protocolVersion: "1.0",
      requestId: "req-capabilities",
      type: "result",
      result: capabilityManifest,
    })}\n`);
    child.emit("close", 0);

    await expect(operation.result).resolves.toMatchObject({
      schemaVersion: "1.1",
      productVersion: "0.5.0",
    });
  });

  it("rejects a capability catalog with duplicate public IDs", async () => {
    const child = fakeChild();
    const malformed = structuredClone(capabilityManifest);
    malformed.frontMatter[0].id = malformed.categories[0].items[0].id;
    const client = new WorkerJsonlClient({
      executablePath: path.resolve("synthetic-worker.exe"),
      spawn: () => child,
    });
    const operation = client.start({
      protocolVersion: "1.0",
      requestId: "req-capabilities",
      command: "describe-capabilities",
    });
    child.stdout.write(`${JSON.stringify({
      protocolVersion: "1.0",
      requestId: "req-capabilities",
      type: "result",
      result: malformed,
    })}\n`);
    child.emit("close", 0);

    await expect(operation.result).rejects.toMatchObject({ code: "WORKER_PROTOCOL_INVALID_RESULT" });
  });

  it("passes an isolated environment to the Worker process", async () => {
    const child = fakeChild();
    const spawn = vi.fn(() => child);
    const environment = { PATH: "synthetic-path", npm_config_cache: "synthetic-cache" };
    const client = new WorkerJsonlClient({
      executablePath: path.resolve("synthetic-worker.exe"),
      environment,
      spawn,
    });
    const operation = client.start({ protocolVersion: "1.0", requestId: "req-env", command: "diagnose" });

    expect(spawn).toHaveBeenCalledWith(
      path.resolve("synthetic-worker.exe"),
      [],
      expect.objectContaining({ env: environment }),
    );
    child.stdout.write(`${JSON.stringify({
      protocolVersion: "1.0",
      requestId: "req-env",
      type: "result",
      result: { checkedAt: "2026-07-16T00:00:00Z", overall: "ready", items: [] },
    })}\n`);
    child.emit("close", 0);
    await expect(operation.result).resolves.toMatchObject({ overall: "ready" });
  });

  it("parses fragmented JSONL and waits for a matching terminal frame", async () => {
    const child = fakeChild();
    const event = vi.fn();
    const client = new WorkerJsonlClient({
      executablePath: path.resolve("synthetic-worker.exe"),
      spawn: () => child,
    });
    const operation = client.start(
      { protocolVersion: "1.0", requestId: "req-1", command: "diagnose" },
      { onEvent: event },
    );
    const eventFrame = JSON.stringify({
      protocolVersion: "1.0",
      requestId: "req-1",
      type: "event",
      event: { jobId: "diagnose", timestamp: "2026-07-16T00:00:00Z", kind: "log", stage: "metadata", message: "ok" },
    });
    const resultFrame = JSON.stringify({
      protocolVersion: "1.0",
      requestId: "req-1",
      type: "result",
      result: {
        checkedAt: "2026-07-16T00:00:00Z",
        overall: "ready",
        items: [],
      },
    });
    child.stdout.write(eventFrame.slice(0, 20));
    child.stdout.write(`${eventFrame.slice(20)}\n${resultFrame}\n`);
    child.emit("close", 0);

    await expect(operation.result).resolves.toEqual({
      checkedAt: "2026-07-16T00:00:00Z",
      overall: "ready",
      items: [],
    });
    expect(event).toHaveBeenCalledOnce();
    expect(event).toHaveBeenCalledWith(expect.objectContaining({ stage: "metadata" }));
  });

  it("rejects a clean exit without a terminal frame", async () => {
    const child = fakeChild();
    const client = new WorkerJsonlClient({
      executablePath: path.resolve("synthetic-worker.exe"),
      spawn: () => child,
    });
    const operation = client.start({ protocolVersion: "1.0", requestId: "req-2", command: "diagnose" });
    child.emit("close", 0);
    await expect(operation.result).rejects.toMatchObject({ code: "WORKER_PROTOCOL_MISSING_TERMINAL" });
  });

  it("accepts a deeply valid template style mapping", async () => {
    const child = fakeChild();
    const client = new WorkerJsonlClient({
      executablePath: path.resolve("synthetic-worker.exe"),
      spawn: () => child,
    });
    const operation = client.start({
      protocolVersion: "1.0",
      requestId: "req-template",
      command: "validate-template",
      docxPath: path.resolve("synthetic.docx"),
      cssPath: path.resolve("synthetic.css"),
    });
    child.stdout.write(`${templateResultFrame(validBodyMapping)}\n`);
    child.emit("close", 0);

    await expect(operation.result).resolves.toMatchObject({
      status: "valid",
      styleMappings: [validBodyMapping],
    });
  });

  it("accepts the inline-code style mapping reported by the Worker", async () => {
    const child = fakeChild();
    const client = new WorkerJsonlClient({
      executablePath: path.resolve("synthetic-worker.exe"),
      spawn: () => child,
    });
    const operation = client.start({
      protocolVersion: "1.0",
      requestId: "req-template",
      command: "validate-template",
      docxPath: path.resolve("synthetic.docx"),
      cssPath: path.resolve("synthetic.css"),
    });
    child.stdout.write(`${templateResultFrame(validInlineCodeMapping)}\n`);
    child.emit("close", 0);

    await expect(operation.result).resolves.toMatchObject({
      status: "valid",
      styleMappings: [validInlineCodeMapping],
    });
  });

  it.each([
    ["unknown role", { ...validBodyMapping, role: "list" }],
    ["unknown status", { ...validBodyMapping, status: "ok" }],
    ["heading without a level", { ...validBodyMapping, role: "heading" }],
    ["level on a non-heading", { ...validBodyMapping, headingLevel: 1 }],
    ["non-string selector", { ...validBodyMapping, cssSelector: ["p"] }],
  ])("rejects a template mapping with %s", async (_label, styleMapping) => {
    const child = fakeChild();
    const client = new WorkerJsonlClient({
      executablePath: path.resolve("synthetic-worker.exe"),
      spawn: () => child,
    });
    const operation = client.start({
      protocolVersion: "1.0",
      requestId: "req-template",
      command: "validate-template",
      docxPath: path.resolve("synthetic.docx"),
      cssPath: path.resolve("synthetic.css"),
    });
    child.stdout.write(`${templateResultFrame(styleMapping)}\n`);
    child.emit("close", 0);

    await expect(operation.result).rejects.toMatchObject({ code: "WORKER_PROTOCOL_INVALID_RESULT" });
  });

  it("preserves a validated Worker error stage", async () => {
    const child = fakeChild();
    const client = new WorkerJsonlClient({
      executablePath: path.resolve("synthetic-worker.exe"),
      spawn: () => child,
    });
    const operation = client.start({ protocolVersion: "1.0", requestId: "req-error", command: "diagnose" });
    child.stdout.write(`${JSON.stringify({
      protocolVersion: "1.0",
      requestId: "req-error",
      type: "error",
      error: { code: "PANDOC_FAILED", message: "synthetic", stage: "pandoc", retryable: true },
    })}\n`);
    child.emit("close", 2);

    await expect(operation.result).rejects.toMatchObject({
      code: "PANDOC_FAILED",
      stage: "pandoc",
      retryable: true,
    });
  });

  it("rejects an unrecognized Worker error stage", async () => {
    const child = fakeChild();
    const client = new WorkerJsonlClient({
      executablePath: path.resolve("synthetic-worker.exe"),
      spawn: () => child,
    });
    const operation = client.start({ protocolVersion: "1.0", requestId: "req-error", command: "diagnose" });
    child.stdout.write(`${JSON.stringify({
      protocolVersion: "1.0",
      requestId: "req-error",
      type: "error",
      error: { code: "BROKEN", message: "synthetic", stage: "unknown-stage" },
    })}\n`);
    child.emit("close", 2);

    await expect(operation.result).rejects.toMatchObject({ code: "WORKER_PROTOCOL_INVALID_ERROR" });
  });

  it("terminates active children during shutdown and rejects later starts", async () => {
    const child = fakeChild();
    const client = new WorkerJsonlClient({
      executablePath: path.resolve("synthetic-worker.exe"),
      spawn: () => child,
    });
    const operation = client.start({ protocolVersion: "1.0", requestId: "req-shutdown", command: "diagnose" });

    let shutdownFinished = false;
    const shutdown = client.shutdown().then(() => {
      shutdownFinished = true;
    });
    expect(child.kill).toHaveBeenCalledOnce();
    expect(shutdownFinished).toBe(false);
    expect(() =>
      client.start({ protocolVersion: "1.0", requestId: "req-after-shutdown", command: "diagnose" }),
    ).toThrow(expect.objectContaining({ code: "APP_SHUTTING_DOWN" }));

    child.emit("close", 1);
    await expect(operation.result).rejects.toMatchObject({ code: "WORKER_PROTOCOL_MISSING_TERMINAL" });
    await shutdown;
    expect(shutdownFinished).toBe(true);
  });
});
