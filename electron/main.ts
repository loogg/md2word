import { execFile } from "node:child_process";
import * as fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { app, dialog, ipcMain, shell, type BrowserWindow } from "electron";
import type { CapabilityManifest, ConversionEvent, EnvironmentStatus, WorkerConversionEvent } from "./contracts";
import { ConversionCoordinator } from "./conversion-coordinator";
import { ConversionQueue } from "./conversion-queue";
import { EnvironmentService, type EnvironmentProbeResult } from "./environment-service";
import { AppError } from "./errors";
import { FileDialogService, type DialogPort } from "./file-dialog-service";
import { HandleRegistry, JobResultRegistry } from "./handle-registry";
import { IPC_CHANNELS } from "./ipc-channels";
import { registerIpcHandlers } from "./ipc";
import { createMainWindow } from "./main-window";
import { sanitizeWorkerEvent } from "./sanitize";
import { resolveTemplateLibraryRoot } from "./runtime-paths";
import { isSetupInstallation, seedInstalledTemplatePackages } from "./installed-templates";
import { TemplateStore } from "./template-store";
import { TemplateDraftValidationService, WorkerTemplateValidator } from "./template-validation-service";
import { WorkerJsonlClient } from "./worker-jsonl-client";

const execFileAsync = promisify(execFile);
const MERMAID_CLI_VERSION = "11.16.0";
const delay = (milliseconds: number) => new Promise<void>((resolve) => setTimeout(resolve, milliseconds));
const moduleDirectory = path.dirname(fileURLToPath(import.meta.url));
const e2eUserDataPath = process.env.MD2WORD_E2E === "1" ? process.env.MD2WORD_E2E_USER_DATA : undefined;
if (!app.isPackaged && e2eUserDataPath && path.isAbsolute(e2eUserDataPath)) {
  app.setPath("userData", path.resolve(e2eUserDataPath));
}
let mainWindow: BrowserWindow | undefined;
let disposeIpc: (() => void) | undefined;
let runtime: Awaited<ReturnType<typeof createRuntime>> | undefined;
let shutdownPromise: Promise<void> | undefined;
let allowQuit = false;

async function resolveExecutable(command: string): Promise<string | undefined> {
  const wherePath = path.join(process.env.SystemRoot ?? "C:\\Windows", "System32", "where.exe");
  try {
    const { stdout } = await execFileAsync(wherePath, [command], {
      encoding: "utf8",
      windowsHide: true,
      timeout: 4_000,
    });
    const candidate = stdout
      .split(/\r?\n/)
      .map((line) => line.trim())
      .find((line) => path.isAbsolute(line));
    return candidate ? path.resolve(candidate) : undefined;
  } catch {
    return undefined;
  }
}

async function resolveMermaidBrowser(): Promise<string | undefined> {
  const developmentOverride = !app.isPackaged ? process.env.MD2WORD_MERMAID_BROWSER_PATH : undefined;
  const programFilesX86 = process.env["ProgramFiles(x86)"];
  const candidates = [
    developmentOverride && path.isAbsolute(developmentOverride) ? path.resolve(developmentOverride) : undefined,
    programFilesX86 ? path.join(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe") : undefined,
    process.env.ProgramFiles ? path.join(process.env.ProgramFiles, "Microsoft", "Edge", "Application", "msedge.exe") : undefined,
    process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, "Microsoft", "Edge", "Application", "msedge.exe") : undefined,
    process.env.ProgramFiles ? path.join(process.env.ProgramFiles, "Google", "Chrome", "Application", "chrome.exe") : undefined,
    programFilesX86 ? path.join(programFilesX86, "Google", "Chrome", "Application", "chrome.exe") : undefined,
    await resolveExecutable("msedge.exe"),
    await resolveExecutable("chrome.exe"),
  ];
  for (const candidate of candidates) {
    if (!candidate) continue;
    try {
      await fs.access(candidate);
      return path.resolve(candidate);
    } catch {
      // Keep checking the known browser locations.
    }
  }
  return undefined;
}

