import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { vi } from "vitest";
import { DEMO_TEMPLATES } from "../data/demoData";
import { createBrowserMockAdapter } from "../lib/mockAdapter";
import type { TemplateProfile } from "../types";
import { TemplatesPage } from "./TemplatesPage";

describe("TemplatesPage warning confirmation", () => {
  it("labels a native Word style fallback separately from a CSS mapping", () => {
    const template: TemplateProfile = structuredClone(DEMO_TEMPLATES[0]);
    template.validation.styleMappings[0] = {
      ...template.validation.styleMappings[0],
      cssSelector: "",
      requestedStyleName: "",
      resolvedStyleId: "Normal",
      resolvedStyleName: "Normal",
      status: "word-fallback",
    };

    render(
      <TemplatesPage
        templates={[template]}
        adapter={createBrowserMockAdapter()}
        onTemplatesChange={() => undefined}
        onDefaultSelected={() => undefined}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: `编辑${template.name}` }));

    expect(screen.getByText("Word 原生")).toBeInTheDocument();
    expect(screen.getByText("Normal")).toBeInTheDocument();
    expect(screen.getByText("行内代码")).toBeInTheDocument();
  });

  it("freshly validates an existing warning before saving it", async () => {
    const template: TemplateProfile = {
      ...structuredClone(DEMO_TEMPLATES[0]),
      validation: {
        ...structuredClone(DEMO_TEMPLATES[0].validation),
        status: "warning",
        summary: "合成警告",
        issues: [
          {
            code: "SYNTHETIC_WARNING",
            severity: "warning",
            target: "style",
            message: "请确认合成警告。",
          },
        ],
      },
    };
    const adapter = createBrowserMockAdapter();
    const validateDraft = vi.fn().mockResolvedValue(template.validation);
    const update = vi.fn().mockResolvedValue(template);
    adapter.templates.validateDraft = validateDraft;
    adapter.templates.update = update;

    render(
      <TemplatesPage
        templates={[template]}
        adapter={adapter}
        onTemplatesChange={() => undefined}
        onDefaultSelected={() => undefined}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: `编辑${template.name}` }));
    fireEvent.click(screen.getByRole("button", { name: "保存模板" }));

    expect(validateDraft).not.toHaveBeenCalled();
    expect(update).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("再次点击“确认警告并保存”");

    fireEvent.click(screen.getByRole("button", { name: "确认警告并保存" }));

    await waitFor(() => expect(update).toHaveBeenCalledOnce());
    expect(validateDraft).toHaveBeenCalledOnce();
    expect(validateDraft.mock.invocationCallOrder[0]).toBeLessThan(update.mock.invocationCallOrder[0]);
  });
});
