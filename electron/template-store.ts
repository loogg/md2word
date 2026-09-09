import { randomUUID } from "node:crypto";
import * as fs from "node:fs/promises";
import path from "node:path";
import type { StyleMode, TemplateProfile, TemplateValidationReport } from "./contracts";
import { AppError } from "./errors";

export interface TemplateValidator {
  validate(docxPath: string, cssPath: string): Promise<TemplateValidationReport>;
}

export type TemplateStoreFileSystem = Pick<
  typeof fs,
  "mkdir" | "readFile" | "writeFile" | "rename" | "rm" | "unlink" | "copyFile" | "access" | "stat" | "lstat" | "readdir"
>;

export interface ImportTemplateRecord {
  name: string;
  description?: string;
  templateSourcePath: string;
  css: { mode: "builtin" } | { mode: "custom"; sourcePath: string };
  mermaidDefaults: TemplateProfile["mermaidDefaults"];
  approvedWarningFingerprint?: string;
}

export interface UpdateTemplateRecord {
  id: string;
  name: string;
  description?: string;
  templateSourcePath?: string;
  css: { mode: "builtin" } | { mode: "custom"; sourcePath?: string };
  mermaidDefaults: TemplateProfile["mermaidDefaults"];
  approvedWarningFingerprint?: string;
}

export interface TemplateSnapshot {
  profile: TemplateProfile;
  docxPath: string;
  cssPath: string;
}

interface TemplateIndexProfile {
  id: string;
  name: string;
  description?: string;
  isDefault?: boolean;
  cssMode?: StyleMode;
  mermaidDefaults?: TemplateProfile["mermaidDefaults"];
  contentFingerprint?: string;
}

interface TemplateIndex {
  version: 2;
  profiles: TemplateIndexProfile[];
}

interface LegacyTemplateIndex {
  version: 1;
  profiles: TemplateProfile[];
}

const PROFILE_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/;
const TRANSACTION_MARKER = ".md2word-transaction.json";

export interface TemplateStoreOptions {
  templatesRoot: string;
  builtinCssPath: string;
  validator: TemplateValidator;
  fs?: TemplateStoreFileSystem;
  now?: () => Date;
  idFactory?: () => string;
}

function isNodeError(error: unknown, code: string): boolean {
  return error instanceof Error && "code" in error && (error as NodeJS.ErrnoException).code === code;
}

function cloneProfile(profile: TemplateProfile): TemplateProfile {
  return structuredClone(profile);
}

class TemplatePackageStore {
  readonly #root: string;
  readonly #builtinCssPath: string;
  readonly #validator: TemplateValidator;
  readonly #fs: TemplateStoreFileSystem;
  readonly #now: () => Date;
  readonly #idFactory: () => string;
  #profiles: TemplateProfile[] = [];
  #initialization?: Promise<void>;
  #mutationTail: Promise<void> = Promise.resolve();
  #accepting = true;

  constructor(options: TemplateStoreOptions) {
    if (!path.isAbsolute(options.templatesRoot) || !path.isAbsolute(options.builtinCssPath)) {
      throw new AppError("INTERNAL_ERROR", "模板存储路径必须是绝对路径。");
    }
    this.#root = path.resolve(options.templatesRoot);
    this.#builtinCssPath = path.resolve(options.builtinCssPath);
    this.#validator = options.validator;
    this.#fs = options.fs ?? fs;
    this.#now = options.now ?? (() => new Date());
    this.#idFactory = options.idFactory ?? (() => `template-${randomUUID()}`);
  }

  get rootPath(): string {
    return this.#root;
  }

  async initialize(): Promise<void> {
    this.#initialization ??= this.#initialize();
    return this.#initialization;
  }

  async list(): Promise<TemplateProfile[]> {
    await this.initialize();
    return this.#profiles.map(cloneProfile);
  }

  async shutdown(): Promise<void> {
    this.#accepting = false;
    await this.#mutationTail;
  }

