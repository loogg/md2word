import * as fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import type {
  ConversionEvent,
  EnvironmentStatus,
  StartConversionInput,
  TemplateProfile,
  WorkerConversionResult,
  WorkerRequestFrame,
} from "../contracts";
import { ConversionCoordinator, type ConversionCoordinatorFileSystem } from "../conversion-coordinator";
import { ConversionQueue } from "../conversion-queue";
import { FileDialogService, type DialogPort } from "../file-dialog-service";
import { HandleRegistry, JobResultRegistry } from "../handle-registry";
import type { TemplateSnapshot, TemplateStore } from "../template-store";
import type { WorkerJsonlClient, WorkerOperation } from "../worker-jsonl-client";

const OWNER_ID = 7;
const WORKER_CONTENT = "synthetic worker output";
const temporaryRoots: string[] = [];

afterEach(async () => {
  await Promise.all(temporaryRoots.splice(0).map((root) => fs.rm(root, { recursive: true, force: true })));
});

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

interface Harness {
  root: string;
  jobsRoot: string;
  templatesRoot: string;
  managedRoot: string;
  outputPath: string;
  handles: HandleRegistry;
  coordinator: ConversionCoordinator;
  workerReady: Promise<void>;
  releaseWorker(): void;
  terminalEvent: Promise<ConversionEvent>;
  collisionPath(): string | undefined;
  hardLinkAttempts(): number;
  selectOutput(target?: string): Promise<string>;
  start(outputHandle: string, mermaidMode?: "auto" | "off" | "required"): Promise<{ jobId: string }>;
}

const readyEnvironment: EnvironmentStatus = {
  checkedAt: "2026-07-16T00:00:00.000Z",
  overall: "ready",
  items: [],
};

async function createHarness(options: {
  collideWithSiblingTemporary?: boolean;
  hardLinkErrorCode?: string;
  createTargetBeforeHardLinkFailure?: boolean;
  environment?: EnvironmentStatus;
} = {}): Promise<Harness> {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "md2word-output-publish-"));
  temporaryRoots.push(root);
  const jobsRoot = path.join(root, "jobs");
  const templatesRoot = path.join(root, "templates");
  const managedRoot = path.join(root, "managed");
  const outputDirectory = path.join(root, "user-output");
  const outputPath = path.join(outputDirectory, "result.docx");
  const sourcePath = path.join(root, "synthetic.md");
  const templateDocxPath = path.join(templatesRoot, "synthetic-template.docx");
  const templateCssPath = path.join(templatesRoot, "synthetic-style.css");
  await Promise.all([
    fs.mkdir(jobsRoot, { recursive: true }),
    fs.mkdir(templatesRoot, { recursive: true }),
    fs.mkdir(managedRoot, { recursive: true }),
    fs.mkdir(outputDirectory, { recursive: true }),
  ]);
  await Promise.all([
    fs.writeFile(sourcePath, "# Synthetic\n", "utf8"),
    fs.writeFile(templateDocxPath, "synthetic template placeholder", "utf8"),
    fs.writeFile(templateCssPath, "p { mso-style-name: 'Synthetic Body'; }", "utf8"),
  ]);

  const handles = new HandleRegistry();
  const sourceHandle = handles.register("markdown", sourcePath, OWNER_ID);
  const workerReady = deferred<void>();
  const workerRelease = deferred<void>();
  const terminal = deferred<ConversionEvent>();
  const collision: { path?: string } = {};
  let hardLinkAttempts = 0;
  const snapshot: TemplateSnapshot = {
    profile: {
      validation: { contentFingerprint: "sha256:synthetic" },
    } as TemplateProfile,
    docxPath: templateDocxPath,
    cssPath: templateCssPath,
  };
  const templates = {
    rootPath: templatesRoot,
    withSnapshot: async <T>(_id: string, use: (value: TemplateSnapshot) => Promise<T> | T): Promise<T> => use(snapshot),
  } as unknown as TemplateStore;
  const worker = {
    start(frame: WorkerRequestFrame): WorkerOperation<WorkerConversionResult> {
      if (frame.command !== "convert") throw new Error("expected a synthetic convert request");
      const result = (async (): Promise<WorkerConversionResult> => {
        await fs.mkdir(path.dirname(frame.request.outputPath), { recursive: true });
        await fs.writeFile(frame.request.outputPath, WORKER_CONTENT, "utf8");
        workerReady.resolve();
        await workerRelease.promise;
        return {
          jobId: frame.request.jobId,
          status: "succeeded",
          durationMs: 1,
          warnings: [],
        };
      })();
      return { result, cancel: () => undefined, forceTerminate: () => undefined };
    },
  } as unknown as WorkerJsonlClient;

  let coordinatorFs: ConversionCoordinatorFileSystem | undefined;
  if (options.collideWithSiblingTemporary || options.hardLinkErrorCode) {
    coordinatorFs = {
      ...fs,
      copyFile: async (source, destination, mode) => {
        const destinationPath = String(destination);
        if (
          options.collideWithSiblingTemporary
          && path.dirname(destinationPath) === path.dirname(outputPath)
          && path.basename(destinationPath).startsWith(`.${path.basename(outputPath)}.`)
          && destinationPath.endsWith(".tmp")
        ) {
          collision.path = destinationPath;
          await fs.writeFile(destinationPath, "pre-existing sibling sentinel", "utf8");
        }
        await fs.copyFile(source, destination, mode);
      },
      link: async (existingPath, newPath) => {
        hardLinkAttempts += 1;
        if (options.hardLinkErrorCode) {
          if (options.createTargetBeforeHardLinkFailure) {
            await fs.writeFile(newPath, "created during hard-link fallback race", "utf8");
          }
          throw Object.assign(new Error("synthetic hard-link failure"), { code: options.hardLinkErrorCode });
        }
        await fs.link(existingPath, newPath);
      },
    };
  }

  const queue = new ConversionQueue();
  const coordinator = new ConversionCoordinator({
    jobsRoot,
    handles,
    results: new JobResultRegistry(),
    templates,
    queue,
    worker,
    checkEnvironment: async () => options.environment ?? readyEnvironment,
    tools: { pandocPath: path.join(root, "pandoc.exe") },
    forbiddenOutputRoots: [jobsRoot, templatesRoot, managedRoot],
    emit: (_ownerId, event) => {
      if (["completed", "failed", "canceled"].includes(event.kind)) terminal.resolve(event);
    },
    fs: coordinatorFs,
    idFactory: () => "job-safety0001",
    requestIdFactory: () => "req-safety0001",
    now: () => new Date("2026-07-16T00:00:00.000Z"),
  });

  return {
    root,
    jobsRoot,
    templatesRoot,
    managedRoot,
    outputPath,
    handles,
    coordinator,
    workerReady: workerReady.promise,
    releaseWorker: () => workerRelease.resolve(),
    terminalEvent: terminal.promise,
    collisionPath: () => collision.path,
    hardLinkAttempts: () => hardLinkAttempts,
    async selectOutput(target = outputPath): Promise<string> {
      const dialog: DialogPort = {
        showOpenDialog: async () => ({ canceled: true, filePaths: [] }),
        showSaveDialog: async () => ({ canceled: false, filePath: target }),
      };
      const picked = await new FileDialogService({ dialog, handles }).pickOutput(OWNER_ID, "result.docx");
      if (!picked) throw new Error("synthetic output selection was unexpectedly canceled");
      return picked.handle;
    },
    start(outputHandle: string, mermaidMode: "auto" | "off" | "required" = "off"): Promise<{ jobId: string }> {
      const input: StartConversionInput = {
        templateId: "template-synthetic",
        sourceHandle,
        outputHandle,
        options: { tocDepth: 3, mermaidMode, mermaidFormat: "png" },
      };
      return coordinator.start(OWNER_ID, input);
    },
  };
}

