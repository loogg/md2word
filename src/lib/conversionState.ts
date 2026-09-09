import type { ConversionError, ConversionEvent, ConversionResult, ConversionStage, PickedOutput } from "../types";

export type ConversionUiStatus =
  | "idle"
  | "choosing-output"
  | "queued"
  | "running"
  | "canceling"
  | "succeeded"
  | "failed"
  | "canceled";

type StableConversionUiStatus = Exclude<ConversionUiStatus, "choosing-output" | "queued" | "running" | "canceling">;

export interface ConversionTaskState {
  status: ConversionUiStatus;
  resumeStatus: StableConversionUiStatus;
  jobId: string | null;
  output: PickedOutput | null;
  stage: ConversionStage | null;
  events: ConversionEvent[];
  result: ConversionResult | null;
  error: ConversionError | null;
  demoFailure: boolean;
}

export const initialConversionTaskState: ConversionTaskState = {
  status: "idle",
  resumeStatus: "idle",
  jobId: null,
  output: null,
  stage: null,
  events: [],
  result: null,
  error: null,
  demoFailure: false,
};

export type ConversionTaskAction =
  | { type: "begin-output"; demoFailure: boolean }
  | { type: "cancel-output" }
  | { type: "start-requested"; output: PickedOutput }
  | { type: "job-accepted"; jobId: string }
  | { type: "event-received"; event: ConversionEvent }
  | { type: "start-failed"; error: ConversionError }
  | { type: "cancel-requested" }
  | { type: "reset" };

export const isActiveConversionStatus = (status: ConversionUiStatus) =>
  status === "choosing-output" || status === "queued" || status === "running" || status === "canceling";

const stableStatus = (state: ConversionTaskState): StableConversionUiStatus => {
  if (state.status === "succeeded" || state.status === "failed" || state.status === "canceled") return state.status;
  return state.resumeStatus;
};

const fallbackResult = (event: ConversionEvent): ConversionResult => ({
  jobId: event.jobId,
  status: event.kind === "completed" ? "succeeded" : event.kind === "canceled" ? "canceled" : "failed",
  durationMs: 0,
  warnings: [],
});

export function conversionTaskReducer(
  state: ConversionTaskState,
  action: ConversionTaskAction,
): ConversionTaskState {
  switch (action.type) {
    case "begin-output":
      if (isActiveConversionStatus(state.status)) return state;
      return {
        ...state,
        status: "choosing-output",
        resumeStatus: stableStatus(state),
        demoFailure: action.demoFailure,
      };
    case "cancel-output":
      if (state.status !== "choosing-output") return state;
      return { ...state, status: state.resumeStatus, demoFailure: false };
    case "start-requested":
      return {
        ...initialConversionTaskState,
        status: "queued",
        resumeStatus: "idle",
        output: action.output,
        stage: "queued",
        demoFailure: state.demoFailure,
      };
    case "job-accepted":
      if (!isActiveConversionStatus(state.status) || (state.jobId && state.jobId !== action.jobId)) return state;
      return { ...state, jobId: action.jobId };
    case "event-received": {
      const { event } = action;
      if (state.jobId && event.jobId !== state.jobId) return state;
      if (!state.jobId && state.status !== "queued") return state;
      const base = {
        ...state,
        jobId: state.jobId ?? event.jobId,
        stage: event.stage ?? state.stage,
        events: [...state.events, event],
      };
      if (event.kind === "queued") return { ...base, status: "queued" };
      if (event.kind === "canceling") return { ...base, status: "canceling" };
      if (event.kind === "completed") {
        const result = event.result ?? fallbackResult(event);
        return { ...base, status: "succeeded", resumeStatus: "succeeded", result, error: null, demoFailure: false };
      }
      if (event.kind === "failed") {
        const result = event.result ?? fallbackResult(event);
        return { ...base, status: "failed", resumeStatus: "failed", result, error: event.error ?? null, demoFailure: false };
      }
      if (event.kind === "canceled") {
        const result = event.result ?? fallbackResult(event);
        return { ...base, status: "canceled", resumeStatus: "canceled", result, error: null, demoFailure: false };
      }
      return { ...base, status: state.status === "canceling" ? "canceling" : "running" };
    }
    case "start-failed": {
      const jobId = state.jobId ?? "start-failed";
      return {
        ...state,
        status: "failed",
        resumeStatus: "failed",
        error: action.error,
        result: { jobId, status: "failed", durationMs: 0, warnings: [] },
        demoFailure: false,
      };
    }
    case "cancel-requested":
      if (state.status !== "queued" && state.status !== "running") return state;
      return { ...state, status: "canceling" };
    case "reset":
      return initialConversionTaskState;
    default:
      return state;
  }
}