  async add(input: ImportTemplateRecord, makeDefault = this.#profiles.length === 0): Promise<TemplateProfile> {
    return this.#serializeMutation(async () => {
      this.#assertUniqueName(input.name);
      const id = this.#safeNewId();
      const stagePath = this.#transientPath("staging", id);
      const finalPath = this.#profileDirectory(id);
      let committedDirectory = false;
      try {
        const staged = await this.#stageFiles(stagePath, id, input.templateSourcePath, input.css);
        const validation = await this.#validator.validate(staged.docxPath, staged.cssPath);
        this.#assertValidationCanSave(validation, input.approvedWarningFingerprint);
        const now = this.#now().toISOString();
        const profile: TemplateProfile = {
          id,
          name: input.name.trim(),
          description: input.description?.trim() ?? "",
          templateFileName: "template.docx",
          css: {
            mode: input.css.mode,
            fileName: "style.css",
          },
          isDefault: makeDefault && validation.status !== "invalid",
          mermaidDefaults: structuredClone(input.mermaidDefaults),
          validation,
          createdAt: now,
          updatedAt: now,
        };
        await this.#writeTransactionMarker(stagePath, "staging", id, profile);
        await this.#fs.rename(stagePath, finalPath);
        committedDirectory = true;
        const next = [...this.#profiles, profile];
        await this.#writeIndex(next);
        this.#profiles = next;
        await this.#safeUnlink(path.join(finalPath, TRANSACTION_MARKER));
        return cloneProfile(profile);
      } catch (error) {
        await this.#safeRemove(committedDirectory ? finalPath : stagePath);
        throw error;
      }
    });
  }

  async update(input: UpdateTemplateRecord): Promise<TemplateProfile> {
    return this.#serializeMutation(async () => {
      const index = this.#profiles.findIndex((profile) => profile.id === input.id);
      if (index < 0) throw new AppError("TEMPLATE_NOT_FOUND", "模板不存在。");
      this.#assertUniqueName(input.name, input.id);
      const previous = this.#profiles[index];
      const finalPath = this.#profileDirectory(previous.id);
      const stagePath = this.#transientPath("staging", previous.id);
      const backupPath = this.#transientPath("backup", previous.id);
      const templateSourcePath = input.templateSourcePath ?? path.join(finalPath, "template.docx");
      const cssSource = this.#resolveUpdateCssSource(previous.css.mode, finalPath, input.css);
      let previousMoved = false;
      let replacementMoved = false;

      try {
        const staged = await this.#stageFiles(stagePath, previous.id, templateSourcePath, cssSource);
        const validation = await this.#validator.validate(staged.docxPath, staged.cssPath);
        this.#assertValidationCanSave(validation, input.approvedWarningFingerprint);
        const updated: TemplateProfile = {
          ...cloneProfile(previous),
          name: input.name.trim(),
          description: input.description?.trim() ?? "",
          templateFileName: "template.docx",
          css: {
            mode: input.css.mode,
            fileName: "style.css",
          },
          mermaidDefaults: structuredClone(input.mermaidDefaults),
          validation,
          updatedAt: this.#now().toISOString(),
        };
        await this.#writeTransactionMarker(stagePath, "staging", previous.id, updated);
        await this.#writeTransactionMarker(finalPath, "backup", previous.id, previous);
        await this.#fs.rename(finalPath, backupPath);
        previousMoved = true;
        await this.#fs.rename(stagePath, finalPath);
        replacementMoved = true;
        const next = this.#profiles.map((profile) => (profile.id === input.id ? updated : profile));
        await this.#writeIndex(next);
        this.#profiles = next;
        if (await this.#safeRemove(backupPath)) await this.#safeUnlink(path.join(finalPath, TRANSACTION_MARKER));
        return cloneProfile(updated);
      } catch (error) {
        if (replacementMoved) await this.#safeRemove(finalPath);
        if (previousMoved) {
          await this.#safeUnlink(path.join(backupPath, TRANSACTION_MARKER));
          await this.#safeRename(backupPath, finalPath);
        } else {
          await this.#safeUnlink(path.join(finalPath, TRANSACTION_MARKER));
        }
        await this.#safeRemove(stagePath);
        throw error;
      }
    });
  }

  async remove(id: string, chooseReplacement = true): Promise<void> {
    await this.#serializeMutation(async () => {
      const existing = this.#profiles.find((profile) => profile.id === id);
      if (!existing) throw new AppError("TEMPLATE_NOT_FOUND", "模板不存在。");
      const finalPath = this.#profileDirectory(id);
      const trashPath = this.#transientPath("trash", id);
      let moved = false;
      try {
        await this.#writeTransactionMarker(finalPath, "trash", id, existing);
        await this.#fs.rename(finalPath, trashPath);
        moved = true;
        let next = this.#profiles.filter((profile) => profile.id !== id);
        if (chooseReplacement && existing.isDefault && next.length > 0) {
          const replacement = next.find((profile) => profile.validation.status !== "invalid");
          next = next.map((profile) => ({ ...profile, isDefault: profile.id === replacement?.id }));
        }
        await this.#writeIndex(next);
        this.#profiles = next;
        await this.#safeRemove(trashPath);
      } catch (error) {
        if (moved) {
          await this.#safeUnlink(path.join(trashPath, TRANSACTION_MARKER));
          await this.#safeRename(trashPath, finalPath);
        } else {
          await this.#safeUnlink(path.join(finalPath, TRANSACTION_MARKER));
        }
        throw error;
      }
    });
  }

  async setDefault(id: string): Promise<TemplateProfile[]> {
    return this.#serializeMutation(async () => {
      const selected = this.#profiles.find((profile) => profile.id === id);
      if (!selected) throw new AppError("TEMPLATE_NOT_FOUND", "模板不存在。");
      if (selected.validation.status === "invalid") {
        throw new AppError("TEMPLATE_INVALID", "校验失败的模板不能设为默认。");
      }
      const next = this.#profiles.map((profile) => ({ ...profile, isDefault: profile.id === id }));
      await this.#writeIndex(next);
      this.#profiles = next;
      return next.map(cloneProfile);
    });
  }

  async setDefaultState(id?: string): Promise<TemplateProfile[]> {
    return this.#serializeMutation(async () => {
      if (id !== undefined) {
        const selected = this.#profiles.find((profile) => profile.id === id);
        if (!selected) throw new AppError("TEMPLATE_NOT_FOUND", "模板不存在。");
        if (selected.validation.status === "invalid") {
          throw new AppError("TEMPLATE_INVALID", "校验失败的模板不能设为默认。");
        }
      }
      const next = this.#profiles.map((profile) => ({ ...profile, isDefault: profile.id === id }));
      await this.#writeIndex(next);
      this.#profiles = next;
      return next.map(cloneProfile);
    });
  }

  async validate(id: string): Promise<TemplateProfile> {
    return this.#serializeMutation(async () => {
      const index = this.#profiles.findIndex((profile) => profile.id === id);
      if (index < 0) throw new AppError("TEMPLATE_NOT_FOUND", "模板不存在。");
      const directory = this.#profileDirectory(id);
      const validation = await this.#validator.validate(
        path.join(directory, "template.docx"),
        path.join(directory, "style.css"),
      );
      const updated = { ...this.#profiles[index], validation, updatedAt: this.#now().toISOString() };
      const next = this.#profiles.map((profile) => (profile.id === id ? updated : profile));
      await this.#writeIndex(next);
      this.#profiles = next;
      return cloneProfile(updated);
    });
  }

  async getSnapshot(id: string): Promise<TemplateSnapshot> {
    return this.withSnapshot(id, async (snapshot) => snapshot);
  }

  /** Holds the template mutation lock until the caller has consumed/copied the DOCX+CSS pair. */
  async withSnapshot<T>(id: string, operation: (snapshot: TemplateSnapshot) => Promise<T>): Promise<T> {
    return this.#serializeMutation(async () => {
      const profile = this.#profiles.find((candidate) => candidate.id === id);
      if (!profile) throw new AppError("TEMPLATE_NOT_FOUND", "模板不存在。");
      if (profile.validation.status === "invalid") throw new AppError("TEMPLATE_INVALID", "模板校验未通过。");
      const directory = this.#profileDirectory(id);
      const docxPath = path.join(directory, "template.docx");
      const cssPath = path.join(directory, "style.css");
      await Promise.all([this.#fs.access(docxPath), this.#fs.access(cssPath)]);
      return operation({ profile: cloneProfile(profile), docxPath, cssPath });
    });
  }

  async #initialize(): Promise<void> {
    await this.#fs.mkdir(this.#root, { recursive: true });
    let indexExists = true;
    try {
      const raw = await this.#fs.readFile(path.join(this.#root, "index.json"), "utf8");
      const parsed = JSON.parse(raw) as Partial<TemplateIndex | LegacyTemplateIndex>;
      if (!Array.isArray(parsed.profiles)) throw new Error("Invalid index shape");
      if (parsed.version === 2) {
        this.#profiles = parsed.profiles.map((profile) => this.#profileFromIndex(this.#parseIndexProfile(profile)));
      } else if (parsed.version === 1) {
        this.#profiles = parsed.profiles.map((profile) => this.#profileFromIndex(this.#toIndexProfile(this.#parseProfile(profile))));
      } else {
        throw new Error("Unsupported index version");
      }
    } catch (error) {
      if (isNodeError(error, "ENOENT")) {
        this.#profiles = [];
        indexExists = false;
      } else {
        throw new AppError("TEMPLATE_INDEX_CORRUPT", "模板索引无法读取，请从诊断中恢复。", false, {
          cause: error,
        });
      }
    }
    const recovered = await this.#recoverInterruptedTransactions();
    const hydrated: TemplateProfile[] = [];
    for (const profile of this.#profiles) hydrated.push(await this.#hydrateProfile(profile));
    this.#profiles = hydrated;
    if (recovered || (!indexExists && this.#profiles.length > 0)) await this.#writeIndex(this.#profiles);
  }

  async #recoverInterruptedTransactions(): Promise<boolean> {
    const entries = await this.#fs.readdir(this.#root, { withFileTypes: true });
    const transients = new Map<string, { staging: string[]; backup: string[]; trash: string[] }>();
    const finalDirectories = new Map<string, string>();
    const transientPattern = /^\.(staging|backup|trash)-(.+)-[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

    for (const entry of entries) {
      if (!entry.isDirectory()) continue;
      const match = transientPattern.exec(entry.name);
      if (match && PROFILE_ID_PATTERN.test(match[2])) {
        const record = transients.get(match[2]) ?? { staging: [], backup: [], trash: [] };
        record[match[1] as "staging" | "backup" | "trash"].push(path.join(this.#root, entry.name));
        transients.set(match[2], record);
      } else if (PROFILE_ID_PATTERN.test(entry.name)) {
        finalDirectories.set(entry.name, path.join(this.#root, entry.name));
      }
    }

    let changed = false;
    const indexedIds = new Set(this.#profiles.map((profile) => profile.id));
    for (const [id, transient] of transients) {
      for (const kind of ["staging", "backup", "trash"] as const) {
        const recognized: string[] = [];
        for (const candidate of transient[kind]) {
          if (await this.#hasTransactionMarker(candidate, kind, id)) recognized.push(candidate);
        }
        transient[kind] = recognized;
      }
      if (transient.staging.length + transient.backup.length + transient.trash.length === 0) transients.delete(id);
    }
    for (const profile of this.#profiles) {
      const finalPath = this.#profileDirectory(profile.id);
      const transient = transients.get(profile.id) ?? { staging: [], backup: [], trash: [] };
      let finalExists = finalDirectories.has(profile.id);

      if (finalExists && transient.backup.length > 0) {
        const manifestMatches = await this.#manifestMatches(finalPath, profile);
        if (!manifestMatches) {
          await this.#safeRemove(finalPath);
          const rollbackPath = transient.backup.shift()!;
          await this.#safeUnlink(path.join(rollbackPath, TRANSACTION_MARKER));
          await this.#fs.rename(rollbackPath, finalPath);
          changed = true;
        }
      } else if (!finalExists) {
        const rollbackPath = transient.backup.shift() ?? transient.trash.shift();
        if (!rollbackPath) {
          throw new AppError("TEMPLATE_INDEX_CORRUPT", `模板 ${profile.name} 的受管文件缺失。`);
        }
        await this.#safeUnlink(path.join(rollbackPath, TRANSACTION_MARKER));
        await this.#fs.rename(rollbackPath, finalPath);
        finalExists = true;
        changed = true;
      }

      if (!finalExists) throw new AppError("TEMPLATE_INDEX_CORRUPT", `模板 ${profile.name} 无法恢复。`);
      await Promise.all([
        this.#fs.access(path.join(finalPath, "template.docx")),
        this.#fs.access(path.join(finalPath, "style.css")),
      ]);
      let cleanupComplete = true;
      for (const candidate of [...transient.staging, ...transient.backup, ...transient.trash]) {
        if (await this.#safeRemove(candidate)) changed = true;
        else cleanupComplete = false;
      }
      if (cleanupComplete) await this.#safeUnlink(path.join(finalPath, TRANSACTION_MARKER));
      await this.#safeUnlink(path.join(finalPath, "profile.json"));
      transients.delete(profile.id);
    }

    for (const [id, finalPath] of finalDirectories) {
      if (indexedIds.has(id)) continue;
      try {
        const marker = await this.#readTransactionMarker(finalPath);
        if (marker?.kind !== "staging" || marker.id !== id || !marker.profile) throw new Error("Missing managed staging marker");
        const manifest = marker.profile;
        if (manifest.id !== id) throw new Error("Profile id mismatch");
        await Promise.all([
          this.#fs.access(path.join(finalPath, "template.docx")),
          this.#fs.access(path.join(finalPath, "style.css")),
        ]);
        const recovered = { ...manifest, isDefault: this.#profiles.length === 0 };
        this.#profiles.push(recovered);
        await Promise.all([
          this.#safeUnlink(path.join(finalPath, TRANSACTION_MARKER)),
          this.#safeUnlink(path.join(finalPath, "profile.json")),
        ]);
        changed = true;
      } catch {
        // Unknown directories are user data until an intact managed manifest proves ownership.
        // Leave them untouched; only generated transient names are eligible for automatic cleanup.
      }
    }

    for (const transient of transients.values()) {
      for (const candidate of [...transient.staging, ...transient.backup, ...transient.trash]) {
        await this.#safeRemove(candidate);
        changed = true;
      }
    }
    return changed;
  }

  async #manifestMatches(directory: string, expected: TemplateProfile): Promise<boolean> {
    try {
      const marker = await this.#readTransactionMarker(directory);
      return marker?.profile !== undefined &&
        JSON.stringify(this.#toIndexProfile(marker.profile)) === JSON.stringify(this.#toIndexProfile(expected));
    } catch {
      return false;
    }
  }

  async #serializeMutation<T>(operation: () => Promise<T>): Promise<T> {
    await this.initialize();
    if (!this.#accepting) throw new AppError("APP_SHUTTING_DOWN", "应用正在退出，不能修改模板库。", true);
    const previous = this.#mutationTail;
    let release!: () => void;
    this.#mutationTail = new Promise<void>((resolve) => {
      release = resolve;
    });
    await previous;
    try {
      return await operation();
    } finally {
      release();
    }
  }

  async #stageFiles(
    stagePath: string,
    id: string,
    templateSourcePath: string,
    css: { mode: "builtin" } | { mode: "custom"; sourcePath: string },
  ): Promise<{ docxPath: string; cssPath: string }> {
    await this.#fs.mkdir(stagePath, { recursive: false });
    await this.#writeTransactionMarker(stagePath, "staging", id);
    const docxPath = path.join(stagePath, "template.docx");
    const cssPath = path.join(stagePath, "style.css");
    await this.#fs.copyFile(templateSourcePath, docxPath);
    await this.#fs.copyFile(css.mode === "builtin" ? this.#builtinCssPath : css.sourcePath, cssPath);
    return { docxPath, cssPath };
  }

  #resolveUpdateCssSource(
    previousMode: StyleMode,
    finalPath: string,
    css: UpdateTemplateRecord["css"],
  ): { mode: "builtin" } | { mode: "custom"; sourcePath: string } {
    if (css.mode === "builtin") return { mode: "builtin" };
    if (css.sourcePath) return { mode: "custom", sourcePath: css.sourcePath };
    if (previousMode !== "custom") {
      throw new AppError("CSS_FILE_REQUIRED", "切换到自定义 CSS 时必须重新选择文件。");
    }
    return { mode: "custom", sourcePath: path.join(finalPath, "style.css") };
  }

  #assertUniqueName(name: string, exceptId?: string): void {
    const normalized = name.trim().toLocaleLowerCase("zh-CN");
    if (!normalized || normalized.length > 100) throw new AppError("INVALID_INPUT", "模板名称无效。");
    if (this.#profiles.some((profile) => profile.id !== exceptId && profile.name.toLocaleLowerCase("zh-CN") === normalized)) {
      throw new AppError("DUPLICATE_TEMPLATE_NAME", "模板名称已存在。");
    }
  }

  #assertValidationCanSave(report: TemplateValidationReport, approvedWarningFingerprint?: string): void {
    if (report.status === "invalid" || report.issues.some((issue) => issue.severity === "error")) {
      throw new AppError("TEMPLATE_INVALID", report.summary || "模板校验未通过。");
    }
    if (report.status === "warning" && report.contentFingerprint !== approvedWarningFingerprint) {
      throw new AppError("TEMPLATE_WARNING_CONFIRMATION_REQUIRED", "模板有警告，请确认后再保存。", true);
    }
  }

  #parseProfile(value: unknown): TemplateProfile {
    if (!value || typeof value !== "object") throw new Error("Invalid profile");
    const profile = value as TemplateProfile;
    if (
      typeof profile.id !== "string" ||
      !PROFILE_ID_PATTERN.test(profile.id) ||
      typeof profile.name !== "string" ||
      profile.name.trim().length === 0 ||
      typeof profile.templateFileName !== "string" ||
      path.basename(profile.templateFileName) !== profile.templateFileName ||
      !profile.css ||
      (profile.css.mode !== "builtin" && profile.css.mode !== "custom") ||
      typeof profile.css.fileName !== "string" ||
      path.basename(profile.css.fileName) !== profile.css.fileName ||
      !profile.validation
    ) {
      throw new Error("Invalid profile");
    }
    return cloneProfile(profile);
  }

  #parseIndexProfile(value: unknown): TemplateIndexProfile {
    if (!value || typeof value !== "object") throw new Error("Invalid compact profile");
    const profile = value as Partial<TemplateIndexProfile>;
    if (
      typeof profile.id !== "string" ||
      !PROFILE_ID_PATTERN.test(profile.id) ||
      typeof profile.name !== "string" ||
      (profile.description !== undefined && typeof profile.description !== "string") ||
      (profile.isDefault !== undefined && typeof profile.isDefault !== "boolean") ||
      (profile.cssMode !== undefined && profile.cssMode !== "builtin" && profile.cssMode !== "custom") ||
      (profile.contentFingerprint !== undefined && typeof profile.contentFingerprint !== "string") ||
      (profile.mermaidDefaults !== undefined &&
        (profile.mermaidDefaults.mode !== "auto" && profile.mermaidDefaults.mode !== "off" && profile.mermaidDefaults.mode !== "required" ||
          profile.mermaidDefaults.format !== "png" && profile.mermaidDefaults.format !== "svg"))
    ) {
      throw new Error("Invalid compact profile");
    }
    return {
      id: profile.id,
      name: profile.name.trim(),
      description: profile.description?.trim() ?? "",
      isDefault: profile.isDefault === true,
      cssMode: profile.cssMode ?? "custom",
      mermaidDefaults: structuredClone(profile.mermaidDefaults ?? { mode: "auto", format: "png" }),
      contentFingerprint: profile.contentFingerprint ?? "",
    };
  }

  #profileFromIndex(profile: TemplateIndexProfile): TemplateProfile {
    return {
      id: profile.id,
      name: profile.name,
      description: profile.description ?? "",
      templateFileName: "template.docx",
      css: { mode: profile.cssMode ?? "custom", fileName: "style.css" },
      isDefault: profile.isDefault === true,
      mermaidDefaults: structuredClone(profile.mermaidDefaults ?? { mode: "auto", format: "png" }),
      validation: {
        status: "warning",
        checkedAt: "",
        contentFingerprint: profile.contentFingerprint ?? "",
        summary: "模板等待重新校验。",
        issues: [],
        capabilities: {
          bodyRange: false,
          coverTitle: false,
          coverSubtitle: false,
          versionTables: [],
          codeBlockStyle: false,
        },
        styleMappings: [],
      },
      createdAt: "",
      updatedAt: "",
    };
  }

  #parseManifestProfile(value: unknown): TemplateProfile {
    if (value && typeof value === "object" && "validation" in value) return this.#parseProfile(value);
    return this.#profileFromIndex(this.#parseIndexProfile(value));
  }

  async #hydrateProfile(profile: TemplateProfile): Promise<TemplateProfile> {
    const directory = this.#profileDirectory(profile.id);
    const docxPath = path.join(directory, "template.docx");
    const cssPath = path.join(directory, "style.css");
    const [validation, docxStat, cssStat] = await Promise.all([
      this.#validator.validate(docxPath, cssPath),
      this.#fs.stat(docxPath),
      this.#fs.stat(cssPath),
    ]);
    const createdMs = Math.min(docxStat.birthtimeMs || docxStat.ctimeMs, cssStat.birthtimeMs || cssStat.ctimeMs);
    const updatedMs = Math.max(docxStat.mtimeMs, cssStat.mtimeMs);
    return {
      ...profile,
      templateFileName: "template.docx",
      css: { mode: profile.css.mode, fileName: "style.css" },
      validation,
      createdAt: new Date(createdMs).toISOString(),
      updatedAt: new Date(updatedMs).toISOString(),
    };
  }

  #toIndexProfile(profile: TemplateProfile): TemplateIndexProfile {
    return {
      id: profile.id,
      name: profile.name,
      description: profile.description,
      isDefault: profile.isDefault,
      cssMode: profile.css.mode,
      mermaidDefaults: structuredClone(profile.mermaidDefaults),
      contentFingerprint: profile.validation.contentFingerprint,
    };
  }

  #safeNewId(): string {
    const id = this.#idFactory();
    if (!PROFILE_ID_PATTERN.test(id) || this.#profiles.some((profile) => profile.id === id)) {
      throw new AppError("INTERNAL_ERROR", "无法创建模板标识。");
    }
    return id;
  }

  #profileDirectory(id: string): string {
    if (!PROFILE_ID_PATTERN.test(id)) throw new AppError("TEMPLATE_INDEX_CORRUPT", "模板索引包含无效标识。");
    const directory = path.resolve(this.#root, id);
    const rootPrefix = this.#root.endsWith(path.sep) ? this.#root : this.#root + path.sep;
    if (!directory.startsWith(rootPrefix)) throw new AppError("TEMPLATE_INDEX_CORRUPT", "模板目录超出应用管理范围。");
    return directory;
  }

  #transientPath(kind: "staging" | "backup" | "trash", id: string): string {
    return path.join(this.#root, `.${kind}-${id}-${randomUUID()}`);
  }

  async #writeIndex(profiles: TemplateProfile[]): Promise<void> {
    await this.#writeJsonAtomic(path.join(this.#root, "index.json"), {
      version: 2,
      profiles: profiles.map((profile) => this.#toIndexProfile(profile)),
    } satisfies TemplateIndex);
  }

  async #writeJsonAtomic(target: string, value: unknown): Promise<void> {
    const temporary = `${target}.${randomUUID()}.tmp`;
    try {
      await this.#writeJson(temporary, value);
      await this.#fs.rename(temporary, target);
    } catch (error) {
      await this.#safeRemove(temporary);
      throw error;
    }
  }

  async #writeJson(target: string, value: unknown): Promise<void> {
    await this.#fs.writeFile(target, `${JSON.stringify(value, null, 2)}\n`, { encoding: "utf8", mode: 0o600 });
  }

  async #writeTransactionMarker(
    directory: string,
    kind: "staging" | "backup" | "trash",
    id: string,
    profile?: TemplateProfile,
  ): Promise<void> {
    await this.#writeJson(path.join(directory, TRANSACTION_MARKER), {
      version: 1,
      kind,
      id,
      ...(profile ? { profile: this.#toIndexProfile(profile) } : {}),
    });
  }

  async #hasTransactionMarker(directory: string, kind: "staging" | "backup" | "trash", id: string): Promise<boolean> {
    const marker = await this.#readTransactionMarker(directory);
    return marker?.kind === kind && marker.id === id;
  }

  async #readTransactionMarker(directory: string): Promise<{
    kind: "staging" | "backup" | "trash";
    id: string;
    profile?: TemplateProfile;
  } | undefined> {
    try {
      const value = JSON.parse(await this.#fs.readFile(path.join(directory, TRANSACTION_MARKER), "utf8")) as Record<string, unknown>;
      if (
        value.version !== 1 ||
        (value.kind !== "staging" && value.kind !== "backup" && value.kind !== "trash") ||
        typeof value.id !== "string" ||
        !PROFILE_ID_PATTERN.test(value.id)
      ) {
        return undefined;
      }
      return {
        kind: value.kind,
        id: value.id,
        profile: value.profile === undefined ? undefined : this.#parseManifestProfile(value.profile),
      };
    } catch {
      return undefined;
    }
  }

  async #safeRemove(target: string): Promise<boolean> {
    try {
      await this.#fs.rm(target, { recursive: true, force: true });
      return true;
    } catch {
      // Cleanup failure is surfaced through diagnostics by the composition root; it must not mask the primary error.
      return false;
    }
  }

  async #safeRename(source: string, destination: string): Promise<void> {
    try {
      await this.#fs.rename(source, destination);
    } catch {
      // A failed rollback leaves a recoverable backup directory for startup diagnostics.
    }
  }

  async #safeUnlink(target: string): Promise<void> {
    try {
      await this.#fs.unlink(target);
    } catch {
      // Reserved transaction markers are best-effort cleanup only.
    }
  }
}

