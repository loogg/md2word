import { fireEvent, render, screen } from "@testing-library/react";
import { createBrowserMockAdapter } from "../lib/mockAdapter";
import { FileDropzone } from "./FileDropzone";

describe("FileDropzone", () => {
  it("rejects multiple dropped files instead of silently taking the first", () => {
    const adapter = createBrowserMockAdapter();
    render(
      <FileDropzone
        value={null}
        capabilities={adapter.runtimeCapabilities}
        onPick={() => adapter.files.pickMarkdown()}
        onRegister={(file) => adapter.files.registerMarkdown(file)}
        onChange={() => undefined}
      />,
    );

    fireEvent.drop(screen.getByRole("button", { name: /拖入 Markdown/ }), {
      dataTransfer: {
        files: [
          new File(["# one"], "one.md", { type: "text/markdown" }),
          new File(["# two"], "two.md", { type: "text/markdown" }),
        ],
      },
    });

    expect(screen.getByRole("alert")).toHaveTextContent("一次只能拖入一个 Markdown 文件");
  });
});
