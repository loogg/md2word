import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import path from "node:path";

const metadata = JSON.parse(await readFile("package.json", "utf8"));
const shippedFiles = metadata.build.files;
assert.deepEqual(shippedFiles, [
  "dist/**/*",
  "dist-electron/main.js",
  "dist-electron/preload.cjs",
  "package.json",
  "!node_modules/**/*",
], "The package whitelist changed; review whether development Bridge files could ship.");

const main = await readFile("dist-electron/main.js", "utf8");
for (const marker of ["dev-browser-bridge.mjs", "MD2WORD_BROWSER_REVIEW", "startBrowserReviewBridge"]) {
  assert.ok(!main.includes(marker), `Production Electron Main still contains ${marker}.`);
}

const assets = await readdir("dist/assets");
for (const asset of assets.filter((name) => name.endsWith(".js"))) {
  const source = await readFile(path.join("dist", "assets", asset), "utf8");
  for (const marker of ["/__md2word_bridge", "BRIDGE_UNAVAILABLE", "createBrowserReviewAdapter"]) {
    assert.ok(!source.includes(marker), `Production Renderer still contains ${marker}.`);
  }
}

process.stdout.write("Production bundles and package whitelist exclude the development Browser Review Bridge.\n");