const USER_PACKAGE_DIRECTORY = "user";

/**
 * A template catalog is a container of self-contained packages. Each immediate
 * child package owns its own index.json and profile directories. The root-level
 * index remains readable only for backward compatibility with pre-0.4 layouts;
 * newly imported templates are always written to templates/user.
 */
export class TemplateStore {
  readonly #root: string;
  readonly #builtinCssPath: string;
  readonly #validator: TemplateValidator;
  readonly #fs: TemplateStoreFileSystem;
  readonly #now: () => Date;
  readonly #idFactory: () => string;
  #packages: TemplatePackageStore[] = [];
  #owners = new Map<string, TemplatePackageStore>();
  #profiles: TemplateProfile[] = [];
  #initialization?: Promise<void>;
  #mutationTail: Promise<void> = Promise.resolve();
  #accepting = true;

  constructor(options: TemplateStoreOptions) {
    if (!path.isAbsolute(options.templatesRoot) || !path.isAbsolute(options.builtinCssPath)) {
      throw new AppError("INTERNAL_ERROR", "模板存储路径必须是绝对路径。");
    }
    this.#root = path.resolve(options.templatesRoot);
    this.#builtinCssPath = path.resolve(options.builtinCssPath);
    this.#validator = options.validator;
    this.#fs = options.fs ?? fs;
    this.#now = options.now ?? (() => new Date());
    this.#idFactory = options.idFactory ?? (() => `template-${randomUUID()}`);
  }