async function commandProbe(command: string | undefined, args: string[]): Promise<EnvironmentProbeResult> {
  if (!command) return { available: false, detail: "未发现所需命令。" };
  try {
    await fs.access(command);
    let executable = command;
    let invocationArgs = args;
    if (process.platform === "win32" && path.basename(command).toLowerCase() === "npx.cmd") {
      const nodeExecutable = path.join(path.dirname(command), "node.exe");
      const npxCli = path.join(path.dirname(command), "node_modules", "npm", "bin", "npx-cli.js");
      await Promise.all([fs.access(nodeExecutable), fs.access(npxCli)]);
      executable = nodeExecutable;
      invocationArgs = [npxCli, ...args];
    }
    const { stdout } = await execFileAsync(executable, invocationArgs, {
      encoding: "utf8",
      windowsHide: true,
      timeout: 8_000,
      maxBuffer: 256 * 1024,
    });
    const firstLine = stdout.split(/\r?\n/, 1)[0]?.trim() || "已检测";
    return { available: true, version: firstLine.slice(0, 100), detail: "已通过本机命令检查。" };
  } catch {
    return { available: false, detail: "命令不可用或版本检查失败。" };
  }
}

function createDialogPort(): DialogPort {
  const requireOwnerWindow = (ownerId: number): BrowserWindow => {
    if (!mainWindow || mainWindow.isDestroyed() || mainWindow.webContents.id !== ownerId) {
      throw new AppError("IPC_FORBIDDEN", "文件对话框请求来源无效。");
    }
    return mainWindow;
  };
  return {
    showOpenDialog: (ownerId, options) => dialog.showOpenDialog(requireOwnerWindow(ownerId), options),
    showSaveDialog: (ownerId, options) => dialog.showSaveDialog(requireOwnerWindow(ownerId), options),
  };
}

