import * as fs from "node:fs/promises";
import path from "node:path";
import { randomUUID } from "node:crypto";
import { AppError } from "./errors";

export const INSTALLATION_MARKER = "md2word-installed";

async function statIfExists(filePath: string) {
  try {
    return await fs.lstat(filePath);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }
}

export async function isSetupInstallation(resourcesRoot: string): Promise<boolean> {
  const marker = await statIfExists(path.join(resourcesRoot, INSTALLATION_MARKER));
  if (!marker) return false;
  if (!marker.isFile() || marker.isSymbolicLink()) {
    throw new AppError("INTERNAL_ERROR", "安装模式标记无效，请重新安装应用。");
  }
  if ((await fs.readFile(path.join(resourcesRoot, INSTALLATION_MARKER), "utf8")) !== "setup") {
    throw new AppError("INTERNAL_ERROR", "安装模式标记无效，请重新安装应用。");
  }
  return true;
}

/** Copy reviewed bundled packages once; never replace a user's edited/empty index. */
export async function seedInstalledTemplatePackages(sourceRoot: string, targetRoot: string): Promise<void> {
  if (!path.isAbsolute(sourceRoot) || !path.isAbsolute(targetRoot) || path.resolve(sourceRoot) === path.resolve(targetRoot)) {
    throw new AppError("INTERNAL_ERROR", "安装版模板初始化路径无效。");
  }
  const sourceStat = await fs.lstat(sourceRoot);
  if (!sourceStat.isDirectory() || sourceStat.isSymbolicLink()) {
    throw new AppError("TEMPLATE_INDEX_CORRUPT", "安装包内的模板目录无效。");
  }
  await fs.mkdir(targetRoot, { recursive: true });
  const targetStat = await fs.lstat(targetRoot);
  if (!targetStat.isDirectory() || targetStat.isSymbolicLink()) {
    throw new AppError("TEMPLATE_INDEX_CORRUPT", "用户模板目录无效。");
  }
  for (const entry of await fs.readdir(sourceRoot, { withFileTypes: true })) {
    if (entry.name.startsWith(".")) continue;
    if (entry.isSymbolicLink()) throw new AppError("TEMPLATE_INDEX_CORRUPT", "安装包不能包含模板目录链接。");
    if (!entry.isDirectory()) continue;
    const packageSource = path.join(sourceRoot, entry.name);
    const indexStat = await statIfExists(path.join(packageSource, "index.json"));
    if (!indexStat) continue;
    if (!indexStat.isFile() || indexStat.isSymbolicLink()) {
      throw new AppError("TEMPLATE_INDEX_CORRUPT", "安装包内的模板索引无效。");
    }
    const packageTarget = path.join(targetRoot, entry.name);
    const existing = await statIfExists(packageTarget);
    if (existing) {
      if (!existing.isDirectory() || existing.isSymbolicLink()) {
        throw new AppError("TEMPLATE_INDEX_CORRUPT", "已有模板包目录无效。");
      }
      continue;
    }
    const staging = path.join(targetRoot, `.seed-${randomUUID()}`);
    try {
      await fs.cp(packageSource, staging, {
        recursive: true,
        filter: async (source) => {
          const stat = await fs.lstat(source);
          if (!stat.isFile() && !stat.isDirectory()) {
            throw new AppError("TEMPLATE_INDEX_CORRUPT", "安装包内的模板不能包含链接或特殊文件。");
          }
          return true;
        },
      });
      await fs.rename(staging, packageTarget);
    } finally {
      await fs.rm(staging, { recursive: true, force: true });
    }
  }
}
