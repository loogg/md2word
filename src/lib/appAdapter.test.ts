import { createAppAdapter } from "./appAdapter";
import { createBrowserMockAdapter } from "./mockAdapter";

afterEach(() => {
  delete window.md2word;
});

describe("app adapter selection", () => {
  it("uses the explicit browser mock when preload is absent", async () => {
    const adapter = createAppAdapter();
    const picked = await adapter.files.registerMarkdown(new File(["# demo"], "demo.md", { type: "text/markdown" }));
    const output = await adapter.demo?.createOutput("演示目录", "demo");

    expect(adapter.runtimeCapabilities.backend).toBe("browser-mock");
    expect(picked.handle).toMatch(/^mock:markdown:/);
    expect(picked).not.toHaveProperty("path");
    expect(output?.fileName).toBe("demo.docx");
    expect(output).not.toHaveProperty("path");
  });

  it("uses window.md2word unchanged in desktop mode", () => {
    const desktop = createBrowserMockAdapter();
    desktop.runtimeCapabilities = {
      backend: "electron",
      fileDialogs: "native",
      templateStorage: "main",
      templateValidation: "worker",
      conversion: "worker",
      environment: "worker",
      shell: "native",
    };
    window.md2word = desktop;

    const adapter = createAppAdapter();

    expect(adapter).toBe(desktop);
    expect(adapter.runtimeCapabilities.backend).toBe("electron");
  });
});