async function createRuntime(): Promise<{
  createWindow(): Promise<BrowserWindow>;
  shutdown(): Promise<void>;
}> {
  // In development Electron is launched with dist-electron/main.js, so app.getAppPath()
  // resolves to the bundle directory rather than the repository/application root.
  const appRoot = app.isPackaged ? app.getAppPath() : path.resolve(moduleDirectory, "..");
  const resourcesRoot = app.isPackaged ? process.resourcesPath : path.join(appRoot, "resources");
  const userData = app.getPath("userData");
  const isInstalled = app.isPackaged && await isSetupInstallation(resourcesRoot);
  const templatesRoot = resolveTemplateLibraryRoot({
    isPackaged: app.isPackaged,
    isInstalled,
    userDataPath: userData,
    executablePath: process.execPath,
    portableExecutableDirectory: process.env.PORTABLE_EXECUTABLE_DIR,
  });
  const jobsRoot = path.join(userData, "jobs");
  const conversionResourcesRoot = path.join(resourcesRoot, "conversion");
  const mermaidCacheOverride = !app.isPackaged ? process.env.MD2WORD_MERMAID_NPM_CACHE : undefined;
  const mermaidNpmCacheRoot = mermaidCacheOverride && path.isAbsolute(mermaidCacheOverride)
    ? path.resolve(mermaidCacheOverride)
    : path.join(userData, "mermaid-npm-cache");
  const mermaidBrowserCacheOverride = !app.isPackaged ? process.env.MD2WORD_MERMAID_BROWSER_CACHE : undefined;
  const mermaidBrowserCacheRoot = mermaidBrowserCacheOverride && path.isAbsolute(mermaidBrowserCacheOverride)
    ? path.resolve(mermaidBrowserCacheOverride)
    : path.join(userData, "mermaid-browser-cache");
  const builtinCssPath = path.join(conversionResourcesRoot, "docx-worddom-style.css");
  const workerOverride = !app.isPackaged ? process.env.MD2WORD_WORKER_PATH : undefined;
  const pandocOverride = !app.isPackaged ? process.env.MD2WORD_PANDOC_PATH : undefined;
  const workerPath = workerOverride
    ? path.resolve(workerOverride)
    : app.isPackaged
      ? path.join(process.resourcesPath, "worker", "MD2Word.Worker.exe")
      : path.join(appRoot, "worker", "publish", "win-x64", "MD2Word.Worker.exe");
  const pandocPath = pandocOverride
    ? path.resolve(pandocOverride)
    : ((await resolveExecutable("pandoc.exe")) ?? path.join(resourcesRoot, "conversion", "pandoc.exe"));
  const npxPath = await resolveExecutable("npx.cmd");
  const mermaidBrowserPath = await resolveMermaidBrowser();

  await fs.rm(jobsRoot, { recursive: true, force: true });
  await fs.mkdir(jobsRoot, { recursive: true });
  await fs.mkdir(mermaidNpmCacheRoot, { recursive: true });
  await fs.mkdir(mermaidBrowserCacheRoot, { recursive: true });

  const handles = new HandleRegistry();
  const results = new JobResultRegistry();
  const worker = new WorkerJsonlClient({
    executablePath: workerPath,
    environment: {
      ...process.env,
      MD2WORD_CONVERSION_RESOURCES: conversionResourcesRoot,
      MD2WORD_MERMAID_NPM_CACHE: mermaidNpmCacheRoot,
      MD2WORD_MERMAID_BROWSER_CACHE: mermaidBrowserCacheRoot,
    },
  });
  let capabilityCatalogPromise: Promise<CapabilityManifest> | undefined;
  const describeCapabilities = (): Promise<CapabilityManifest> => {
    if (capabilityCatalogPromise) return capabilityCatalogPromise;
    const requestId = `req-capabilities-${Date.now()}`;
    capabilityCatalogPromise = worker.start<CapabilityManifest>(
      { protocolVersion: "1.0", requestId, command: "describe-capabilities" },
      { timeoutMs: 15_000 },
    ).result.catch((error: unknown) => {
      capabilityCatalogPromise = undefined;
      throw error;
    });
    return capabilityCatalogPromise;
  };
  const templateValidator = new WorkerTemplateValidator(worker);
  if (isInstalled) {
    await seedInstalledTemplatePackages(path.join(path.dirname(process.execPath), "templates"), templatesRoot);
  }
  const templates = new TemplateStore({ templatesRoot, builtinCssPath, validator: templateValidator });
  await templates.initialize();
  const draftValidation = new TemplateDraftValidationService({
    handles,
    templates,
    validator: templateValidator,
    builtinCssPath,
  });

  let cachedDiagnosis: { status: EnvironmentStatus; expiresAt: number } | undefined;
  let diagnosisInFlight: Promise<EnvironmentStatus> | undefined;
  const diagnoseWorker = async (): Promise<EnvironmentStatus> => {
    if (cachedDiagnosis && cachedDiagnosis.expiresAt >= Date.now()) return cachedDiagnosis.status;
    if (diagnosisInFlight) return diagnosisInFlight;
    diagnosisInFlight = (async () => {
      await fs.access(workerPath);
      const requestId = `req-diagnose-${Date.now()}`;
      const status = await worker.start<EnvironmentStatus>(
        { protocolVersion: "1.0", requestId, command: "diagnose" },
        { timeoutMs: 15_000 },
      ).result;
      cachedDiagnosis = { status, expiresAt: Date.now() + 1_000 };
      return status;
    })();
    try {
      return await diagnosisInFlight;
    } finally {
      diagnosisInFlight = undefined;
    }
  };
  const diagnosisProbe = (id: "word" | "worker") => async (): Promise<EnvironmentProbeResult> => {
    const status = await diagnoseWorker();
    const item = status.items.find((candidate) => candidate.id === id);
    if (!item) throw new AppError("WORKER_PROTOCOL_INVALID_RESULT", `Worker 诊断缺少 ${id} 状态。`);
    return {
      available: item.status === "ready",
      version: item.version,
      detail: item.detail,
    };
  };

  const environment = new EnvironmentService({
    probes: {
      word: diagnosisProbe("word"),
      pandoc: () => commandProbe(pandocPath, ["--version"]),
      worker: diagnosisProbe("worker"),
      mermaid: async () => {
        const result = await commandProbe(npxPath, ["--version"]);
        if (!result.available || !mermaidBrowserPath) {
          return {
            available: false,
            version: result.version ? `npx ${result.version}` : undefined,
            detail: !mermaidBrowserPath
              ? "未检测到可供 Mermaid 使用的 Microsoft Edge 或 Google Chrome。"
              : result.detail,
          };
        }
        return {
          available: true,
          version: `${MERMAID_CLI_VERSION}（固定）`,
          detail: `npx ${result.version ?? "已检测"} 已就绪；首次使用可能通过 npm 获取固定版本渲染器，文档内容不会上传。`,
        };
      },
    },
  });

  const sendPublicEvent = (ownerId: number, event: ConversionEvent) => {
    if (!mainWindow || mainWindow.isDestroyed() || mainWindow.webContents.id !== ownerId) return;
    mainWindow.webContents.send(IPC_CHANNELS.conversionsEvent, event);
  };
  const sendWorkerEvent = (ownerId: number, event: WorkerConversionEvent) =>
    sendPublicEvent(ownerId, sanitizeWorkerEvent(event));
  const queue = new ConversionQueue({ onEvent: sendWorkerEvent });
  const coordinator = new ConversionCoordinator({
    jobsRoot,
    handles,
    results,
    templates,
    queue,
    worker,
    checkEnvironment: () => environment.check(),
    tools: { pandocPath, npxPath, mermaidBrowserPath },
    forbiddenOutputRoots: [userData, templatesRoot, jobsRoot, resourcesRoot, appRoot],
    emit: sendPublicEvent,
  });
  const dialogs = new FileDialogService({ dialog: createDialogPort(), handles });

  const createWindow = async () => {
    const preloadOverride = !app.isPackaged ? process.env.MD2WORD_PRELOAD_PATH : undefined;
    const preloadPath = preloadOverride
      ? path.resolve(preloadOverride)
      : path.join(moduleDirectory, "preload.cjs");
    const rendererHtmlPath = path.join(appRoot, "dist", "index.html");
    const developmentUrl = app.isPackaged ? undefined : process.env.VITE_DEV_SERVER_URL;
    const window = await createMainWindow({
      preloadPath,
      rendererHtmlPath,
      developmentUrl,
      beforeLoad: (createdWindow) => {
        mainWindow = createdWindow;
        disposeIpc?.();
        disposeIpc = registerIpcHandlers({
          ipcMain,
          getTrustedWebContents: () => mainWindow?.webContents,
          handles,
          results,
          dialogs,
          templates,
          draftValidation,
          conversions: coordinator,
          environment,
          describeCapabilities,
          shell,
        });
        const ownerId = createdWindow.webContents.id;
        createdWindow.webContents.once("destroyed", () => {
          handles.revokeOwner(ownerId);
          results.revokeOwner(ownerId);
          draftValidation.revokeOwner(ownerId);
        });
        createdWindow.once("closed", () => {
          if (mainWindow === createdWindow) mainWindow = undefined;
        });
      },
    });
    return window;
  };

  let runtimeShutdown: Promise<void> | undefined;
  const shutdown = () => {
    runtimeShutdown ??= (async () => {
      const graceful = (async () => {
        const conversionShutdown = coordinator.shutdown();
        await Promise.allSettled([templates.shutdown(), conversionShutdown]);
        await worker.shutdown();
      })();
      const completed = await Promise.race([
        graceful.then(() => true, () => true),
        delay(30_000).then(() => false),
      ]);
      if (!completed) {
        worker.forceTerminateAll();
        await Promise.race([graceful.then(() => undefined, () => undefined), delay(2_000)]);
      }
    })();
    return runtimeShutdown;
  };

  return { createWindow, shutdown };
}

