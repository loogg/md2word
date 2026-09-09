import { fireEvent, render, screen } from "@testing-library/react";
import { DEMO_ENVIRONMENT, DEMO_TEMPLATES } from "../data/demoData";
import { createBrowserMockAdapter } from "../lib/mockAdapter";
import { initialConversionTaskState } from "../lib/conversionState";
import type { TemplateProfile } from "../types";
import { GeneratePage } from "./GeneratePage";

describe("GeneratePage template validation boundary", () => {
  it("allows inspecting an invalid template but blocks conversion", () => {
    const adapter = createBrowserMockAdapter();
    const invalidTemplate: TemplateProfile = {
      ...structuredClone(DEMO_TEMPLATES[0]),
      validation: {
        ...structuredClone(DEMO_TEMPLATES[0].validation),
        status: "invalid",
        summary: "缺少正文范围书签",
        capabilities: {
          ...structuredClone(DEMO_TEMPLATES[0].validation.capabilities),
          bodyRange: false,
        },
        issues: [
          {
            code: "BODY_BOOKMARK_MISSING",
            severity: "error",
            target: "bookmark",
            message: "未找到 MANUAL_BODY_START / MANUAL_BODY_END。",
          },
        ],
      },
    };

    render(
      <GeneratePage
        templates={[invalidTemplate]}
        selectedTemplateId={invalidTemplate.id}
        onTemplateChange={() => undefined}
        environment={DEMO_ENVIRONMENT}
        capabilities={adapter.runtimeCapabilities}
        markdown={null}
        task={initialConversionTaskState}
        onMarkdownChange={() => undefined}
        onPickMarkdown={() => adapter.files.pickMarkdown()}
        onRegisterMarkdown={(file) => adapter.files.registerMarkdown(file)}
        onRequestOutput={async () => undefined}
        onCancelOutput={() => undefined}
        onConfirmDemoOutput={async () => undefined}
        onCancelConversion={async () => undefined}
        onOpenOutput={async () => undefined}
        onRevealOutput={async () => undefined}
        onNavigateTemplates={() => undefined}
      />,
    );

    fireEvent.change(screen.getByLabelText("选择 Markdown 文件"), {
      target: { files: [new File(["# 验收"], "验收.md", { type: "text/markdown" })] },
    });

    expect(screen.getByRole("button", { name: "生成 Word" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "演示错误" })).toBeDisabled();
    expect(screen.getByText("缺少正文范围书签")).toBeInTheDocument();
    expect(screen.getByText("未找到 MANUAL_BODY_START / MANUAL_BODY_END。")).toBeInTheDocument();
  });

  it("shows result warnings alongside a successful output", () => {
    const adapter = createBrowserMockAdapter();
    render(
      <GeneratePage
        templates={DEMO_TEMPLATES}
        selectedTemplateId={DEMO_TEMPLATES[0].id}
        onTemplateChange={() => undefined}
        environment={DEMO_ENVIRONMENT}
        capabilities={adapter.runtimeCapabilities}
        markdown={null}
        task={{
          ...initialConversionTaskState,
          status: "succeeded",
          resumeStatus: "succeeded",
          jobId: "job-warning01",
          result: {
            jobId: "job-warning01",
            status: "succeeded",
            outputFileName: "synthetic.docx",
            outputDisplayPath: "已保存\\synthetic.docx",
            durationMs: 12,
            warnings: ["代码块样式使用了回退值。", "图注编号需要人工复核。"],
          },
        }}
        onMarkdownChange={() => undefined}
        onPickMarkdown={() => adapter.files.pickMarkdown()}
        onRegisterMarkdown={(file) => adapter.files.registerMarkdown(file)}
        onRequestOutput={async () => undefined}
        onCancelOutput={() => undefined}
        onConfirmDemoOutput={async () => undefined}
        onCancelConversion={async () => undefined}
        onOpenOutput={async () => undefined}
        onRevealOutput={async () => undefined}
        onNavigateTemplates={() => undefined}
      />,
    );

    const warnings = screen.getByLabelText("转换警告");
    expect(warnings).toHaveTextContent("已生成，但有 2 条警告");
    expect(warnings).toHaveTextContent("代码块样式使用了回退值。");
    expect(warnings).toHaveTextContent("图注编号需要人工复核。");
  });
});
