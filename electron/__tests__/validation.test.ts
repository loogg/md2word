import { describe, expect, it } from "vitest";
import { parseAddTemplateInput, parseStartConversionInput } from "../validation";

const picked = (prefix: string) => ({
  handle: `${prefix}_${"a".repeat(24)}`,
  fileName: "synthetic.file",
  size: 1,
  lastModified: 1,
});

describe("IPC input validation", () => {
  it("reads a custom CSS handle only from the nested CSS record", () => {
    const input = parseAddTemplateInput({
      name: "合成模板",
      description: "",
      templateFile: picked("template-docx"),
      css: { mode: "custom", file: picked("css") },
      mermaidDefaults: { mode: "auto", format: "png" },
    });
    expect(input.css.file?.handle).toMatch(/^css_/);
  });

  it("rejects path-bearing or forged conversion input", () => {
    expect(() =>
      parseStartConversionInput({
        templateId: "template-valid",
        sourceHandle: "C:\\private\\source.md",
        outputHandle: "output-docx_forged",
        options: { tocDepth: 3, mermaidMode: "auto", mermaidFormat: "png" },
      }),
    ).toThrow(/授权已失效/);
  });
});
