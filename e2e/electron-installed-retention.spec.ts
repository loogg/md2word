import path from "node:path";
import { _electron as electron, expect, test } from "@playwright/test";

test("Setup reinstall keeps the isolated user template and default selection", async () => {
  test.skip(process.env.MD2WORD_RUN_SETUP_RETENTION_E2E !== "1", "Run after a dedicated Setup reinstall.");
  const outputRoot = path.resolve("output");
  const executablePath = path.resolve(process.env.MD2WORD_SETUP_EXE ?? "output/setup-install-test/MD2Word.exe");
  const userDataPath = path.resolve(process.env.MD2WORD_SETUP_USER_DATA ?? "output/e2e-setup-user-data");
  if (!executablePath.startsWith(`${outputRoot}${path.sep}`) || !userDataPath.startsWith(`${outputRoot}${path.sep}`)) {
    throw new Error("Setup retention E2E paths must stay under the workspace output directory.");
  }

  const application = await electron.launch({ executablePath, args: [`--user-data-dir=${userDataPath}`] });
  try {
    expect(await application.evaluate(({ app }) => app.getPath("userData"))).toBe(userDataPath);
    const window = await application.firstWindow();
    await expect(window.getByRole("heading", { name: "生成 Word", exact: true })).toBeVisible();
    const templates = await window.evaluate(() => window.md2word!.templates.list());
    expect(templates).toEqual([expect.objectContaining({ name: "合成安装验收模板", isDefault: true })]);
  } finally {
    await application.close();
  }
});
