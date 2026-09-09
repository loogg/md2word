import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import App from "./App";

describe("MD2Word prototype", () => {
  it("navigates between the four primary pages", () => {
    render(<App />);

    expect(screen.getByRole("heading", { name: "把 Markdown 交给正确的 Word 模板" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /模板管理/ }));
    const templatesHeading = screen.getByRole("heading", { name: "让模板和 CSS 永远保持正确配对" });
    expect(templatesHeading).toBeInTheDocument();
    expect(templatesHeading).toHaveFocus();

    fireEvent.click(screen.getByRole("button", { name: /能力说明/ }));
    expect(screen.getByRole("heading", { name: "MD2Word 支持能力说明" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /环境与设置/ }));
    expect(screen.getByRole("heading", { name: "在启动 Word 前先把环境说清楚" })).toBeInTheDocument();
  });

  it("adds a validated template profile and keeps DOCX/CSS as one profile", async () => {
    render(<App />);
    fireEvent.click(screen.getByRole("button", { name: /模板管理/ }));
    fireEvent.click(screen.getByRole("button", { name: "添加模板" }));

    fireEvent.change(screen.getByLabelText("模板名称"), { target: { value: "测试记录" } });
    fireEvent.change(screen.getByLabelText("模板用途"), { target: { value: "用于自动化验收" } });
    fireEvent.change(screen.getByLabelText("模板文件名模拟输入"), { target: { value: "测试记录模板.docx" } });
    fireEvent.click(screen.getByRole("button", { name: "保存模板" }));

    await waitFor(() => expect(screen.getByText("测试记录")).toBeInTheDocument());
    expect(screen.getByText("测试记录模板.docx")).toBeInTheDocument();
    expect(screen.getAllByText("内置默认样式").length).toBeGreaterThan(0);
  });

  it("shows an actionable validation error for a template without body bookmarks", async () => {
    render(<App />);
    fireEvent.click(screen.getByRole("button", { name: /模板管理/ }));
    fireEvent.click(screen.getByRole("button", { name: "添加模板" }));

    fireEvent.change(screen.getByLabelText("模板名称"), { target: { value: "旧技术报告" } });
    fireEvent.change(screen.getByLabelText("模板文件名模拟输入"), { target: { value: "无书签旧版模板.docx" } });
    fireEvent.click(screen.getByRole("button", { name: "校验配置" }));

    await waitFor(() => expect(screen.getByText("缺少正文范围书签")).toBeInTheDocument());
    expect(screen.getAllByText(/MANUAL_BODY_START/).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: "查看对应能力" }));
    expect(screen.getByText("CAP-TEMPLATE-BODY-RANGE")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "正文书签装配" })).toBeInTheDocument();
  });

  it("selects Markdown and cancels the save-as dialog without creating a task", async () => {
    render(<App />);
    const markdown = new File(["# 测试"], "测试说明.md", { type: "text/markdown" });
    fireEvent.change(screen.getByLabelText("选择 Markdown 文件"), { target: { files: [markdown] } });

    await waitFor(() => expect(screen.getByRole("button", { name: "生成 Word" })).toBeEnabled());
    fireEvent.click(screen.getByRole("button", { name: "生成 Word" }));
    expect(screen.getByRole("dialog", { name: "另存为 Word 文档" })).toBeInTheDocument();
    expect(screen.getByLabelText("文件名")).toHaveValue("测试说明.docx");

    fireEvent.click(screen.getByRole("button", { name: "取消" }));
    expect(screen.queryByRole("dialog", { name: "另存为 Word 文档" })).not.toBeInTheDocument();
    expect(screen.getByText("等待生成任务")).toBeInTheDocument();
  });

  it("simulates a missing Word environment and restores it", async () => {
    render(<App />);
    fireEvent.click(screen.getByRole("button", { name: /环境与设置/ }));
    fireEvent.click(screen.getByRole("button", { name: "模拟 Word 缺失" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "恢复 Word 就绪" })).toBeInTheDocument());
    expect(screen.getByText("Word 环境缺失")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "恢复 Word 就绪" }));

    await waitFor(() => expect(screen.getAllByText("Office 16 · 模拟就绪").length).toBeGreaterThan(0));
    expect(screen.getByText("Windows / Word 已检测")).toBeInTheDocument();
  });

  it("keeps the selected Markdown when navigating away and back", async () => {
    render(<App />);
    fireEvent.change(screen.getByLabelText("选择 Markdown 文件"), {
      target: { files: [new File(["# 状态"], "状态保持.md", { type: "text/markdown" })] },
    });
    await waitFor(() => expect(screen.getByText("状态保持.md")).toBeInTheDocument());

    fireEvent.click(screen.getByRole("button", { name: /模板管理/ }));
    fireEvent.click(screen.getByRole("button", { name: /生成 Word/ }));

    expect(screen.getByText("状态保持.md")).toBeInTheDocument();
  });
});
