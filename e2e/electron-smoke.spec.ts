import path from "node:path";
import os from "node:os";
import * as fs from "node:fs/promises";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { _electron as electron, expect, test } from "@playwright/test";

const execFileAsync = promisify(execFile);
type ElectronApplication = Awaited<ReturnType<typeof electron.launch>>;

async function selectOpenDialogFile(application: ElectronApplication, selectedPath: string) {
  await application.evaluate(({ dialog }, filePath) => {
    Object.defineProperty(dialog, "showOpenDialog", {
      configurable: true,
      value: async () => ({ canceled: false, filePaths: [filePath] }),
    });
  }, selectedPath);
}

async function selectSaveDialogFile(application: ElectronApplication, selectedPath: string) {
  await application.evaluate(({ dialog }, filePath) => {
    Object.defineProperty(dialog, "showSaveDialog", {
      configurable: true,
      value: async () => ({ canceled: false, filePath }),
    });
  }, selectedPath);
}

test("desktop shell exposes only the narrow API and keeps the four-page workflow usable", async () => {
  const userDataPath = path.resolve("output/e2e-user-data");
  await fs.rm(userDataPath, { recursive: true, force: true });
  const packagedExecutable = process.env.MD2WORD_E2E_EXECUTABLE;
  const application = await electron.launch(packagedExecutable
    ? {
        executablePath: path.resolve(packagedExecutable),
        args: [`--user-data-dir=${userDataPath}`],
        env: { ...process.env, NODE_ENV: "test" },
      }
    : {
        args: [path.resolve("dist-electron/main.js")],
        env: {
          ...process.env,
          NODE_ENV: "test",
          MD2WORD_E2E: "1",
          MD2WORD_E2E_USER_DATA: userDataPath,
          MD2WORD_MERMAID_NPM_CACHE: path.resolve("output/e2e-mermaid-npm-cache"),
        },
      });

  try {
    const window = await application.firstWindow();
    await expect(window.getByRole("heading", { name: /Markdown.*Word 模板/ })).toBeVisible();

    const boundary = await window.evaluate(() => ({
      hasApi: typeof window.md2word === "object",
      nodeProcess: typeof (window as Window & { process?: unknown }).process,
      nodeRequire: typeof (window as Window & { require?: unknown }).require,
      apiKeys: Object.keys(window.md2word ?? {}).sort(),
    }));

    expect(boundary).toEqual({
      hasApi: true,
      nodeProcess: "undefined",
      nodeRequire: "undefined",
      apiKeys: ["capabilities", "conversions", "environment", "files", "runtimeCapabilities", "shell", "templates"],
    });

    const structuredError = await window.evaluate(async () => {
      try {
        await window.md2word!.conversions.cancel("missing-job");
        return null;
      } catch (error) {
        const value = error as Error & { code?: string; retryable?: boolean };
        return { code: value.code, message: value.message, retryable: value.retryable };
      }
    });
    expect(structuredError).toEqual({ code: "JOB_NOT_FOUND", message: "任务不存在或已经结束。", retryable: false });

    const environment = await window.evaluate(() => window.md2word!.environment.check());
    expect(environment.items.filter((item) => item.required).every((item) => item.status === "ready")).toBe(true);
    const capabilities = await window.evaluate(() => window.md2word!.capabilities.describe());
    expect(capabilities).toMatchObject({ schemaVersion: "1.1", productVersion: "0.6.1", protocolVersion: "1.0" });
    expect(capabilities.frontMatter.map((item) => item.key)).toContain("word_heading_numbering");

    await window.getByRole("button", { name: "模板管理 管理 DOCX 与 CSS", exact: true }).click();
    await expect(window.getByRole("heading", { name: /模板和 CSS/ })).toBeVisible();

    await window.getByRole("button", { name: "能力说明 语法、元数据与边界", exact: true }).click();
    await expect(window.getByRole("heading", { name: "MD2Word 支持能力说明", exact: true })).toBeVisible();

    await window.getByRole("button", { name: "环境与设置 依赖检查与偏好", exact: true }).click();
    await expect(window.getByRole("heading", { name: "在启动 Word 前先把环境说清楚", exact: true })).toBeVisible();
  } finally {
    await application.close();
    await fs.rm(userDataPath, { recursive: true, force: true });
  }
});

