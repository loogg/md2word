import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { setTimeout as pause } from "node:timers/promises";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const electronPackageRoot = path.join(projectRoot, "node_modules", "electron");
const env = {
  ...process.env,
  MD2WORD_BROWSER_REVIEW: "1",
  MD2WORD_BRIDGE_TOKEN: randomBytes(32).toString("hex"),
  VITE_MD2WORD_BROWSER_REVIEW: "1",
  VITE_DEV_SERVER_URL: "http://127.0.0.1:4173",
};

if (process.platform !== "win32" || process.arch !== "x64") {
  throw new Error("Browser Review requires a Windows x64 development host.");
}

function launch(executable, args) {
  return spawn(executable, args, { cwd: projectRoot, env, stdio: "inherit", windowsHide: true });
}

async function run(executable, args) {
  const child = launch(executable, args);
  const code = await new Promise((resolve, reject) => {
    child.once("error", reject);
    child.once("exit", resolve);
  });
  if (code !== 0) throw new Error(`${path.basename(executable)} failed with exit code ${code}.`);
}

async function waitFor(url, processToWatch, headers) {
  for (let attempt = 0; attempt < 80; attempt += 1) {
    if (processToWatch.exitCode !== null) throw new Error("Browser Review process exited before becoming ready.");
    try {
      const response = await fetch(url, { headers, signal: AbortSignal.timeout(700) });
      if (response.ok) return;
    } catch {
      // The local server is still starting.
    }
    await pause(250);
  }
  throw new Error(`Browser Review did not become ready: ${url}`);
}

await run(process.execPath, [path.join(electronPackageRoot, "install.js")]);
const vite = launch(process.execPath, [path.join(projectRoot, "node_modules", "vite", "bin", "vite.js"), "--host", "127.0.0.1"]);
let desktop;
const stop = () => {
  desktop?.kill();
  vite.kill();
};
process.once("SIGINT", stop);
process.once("SIGTERM", stop);
try {
  await waitFor("http://127.0.0.1:4173/", vite);
  desktop = launch(path.join(electronPackageRoot, "dist", "electron.exe"), [path.join(projectRoot, "dist-electron", "main.js")]);
  await waitFor("http://127.0.0.1:4174/health", desktop, { "x-md2word-review-token": env.MD2WORD_BRIDGE_TOKEN });
  const proxiedHealth = await fetch("http://127.0.0.1:4173/__md2word_bridge/health", { signal: AbortSignal.timeout(2_000) });
  if (!proxiedHealth.ok) throw new Error("Vite could not reach the Browser Review Bridge.");
  process.stdout.write("Browser Review ready: http://127.0.0.1:4173/ (real desktop backend)\n");
  await Promise.race([
    new Promise((resolve) => desktop.once("exit", resolve)),
    new Promise((resolve) => vite.once("exit", resolve)),
  ]);
} finally {
  stop();
}
