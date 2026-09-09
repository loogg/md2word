import path from "node:path";
import * as fs from "node:fs/promises";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { _electron as electron, expect, test } from "@playwright/test";

const execFileAsync = promisify(execFile);
const screenshotDirectory = path.resolve("doc/uiPrototype/screenshots");

test("refreshes the Desktop MVP screenshot acceptance set", async () => {
  test.skip(process.env.MD2WORD_UPDATE_SCREENSHOTS !== "1", "Set MD2WORD_UPDATE_SCREENSHOTS=1 to refresh committed UI evidence.");

  const fixtureRoot = path.resolve("output/desktop-screenshot-fixture");
  const userDataPath = path.resolve("output/e2e-screenshot-user-data");
  await execFileAsync("powershell.exe", [
    "-NoProfile",
    "-NonInteractive",
    "-File",
    path.resolve("scripts/New-SyntheticAcceptanceFixture.ps1"),
    "-OutputDirectory",
    "output/desktop-screenshot-fixture",
  ]);
  await fs.rm(userDataPath, { recursive: true, force: true });
  await fs.mkdir(screenshotDirectory, { recursive: true });

  const application = await electron.launch({
    args: [path.resolve("dist-electron/main.js")],
    env: {
      ...process.env,
      NODE_ENV: "test",
      MD2WORD_E2E: "1",
      MD2WORD_E2E_USER_DATA: userDataPath,
    },
  });

  const templatePath = path.join(fixtureRoot, "synthetic-template.docx");
  const cssPath = path.join(fixtureRoot, "synthetic-style.css");
  const markdownPath = path.join(fixtureRoot, "synthetic-lists.md");

  try {
    const window = await application.firstWindow();
    await expect(window.getByRole("heading", { name: /Markdown.*Word 模板/ })).toBeVisible();

    await application.evaluate(({ dialog }, filePath) => {
      Object.defineProperty(dialog, "showOpenDialog", {
        configurable: true,
        value: async () => ({ canceled: false, filePaths: [filePath] }),
      });
    }, templatePath);
    const templateFile = await window.evaluate(() => window.md2word!.files.pickTemplateDocx());
    await application.evaluate(({ dialog }, filePath) => {
      Object.defineProperty(dialog, "showOpenDialog", {
        configurable: true,
        value: async () => ({ canceled: false, filePaths: [filePath] }),
      });
    }, cssPath);
    const cssFile = await window.evaluate(() => window.md2word!.files.pickCss());
    expect(templateFile).not.toBeNull();
    expect(cssFile).not.toBeNull();

    await window.evaluate(async ({ templateFile, cssFile }) => {
      if (!templateFile || !cssFile) throw new Error("Synthetic screenshot fixture selection failed");
      const common = {
        templateFile,
        css: { mode: "custom" as const, file: cssFile },
        mermaidDefaults: { mode: "off" as const, format: "png" as const },
      };
      await window.md2word!.templates.add({
        ...common,
        name: "合成说明书模板",
        description: "桌面验收：有序与无序列表使用不同 Word 样式",
      });
      await window.md2word!.templates.add({
        ...common,
        name: "合成技术报告模板",
        description: "桌面验收：同一 DOCX 与 CSS 配置单元",
      });
    }, { templateFile, cssFile });
    await window.reload();
    await expect(window.getByText("桌面正式接入", { exact: true })).toBeVisible();

    await application.evaluate(({ BrowserWindow }) => {
      BrowserWindow.getAllWindows()[0]?.setContentSize(1440, 900);
    });
    await window.waitForTimeout(300);
    await application.evaluate(({ dialog }, filePath) => {
      Object.defineProperty(dialog, "showOpenDialog", {
        configurable: true,
        value: async () => ({ canceled: false, filePaths: [filePath] }),
      });
    }, markdownPath);
    await window.getByRole("button", { name: /拖入 Markdown/ }).click();
    await expect(window.getByText("synthetic-lists.md", { exact: true })).toBeVisible();
    await window.screenshot({ path: path.join(screenshotDirectory, "generate-word-1440x900.png"), animations: "disabled", scale: "css" });

    await application.evaluate(({ BrowserWindow }) => {
      BrowserWindow.getAllWindows()[0]?.setContentSize(1280, 800);
    });
    await window.waitForTimeout(300);
    await window.screenshot({ path: path.join(screenshotDirectory, "generate-word-1280x800.png"), animations: "disabled", scale: "css" });

    await application.evaluate(({ BrowserWindow }) => {
      BrowserWindow.getAllWindows()[0]?.setContentSize(1440, 900);
    });
    await window.waitForTimeout(300);
    await window.getByRole("button", { name: /更换模板，当前为/ }).click();
    await expect(window.getByRole("dialog", { name: "更换模板" })).toBeVisible();
    await window.screenshot({ path: path.join(screenshotDirectory, "template-selector-1440x900.png"), animations: "disabled", scale: "css" });
    await window.keyboard.press("Escape");

    await window.getByRole("button", { name: "模板管理 管理 DOCX 与 CSS", exact: true }).click();
    await expect(window.getByRole("heading", { name: /模板和 CSS/ })).toBeVisible();
    const statusColumnLeftEdges = await window.locator("article").evaluateAll((rows) => rows.map((row) => {
      const badge = [...row.querySelectorAll("span")].find((element) => element.textContent?.trim() === "校验通过");
      if (!badge) throw new Error("Template status badge is missing");
      return badge.getBoundingClientRect().left;
    }));
    expect(statusColumnLeftEdges).toHaveLength(2);
    expect(Math.max(...statusColumnLeftEdges) - Math.min(...statusColumnLeftEdges)).toBeLessThan(1);
    await window.screenshot({ path: path.join(screenshotDirectory, "templates-1440x900.png"), animations: "disabled", scale: "css" });

    await window.getByRole("button", { name: "添加模板", exact: true }).click();
    const templateEditor = window.getByRole("dialog", { name: "添加模板配置" });
    await expect(templateEditor).toBeVisible();
    const editorPanel = templateEditor.locator(":scope > div");
    let editorBounds = await editorPanel.boundingBox();
    const viewport = await window.evaluate(() => ({ width: innerWidth, height: innerHeight }));
    expect(editorBounds).not.toBeNull();
    expect(editorBounds!.x).toBeGreaterThanOrEqual(0);
    expect(editorBounds!.y).toBeGreaterThanOrEqual(0);
    expect(editorBounds!.x + editorBounds!.width).toBeLessThanOrEqual(viewport.width);
    expect(editorBounds!.y + editorBounds!.height).toBeLessThanOrEqual(viewport.height);
    await window.screenshot({ path: path.join(screenshotDirectory, "template-editor-1440x900.png"), animations: "disabled", scale: "css" });

    await application.evaluate(({ BrowserWindow }) => {
      BrowserWindow.getAllWindows()[0]?.setContentSize(1100, 720);
    });
    await window.waitForTimeout(300);
    editorBounds = await editorPanel.boundingBox();
    const minimumViewport = await window.evaluate(() => ({ width: innerWidth, height: innerHeight }));
    expect(editorBounds).not.toBeNull();
    expect(editorBounds!.x).toBeGreaterThanOrEqual(0);
    expect(editorBounds!.y).toBeGreaterThanOrEqual(0);
    expect(editorBounds!.x + editorBounds!.width).toBeLessThanOrEqual(minimumViewport.width);
    expect(editorBounds!.y + editorBounds!.height).toBeLessThanOrEqual(minimumViewport.height);

    await application.evaluate(({ BrowserWindow }) => {
      BrowserWindow.getAllWindows()[0]?.setContentSize(1440, 900);
    });
    await window.waitForTimeout(300);
    await window.keyboard.press("Escape");

    await window.getByRole("button", { name: "能力说明 语法、元数据与边界", exact: true }).click();
    await expect(window.getByRole("heading", { name: "MD2Word 支持能力说明", exact: true })).toBeVisible();
    await window.getByRole("tab", { name: /Front Matter/ }).click();
    await window.screenshot({ path: path.join(screenshotDirectory, "capabilities-1440x900.png"), animations: "disabled", scale: "css" });

    await window.getByRole("button", { name: "环境与设置 依赖检查与偏好", exact: true }).click();
    await expect(window.getByRole("heading", { name: "在启动 Word 前先把环境说清楚", exact: true })).toBeVisible();
    await window.screenshot({ path: path.join(screenshotDirectory, "settings-1440x900.png"), animations: "disabled", scale: "css" });

    await window.getByRole("button", { name: "打开模板库目录", exact: true }).scrollIntoViewIfNeeded();
    await expect(window.getByText("便携版使用 EXE 同级 templates；安装版使用用户数据目录，由 Main 原子维护。", { exact: true })).toBeVisible();
    await window.screenshot({ path: path.join(screenshotDirectory, "settings-template-storage-1440x900.png"), animations: "disabled", scale: "css" });
    await window.getByRole("heading", { name: "在启动 Word 前先把环境说清楚", exact: true }).scrollIntoViewIfNeeded();

    await application.evaluate(({ ipcMain }) => {
      ipcMain.removeHandler("md2word:environment:check");
      ipcMain.handle("md2word:environment:check", async () => ({
        ok: true,
        value: {
          checkedAt: new Date().toISOString(),
          overall: "blocked",
          items: [
            { id: "windows", name: "Windows", version: "Windows 11 · x64", detail: "当前系统可运行桌面版。", status: "ready", required: true },
            { id: "word", name: "Microsoft Word", version: "未检测到", detail: "未发现可用的 Microsoft Word。", status: "blocked", required: true },
            { id: "pandoc", name: "Pandoc", version: "已检测", detail: "已通过本机命令检查。", status: "ready", required: true },
            { id: "worker", name: "C# Word Worker", version: "0.6.1", detail: "Worker JSONL 协议可用。", status: "ready", required: true },
            { id: "mermaid", name: "Mermaid CLI", version: "不可用", detail: "可选组件未安装。", status: "optional-missing", required: false },
          ],
        },
      }));
    });
    await window.getByRole("button", { name: "重新检查", exact: true }).click();
    await expect(window.getByText("未发现可用的 Microsoft Word。", { exact: true })).toBeVisible();
    await window.screenshot({ path: path.join(screenshotDirectory, "settings-word-missing-1440x900.png"), animations: "disabled", scale: "css" });
  } finally {
    await application.close();
    await fs.rm(userDataPath, { recursive: true, force: true });
  }
});
