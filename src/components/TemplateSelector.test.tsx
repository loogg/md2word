import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { DEMO_TEMPLATES } from "../data/demoData";
import { TemplateSelector } from "./TemplateSelector";

function SelectorHarness() {
  const [selectedId, setSelectedId] = useState(DEMO_TEMPLATES[0].id);
  return (
    <TemplateSelector
      templates={DEMO_TEMPLATES}
      selectedTemplateId={selectedId}
      onSelect={setSelectedId}
      onManage={() => undefined}
    />
  );
}

describe("TemplateSelector", () => {
  it("keeps only the current template in the page and switches through search", () => {
    render(<SelectorHarness />);

    expect(screen.queryByText("技术报告")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /更换模板，当前为使用说明书/ }));

    const search = screen.getByRole("textbox", { name: "搜索可用模板" });
    expect(search).toHaveFocus();
    fireEvent.change(search, { target: { value: "技术报告模板.docx" } });
    fireEvent.click(screen.getByRole("option", { name: /技术报告/ }));

    expect(screen.queryByRole("dialog", { name: "更换模板" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /更换模板，当前为技术报告/ })).toBeInTheDocument();
  });

  it("shows a recoverable empty search state", () => {
    render(<SelectorHarness />);
    fireEvent.click(screen.getByRole("button", { name: /更换模板/ }));
    fireEvent.change(screen.getByRole("textbox", { name: "搜索可用模板" }), { target: { value: "不存在的模板" } });

    expect(screen.getByText("没有匹配的模板")).toBeInTheDocument();
    const clearButton = screen.getByRole("button", { name: "清除搜索" });
    clearButton.focus();
    fireEvent.click(clearButton);
    expect(screen.getAllByRole("option")).toHaveLength(2);
    expect(screen.getByRole("textbox", { name: "搜索可用模板" })).toHaveFocus();
  });

  it("traps stray focus, closes with Escape and restores focus to the trigger", () => {
    render(<SelectorHarness />);
    const trigger = screen.getByRole("button", { name: /更换模板/ });
    trigger.focus();
    fireEvent.click(trigger);
    expect(screen.getByRole("textbox", { name: "搜索可用模板" })).toHaveFocus();

    trigger.focus();
    fireEvent.keyDown(window, { key: "Tab" });
    expect(screen.getByRole("button", { name: "关闭" })).toHaveFocus();

    fireEvent.keyDown(window, { key: "Escape" });

    expect(screen.queryByRole("dialog", { name: "更换模板" })).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
  });
});
