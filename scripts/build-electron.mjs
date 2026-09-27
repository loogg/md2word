import { mkdir } from "node:fs/promises";
import { build } from "esbuild";

const devBridge = process.argv.includes("--dev-bridge");
await mkdir("dist-electron", { recursive: true });

const shared = {
  bundle: true,
  platform: "node",
  target: "node24",
  external: ["electron"],
  sourcemap: true,
  logLevel: "info",
};

await Promise.all([
  build({
    ...shared,
    entryPoints: ["electron/main.ts"],
    outfile: "dist-electron/main.js",
    format: "esm",
    define: { __MD2WORD_ENABLE_BROWSER_REVIEW__: devBridge ? "true" : "false" },
    minifySyntax: true,
  }),
  build({
    ...shared,
    entryPoints: ["electron/preload.ts"],
    outfile: "dist-electron/preload.cjs",
    format: "cjs",
  }),
]);
