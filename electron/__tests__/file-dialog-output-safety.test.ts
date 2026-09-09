import * as fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { FileDialogService, type DialogPort } from "../file-dialog-service";
import { HandleRegistry } from "../handle-registry";

const temporaryRoots: string[] = [];

afterEach(async () => {
  await Promise.all(temporaryRoots.splice(0).map((root) => fs.rm(root, { recursive: true, force: true })));
});

async function temporaryRoot(): Promise<string> {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "md2word-output-dialog-"));
  temporaryRoots.push(root);
  return root;
}

function serviceFor(filePath: string, handles: HandleRegistry): FileDialogService {
  const dialog: DialogPort = {
    showOpenDialog: async () => ({ canceled: true, filePaths: [] }),
    showSaveDialog: async () => ({ canceled: false, filePath }),
  };
  return new FileDialogService({ dialog, handles });
}

describe("FileDialogService output grants", () => {
  it("records the identity of an existing regular output file", async () => {
    const root = await temporaryRoot();
    const outputPath = path.join(root, "existing.docx");
    await fs.writeFile(outputPath, "synthetic existing output", "utf8");
    const handles = new HandleRegistry({ randomToken: () => "a".repeat(24) });

    const picked = await serviceFor(outputPath, handles).pickOutput(7, "suggested.docx");

    expect(picked).not.toBeNull();
    const grant = handles.resolveOutput(picked!.handle, 7);
    expect(grant.absolutePath).toBe(outputPath);
    expect(grant.expected).toEqual({
      existed: true,
      size: Buffer.byteLength("synthetic existing output"),
      mtimeMs: expect.any(Number),
      ctimeMs: expect.any(Number),
      ino: expect.any(Number),
      dev: expect.any(Number),
    });
  });

  it("records that a regular output target did not exist when it was selected", async () => {
    const root = await temporaryRoot();
    const outputPath = path.join(root, "new.docx");
    const handles = new HandleRegistry({ randomToken: () => "b".repeat(24) });

    const picked = await serviceFor(outputPath, handles).pickOutput(7, "suggested.docx");

    expect(picked).not.toBeNull();
    expect(handles.resolveOutput(picked!.handle, 7)).toEqual({
      absolutePath: outputPath,
      expected: { existed: false },
    });
  });

  it("rejects a directory even when its name has a .docx suffix", async () => {
    const root = await temporaryRoot();
    const outputPath = path.join(root, "directory.docx");
    await fs.mkdir(outputPath);

    await expect(
      serviceFor(outputPath, new HandleRegistry()).pickOutput(7, "suggested.docx"),
    ).rejects.toMatchObject({ code: "INVALID_OUTPUT_PATH" });
  });

  it("rejects a symbolic link instead of following it as an output file", async () => {
    const root = await temporaryRoot();
    const targetPath = path.join(root, "target.txt");
    const outputPath = path.join(root, "linked.docx");
    await fs.writeFile(targetPath, "synthetic link target", "utf8");
    try {
      await fs.symlink(targetPath, outputPath, "file");
    } catch (error) {
      if (!(error instanceof Error && "code" in error && (error as NodeJS.ErrnoException).code === "EPERM")) throw error;
      const directoryTarget = path.join(root, "target-directory");
      await fs.mkdir(directoryTarget);
      await fs.symlink(directoryTarget, outputPath, "junction");
    }
    expect((await fs.lstat(outputPath)).isSymbolicLink()).toBe(true);

    await expect(
      serviceFor(outputPath, new HandleRegistry()).pickOutput(7, "suggested.docx"),
    ).rejects.toMatchObject({ code: "INVALID_OUTPUT_PATH" });
  });
});
