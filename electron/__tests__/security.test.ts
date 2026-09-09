import path from "node:path";
import { describe, expect, it } from "vitest";
import {
  assertSafeDevelopmentUrl,
  buildContentSecurityPolicy,
  secureWebPreferences,
} from "../security";

describe("Electron security baseline", () => {
  it("locks down renderer privileges", () => {
    const preferences = secureWebPreferences(path.resolve("dist-electron/preload.cjs"));
    expect(preferences).toMatchObject({
      nodeIntegration: false,
      nodeIntegrationInWorker: false,
      nodeIntegrationInSubFrames: false,
      contextIsolation: true,
      sandbox: true,
      webSecurity: true,
      webviewTag: false,
    });
  });

  it("uses a restrictive CSP and only accepts a loopback development server", () => {
    const production = buildContentSecurityPolicy();
    expect(production).toContain("default-src 'self'");
    expect(production).toContain("object-src 'none'");
    expect(production).not.toContain("unsafe-eval");
    expect(assertSafeDevelopmentUrl("http://127.0.0.1:4173/path")).toBe("http://127.0.0.1:4173");
    expect(() => assertSafeDevelopmentUrl("https://example.com")).toThrow(/本机开发服务器/);
  });
});
