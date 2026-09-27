import { spawn, type ChildProcess } from "node:child_process";
import { randomBytes } from "node:crypto";
import * as fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { _electron as electron, chromium, expect, test } from "@playwright/test";

const appUrl = "http://127.0.0.1:4173";
const bridgeUrl = `${appUrl}/__md2word_bridge/health`;

async function waitFor(url: string, child: ChildProcess, timeoutMs = 30_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error("Browser Review development process exited early.");
    try {
      if ((await fetch(url, { signal: AbortSignal.timeout(1_000) })).ok) return;
    } catch { /* The local server is still starting. */ }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error(`Browser Review did not become ready: ${url}`);
}

test("browser review uses real Main, native file dialogs, and the Word Worker", async () => {
  test.setTimeout(240_000);
  test.skip(process.env.MD2WORD_RUN_BROWSER_REVIEW_E2E !== "1", "Requires Windows, Word, Pandoc, and the built Worker.");

  const outputRoot = path.resolve("output");
  const userDataPath = path.join(outputRoot, "e2e-browser-review-user-data");
  if (!userDataPath.startsWith(`${outputRoot}${path.sep}`)) throw new Error("Unexpected E2E userData path.");
  await fs.rm(userDataPath, { recursive: true, force: true });
  const fixtureRoot = await fs.mkdtemp(path.join(os.tmpdir(), "md2word-browser-review-e2e-"));
  if (!fixtureRoot.startsWith(path.join(os.tmpdir(), "md2word-browser-review-e2e-"))) {
    throw new Error("Unexpected synthetic fixture path.");
  }
  const markdownPath = path.join(fixtureRoot, "synthetic-review.md");
  const outputPath = path.join(fixtureRoot, "synthetic-review.docx");
  await fs.writeFile(markdownPath, "# 合成审查文档\n\n仅用于 Browser Review Bridge 的本机转换验收。\n", "utf8");

  const env = {
    ...process.env,
    MD2WORD_BROWSER_REVIEW: "1",
    MD2WORD_BRIDGE_TOKEN: randomBytes(32).toString("hex"),
    VITE_MD2WORD_BROWSER_REVIEW: "1",
    VITE_DEV_SERVER_URL: appUrl,
    MD2WORD_E2E: "1",
    MD2WORD_E2E_USER_DATA: userDataPath,
  };
  const vite = spawn(process.execPath, [path.resolve("node_modules/vite/bin/vite.js"), "--host", "127.0.0.1"], {
    cwd: process.cwd(), env, stdio: "ignore", windowsHide: true,
  });
  let application: Awaited<ReturnType<typeof electron.launch>> | undefined;
  let browser: Awaited<ReturnType<typeof chromium.launch>> | undefined;
  try {
    await waitFor(appUrl, vite);
    application = await electron.launch({ args: [path.resolve("dist-electron/main.js")], env });
    await waitFor(bridgeUrl, vite, 45_000);
    browser = await chromium.launch();
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    page.setDefaultTimeout(10_000);
    await page.goto(appUrl);

    await expect(page.getByText("浏览器审查 · 真实后端").first()).toBeVisible();
    await expect(page.getByText("公开参考模板").first()).toBeVisible();
    await expect(page.getByRole("button", { name: "生成 Word", exact: true })).toBeDisabled();
    const environment = await page.evaluate(async () => {
      const response = await fetch("/__md2word_bridge/invoke", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ channel: "md2word:environment:check", args: [] }),
      });
      return response.json();
    });
    expect(environment).toMatchObject({ ok: true });
    expect(environment.value.items.filter((item: { required: boolean }) => item.required)
      .every((item: { status: string }) => item.status === "ready")).toBe(true);

    if (process.env.MD2WORD_UPDATE_BROWSER_REVIEW_SCREENSHOT === "1") {
      await page.screenshot({ path: path.resolve("doc/uiPrototype/screenshots/browser-review-1440x900.png") });
    }

    await application.evaluate(({ dialog }, selectedPath) => {
      Object.defineProperty(dialog, "showOpenDialog", {
        configurable: true,
        value: async () => ({ canceled: false, filePaths: [selectedPath] }),
      });
    }, markdownPath);
    await page.getByRole("button", { name: /点击选择 Markdown/ }).click();
    await expect(page.getByText("synthetic-review.md").first()).toBeVisible();
    await expect(page.getByRole("button", { name: "生成 Word", exact: true })).toBeEnabled();

    await application.evaluate(({ dialog }) => {
      Object.defineProperty(dialog, "showSaveDialog", {
        configurable: true,
        value: async () => ({ canceled: true }),
      });
    });
    await page.getByRole("button", { name: "生成 Word", exact: true }).click();
    await expect(page.getByText("等待生成任务")).toBeVisible();
    await expect(fs.stat(outputPath)).rejects.toThrow();

    await application.evaluate(({ dialog }, selectedPath) => {
      Object.defineProperty(dialog, "showSaveDialog", {
        configurable: true,
        value: async () => ({ canceled: false, filePath: selectedPath }),
      });
    }, outputPath);
    await page.getByRole("button", { name: "生成 Word", exact: true }).click();
    await expect(page.getByText("生成完成", { exact: true })).toBeVisible({ timeout: 180_000 });
    expect((await fs.stat(outputPath)).size).toBeGreaterThan(0);
    await expect(page.getByRole("button", { name: "打开文件" })).toBeVisible();
    await expect(page.getByRole("button", { name: "在文件夹中显示" })).toBeVisible();
    await application.evaluate(({ shell }) => {
      (globalThis as typeof globalThis & { bridgeOpenedPaths?: string[]; bridgeRevealedPaths?: string[] }).bridgeOpenedPaths = [];
      (globalThis as typeof globalThis & { bridgeOpenedPaths?: string[]; bridgeRevealedPaths?: string[] }).bridgeRevealedPaths = [];
      Object.defineProperty(shell, "openPath", { configurable: true, value: async (value: string) => {
        (globalThis as typeof globalThis & { bridgeOpenedPaths?: string[] }).bridgeOpenedPaths!.push(value);
        return "";
      } });
      Object.defineProperty(shell, "showItemInFolder", { configurable: true, value: (value: string) => {
        (globalThis as typeof globalThis & { bridgeRevealedPaths?: string[] }).bridgeRevealedPaths!.push(value);
      } });
    });
    await page.getByRole("button", { name: "打开文件" }).click();
    await page.getByRole("button", { name: "在文件夹中显示" }).click();
    expect(await application.evaluate(() => (globalThis as typeof globalThis & { bridgeOpenedPaths?: string[] }).bridgeOpenedPaths))
      .toContain(outputPath);
    expect(await application.evaluate(() => (globalThis as typeof globalThis & { bridgeRevealedPaths?: string[] }).bridgeRevealedPaths))
      .toContain(outputPath);

    await page.getByRole("button", { name: "模板管理 管理 DOCX 与 CSS", exact: true }).click();
    await expect(page.getByRole("heading", { name: "模板管理", exact: true })).toBeVisible();
    await page.getByRole("textbox", { name: "搜索模板" }).fill("不存在的合成模板");
    await expect(page.getByText("没有匹配的模板")).toBeVisible();
    await page.getByRole("textbox", { name: "搜索模板" }).fill("");
    await page.getByRole("button", { name: "添加模板" }).click();
    const editor = page.getByRole("dialog", { name: "添加模板配置" });
    await editor.getByRole("textbox", { name: "模板名称" }).fill("合成审查模板");
    await editor.getByRole("textbox", { name: "模板用途" }).fill("仅用于隔离 Browser Review 验收");
    await application.evaluate(({ dialog }, selectedPath) => {
      Object.defineProperty(dialog, "showOpenDialog", {
        configurable: true,
        value: async () => ({ canceled: false, filePaths: [selectedPath] }),
      });
    }, path.resolve("resources/templates/reference/public-reference-template/template.docx"));
    await editor.getByRole("button", { name: /选择 .docx 模板文件/ }).click();
    await editor.getByRole("button", { name: /选择自定义 CSS/ }).click();
    await application.evaluate(({ dialog }, selectedPath) => {
      Object.defineProperty(dialog, "showOpenDialog", {
        configurable: true,
        value: async () => ({ canceled: false, filePaths: [selectedPath] }),
      });
    }, path.resolve("resources/templates/reference/public-reference-template/style.css"));
    await editor.getByRole("button", { name: "选择 .css 样式文件" }).click();
    await editor.getByRole("combobox", { name: "Mermaid 默认模式" }).selectOption("off");
    await editor.getByRole("button", { name: "校验配置" }).click();
    await expect(editor.getByText("校验通过").first()).toBeVisible({ timeout: 30_000 });
    await editor.getByRole("button", { name: "保存模板" }).click();
    await expect(editor).toBeHidden();
    await expect(page.getByRole("heading", { name: "合成审查模板" })).toBeVisible();
    await page.getByRole("button", { name: "将合成审查模板设为默认" }).click();
    await page.getByRole("button", { name: "重新校验合成审查模板" }).click();
    await page.getByRole("button", { name: "编辑合成审查模板" }).click();
    const edit = page.getByRole("dialog", { name: "编辑模板配置" });
    await edit.getByRole("textbox", { name: "模板用途" }).fill("已更新的合成审查用途");
    await edit.getByRole("button", { name: "保存模板" }).click();
    await expect(edit).toBeHidden();
    await expect(page.getByText("已更新的合成审查用途")).toBeVisible();
    await page.getByRole("button", { name: "删除合成审查模板" }).click();
    const confirmDelete = page.getByRole("dialog", { name: "删除模板配置" });
    await expect(confirmDelete.getByText("只删除应用管理副本，不删除用户原文件。")).toBeVisible();
    await confirmDelete.getByRole("button", { name: "确认删除" }).click();
    await expect(page.getByRole("heading", { name: "合成审查模板" })).toBeHidden();
    await expect(page.getByRole("heading", { name: "公开参考模板" })).toBeVisible();

    await page.getByRole("button", { name: "能力说明 语法、元数据与边界", exact: true }).click();
    await expect(page.getByRole("heading", { name: "MD2Word 支持能力说明" })).toBeVisible();
    await page.getByRole("tab", { name: /Front Matter/ }).click();
    await page.getByRole("textbox", { name: "搜索能力说明" }).fill("不存在的合成能力");
    await expect(page.getByText(/没有匹配/)).toBeVisible();
    await page.getByRole("textbox", { name: "搜索能力说明" }).fill("");
    await page.getByRole("tab", { name: /模板契约/ }).click();
    await page.getByRole("tab", { name: /边界与环境/ }).click();
    await page.getByRole("button", { name: "环境与设置 依赖检查与偏好", exact: true }).click();
    await page.getByRole("button", { name: "重新检查" }).click();
    await expect(page.getByText("浏览器审查使用隔离的真实模板库，由桌面 Main 原子维护。")).toBeVisible();
    await page.getByRole("button", { name: "打开模板库目录" }).click();
    expect(await application.evaluate(() => (globalThis as typeof globalThis & { bridgeOpenedPaths?: string[] }).bridgeOpenedPaths))
      .toEqual(expect.arrayContaining([expect.stringMatching(/templates$/i)]));
    await page.getByRole("button", { name: "关于 版本与软件更新", exact: true }).click();
    await expect(page.getByText(/运行形式：.*浏览器审查（真实桌面后端）/)).toBeVisible();
    await application.evaluate(({ shell }) => {
      (globalThis as typeof globalThis & { bridgeOpenedLinks?: string[] }).bridgeOpenedLinks = [];
      Object.defineProperty(shell, "openExternal", { configurable: true, value: async (value: string) => {
        (globalThis as typeof globalThis & { bridgeOpenedLinks?: string[] }).bridgeOpenedLinks!.push(value);
      } });
    });
    await page.getByRole("button", { name: "GitHub", exact: true }).click();
    await page.getByRole("button", { name: "更新日志" }).click();
    expect(await application.evaluate(() => (globalThis as typeof globalThis & { bridgeOpenedLinks?: string[] }).bridgeOpenedLinks))
      .toEqual(["https://github.com/loogg/md2word", "https://github.com/loogg/md2word/releases"]);
  } finally {
    await browser?.close();
    await application?.close();
    vite.kill();
    await fs.rm(userDataPath, { recursive: true, force: true });
    await fs.rm(fixtureRoot, { recursive: true, force: true });
  }
});