async function finish(harness: Harness): Promise<ConversionEvent> {
  harness.releaseWorker();
  const terminal = await harness.terminalEvent;
  await harness.coordinator.shutdown();
  return terminal;
}

describe("ConversionCoordinator output publication", () => {
  it("blocks required Mermaid before consuming the output grant when npx is unavailable", async () => {
    const environment: EnvironmentStatus = {
      checkedAt: "2026-07-16T00:00:00.000Z",
      overall: "degraded",
      items: [{
        id: "mermaid",
        name: "Mermaid CLI",
        status: "optional-missing",
        required: false,
        version: "不可用",
        detail: "未检测到 npx。",
      }],
    };
    const harness = await createHarness({ environment });
    const outputHandle = await harness.selectOutput();

    await expect(harness.start(outputHandle, "required")).rejects.toMatchObject({
      code: "MERMAID_REQUIRED_UNAVAILABLE",
    });

    await harness.start(outputHandle, "off");
    await harness.workerReady;
    const terminal = await finish(harness);
    expect(terminal.kind).toBe("completed");
  });

  it("publishes a new output without overwriting another path", async () => {
    const harness = await createHarness();
    const outputHandle = await harness.selectOutput();
    await harness.start(outputHandle);
    await harness.workerReady;

    const terminal = await finish(harness);

    expect(terminal.kind).toBe("completed");
    await expect(fs.readFile(harness.outputPath, "utf8")).resolves.toBe(WORKER_CONTENT);
  });

  it.each(["EPERM", "ENOTSUP", "EOPNOTSUPP", "ENOSYS", "EXDEV"])(
    "falls back to an exclusive copy when hard links fail with %s",
    async (hardLinkErrorCode) => {
      const harness = await createHarness({ hardLinkErrorCode });
      const outputHandle = await harness.selectOutput();
      await harness.start(outputHandle);
      await harness.workerReady;

      const terminal = await finish(harness);

      expect(terminal.kind).toBe("completed");
      expect(harness.hardLinkAttempts()).toBe(1);
      await expect(fs.readFile(harness.outputPath, "utf8")).resolves.toBe(WORKER_CONTENT);
    },
  );

  it("does not overwrite a new target created before the exclusive-copy fallback", async () => {
    const harness = await createHarness({
      hardLinkErrorCode: "EPERM",
      createTargetBeforeHardLinkFailure: true,
    });
    const outputHandle = await harness.selectOutput();
    await harness.start(outputHandle);
    await harness.workerReady;

    const terminal = await finish(harness);

    expect(terminal).toMatchObject({ kind: "failed", error: { code: "OUTPUT_COMMIT_FAILED" } });
    await expect(fs.readFile(harness.outputPath, "utf8")).resolves.toBe(
      "created during hard-link fallback race",
    );
  });

  it("atomically replaces an existing regular file that stayed unchanged", async () => {
    const harness = await createHarness();
    await fs.writeFile(harness.outputPath, "approved existing output", "utf8");
    const outputHandle = await harness.selectOutput();
    await harness.start(outputHandle);
    await harness.workerReady;

    const terminal = await finish(harness);

    expect(terminal.kind).toBe("completed");
    await expect(fs.readFile(harness.outputPath, "utf8")).resolves.toBe(WORKER_CONTENT);
  });

  it("refuses a new target created after selection and preserves its content", async () => {
    const harness = await createHarness();
    const outputHandle = await harness.selectOutput();
    await harness.start(outputHandle);
    await harness.workerReady;
    await fs.writeFile(harness.outputPath, "created by another process", "utf8");

    const terminal = await finish(harness);

    expect(terminal).toMatchObject({ kind: "failed", error: { code: "OUTPUT_TARGET_CHANGED" } });
    await expect(fs.readFile(harness.outputPath, "utf8")).resolves.toBe("created by another process");
  });

  it("refuses an existing target modified after selection and preserves the modification", async () => {
    const harness = await createHarness();
    await fs.writeFile(harness.outputPath, "approved existing output", "utf8");
    const outputHandle = await harness.selectOutput();
    await harness.start(outputHandle);
    await harness.workerReady;
    await fs.writeFile(harness.outputPath, "externally modified after the output dialog", "utf8");

    const terminal = await finish(harness);

    expect(terminal).toMatchObject({ kind: "failed", error: { code: "OUTPUT_TARGET_CHANGED" } });
    await expect(fs.readFile(harness.outputPath, "utf8")).resolves.toBe(
      "externally modified after the output dialog",
    );
  });

  it("never recursively removes a directory that appears at the selected output path", async () => {
    const harness = await createHarness();
    const outputHandle = await harness.selectOutput();
    await harness.start(outputHandle);
    await harness.workerReady;
    await fs.mkdir(harness.outputPath);
    const keepPath = path.join(harness.outputPath, "keep.txt");
    await fs.writeFile(keepPath, "must survive", "utf8");

    const terminal = await finish(harness);

    expect(terminal).toMatchObject({ kind: "failed", error: { code: "OUTPUT_TARGET_CHANGED" } });
    await expect(fs.readFile(keepPath, "utf8")).resolves.toBe("must survive");
  });

  it.each([
    ["jobs root", (harness: Harness) => path.join(harness.jobsRoot, "forbidden.docx")],
    ["templates root", (harness: Harness) => path.join(harness.templatesRoot, "forbidden.docx")],
    ["another managed root", (harness: Harness) => path.join(harness.managedRoot, "forbidden.docx")],
  ])("rejects output inside the %s", async (_label, targetFor) => {
    const harness = await createHarness();
    const outputHandle = await harness.selectOutput(targetFor(harness));

    await expect(harness.start(outputHandle)).rejects.toMatchObject({ code: "OUTPUT_PATH_FORBIDDEN" });
    await harness.coordinator.shutdown();
  });

  it("does not overwrite a colliding sibling temporary file", async () => {
    const harness = await createHarness({ collideWithSiblingTemporary: true });
    const outputHandle = await harness.selectOutput();
    await harness.start(outputHandle);
    await harness.workerReady;

    const terminal = await finish(harness);
    const collisionPath = harness.collisionPath();

    expect(terminal).toMatchObject({ kind: "failed", error: { code: "OUTPUT_COMMIT_FAILED" } });
    expect(collisionPath).toBeDefined();
    await expect(fs.readFile(collisionPath!, "utf8")).resolves.toBe("pre-existing sibling sentinel");
    await expect(fs.access(harness.outputPath)).rejects.toMatchObject({ code: "ENOENT" });
  });
});
