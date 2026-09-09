import * as fs from "node:fs/promises";
import path from "node:path";

const PROFILE_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/;
const REQUIRED_PROFILE_FILES = ["template.docx", "style.css"];

async function requireRegularFile(filePath, label) {
  const stat = await fs.lstat(filePath);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size <= 0) {
    throw new Error(`${label} must be a non-empty regular file.`);
  }
}

export async function validateTemplateLibrary(rootPath) {
  if (!path.isAbsolute(rootPath)) throw new Error("The template library path must be absolute.");
  const root = path.resolve(rootPath);
  const rootStat = await fs.lstat(root);
  if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) {
    throw new Error("The template library must be a real directory, not a link.");
  }

  const indexPath = path.join(root, "index.json");
  await requireRegularFile(indexPath, "Template index");
  const index = JSON.parse(await fs.readFile(indexPath, "utf8"));
  if (![1, 2].includes(index?.version) || !Array.isArray(index.profiles) || index.profiles.length === 0) {
    throw new Error("The template index must contain at least one supported profile.");
  }

  const ids = new Set();
  for (const [profileIndex, profile] of index.profiles.entries()) {
    const id = profile?.id;
    if (typeof id !== "string" || !PROFILE_ID_PATTERN.test(id) || ids.has(id)) {
      throw new Error(`Template entry ${profileIndex + 1} has an invalid or duplicate id.`);
    }
    if (index.version === 2 && (typeof profile?.name !== "string" || profile.name.trim().length === 0)) {
      throw new Error(`Template entry ${profileIndex + 1} must have a non-empty name.`);
    }
    ids.add(id);
    const profileRoot = path.join(root, id);
    const profileStat = await fs.lstat(profileRoot);
    if (!profileStat.isDirectory() || profileStat.isSymbolicLink()) {
      throw new Error(`Template entry ${profileIndex + 1} must use a real directory.`);
    }
    for (const fileName of REQUIRED_PROFILE_FILES) {
      await requireRegularFile(path.join(profileRoot, fileName), `Template entry ${profileIndex + 1} ${fileName}`);
    }
  }

  return { root, indexPath, profiles: index.profiles.map((profile) => ({ id: profile.id })) };
}

export async function validateTemplateCatalog(rootPath) {
  if (!path.isAbsolute(rootPath)) throw new Error("The template catalog path must be absolute.");
  const root = path.resolve(rootPath);
  const rootStat = await fs.lstat(root);
  if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) {
    throw new Error("The template catalog must be a real directory, not a link.");
  }
  try {
    await fs.lstat(path.join(root, "index.json"));
    throw new Error("The template catalog root must not contain index.json; put each template package in a child directory.");
  } catch (error) {
    if (error?.code !== "ENOENT") throw error;
  }

  const bundles = [];
  const profileIds = new Set();
  let defaultCount = 0;
  for (const entry of (await fs.readdir(root, { withFileTypes: true }))
    .sort((left, right) => left.name.localeCompare(right.name, "en"))) {
    if (!entry.isDirectory() || entry.isSymbolicLink()) continue;
    const bundleRoot = path.join(root, entry.name);
    try {
      await fs.lstat(path.join(bundleRoot, "index.json"));
    } catch (error) {
      if (error?.code === "ENOENT") continue;
      throw error;
    }
    const bundle = await validateTemplateLibrary(bundleRoot);
    const index = JSON.parse(await fs.readFile(bundle.indexPath, "utf8"));
    for (const profile of index.profiles) {
      if (profileIds.has(profile.id)) throw new Error(`Template id is duplicated across packages: ${profile.id}`);
      profileIds.add(profile.id);
      if (profile.isDefault === true) defaultCount += 1;
    }
    bundles.push({ name: entry.name, root: bundle.root, profiles: bundle.profiles });
  }
  if (bundles.length === 0) throw new Error("The template catalog must contain at least one child package with index.json.");
  if (defaultCount > 1) throw new Error("Only one template across all packages may be marked as default.");
  return { root, bundles };
}
