import path from "node:path";
import { describe, expect, it } from "vitest";
import { HandleRegistry, JobResultRegistry } from "../handle-registry";

describe("HandleRegistry", () => {
  it("binds opaque handles to an owner and consumes output grants once", () => {
    const registry = new HandleRegistry({
      now: () => 100,
      randomToken: () => "a".repeat(24),
    });
    const outputPath = path.resolve("synthetic-output.docx");
    const handle = registry.register("output-docx", outputPath, 7);

    expect(handle).not.toContain(outputPath);
    expect(() => registry.resolve(handle, "output-docx", 8)).toThrow(/授权已失效/);

    const second = registry.register("output-docx", outputPath, 7);
    expect(registry.resolve(second, "output-docx", 7, { consume: true })).toBe(outputPath);
    expect(() => registry.resolve(second, "output-docx", 7)).toThrow(/授权已失效/);
  });

  it("never resolves another renderer's completed job", () => {
    const results = new JobResultRegistry();
    const outputPath = path.resolve("synthetic-output.docx");
    results.register("job-12345678", outputPath, 1);

    expect(results.resolve("job-12345678", 1)).toBe(outputPath);
    expect(() => results.resolve("job-12345678", 2)).toThrow(/没有可打开/);
  });
});
