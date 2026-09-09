import { errorMessage } from "./errorMessage";

describe("errorMessage", () => {
  it("reads both native and structured IPC errors", () => {
    expect(errorMessage(new Error("native"), "fallback")).toBe("native");
    expect(errorMessage({ code: "JOB_NOT_FOUND", message: "structured", retryable: false }, "fallback")).toBe("structured");
  });

  it("uses the fallback for malformed values", () => {
    expect(errorMessage({ message: 42 }, "fallback")).toBe("fallback");
  });
});
