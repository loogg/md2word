import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { configDefaults, defineConfig } from "vitest/config";

const browserReview = process.env.MD2WORD_BROWSER_REVIEW === "1";
const bridgeSecret = process.env.MD2WORD_BRIDGE_TOKEN;
if (browserReview && !/^[0-9a-f]{64}$/.test(bridgeSecret ?? "")) {
  throw new Error("Browser Review requires a random bridge token from its launcher.");
}

export default defineConfig({
  // Packaged Electron loads dist/index.html over file://, so every emitted asset must stay relative.
  base: "./",
  plugins: [react(), tailwindcss()],
  server: {
    host: "127.0.0.1",
    port: 4173,
    strictPort: true,
    proxy: browserReview ? {
      "/__md2word_bridge": {
        target: "http://127.0.0.1:4174",
        changeOrigin: true,
        rewrite: (requestPath) => requestPath.replace(/^\/__md2word_bridge/, ""),
        headers: { "x-md2word-review-token": bridgeSecret! },
      },
    } : undefined,
  },
  test: {
    globals: true,
    environment: "jsdom",
    setupFiles: "./src/test/setup.ts",
    css: true,
    exclude: [...configDefaults.exclude, "e2e/**"],
  },
});
