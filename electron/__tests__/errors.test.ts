import { describe, expect, it } from "vitest";
import { AppError, toPublicError } from "../errors";

describe("public errors", () => {
  it("preserves a conversion stage while exposing only public fields", () => {
    const cause = new Error("private cause");
    const error = new AppError("PANDOC_FAILED", "Pandoc failed", true, { stage: "pandoc", cause });

    expect(toPublicError(error)).toEqual({
      code: "PANDOC_FAILED",
      message: "Pandoc failed",
      stage: "pandoc",
      retryable: true,
    });
    expect(toPublicError(error)).not.toHaveProperty("cause");
  });
});