const singleInstance = app.requestSingleInstanceLock();
if (!singleInstance) {
  app.quit();
} else {
  void app.whenReady()
    .then(async () => {
      const createdRuntime = await createRuntime();
      runtime = createdRuntime;
      await createdRuntime.createWindow();
      app.on("second-instance", () => {
        if (mainWindow) {
          if (mainWindow.isMinimized()) mainWindow.restore();
          mainWindow.focus();
        }
      });
      app.on("activate", () => {
        if (!mainWindow) void createdRuntime.createWindow();
      });
    })
    .catch(async (error: unknown) => {
      const message = error instanceof AppError ? error.message : "应用初始化失败，请检查安装资源和模板库。";
      await dialog.showMessageBox({
        type: "error",
        title: "MD2Word 无法启动",
        message,
        detail: "未打开任何文档，也未启动转换任务。",
      });
      allowQuit = true;
      app.quit();
    });
}

app.on("window-all-closed", () => app.quit());
app.on("before-quit", (event) => {
  if (allowQuit) return;
  event.preventDefault();
  disposeIpc?.();
  disposeIpc = undefined;
  shutdownPromise ??= (async () => {
    try {
      await runtime?.shutdown();
    } finally {
      allowQuit = true;
      app.quit();
    }
  })();
});
app.on("will-quit", () => {
  disposeIpc?.();
  disposeIpc = undefined;
});
