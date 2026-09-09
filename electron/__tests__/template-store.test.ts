import * as fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { randomUUID } from "node:crypto";
import { afterEach, describe, expect, it } from "vitest";
import type { TemplateValidationReport } from "../contracts";
import { TemplateStore, type TemplateStoreFileSystem } from "../template-store";

const roots: string[] = [];

afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => fs.rm(root, { recursive: true, force: true })));
});

function report(status: "valid" | "warning" | "invalid" = "valid"): TemplateValidationReport {
  return {
    status,
    checkedAt: "2026-07-16T00:00:00.000Z",
    contentFingerprint: "sha256:synthetic",
    summary: status === "invalid" ? "合成模板无效" : "合成模板可用",
    issues:
      status === "invalid"
        ? [{ code: "BODY_BOOKMARK_MISSING", severity: "error", target: "bookmark", message: "缺少合成书签" }]
        : [],
    capabilities: {
      bodyRange: status !== "invalid",
      coverTitle: false,
      coverSubtitle: false,
      versionTables: [],
      codeBlockStyle: true,
    },
    styleMappings: [
      {
        role: "unordered-list",
        cssSelector: "ul > li",
        requestedStyleName: "Synthetic Bullet",
        resolvedStyleId: "SyntheticBullet",
        resolvedStyleName: "Synthetic Bullet",
        status: "resolved",
      },
      {
        role: "ordered-list",
        cssSelector: "ol > li",
        requestedStyleName: "Synthetic Number",
        resolvedStyleId: "SyntheticNumber",
        resolvedStyleName: "Synthetic Number",
        status: "resolved",
      },
    ],
  };
}

async function fixture() {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "md2word-electron-test-"));
  roots.push(root);
  const templatesRoot = path.join(root, "templates");
  const sourceDocx = path.join(root, "synthetic-template.docx");
  const sourceCss = path.join(root, "synthetic-style.css");
  const builtinCss = path.join(root, "builtin.css");
  await Promise.all([
    fs.writeFile(sourceDocx, "synthetic-openxml-placeholder"),
    fs.writeFile(sourceCss, "ol > li { mso-style-name: 'Synthetic Number'; }"),
    fs.writeFile(builtinCss, "ul > li { mso-style-name: 'Synthetic Bullet'; }"),
  ]);
  return { root, templatesRoot, sourceDocx, sourceCss, builtinCss };
}

async function writeTemplatePackage(
  templatesRoot: string,
  packageName: string,
  id: string,
  name: string,
  isDefault = false,
) {
  const packageRoot = path.join(templatesRoot, packageName);
  const profileRoot = path.join(packageRoot, id);
  const profile = {
    id,
    name,
    description: "合成模板包",
    isDefault,
    cssMode: "custom" as const,
    mermaidDefaults: { mode: "off" as const, format: "png" as const },
  };
  await fs.mkdir(profileRoot, { recursive: true });
  await Promise.all([
    fs.writeFile(path.join(packageRoot, "index.json"), JSON.stringify({ version: 2, profiles: [profile] })),
    fs.writeFile(path.join(profileRoot, "template.docx"), "synthetic-openxml-placeholder"),
    fs.writeFile(path.join(profileRoot, "style.css"), "/* synthetic */"),
  ]);
}

