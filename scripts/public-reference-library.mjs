import { spawn } from "node:child_process";
import * as fs from "node:fs/promises";
import path from "node:path";
import { validateTemplateCatalog } from "./private-template-library.mjs";

async function validateWithWorker(workerPath, docxPath, cssPath, requestId) {
  return new Promise((resolve, reject) => {
    const child = spawn(workerPath, [], {
      windowsHide: true,
      shell: false,
      stdio: ["pipe", "pipe", "pipe"],
    });
    let stdout = "";
    let stderrBytes = 0;
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderrBytes += chunk.length; });
    child.once("error", reject);
    child.once("close", (code) => {
      const lines = stdout.split(/\r?\n/).filter((line) => line.trim());
      if (code !== 0 || lines.length !== 1) {
        reject(new Error(`Public template validation failed (exit ${code}, frames ${lines.length}, stderr ${stderrBytes} bytes).`));
        return;
      }
      try {
        const frame = JSON.parse(lines[0]);
        if (frame?.type !== "result" || frame?.requestId !== requestId || !frame.result) {
          throw new Error("Worker returned an unexpected public template validation frame.");
        }
        resolve(frame.result);
      } catch (error) {
        reject(error);
      }
    });
    child.stdin.end(`${JSON.stringify({
      protocolVersion: "1.0",
      requestId,
      command: "validate-template",
      docxPath,
      cssPath,
    })}\n`, "utf8");
  });
}

export async function stagePublicTemplateCatalog(options) {
  const projectRoot = path.resolve(options.projectRoot);
  const stageRoot = path.resolve(options.stageRoot);
  const expectedSourceRoot = path.join(projectRoot, "resources", "templates");
  const sourceRoot = path.resolve(options.sourceRoot ?? expectedSourceRoot);
  if (stageRoot !== path.join(projectRoot, "templates", "local")) {
    throw new Error("Refusing to stage the public template catalog outside templates/local.");
  }
  if (sourceRoot !== expectedSourceRoot) {
    throw new Error("Public template packages must be read from the tracked resources/templates catalog.");
  }

  const sourceCatalog = await validateTemplateCatalog(sourceRoot);
  const validate = options.validateTemplate
    ?? ((templatePath, stylePath, requestId) => validateWithWorker(options.workerPath, templatePath, stylePath, requestId));
  const filesToCopy = [];
  let validationIndex = 0;

  for (const bundle of sourceCatalog.bundles) {
    const indexPath = path.join(bundle.root, "index.json");
    const index = JSON.parse(await fs.readFile(indexPath, "utf8"));
    filesToCopy.push({ source: indexPath, destination: path.join(stageRoot, bundle.name, "index.json") });

    for (const profile of index.profiles) {
      const profileRoot = path.join(bundle.root, profile.id);
      const docxPath = path.join(profileRoot, "template.docx");
      const cssPath = path.join(profileRoot, "style.css");
      const requestId = `package-public-${++validationIndex}`;
      const validation = await validate(docxPath, cssPath, requestId);
      if (
        validation?.status !== "valid"
        || !validation.contentFingerprint
        || !Array.isArray(validation.issues)
        || validation.issues.some((issue) => issue?.severity === "error")
        || !Array.isArray(validation.styleMappings)
      ) {
        throw new Error(`Public template ${bundle.name}/${profile.id} did not pass Worker validation without warnings or errors.`);
      }
      const stagedProfileRoot = path.join(stageRoot, bundle.name, profile.id);
      filesToCopy.push(
        { source: docxPath, destination: path.join(stagedProfileRoot, "template.docx") },
        { source: cssPath, destination: path.join(stagedProfileRoot, "style.css") },
      );
    }
  }

  await fs.rm(stageRoot, { recursive: true, force: true });
  try {
    for (const file of filesToCopy) {
      await fs.mkdir(path.dirname(file.destination), { recursive: true });
      await fs.copyFile(file.source, file.destination);
    }
    await validateTemplateCatalog(stageRoot);
  } catch (error) {
    await fs.rm(stageRoot, { recursive: true, force: true });
    throw error;
  }
}
