import path from "node:path";
import * as fs from "node:fs/promises";
import { _electron as electron, expect, test } from "@playwright/test";

test("Setup stores imported templates in userData and preserves them on restart", async () => {
  test.skip(process.env.MD2WORD_RUN_SETUP_E2E !== "1", "Run after installing the Setup package in a dedicated test directory.");
  const { version } = JSON.parse(await fs.readFile(path.resolve("package.json"), "utf8")) as { version: string };
  const executablePath = path.resolve(process.env.MD2WORD_SETUP_EXE ?? "output/setup-install-test/MD2Word.exe");
  const installRoot = path.dirname(executablePath);
  const userDataPath = path.resolve("output/e2e-setup-user-data");
  await fs.rm(userDataPath, { recursive: true, force: true });
  expect(await fs.readFile(path.join(installRoot, "resources", "md2word-installed"), "utf8")).toBe("setup");
  const installedIndexPath = path.join(installRoot, "templates", "reference", "index.json");
  const installedIndex = await fs.readFile(installedIndexPath, "utf8");
  const launch = () => electron.launch({ executablePath, args: [`--user-data-dir=${userDataPath}`] });
  let application = await launch();
  try {
    expect(await application.evaluate(({ app }) => app.getPath("userData"))).toBe(userDataPath);
    let window = await application.firstWindow();
    await expect(window.getByRole("heading", { name: /Markdown.*Word 模板/ })).toBeVisible();
    expect(await window.evaluate(() => window.md2word!.capabilities.describe())).toMatchObject({ productVersion: version });
    expect(await window.evaluate(() => window.md2word!.templates.list())).toHaveLength(1);
    const registered = [];
    for (const fileName of ["template.docx", "style.css"]) {
      await application.evaluate(({ dialog }, filePath) => {
        Object.defineProperty(dialog, "showOpenDialog", {
          configurable: true,
          value: async () => ({ canceled: false, filePaths: [filePath] }),
        });
      }, path.join(installRoot, "templates", "reference", "public-reference-template", fileName));
      registered.push(await window.evaluate((isDocx) => isDocx
        ? window.md2word!.files.pickTemplateDocx()
        : window.md2word!.files.pickCss(), fileName.endsWith("docx")));
    }
    const imported = await window.evaluate(async ([templateFile, cssFile]) => {
      if (!templateFile || !cssFile) throw new Error("Synthetic template selection failed");
      return window.md2word!.templates.add({
        name: "合成安装验收模板", description: "仅用于 Setup 生命周期验收", templateFile, css: { mode: "custom", file: cssFile },
        mermaidDefaults: { mode: "off", format: "png" },
      });
    }, registered);
    expect(imported.validation.status).toBe("valid");
    await fs.access(path.join(userDataPath, "templates", "user", imported.id, "template.docx"));
    await expect(fs.access(path.join(installRoot, "templates", "user"))).rejects.toMatchObject({ code: "ENOENT" });
    await window.evaluate(async (id) => {
      await window.md2word!.templates.remove("public-reference-template");
      await window.md2word!.templates.setDefault(id);
    }, imported.id);
    await application.close();
    application = await launch();
    window = await application.firstWindow();
    await expect(window.getByRole("heading", { name: /Markdown.*Word 模板/ })).toBeVisible();
    expect(await window.evaluate(() => window.md2word!.templates.list())).toEqual([
      expect.objectContaining({ id: imported.id, isDefault: true }),
    ]);
    expect(await fs.readFile(installedIndexPath, "utf8")).toBe(installedIndex);
  } finally {
    await application.close();
    // Keep this isolated synthetic userData for the installer-upgrade retention check.
  }
});
