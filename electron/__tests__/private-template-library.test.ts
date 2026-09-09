import * as fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { validateTemplateCatalog, validateTemplateLibrary } from "../../scripts/private-template-library.mjs";
import { stagePublicTemplateCatalog } from "../../scripts/public-reference-library.mjs";

const roots: string[] = [];

afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => fs.rm(root, { recursive: true, force: true })));
});

async function syntheticLibrary(root: string) {
  const profile = { id: "synthetic-template", name: "Synthetic template" };
  const profileRoot = path.join(root, profile.id);
  await fs.mkdir(profileRoot, { recursive: true });
  await Promise.all([
    fs.writeFile(path.join(root, "index.json"), JSON.stringify({ version: 2, profiles: [profile] })),
    fs.writeFile(path.join(profileRoot, "template.docx"), "synthetic-openxml-placeholder"),
    fs.writeFile(path.join(profileRoot, "style.css"), "p { mso-style-name: 'Synthetic Body'; }"),
  ]);
}

describe("template package validation", () => {
  it("validates an external self-contained template package", async () => {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), "md2word-private-package-"));
    roots.push(root);
    const source = path.join(root, "external-source");
    await syntheticLibrary(source);

    await expect(validateTemplateLibrary(source)).resolves.toMatchObject({
      profiles: [{ id: "synthetic-template" }],
    });
  });

  it("stages every complete public source package without synthesizing metadata", async () => {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), "md2word-public-package-"));
    roots.push(root);
    const project = path.join(root, "workspace");
    const sourceCatalog = path.join(project, "resources", "templates");
    const reference = path.join(sourceCatalog, "reference");
    const generated = path.join(reference, "public-reference-template");
    const examples = path.join(sourceCatalog, "examples");
    const stage = path.join(project, "templates", "local");
    await fs.mkdir(generated, { recursive: true });
    await syntheticLibrary(examples);
    const docxPath = path.join(generated, "template.docx");
    const cssPath = path.join(generated, "style.css");
    const referenceIndex = `${JSON.stringify({
      version: 2,
      profiles: [{
        id: "public-reference-template",
        name: "公开参考模板",
        mermaidDefaults: { mode: "auto", format: "png" },
      }],
    }, null, 2)}\n`;
    await Promise.all([
      fs.writeFile(docxPath, "synthetic-openxml-placeholder"),
      fs.writeFile(cssPath, "/* Word-native fallback */"),
      fs.writeFile(path.join(reference, "index.json"), referenceIndex),
      fs.writeFile(path.join(reference, "NOTES.md"), "must not be packaged"),
      fs.writeFile(path.join(sourceCatalog, "README.md"), "catalog documentation"),
    ]);

    const validated: string[] = [];
    await stagePublicTemplateCatalog({
      projectRoot: project,
      stageRoot: stage,
      sourceRoot: sourceCatalog,
      validateTemplate: async (templatePath, stylePath, requestId) => {
        validated.push(`${path.relative(sourceCatalog, templatePath)}|${path.relative(sourceCatalog, stylePath)}|${requestId}`);
        return ({
          status: "valid",
          checkedAt: "2026-07-19T00:00:00.000Z",
          contentFingerprint: "sha256:public-synthetic",
          summary: "Synthetic public reference template is valid.",
          issues: [],
          capabilities: { bodyRange: true, coverTitle: true, coverSubtitle: true, versionTables: [], codeBlockStyle: true },
          styleMappings: [],
        });
      },
    });

    const catalog = await validateTemplateCatalog(stage);
    expect(catalog.bundles).toMatchObject([
      { name: "examples", profiles: [{ id: "synthetic-template" }] },
      { name: "reference", profiles: [{ id: "public-reference-template" }] },
    ]);
    expect(validated).toEqual([
      `${path.join("examples", "synthetic-template", "template.docx")}|${path.join("examples", "synthetic-template", "style.css")}|package-public-1`,
      `${path.join("reference", "public-reference-template", "template.docx")}|${path.join("reference", "public-reference-template", "style.css")}|package-public-2`,
    ]);
    const index = JSON.parse(await fs.readFile(path.join(stage, "reference", "index.json"), "utf8"));
    expect(index.profiles).toHaveLength(1);
    expect(index.profiles[0]).toMatchObject({
      id: "public-reference-template",
      name: "公开参考模板",
      mermaidDefaults: { mode: "auto", format: "png" },
    });
    expect(index.version).toBe(2);
    expect(index.profiles[0]).not.toHaveProperty("validation");
    expect(await fs.readFile(path.join(stage, "reference", "index.json"), "utf8")).toBe(referenceIndex);
    await expect(fs.access(path.join(stage, "README.md"))).rejects.toMatchObject({ code: "ENOENT" });
    await expect(fs.access(path.join(stage, "reference", "NOTES.md"))).rejects.toMatchObject({ code: "ENOENT" });
    await expect(fs.access(path.join(stage, "reference", "public-reference-template", "profile.json"))).rejects.toMatchObject({ code: "ENOENT" });
  });
});
