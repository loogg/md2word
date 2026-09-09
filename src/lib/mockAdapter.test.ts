import { validateTemplateDraft } from "./mockAdapter";
import type { TemplateDraft } from "../types";

const baseDraft: TemplateDraft = {
  name: "验收模板",
  description: "校验模拟结果",
  templateFileName: "验收模板.docx",
  templateFileHandle: "mock:template",
  styleMode: "builtin",
  styleCssFileName: "",
  styleCssHandle: null,
  mermaidMode: "auto",
  mermaidFormat: "png",
};

describe("template validation mock", () => {
  it("reports missing body bookmarks", () => {
    const report = validateTemplateDraft({ ...baseDraft, templateFileName: "无书签旧版模板.docx" });

    expect(report.status).toBe("invalid");
    expect(report.issues[0]?.code).toBe("BODY_BOOKMARK_MISSING");
  });

  it("reports CSS styles that are absent from the DOCX", () => {
    const report = validateTemplateDraft({
      ...baseDraft,
      styleMode: "custom",
      styleCssFileName: "missing-style.css",
    });

    expect(report.status).toBe("invalid");
    expect(report.issues[0]?.code).toBe("WORD_STYLE_MISSING");
    expect(report.styleMappings.find((mapping) => mapping.role === "code-block")?.status).toBe("missing");
  });

  it("reports an unreadable DOCX", () => {
    const report = validateTemplateDraft({ ...baseDraft, templateFileName: "不可读模板.docx" });

    expect(report.status).toBe("invalid");
    expect(report.issues[0]?.code).toBe("TEMPLATE_FILE_UNREADABLE");
  });

  it("reports explicit ordered and unordered list Word styles", () => {
    const report = validateTemplateDraft({
      ...baseDraft,
      styleMode: "custom",
      styleCssFileName: "tech-report.css",
      styleCssHandle: "mock:css",
    });

    expect(report.styleMappings.find((mapping) => mapping.role === "ordered-list")?.resolvedStyleName).toBe("示例 有序列项");
    expect(report.styleMappings.find((mapping) => mapping.role === "unordered-list")?.resolvedStyleName).toBe("示例 正文");
    expect(report.styleMappings.find((mapping) => mapping.role === "inline-code")?.resolvedStyleName).toBe("示例 正文");
  });
});
