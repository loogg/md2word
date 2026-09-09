import * as fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { INSTALLATION_MARKER, isSetupInstallation, seedInstalledTemplatePackages } from "../installed-templates";

const roots: string[] = [];
afterEach(async () => {
  for (const root of roots.splice(0)) await fs.rm(root, { recursive: true, force: true });
});

async function fixture() {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "md2word-installed-synthetic-"));
  roots.push(root);
  const source = path.join(root, "bundled");
  const target = path.join(root, "user-data", "templates");
  await fs.mkdir(path.join(source, "reference", "synthetic"), { recursive: true });
  await fs.writeFile(path.join(source, "reference", "index.json"), JSON.stringify({ version: 2, profiles: [{ id: "synthetic" }] }));
  // Public, synthetic placeholder bytes: this test checks copying, not DOCX validation.
  await fs.writeFile(path.join(source, "reference", "synthetic", "template.docx"), "synthetic placeholder");
  await fs.writeFile(path.join(source, "reference", "synthetic", "style.css"), "/* synthetic CSS */");
  return { root, source, target };
}

describe("installed template library", () => {
  it("distinguishes installed and portable payloads by the installer-owned marker", async () => {
    const { root } = await fixture();
    expect(await isSetupInstallation(root)).toBe(false);
    await fs.writeFile(path.join(root, INSTALLATION_MARKER), "setup");
    expect(await isSetupInstallation(root)).toBe(true);
  });

  it("seeds complete public packages beside existing user imports", async () => {
    const { source, target } = await fixture();
    await fs.mkdir(path.join(target, "user"), { recursive: true });
    await fs.writeFile(path.join(target, "user", "index.json"), "existing synthetic imports");
    await seedInstalledTemplatePackages(source, target);
    expect(await fs.readFile(path.join(target, "reference", "synthetic", "template.docx"), "utf8")).toBe("synthetic placeholder");
    expect(await fs.readFile(path.join(target, "reference", "synthetic", "style.css"), "utf8")).toBe("/* synthetic CSS */");
    expect(await fs.readFile(path.join(target, "user", "index.json"), "utf8")).toBe("existing synthetic imports");
    expect((await fs.readdir(target)).some(name => name.startsWith(".seed-"))).toBe(false);
  });

  it("preserves user edits across application restarts and bundled template upgrades", async () => {
    const { source, target } = await fixture();
    await seedInstalledTemplatePackages(source, target);
    await fs.writeFile(path.join(target, "reference", "synthetic", "style.css"), "/* user edit */");
    await fs.writeFile(path.join(source, "reference", "synthetic", "style.css"), "/* new bundled version */");
    await seedInstalledTemplatePackages(source, target);
    expect(await fs.readFile(path.join(target, "reference", "synthetic", "style.css"), "utf8")).toBe("/* user edit */");
  });

  it("does not resurrect templates removed from an existing package", async () => {
    const { source, target } = await fixture();
    await seedInstalledTemplatePackages(source, target);
    await fs.writeFile(path.join(target, "reference", "index.json"), '{"version":2,"profiles":[]}');
    await fs.rm(path.join(target, "reference", "synthetic"), { recursive: true });
    await seedInstalledTemplatePackages(source, target);
    expect(await fs.readdir(path.join(target, "reference"))).toEqual(["index.json"]);
  });

  it("rejects linked package content without publishing a partial package", async () => {
    const { root, source, target } = await fixture();
    const outside = path.join(root, "outside");
    await fs.mkdir(outside);
    await fs.symlink(outside, path.join(source, "reference", "linked"), "junction");
    await expect(seedInstalledTemplatePackages(source, target)).rejects.toMatchObject({ code: "TEMPLATE_INDEX_CORRUPT" });
    expect(await fs.readdir(target)).toEqual([]);
  });
});
