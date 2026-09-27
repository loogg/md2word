import path from "node:path";
import * as fs from "node:fs/promises";
import { _electron as electron, expect, test } from "@playwright/test";

function testPaths() {
  const outputRoot = path.resolve("output");
  const executablePath = path.resolve(process.env.MD2WORD_UPGRADE_EXE ?? "output/setup-cross-version-test/MD2Word.exe");
  const userDataPath = path.resolve(process.env.MD2WORD_UPGRADE_USER_DATA ?? "output/e2e-cross-version-user-data");
  if (!executablePath.startsWith(`${outputRoot}${path.sep}`) || !userDataPath.startsWith(`${outputRoot}${path.sep}`)) {
    throw new Error("Cross-version E2E paths must stay under the workspace output directory.");
  }
  return { executablePath, userDataPath };
}

test("0.6.1 Setup creates an isolated, user-managed template state", async () => {
  test.skip(process.env.MD2WORD_UPGRADE_PHASE !== "seed", "Run after installing 0.6.1 in a dedicated directory.");
  const { executablePath, userDataPath } = testPaths();
  await expect(fs.access(userDataPath)).rejects.toMatchObject({ code: "ENOENT" });
  const application = await electron.launch({ executablePath, args: [`--user-data-dir=${userDataPath}`] });
  try {
    expect(await application.evaluate(({ app }) => app.getVersion())).toBe("0.6.1");
    const window = await application.firstWindow();
    await expect(window.getByRole("heading", { name: "把 Markdown 交给正确的 Word 模板" })).toBeVisible();
    expect(await window.evaluate(() => window.md2word!.templates.list())).toHaveLength(1);

    const sourceRoot = path.join(path.dirname(executablePath), "templates", "reference", "public-reference-template");
    const selected = [];
    for (const [name, isDocx] of [["template.docx", true], ["style.css", false]] as const) {
      await application.evaluate(({ dialog }, filePath) => {
        Object.defineProperty(dialog, "showOpenDialog", {
          configurable: true,
          value: async () => ({ canceled: false, filePaths: [filePath] }),
        });
      }, path.join(sourceRoot, name));
      selected.push(await window.evaluate((docx) => docx
        ? window.md2word!.files.pickTemplateDocx()
        : window.md2word!.files.pickCss(), isDocx));
    }
    const imported = await window.evaluate(async ([templateFile, cssFile]) => {
      if (!templateFile || !cssFile) throw new Error("Synthetic upgrade sources could not be selected.");
      return window.md2word!.templates.add({
        name: "合成跨版本模板",
        description: "公开参考模板的合成升级验收",
        templateFile,
        css: { mode: "custom", file: cssFile },
        mermaidDefaults: { mode: "off", format: "png" },
      });
    }, selected);
    expect(imported.validation.status).toBe("valid");
    await window.evaluate(async (id) => {
      await window.md2word!.templates.remove("public-reference-template");
      await window.md2word!.templates.setDefault(id);
    }, imported.id);
    expect(await window.evaluate(() => window.md2word!.templates.list()))
      .toEqual([expect.objectContaining({ id: imported.id, isDefault: true })]);
  } finally {
    await application.close();
  }
});

test("0.7.0 Setup upgrade preserves the isolated template and default selection", async () => {
  test.skip(process.env.MD2WORD_UPGRADE_PHASE !== "verify", "Run after upgrading the dedicated installation to 0.7.0.");
  const { executablePath, userDataPath } = testPaths();
  const application = await electron.launch({ executablePath, args: [`--user-data-dir=${userDataPath}`] });
  try {
    expect(await application.evaluate(({ app }) => app.getVersion())).toBe("0.7.0");
    expect(await application.evaluate(({ app }) => app.getPath("userData"))).toBe(userDataPath);
    const window = await application.firstWindow();
    await expect(window.getByRole("heading", { name: "生成 Word", exact: true })).toBeVisible();
    expect(await window.evaluate(() => window.md2word!.templates.list())).toEqual([
      expect.objectContaining({ name: "合成跨版本模板", isDefault: true, validation: expect.objectContaining({ status: "valid" }) }),
    ]);
    await window.getByRole("button", { name: "关于 版本与软件更新", exact: true }).click();
    await expect(window.getByText("v0.7.0", { exact: true })).toBeVisible();
  } finally {
    await application.close();
  }
});
