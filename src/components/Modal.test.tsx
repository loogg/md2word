import { render, screen } from "@testing-library/react";
import { Modal } from "./Modal";

describe("Modal", () => {
  it("renders at the document root so transformed page containers cannot clip it", () => {
    render(
      <div className="page-enter overflow-hidden">
        <Modal open title="测试弹窗" onClose={() => undefined}>
          <p>弹窗内容</p>
        </Modal>
      </div>,
    );

    const dialog = screen.getByRole("dialog", { name: "测试弹窗" });
    expect(dialog.parentElement).toBe(document.body);
    expect(dialog).toHaveClass("fixed", "inset-0");
  });
});
