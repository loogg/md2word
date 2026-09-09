import { describe, expect, it, vi } from "vitest";
import type { WorkerConversionResult } from "../contracts";
import { ConversionQueue } from "../conversion-queue";

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

describe("ConversionQueue", () => {
  it("starts jobs strictly FIFO with one active Worker", async () => {
    const starts: string[] = [];
    const first = deferred<WorkerConversionResult>();
    const second = deferred<WorkerConversionResult>();
    const queue = new ConversionQueue();
    const result1 = queue.enqueue({
      jobId: "job-11111111",
      ownerId: 1,
      start: () => {
        starts.push("first");
        return { result: first.promise, cancel: vi.fn(), forceTerminate: vi.fn() };
      },
    });
    const result2 = queue.enqueue({
      jobId: "job-22222222",
      ownerId: 1,
      start: () => {
        starts.push("second");
        return { result: second.promise, cancel: vi.fn(), forceTerminate: vi.fn() };
      },
    });
    await Promise.resolve();
    expect(starts).toEqual(["first"]);
    first.resolve({ jobId: "job-11111111", status: "succeeded", durationMs: 1, warnings: [] });
    await result1;
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(starts).toEqual(["first", "second"]);
    second.resolve({ jobId: "job-22222222", status: "succeeded", durationMs: 1, warnings: [] });
    await result2;
  });

  it("cancels a queued job without starting its Worker", async () => {
    const active = deferred<WorkerConversionResult>();
    const secondStart = vi.fn();
    const queue = new ConversionQueue();
    const first = queue.enqueue({
      jobId: "job-aaaaaaaa",
      ownerId: 9,
      start: () => ({ result: active.promise, cancel: vi.fn(), forceTerminate: vi.fn() }),
    });
    const second = queue.enqueue({
      jobId: "job-bbbbbbbb",
      ownerId: 9,
      start: secondStart,
    });
    await Promise.resolve();
    queue.cancel("job-bbbbbbbb", 9);
    await expect(second).resolves.toMatchObject({ status: "canceled" });
    expect(secondStart).not.toHaveBeenCalled();
    active.resolve({ jobId: "job-aaaaaaaa", status: "canceled", durationMs: 1, warnings: [] });
    await first;
  });

  it("forwards exactly one Worker-owned started event", async () => {
    const active = deferred<WorkerConversionResult>();
    const onEvent = vi.fn();
    const queue = new ConversionQueue({ onEvent });
    const result = queue.enqueue({
      jobId: "job-started1",
      ownerId: 7,
      start: (emit) => {
        emit({
          jobId: "job-started1",
          timestamp: "2026-07-16T00:00:00.000Z",
          kind: "started",
          stage: "preparing",
          level: "info",
          message: "Worker started",
        });
        return { result: active.promise, cancel: vi.fn(), forceTerminate: vi.fn() };
      },
    });
    await Promise.resolve();

    const startedEvents = onEvent.mock.calls
      .map(([, event]) => event)
      .filter((event) => event.kind === "started");
    expect(startedEvents).toHaveLength(1);
    expect(startedEvents[0]).toMatchObject({ message: "Worker started" });

    active.resolve({ jobId: "job-started1", status: "succeeded", durationMs: 1, warnings: [] });
    await result;
  });

  it("cancels pending and active jobs, waits through forced shutdown, and rejects new work", async () => {
    vi.useFakeTimers();
    try {
      const active = deferred<WorkerConversionResult>();
      const cancel = vi.fn();
      const forceTerminate = vi.fn(() => {
        active.resolve({ jobId: "job-shutdown-active", status: "canceled", durationMs: 1, warnings: [] });
      });
      const pendingStart = vi.fn();
      const queue = new ConversionQueue({ graceCancelMs: 25 });
      const activeResult = queue.enqueue({
        jobId: "job-shutdown-active",
        ownerId: 4,
        start: () => ({ result: active.promise, cancel, forceTerminate }),
      });
      const pendingResult = queue.enqueue({
        jobId: "job-shutdown-pending",
        ownerId: 4,
        start: pendingStart,
      });
      await Promise.resolve();

      let shutdownFinished = false;
      const shutdown = queue.shutdown().then(() => {
        shutdownFinished = true;
      });

      await expect(pendingResult).resolves.toMatchObject({
        jobId: "job-shutdown-pending",
        status: "canceled",
      });
      expect(pendingStart).not.toHaveBeenCalled();
      expect(cancel).toHaveBeenCalledOnce();
      expect(cancel).toHaveBeenCalledWith("job-shutdown-active");
      expect(forceTerminate).not.toHaveBeenCalled();
      expect(shutdownFinished).toBe(false);
      expect(() =>
        queue.enqueue({
          jobId: "job-after-shutdown",
          ownerId: 4,
          start: vi.fn(),
        }),
      ).toThrow(expect.objectContaining({ code: "APP_SHUTTING_DOWN" }));

      await vi.advanceTimersByTimeAsync(24);
      expect(forceTerminate).not.toHaveBeenCalled();
      expect(shutdownFinished).toBe(false);

      await vi.advanceTimersByTimeAsync(1);
      expect(forceTerminate).toHaveBeenCalledOnce();
      await expect(activeResult).resolves.toMatchObject({ status: "canceled" });
      await shutdown;
      expect(shutdownFinished).toBe(true);
      expect(queue.size).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });
});