  get rootPath(): string {
    return this.#root;
  }

  async initialize(): Promise<void> {
    this.#initialization ??= this.#initialize();
    return this.#initialization;
  }

  async list(): Promise<TemplateProfile[]> {
    await this.initialize();
    return this.#profiles.map(cloneProfile);
  }

  async shutdown(): Promise<void> {
    this.#accepting = false;
    await this.#mutationTail;
    await Promise.all(this.#packages.map((templatePackage) => templatePackage.shutdown()));
  }

  async add(input: ImportTemplateRecord): Promise<TemplateProfile> {
    return this.#serializeMutation(async () => {
      this.#assertUniqueName(input.name);
      const target = await this.#ensureUserPackage();
      const added = await target.add(input, this.#profiles.length === 0);
      await this.#refreshCatalog();
      return cloneProfile(added);
    });
  }

  async update(input: UpdateTemplateRecord): Promise<TemplateProfile> {
    return this.#serializeMutation(async () => {
      this.#assertUniqueName(input.name, input.id);
      const owner = this.#requireOwner(input.id);
      const updated = await owner.update(input);
      await this.#refreshCatalog();
      return cloneProfile(updated);
    });
  }

  async remove(id: string): Promise<void> {
    await this.#serializeMutation(async () => {
      const owner = this.#requireOwner(id);
      const existing = this.#profiles.find((profile) => profile.id === id)!;
      await owner.remove(id, false);
      await this.#refreshCatalog();
      if (existing.isDefault && this.#profiles.length > 0) {
        const replacement = this.#profiles.find((profile) => profile.validation.status !== "invalid");
        await this.#applyDefault(replacement?.id);
      }
    });
  }

  async setDefault(id: string): Promise<TemplateProfile[]> {
    return this.#serializeMutation(async () => {
      const selected = this.#profiles.find((profile) => profile.id === id);
      if (!selected) throw new AppError("TEMPLATE_NOT_FOUND", "模板不存在。");
      if (selected.validation.status === "invalid") {
        throw new AppError("TEMPLATE_INVALID", "校验失败的模板不能设为默认。");
      }
      await this.#applyDefault(id);
      return this.#profiles.map(cloneProfile);
    });
  }

  async validate(id: string): Promise<TemplateProfile> {
    return this.#serializeMutation(async () => {
      const updated = await this.#requireOwner(id).validate(id);
      await this.#refreshCatalog();
      return cloneProfile(updated);
    });
  }

  async getSnapshot(id: string): Promise<TemplateSnapshot> {
    return this.withSnapshot(id, async (snapshot) => snapshot);
  }

  async withSnapshot<T>(id: string, operation: (snapshot: TemplateSnapshot) => Promise<T>): Promise<T> {
    await this.initialize();
    return this.#requireOwner(id).withSnapshot(id, operation);
  }

  async #initialize(): Promise<void> {
    await this.#fs.mkdir(this.#root, { recursive: true });
    const packageRoots: string[] = [];
    if (await this.#hasRegularIndex(this.#root)) packageRoots.push(this.#root);

    const entries = await this.#fs.readdir(this.#root, { withFileTypes: true });
    for (const entry of entries.sort((left, right) => left.name.localeCompare(right.name, "en"))) {
      if (!entry.isDirectory() || entry.name.startsWith(".")) continue;
      const candidate = path.join(this.#root, entry.name);
      if (await this.#hasRegularIndex(candidate)) packageRoots.push(candidate);
    }

    for (const packageRoot of packageRoots) {
      const templatePackage = this.#createPackage(packageRoot);
      await templatePackage.initialize();
      this.#packages.push(templatePackage);
    }
    await this.#refreshCatalog();
  }

  async #hasRegularIndex(packageRoot: string): Promise<boolean> {
    const indexPath = path.join(packageRoot, "index.json");
    try {
      const stat = await this.#fs.lstat(indexPath);
      if (!stat.isFile() || stat.isSymbolicLink()) {
        throw new AppError("TEMPLATE_INDEX_CORRUPT", `模板包索引不是普通文件：${indexPath}`);
      }
      return true;
    } catch (error) {
      if (isNodeError(error, "ENOENT")) return false;
      throw error;
    }
  }

  #createPackage(packageRoot: string): TemplatePackageStore {
    return new TemplatePackageStore({
      templatesRoot: packageRoot,
      builtinCssPath: this.#builtinCssPath,
      validator: this.#validator,
      fs: this.#fs,
      now: this.#now,
      idFactory: () => {
        const id = this.#idFactory();
        if (this.#owners.has(id)) throw new AppError("INTERNAL_ERROR", "无法创建模板标识。");
        return id;
      },
    });
  }

  async #ensureUserPackage(): Promise<TemplatePackageStore> {
    const userRoot = path.join(this.#root, USER_PACKAGE_DIRECTORY);
    const existing = this.#packages.find((templatePackage) => templatePackage.rootPath === userRoot);
    if (existing) return existing;
    const templatePackage = this.#createPackage(userRoot);
    await templatePackage.initialize();
    this.#packages.push(templatePackage);
    return templatePackage;
  }

  async #refreshCatalog(): Promise<void> {
    const owners = new Map<string, TemplatePackageStore>();
    const profiles: TemplateProfile[] = [];
    for (const templatePackage of this.#packages) {
      for (const profile of await templatePackage.list()) {
        if (owners.has(profile.id)) {
          throw new AppError("TEMPLATE_INDEX_CORRUPT", `多个模板包使用了相同模板 ID：${profile.id}`);
        }
        owners.set(profile.id, templatePackage);
        profiles.push(profile);
      }
    }
    const defaults = profiles.filter((profile) => profile.isDefault);
    if (defaults.length > 1) {
      throw new AppError("TEMPLATE_INDEX_CORRUPT", "多个模板包同时声明了默认模板，请只保留一个默认模板。");
    }
    this.#owners = owners;
    this.#profiles = profiles;
  }

  async #applyDefault(id?: string): Promise<void> {
    const selectedOwner = id === undefined ? undefined : this.#requireOwner(id);
    for (const templatePackage of this.#packages) {
      await templatePackage.setDefaultState(templatePackage === selectedOwner ? id : undefined);
    }
    await this.#refreshCatalog();
  }

  #requireOwner(id: string): TemplatePackageStore {
    const owner = this.#owners.get(id);
    if (!owner) throw new AppError("TEMPLATE_NOT_FOUND", "模板不存在。");
    return owner;
  }

  #assertUniqueName(name: string, exceptId?: string): void {
    const normalized = name.trim().toLocaleLowerCase("zh-CN");
    if (!normalized || normalized.length > 100) throw new AppError("INVALID_INPUT", "模板名称无效。");
    if (this.#profiles.some((profile) => profile.id !== exceptId && profile.name.toLocaleLowerCase("zh-CN") === normalized)) {
      throw new AppError("DUPLICATE_TEMPLATE_NAME", "模板名称已存在。");
    }
  }

  async #serializeMutation<T>(operation: () => Promise<T>): Promise<T> {
    await this.initialize();
    if (!this.#accepting) throw new AppError("APP_SHUTTING_DOWN", "应用正在退出，不能修改模板库。", true);
    const previous = this.#mutationTail;
    let release!: () => void;
    this.#mutationTail = new Promise<void>((resolve) => { release = resolve; });
    await previous;
    try {
      return await operation();
    } finally {
      release();
    }
  }
}
