import { conversionTaskReducer, initialConversionTaskState } from "./conversionState";
import type { ConversionEvent, PickedOutput } from "../types";

const output: PickedOutput = {
  handle: "output-handle",
  fileName: "result.docx",
  displayPath: "已选择 / result.docx",
};

const event = (patch: Partial<ConversionEvent>): ConversionEvent => ({
  jobId: "job-1",
  timestamp: "2026-07-16T00:00:00.000Z",
  kind: "stage",
  stage: "preparing",
  level: "info",
  message: "开始",
  ...patch,
});

describe("conversionTaskReducer", () => {
  it("cancels output selection without creating a job or log", () => {
    const choosing = conversionTaskReducer(initialConversionTaskState, { type: "begin-output", demoFailure: false });
    const canceled = conversionTaskReducer(choosing, { type: "cancel-output" });

    expect(canceled.status).toBe("idle");
    expect(canceled.jobId).toBeNull();
    expect(canceled.events).toEqual([]);
  });

  it("tracks queued, running, canceling and terminal worker events", () => {
    let state = conversionTaskReducer(initialConversionTaskState, { type: "begin-output", demoFailure: false });
    state = conversionTaskReducer(state, { type: "start-requested", output });
    expect(state.status).toBe("queued");

    state = conversionTaskReducer(state, { type: "event-received", event: event({ kind: "queued", stage: "queued" }) });
    state = conversionTaskReducer(state, { type: "event-received", event: event({ kind: "started" }) });
    expect(state.status).toBe("running");
    expect(state.jobId).toBe("job-1");

    state = conversionTaskReducer(state, { type: "cancel-requested" });
    expect(state.status).toBe("canceling");
    state = conversionTaskReducer(state, { type: "event-received", event: event({ kind: "canceling", stage: "cleanup" }) });
    state = conversionTaskReducer(state, {
      type: "event-received",
      event: event({ kind: "canceled", stage: "cleanup", result: { jobId: "job-1", status: "canceled", durationMs: 20, warnings: [] } }),
    });
    expect(state.status).toBe("canceled");
    expect(state.result?.status).toBe("canceled");
  });

  it("ignores events for a different job", () => {
    let state = conversionTaskReducer(initialConversionTaskState, { type: "start-requested", output });
    state = conversionTaskReducer(state, { type: "job-accepted", jobId: "job-1" });
    const unchanged = conversionTaskReducer(state, { type: "event-received", event: event({ jobId: "job-other" }) });

    expect(unchanged).toBe(state);
  });
});