describe("TemplateStore", () => {
  it("discovers independent reference and manually copied private packages", async () => {
    const files = await fixture();
    await writeTemplatePackage(files.templatesRoot, "reference", "public-reference", "公开参考模板");
    await writeTemplatePackage(files.templatesRoot, "private-bundle", "private-report", "内部技术报告", true);
    const store = new TemplateStore({
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => report() },
    });

    const profiles = await store.list();
    expect(profiles.map((profile) => profile.id).sort()).toEqual(["private-report", "public-reference"]);
    expect(profiles.find((profile) => profile.isDefault)?.id).toBe("private-report");
    await expect(store.getSnapshot("public-reference")).resolves.toMatchObject({
      docxPath: path.join(files.templatesRoot, "reference", "public-reference", "template.docx"),
    });
    const changed = await store.setDefault("public-reference");
    expect(changed.filter((profile) => profile.isDefault).map((profile) => profile.id)).toEqual(["public-reference"]);
    const privateIndex = JSON.parse(await fs.readFile(path.join(files.templatesRoot, "private-bundle", "index.json"), "utf8"));
    const referenceIndex = JSON.parse(await fs.readFile(path.join(files.templatesRoot, "reference", "index.json"), "utf8"));
    expect(privateIndex.profiles[0].isDefault).toBe(false);
    expect(referenceIndex.profiles[0].isDefault).toBe(true);
  });

  it("reads a legacy version 1 package and revalidates its DOCX/CSS pair", async () => {
    const files = await fixture();
    const packageRoot = path.join(files.templatesRoot, "legacy");
    const profileRoot = path.join(packageRoot, "legacy-template");
    const legacyProfile = {
      id: "legacy-template",
      name: "旧版模板",
      description: "只读兼容",
      templateFileName: "old-name.docx",
      css: { mode: "custom" as const, fileName: "old-name.css" },
      isDefault: false,
      mermaidDefaults: { mode: "off" as const, format: "png" as const },
      validation: { ...report(), contentFingerprint: "sha256:legacy-cache" },
      createdAt: "2026-07-19T00:00:00.000Z",
      updatedAt: "2026-07-19T00:00:00.000Z",
    };
    await fs.mkdir(profileRoot, { recursive: true });
    await Promise.all([
      fs.writeFile(path.join(packageRoot, "index.json"), JSON.stringify({ version: 1, profiles: [legacyProfile] })),
      fs.writeFile(path.join(profileRoot, "template.docx"), "synthetic-openxml-placeholder"),
      fs.writeFile(path.join(profileRoot, "style.css"), "/* synthetic */"),
      fs.writeFile(path.join(profileRoot, "profile.json"), JSON.stringify(legacyProfile)),
    ]);
    let validations = 0;
    const store = new TemplateStore({
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => { validations += 1; return report(); } },
    });

    await expect(store.list()).resolves.toMatchObject([{
      id: "legacy-template",
      templateFileName: "template.docx",
      css: { mode: "custom", fileName: "style.css" },
      validation: { contentFingerprint: "sha256:synthetic" },
    }]);
    expect(validations).toBe(1);
  });

  it("rejects duplicate ids across template packages", async () => {
    const files = await fixture();
    await writeTemplatePackage(files.templatesRoot, "reference", "same-id", "公开参考模板");
    await writeTemplatePackage(files.templatesRoot, "private-bundle", "same-id", "内部技术报告");
    const store = new TemplateStore({
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => report() },
    });

    await expect(store.list()).rejects.toMatchObject({ code: "TEMPLATE_INDEX_CORRUPT" });
  });

  it("rejects multiple defaults across template packages", async () => {
    const files = await fixture();
    await writeTemplatePackage(files.templatesRoot, "reference", "public-reference", "公开参考模板", true);
    await writeTemplatePackage(files.templatesRoot, "private-bundle", "private-report", "内部技术报告", true);
    const store = new TemplateStore({
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => report() },
    });

    await expect(store.list()).rejects.toMatchObject({ code: "TEMPLATE_INDEX_CORRUPT" });
  });

  it("never deletes an unknown user-created directory while recovering managed transactions", async () => {
    const files = await fixture();
    const notes = path.join(files.templatesRoot, "notes");
    await fs.mkdir(notes, { recursive: true });
    await fs.writeFile(path.join(notes, "keep.txt"), "user-owned");
    const store = new TemplateStore({
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => report() },
    });

    await expect(store.list()).resolves.toEqual([]);
    await expect(fs.readFile(path.join(notes, "keep.txt"), "utf8")).resolves.toBe("user-owned");
  });

  it("preserves a transient-looking directory that has no managed transaction marker", async () => {
    const files = await fixture();
    const lookalike = path.join(files.templatesRoot, `.backup-notes-${randomUUID()}`);
    await fs.mkdir(lookalike, { recursive: true });
    await fs.writeFile(path.join(lookalike, "keep.txt"), "not-a-template-transaction");
    const store = new TemplateStore({
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => report() },
    });

    await expect(store.list()).resolves.toEqual([]);
    await expect(fs.readFile(path.join(lookalike, "keep.txt"), "utf8")).resolves.toBe("not-a-template-transaction");
  });

  it("rejects a persisted profile id that could escape the managed template root", async () => {
    const files = await fixture();
    await fs.mkdir(files.templatesRoot, { recursive: true });
    await fs.writeFile(path.join(files.templatesRoot, "index.json"), JSON.stringify({
      version: 2,
      profiles: [{
        id: "../outside",
        name: "tampered",
      }],
    }));
    const store = new TemplateStore({
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => report() },
    });

    await expect(store.list()).rejects.toMatchObject({ code: "TEMPLATE_INDEX_CORRUPT" });
  });

  it("copies DOCX and CSS as one validated profile and exposes no path", async () => {
    const files = await fixture();
    const store = new TemplateStore({
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => report() },
      idFactory: () => "template-synthetic",
      now: () => new Date("2026-07-16T00:00:00.000Z"),
    });

    const profile = await store.add({
      name: "合成模板",
      description: "无业务内容",
      templateSourcePath: files.sourceDocx,
      css: { mode: "custom", sourcePath: files.sourceCss },
      mermaidDefaults: { mode: "auto", format: "png" },
    });

    expect(profile).not.toHaveProperty("docxPath");
    expect(profile.validation.styleMappings.map((mapping) => mapping.role)).toEqual([
      "unordered-list",
      "ordered-list",
    ]);
    await expect(fs.readFile(path.join(files.templatesRoot, "user", profile.id, "template.docx"), "utf8")).resolves.toBe(
      "synthetic-openxml-placeholder",
    );
    await expect(fs.readFile(path.join(files.templatesRoot, "user", profile.id, "style.css"), "utf8")).resolves.toContain(
      "Synthetic Number",
    );
    const index = JSON.parse(await fs.readFile(path.join(files.templatesRoot, "user", "index.json"), "utf8"));
    expect(index).toMatchObject({
      version: 2,
      profiles: [{ id: profile.id, name: "合成模板", cssMode: "custom" }],
    });
    expect(index.profiles[0]).not.toHaveProperty("validation");
    await expect(fs.access(path.join(files.templatesRoot, "user", profile.id, "profile.json"))).rejects.toMatchObject({ code: "ENOENT" });
  });

  it("rolls back the copied pair when the atomic index commit fails", async () => {
    const files = await fixture();
    const injectedFs: TemplateStoreFileSystem = {
      ...fs,
      rename: async (source, destination) => {
        if (path.basename(String(destination)) === "index.json") {
          const error = new Error("synthetic index failure") as NodeJS.ErrnoException;
          error.code = "EACCES";
          throw error;
        }
        return fs.rename(source, destination);
      },
    };
    const store = new TemplateStore({
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => report() },
      idFactory: () => "template-rollback",
      fs: injectedFs,
    });

    await expect(
      store.add({
        name: "回滚模板",
        templateSourcePath: files.sourceDocx,
        css: { mode: "builtin" },
        mermaidDefaults: { mode: "off", format: "png" },
      }),
    ).rejects.toThrow(/synthetic index failure/);
    await expect(fs.access(path.join(files.templatesRoot, "user", "template-rollback"))).rejects.toMatchObject({ code: "ENOENT" });
  });

  it("restores a managed backup left by an interrupted update", async () => {
    const files = await fixture();
    const options = {
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => report() },
      idFactory: () => "template-recovery",
    };
    const store = new TemplateStore(options);
    const profile = await store.add({
      name: "可恢复模板",
      templateSourcePath: files.sourceDocx,
      css: { mode: "custom", sourcePath: files.sourceCss },
      mermaidDefaults: { mode: "off" as const, format: "png" as const },
    });
    const userPackageRoot = path.join(files.templatesRoot, "user");
    const finalPath = path.join(userPackageRoot, profile.id);
    const backupPath = path.join(userPackageRoot, `.backup-${profile.id}-${randomUUID()}`);
    await fs.writeFile(path.join(finalPath, ".md2word-transaction.json"), JSON.stringify({
      version: 1,
      kind: "backup",
      id: profile.id,
    }));
    await fs.rename(finalPath, backupPath);

    const recovered = new TemplateStore(options);
    await expect(recovered.list()).resolves.toHaveLength(1);
    await expect(fs.readFile(path.join(finalPath, "template.docx"), "utf8")).resolves.toBe("synthetic-openxml-placeholder");
    await expect(fs.access(backupPath)).rejects.toMatchObject({ code: "ENOENT" });
  });

  it("keeps the indexed replacement when only backup cleanup was interrupted", async () => {
    const files = await fixture();
    const options = {
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => report() },
      idFactory: () => "template-committed-recovery",
    };
    const store = new TemplateStore(options);
    const profile = await store.add({
      name: "已提交模板",
      templateSourcePath: files.sourceDocx,
      css: { mode: "custom", sourcePath: files.sourceCss },
      mermaidDefaults: { mode: "off" as const, format: "png" as const },
    });
    const userPackageRoot = path.join(files.templatesRoot, "user");
    const finalPath = path.join(userPackageRoot, profile.id);
    const backupPath = path.join(userPackageRoot, `.backup-${profile.id}-${randomUUID()}`);
    const index = JSON.parse(await fs.readFile(path.join(userPackageRoot, "index.json"), "utf8"));
    await fs.cp(finalPath, backupPath, { recursive: true });
    await Promise.all([
      fs.writeFile(path.join(finalPath, "template.docx"), "committed-replacement"),
      fs.writeFile(path.join(finalPath, ".md2word-transaction.json"), JSON.stringify({
        version: 1,
        kind: "staging",
        id: profile.id,
        profile: index.profiles[0],
      })),
      fs.writeFile(path.join(backupPath, ".md2word-transaction.json"), JSON.stringify({
        version: 1,
        kind: "backup",
        id: profile.id,
        profile: index.profiles[0],
      })),
    ]);

    const recovered = new TemplateStore(options);
    await expect(recovered.list()).resolves.toHaveLength(1);
    await expect(fs.readFile(path.join(finalPath, "template.docx"), "utf8")).resolves.toBe("committed-replacement");
    await expect(fs.access(backupPath)).rejects.toMatchObject({ code: "ENOENT" });
    await expect(fs.access(path.join(finalPath, ".md2word-transaction.json"))).rejects.toMatchObject({ code: "ENOENT" });
  });

  it("holds the pair lock until a snapshot consumer finishes", async () => {
    const files = await fixture();
    const store = new TemplateStore({
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => report() },
      idFactory: () => "template-lock",
    });
    const profile = await store.add({
      name: "锁定模板",
      templateSourcePath: files.sourceDocx,
      css: { mode: "custom", sourcePath: files.sourceCss },
      mermaidDefaults: { mode: "off", format: "png" },
    });
    let releaseSnapshot!: () => void;
    let snapshotEntered!: () => void;
    const entered = new Promise<void>((resolve) => { snapshotEntered = resolve; });
    const held = store.withSnapshot(profile.id, async () => {
      snapshotEntered();
      await new Promise<void>((resolve) => { releaseSnapshot = resolve; });
    });
    await entered;
    let updateSettled = false;
    const update = store.update({
      id: profile.id,
      name: "锁定模板更新",
      css: { mode: "custom" },
      mermaidDefaults: { mode: "off", format: "png" },
    }).finally(() => { updateSettled = true; });
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(updateSettled).toBe(false);
    releaseSnapshot();
    await held;
    await expect(update).resolves.toMatchObject({ name: "锁定模板更新" });
  });

  it("rejects a warning approval when the revalidated file-pair fingerprint changed", async () => {
    const files = await fixture();
    const store = new TemplateStore({
      templatesRoot: files.templatesRoot,
      builtinCssPath: files.builtinCss,
      validator: { validate: async () => ({ ...report("warning"), contentFingerprint: "sha256:new" }) },
      idFactory: () => "template-warning",
    });

    await expect(store.add({
      name: "警告模板",
      templateSourcePath: files.sourceDocx,
      css: { mode: "custom", sourcePath: files.sourceCss },
      mermaidDefaults: { mode: "off", format: "png" },
      approvedWarningFingerprint: "sha256:old",
    })).rejects.toMatchObject({ code: "TEMPLATE_WARNING_CONFIRMATION_REQUIRED" });
  });
});
