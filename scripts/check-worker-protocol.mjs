import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";

const workerPath = path.resolve("worker/publish/win-x64/Md2Word.Worker.exe");
const productVersion = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")).version;
const publishedResourceRoot = path.resolve("worker/publish/win-x64/resources/conversion");
const requiredPublishedResources = [
  "capabilities.json",
  "docx-worddom-style.css",
  "worddom-template.html",
  "filters/manifest.json",
];

if (!existsSync(workerPath)) {
  throw new Error("Published Worker is missing; run npm run build:worker first.");
}

for (const relativePath of requiredPublishedResources) {
  const resourcePath = path.join(publishedResourceRoot, ...relativePath.split("/"));
  if (!existsSync(resourcePath)) {
    throw new Error(`Published Worker resource is missing: ${relativePath}`);
  }
}

function request(frame) {
  return new Promise((resolve, reject) => {
    const child = spawn(workerPath, [], { windowsHide: true, shell: false, stdio: ["pipe", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.on("error", reject);
    child.on("close", (code) => {
      const lines = stdout.split(/\r?\n/).filter((line) => line.trim());
      if (code !== 0 || lines.length !== 1) {
        reject(new Error(`Worker protocol smoke failed (exit ${code}, frames ${lines.length}, stderr ${stderr.length} bytes).`));
        return;
      }
      try {
        resolve(JSON.parse(lines[0]));
      } catch (error) {
        reject(new Error("Worker emitted invalid JSON.", { cause: error }));
      }
    });
    child.stdin.end(`${JSON.stringify(frame)}\n`, "utf8");
  });
}

function requestConvertWithOpenControlPipe(frame) {
  return new Promise((resolve, reject) => {
    const child = spawn(workerPath, [], { windowsHide: true, shell: false, stdio: ["pipe", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    let settled = false;
    const timeout = setTimeout(() => {
      if (settled) return;
      settled = true;
      child.kill();
      reject(new Error("Convert did not start while its control pipe remained open."));
    }, 8_000);
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.stdin.on("error", () => {});
    child.on("error", (error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      reject(error);
    });
    child.on("close", (code) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      const lines = stdout.split(/\r?\n/).filter((line) => line.trim());
      try {
        resolve({ code, frames: lines.map((line) => JSON.parse(line)), stderrLength: stderr.length });
      } catch (error) {
        reject(new Error("Convert lifecycle emitted invalid JSON.", { cause: error }));
      }
    });
    // Deliberately keep stdin open: the same pipe must remain available for a
    // later cancel frame without delaying the initial conversion request.
    child.stdin.write(`${JSON.stringify(frame)}\n`, "utf8");
  });
}

const diagnose = await request({ protocolVersion: "1.0", requestId: "smoke-diagnose", command: "diagnose" });
if (diagnose.type !== "result" || diagnose.requestId !== "smoke-diagnose" || !Array.isArray(diagnose.result?.items)) {
  throw new Error("Diagnose response does not match protocol 1.0.");
}

const capabilities = await request({
  protocolVersion: "1.0",
  requestId: "smoke-capabilities",
  command: "describe-capabilities",
});
if (
  capabilities.type !== "result"
  || capabilities.requestId !== "smoke-capabilities"
  || capabilities.result?.schemaVersion !== "1.1"
  || capabilities.result?.productVersion !== productVersion
  || !Array.isArray(capabilities.result?.frontMatter)
  || !capabilities.result.frontMatter.some((item) => item.key === "word_heading_numbering")
) {
  throw new Error("Capability catalog response does not match the versioned contract.");
}

const validation = await request({
  protocolVersion: "1.0",
  requestId: "smoke-validate",
  command: "validate-template",
  docxPath: "Z:\\md2word-protocol-smoke\\missing-template.docx",
  cssPath: "Z:\\md2word-protocol-smoke\\missing-style.css",
});
if (
  validation.type !== "result"
  || validation.requestId !== "smoke-validate"
  || validation.result?.status !== "invalid"
  || !Array.isArray(validation.result?.styleMappings)
  || Object.hasOwn(validation.result ?? {}, "styleMap")
) {
  throw new Error("Template validation response does not match the shared styleMappings contract.");
}

const openPipeConversion = await requestConvertWithOpenControlPipe({
  protocolVersion: "1.0",
  requestId: "smoke-convert-open-pipe",
  command: "convert",
  request: {
    jobId: "smoke-convert-open-pipe",
    templateId: "synthetic-missing-template",
    sourcePath: "Z:\\md2word-protocol-smoke\\missing-source.md",
    outputPath: "Z:\\md2word-protocol-smoke\\missing-output.docx",
    templateSnapshot: {
      docxPath: "Z:\\md2word-protocol-smoke\\missing-template.docx",
      cssPath: "Z:\\md2word-protocol-smoke\\missing-style.css",
      validationFingerprint: "sha256:synthetic",
    },
    tools: { pandocPath: "pandoc" },
    options: { tocDepth: 3, mermaidMode: "off", mermaidFormat: "png" },
  },
});
const openPipeKinds = openPipeConversion.frames
  .filter((frame) => frame.type === "event")
  .map((frame) => frame.event?.kind);
const openPipeError = openPipeConversion.frames.find((frame) => frame.type === "error");
if (
  openPipeConversion.code !== 2
  || !openPipeKinds.includes("started")
  || !openPipeKinds.includes("failed")
  || openPipeError?.error?.code !== "SOURCE_FILE_UNREADABLE"
) {
  throw new Error(
    `Convert open-pipe lifecycle is invalid (exit ${openPipeConversion.code}, frames ${openPipeConversion.frames.length}, stderr ${openPipeConversion.stderrLength} bytes).`,
  );
}

process.stdout.write("Worker protocol 1.0 smoke passed (resources + diagnose + describe-capabilities + validate-template + open control pipe).\n");
