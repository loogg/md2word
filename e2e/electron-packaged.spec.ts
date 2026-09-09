import path from "node:path";
import * as fs from "node:fs/promises";
import { _electron as electron, expect, test } from "@playwright/test";

test("packaged Windows app boots with the bundled Worker and narrow preload API", async () => {
  test.skip(process.env.MD2WORD_RUN_PACKAGED_E2E !== "1", "Set MD2WORD_RUN_PACKAGED_E2E=1 after building the Windows package.");

  const executablePath = path.resolve(process.env.MD2WORD_PACKAGED_EXE ?? "output/e2e-packaged-app/MD2Word.exe");
  const packagedResourcesRoot = path.join(path.dirname(executablePath), "resources");
  const packagedTemplatesRoot = path.join(path.dirname(executablePath), "templates");
  const packagedCapabilityDocument = path.join(path.dirname(executablePath), "MD2Word-支持能力说明.md");
  const expectedTemplateId = process.env.MD2WORD_EXPECTED_TEMPLATE_ID;
  const expectedTemplateCount = process.env.MD2WORD_EXPECTED_TEMPLATE_COUNT
    ? Number.parseInt(process.env.MD2WORD_EXPECTED_TEMPLATE_COUNT, 10)
    : undefined;
  const userDataPath = path.resolve("output/e2e-packaged-user-data");
  await fs.rm(userDataPath, { recursive: true, force: true });
  await expect(fs.stat(executablePath)).resolves.toMatchObject({ size: expect.any(Number) });
  for (const relativePath of [
    "worker/Md2Word.Worker.exe",
    "conversion/capabilities.json",
    "conversion/worddom-template.html",
    "conversion/filters/manifest.json",
  ]) {
    await expect(fs.stat(path.join(packagedResourcesRoot, relativePath))).resolves.toMatchObject({ size: expect.any(Number) });
  }
  await expect(fs.stat(packagedCapabilityDocument)).resolves.toMatchObject({ size: expect.any(Number) });
  await expect(fs.stat(path.join(packagedTemplatesRoot, "reference", "index.json"))).resolves.toMatchObject({ size: expect.any(Number) });

  const application = await electron.launch({
    executablePath,
    args: [`--user-data-dir=${userDataPath}`],
    env: { ...process.env, NODE_ENV: "test" },
  });

  try {
    const window = await application.firstWindow();
    await expect(window.getByRole("heading", { name: /Markdown.*Word 模板/ })).toBeVisible();
    await expect(window.getByText("桌面正式接入", { exact: true })).toBeVisible();
    const runtime = await window.evaluate(async (templateId) => ({
      capabilities: window.md2word!.runtimeCapabilities,
      capabilityCatalog: await window.md2word!.capabilities.describe(),
      apiKeys: Object.keys(window.md2word ?? {}).sort(),
      environment: await window.md2word!.environment.check(),
      templates: await window.md2word!.templates.list(),
      validatedTemplate: templateId ? await window.md2word!.templates.validate(templateId) : undefined,
      nodeProcess: typeof (window as Window & { process?: unknown }).process,
    }), expectedTemplateId);
    expect(runtime.capabilities).toMatchObject({
      backend: "electron",
      fileDialogs: "native",
      templateStorage: "main",
      templateValidation: "worker",
      conversion: "worker",
    });
    expect(runtime.apiKeys).toEqual(["capabilities", "conversions", "environment", "files", "runtimeCapabilities", "shell", "templates"]);
    expect(runtime.capabilityCatalog).toMatchObject({ schemaVersion: "1.1", productVersion: "0.5.0", protocolVersion: "1.0" });
    expect(runtime.capabilityCatalog.frontMatter.map((item) => item.key)).toContain("word_heading_numbering");
    expect(runtime.nodeProcess).toBe("undefined");
    expect(runtime.environment.items.filter((item) => item.required).every((item) => item.status === "ready")).toBe(true);
    expect(runtime.templates).not.toHaveLength(0);
    if (expectedTemplateCount !== undefined) expect(runtime.templates).toHaveLength(expectedTemplateCount);
    if (expectedTemplateId) {
      expect(runtime.templates.map((template) => template.id)).toContain(expectedTemplateId);
      expect(runtime.validatedTemplate).toMatchObject({ id: expectedTemplateId, validation: { status: "valid" } });
    }
  } finally {
    await application.close();
    await fs.rm(userDataPath, { recursive: true, force: true });
  }
});
