import * as fs from "node:fs/promises";
import path from "node:path";
import type { PickedFile, PickedOutput } from "./contracts";
import { AppError } from "./errors";
import { HandleRegistry, type OutputTargetState } from "./handle-registry";
import { assertExtension, normalizeSuggestedDocxName } from "./validation";

export interface OpenDialogOptions {
  title: string;
  properties: Array<"openFile">;
  filters: Array<{ name: string; extensions: string[] }>;
}

export interface SaveDialogOptions {
  title: string;
  defaultPath: string;
  filters: Array<{ name: string; extensions: string[] }>;
  properties: Array<"showOverwriteConfirmation" | "createDirectory">;
}

export interface DialogPort {
  showOpenDialog(ownerId: number, options: OpenDialogOptions): Promise<{ canceled: boolean; filePaths: string[] }>;
  showSaveDialog(ownerId: number, options: SaveDialogOptions): Promise<{ canceled: boolean; filePath?: string }>;
}

export interface FileDialogServiceDependencies {
  dialog: DialogPort;
  handles: HandleRegistry;
  fs?: Pick<typeof fs, "stat" | "lstat" | "access">;
}

export class FileDialogService {
  readonly #dialog: DialogPort;
  readonly #handles: HandleRegistry;
  readonly #fs: Pick<typeof fs, "stat" | "lstat" | "access">;

  constructor(dependencies: FileDialogServiceDependencies) {
    this.#dialog = dependencies.dialog;
    this.#handles = dependencies.handles;
    this.#fs = dependencies.fs ?? fs;
  }

  pickMarkdown(ownerId: number): Promise<PickedFile | null> {
    return this.#pick(ownerId, "markdown", [".md", ".markdown"], {
      title: "选择 Markdown 文件",
      properties: ["openFile"],
      filters: [{ name: "Markdown", extensions: ["md", "markdown"] }],
    });
  }

  /**
   * Called only by the private preload drag/drop channel after webUtils.getPathForFile(File).
   * Page code never supplies a path to the public API.
   */
  registerMarkdown(ownerId: number, osBackedPath: string): Promise<PickedFile> {
    return this.#registerPicked(ownerId, "markdown", osBackedPath, [".md", ".markdown"], "Markdown");
  }

  pickTemplateDocx(ownerId: number): Promise<PickedFile | null> {
    return this.#pick(ownerId, "template-docx", [".docx"], {
      title: "选择 Word 模板",
      properties: ["openFile"],
      filters: [{ name: "Word 模板", extensions: ["docx"] }],
    });
  }

  pickCss(ownerId: number): Promise<PickedFile | null> {
    return this.#pick(ownerId, "css", [".css"], {
      title: "选择 CSS 样式",
      properties: ["openFile"],
      filters: [{ name: "CSS", extensions: ["css"] }],
    });
  }

  async pickOutput(ownerId: number, suggestedName: unknown): Promise<PickedOutput | null> {
    const defaultPath = normalizeSuggestedDocxName(suggestedName);
    const result = await this.#dialog.showSaveDialog(ownerId, {
      title: "另存为 Word 文档",
      defaultPath,
      filters: [{ name: "Word 文档", extensions: ["docx"] }],
      properties: ["showOverwriteConfirmation", "createDirectory"],
    });
    if (result.canceled || !result.filePath) return null;
    if (!path.isAbsolute(result.filePath)) throw new AppError("INVALID_OUTPUT_PATH", "输出位置无效。");
    assertExtension(result.filePath, [".docx"], "输出");
    let outputTargetState: OutputTargetState;
    try {
      const stats = await this.#fs.lstat(result.filePath);
      if (!stats.isFile() || stats.isSymbolicLink()) {
        throw new AppError("INVALID_OUTPUT_PATH", "输出位置必须是普通文件，不能是目录或链接。");
      }
      outputTargetState = {
        existed: true,
        size: stats.size,
        mtimeMs: stats.mtimeMs,
        ctimeMs: stats.ctimeMs,
        ino: stats.ino,
        dev: stats.dev,
      };
    } catch (error) {
      if (!(error instanceof Error && "code" in error && (error as NodeJS.ErrnoException).code === "ENOENT")) throw error;
      outputTargetState = { existed: false };
    }
    const handle = this.#handles.register("output-docx", result.filePath, ownerId, {
      ttlMs: 15 * 60_000,
      singleUse: true,
      outputTargetState,
    });
    const fileName = path.basename(result.filePath);
    return { handle, fileName, displayPath: `已选择\\${fileName}` };
  }

  async #pick(
    ownerId: number,
    kind: "markdown" | "template-docx" | "css",
    extensions: readonly string[],
    options: OpenDialogOptions,
  ): Promise<PickedFile | null> {
    const result = await this.#dialog.showOpenDialog(ownerId, options);
    if (result.canceled || result.filePaths.length === 0) return null;
    if (result.filePaths.length !== 1 || !path.isAbsolute(result.filePaths[0])) {
      throw new AppError("INVALID_FILE_SELECTION", "请选择一个本机文件。");
    }
    return this.#registerPicked(ownerId, kind, result.filePaths[0], extensions, options.title);
  }

  async #registerPicked(
    ownerId: number,
    kind: "markdown" | "template-docx" | "css",
    absolutePath: string,
    extensions: readonly string[],
    label: string,
  ): Promise<PickedFile> {
    if (!path.isAbsolute(absolutePath)) throw new AppError("INVALID_FILE_SELECTION", "请选择本机文件。");
    assertExtension(absolutePath, extensions, label);
    await this.#fs.access(absolutePath);
    const stats = await this.#fs.stat(absolutePath);
    if (!stats.isFile()) throw new AppError("INVALID_FILE_SELECTION", "所选项目不是可读文件。");
    const handle = this.#handles.register(kind, absolutePath, ownerId);
    return {
      handle,
      fileName: path.basename(absolutePath),
      size: stats.size,
      lastModified: stats.mtimeMs,
    };
  }
}