test("desktop IPC stack performs a real synthetic Word conversion with lists, Mermaid, and caption styles", async () => {
  test.setTimeout(240_000);
  test.skip(process.env.MD2WORD_RUN_DESKTOP_WORD_E2E !== "1", "Set MD2WORD_RUN_DESKTOP_WORD_E2E=1 on a Windows Word host.");

  const fixtureRoot = path.resolve("output/desktop-word-e2e");
  const userDataPath = path.resolve("output/e2e-desktop-word-user-data");
  const temporaryOutputRoot = await fs.mkdtemp(path.join(os.tmpdir(), "md2word-desktop-word-e2e-"));
  await execFileAsync("powershell.exe", [
    "-NoProfile",
    "-NonInteractive",
    "-File",
    path.resolve("scripts/New-SyntheticAcceptanceFixture.ps1"),
    "-OutputDirectory",
    "output/desktop-word-e2e",
  ]);
  await fs.rm(userDataPath, { recursive: true, force: true });
  const templatePath = path.join(fixtureRoot, "synthetic-template.docx");
  const cssPath = path.join(fixtureRoot, "synthetic-style.css");
  const markdownPath = path.join(fixtureRoot, "synthetic-lists.md");
  const outputPath = path.join(temporaryOutputRoot, "desktop-stack-output.docx");
  const acceptanceOutputPath = path.join(fixtureRoot, "desktop-stack-output.docx");

  const packagedExecutable = process.env.MD2WORD_E2E_EXECUTABLE;
  const application = await electron.launch(packagedExecutable
    ? {
        executablePath: path.resolve(packagedExecutable),
        args: [`--user-data-dir=${userDataPath}`],
        env: { ...process.env, NODE_ENV: "test" },
      }
    : {
        args: [path.resolve("dist-electron/main.js")],
        env: {
          ...process.env,
          NODE_ENV: "test",
          MD2WORD_E2E: "1",
          MD2WORD_E2E_USER_DATA: userDataPath,
          MD2WORD_MERMAID_NPM_CACHE: path.resolve("output/e2e-mermaid-npm-cache"),
        },
      });

  try {
    const window = await application.firstWindow();
    await expect(window.getByRole("heading", { name: /Markdown.*Word 模板/ })).toBeVisible();

    await selectOpenDialogFile(application, templatePath);
    const templateFile = await window.evaluate(() => window.md2word!.files.pickTemplateDocx());
    await selectOpenDialogFile(application, cssPath);
    const cssFile = await window.evaluate(() => window.md2word!.files.pickCss());
    expect(templateFile).not.toBeNull();
    expect(cssFile).not.toBeNull();

    const profile = await window.evaluate(async ({ templateFile, cssFile }) => {
      if (!templateFile || !cssFile) throw new Error("Synthetic file selection failed");
      return window.md2word!.templates.add({
        name: "合成列表验收模板",
        description: "仅用于桌面端到端测试",
        templateFile,
        css: { mode: "custom", file: cssFile },
        mermaidDefaults: { mode: "required", format: "png" },
      });
    }, { templateFile, cssFile });
    expect(profile.validation.styleMappings).toEqual(expect.arrayContaining([
      expect.objectContaining({ role: "ordered-list", resolvedStyleName: "示例 有序列项", status: "resolved" }),
      expect.objectContaining({ role: "unordered-list", resolvedStyleName: "示例 正文", status: "resolved" }),
    ]));

    await selectOpenDialogFile(application, markdownPath);
    const source = await window.evaluate(() => window.md2word!.files.pickMarkdown());
    await selectSaveDialogFile(application, outputPath);
    const output = await window.evaluate(() => window.md2word!.files.pickOutput("desktop-stack-output.docx"));
    expect(source).not.toBeNull();
    expect(output).not.toBeNull();

    const completion = await window.evaluate(async ({ templateId, source, output }) => {
      if (!source || !output) throw new Error("Synthetic conversion handles are missing");
      return new Promise<{
        kind: string;
        stages: string[];
        warnings: string[];
        errorCode?: string;
        errorMessage?: string;
        errorStage?: string;
        timeline: string;
      }>((resolve, reject) => {
        let jobId: string | undefined;
        const events: Array<{
          jobId: string;
          kind: string;
          stage?: string;
          result?: { warnings: string[] };
          error?: { code: string; message: string; stage?: string };
        }> = [];
        const timeout = window.setTimeout(() => {
          unsubscribe();
          const timeline = events.map((event) => `${event.kind}:${event.stage ?? "-"}:${event.error?.code ?? "-"}`).join(",");
          reject(new Error(`Timed out waiting for the synthetic desktop conversion (${timeline})`));
        }, 210_000);
        const finish = (event: (typeof events)[number]) => {
          window.clearTimeout(timeout);
          unsubscribe();
          resolve({
            kind: event.kind,
            stages: events.filter((item) => item.jobId === event.jobId && item.stage).map((item) => item.stage!),
            warnings: event.result?.warnings ?? [],
            errorCode: event.error?.code,
            errorMessage: event.error?.message,
            errorStage: event.error?.stage,
            timeline: events
              .filter((item) => item.jobId === event.jobId)
              .map((item) => `${item.kind}:${item.stage ?? "-"}:${item.error?.code ?? "-"}`)
              .join(","),
          });
        };
        const unsubscribe = window.md2word!.conversions.onEvent((event) => {
          events.push(event);
          if (jobId === event.jobId && ["completed", "failed", "canceled"].includes(event.kind)) finish(event);
        });
        void window.md2word!.conversions.start({
          templateId,
          sourceHandle: source.handle,
          outputHandle: output.handle,
          options: { tocDepth: 3, mermaidMode: "required", mermaidFormat: "png" },
        }).then((accepted) => {
          jobId = accepted.jobId;
          const terminal = events.find((event) => event.jobId === jobId && ["completed", "failed", "canceled"].includes(event.kind));
          if (terminal) finish(terminal);
        }, (reason: unknown) => {
          const error = reason as { code?: string; message?: string };
          reject(new Error(`${error.code ?? "CONVERSION_START_FAILED"}: ${error.message ?? "Conversion start failed"}`));
        });
      });
    }, { templateId: profile.id, source, output });

    if (completion.kind !== "completed") {
      throw new Error(`Synthetic desktop conversion failed: ${JSON.stringify(completion)}`);
    }
    expect(completion).toMatchObject({ kind: "completed", errorCode: undefined });
    expect(completion.stages).toEqual(expect.arrayContaining(["metadata", "pandoc", "mermaid", "word-import", "openxml-finalize", "cleanup"]));
    expect(completion.warnings.length).toBeGreaterThanOrEqual(1);
    await expect(fs.stat(outputPath)).resolves.toMatchObject({ size: expect.any(Number) });
    expect((await fs.stat(outputPath)).size).toBeGreaterThan(0);
    await fs.copyFile(outputPath, acceptanceOutputPath);
  } finally {
    await application.close();
    await fs.rm(userDataPath, { recursive: true, force: true });
    await fs.rm(temporaryOutputRoot, { recursive: true, force: true });
  }
});
