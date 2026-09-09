import path from "node:path";
import type { BrowserWindowConstructorOptions, Session, WebContents, WebPreferences } from "electron";
import { AppError } from "./errors";

const hardenedSessions = new WeakSet<Session>();

export function secureWebPreferences(preloadPath: string): WebPreferences {
  if (!path.isAbsolute(preloadPath)) throw new AppError("INTERNAL_ERROR", "preload 路径必须是绝对路径。");
  return {
    preload: preloadPath,
    nodeIntegration: false,
    nodeIntegrationInWorker: false,
    nodeIntegrationInSubFrames: false,
    contextIsolation: true,
    sandbox: true,
    webSecurity: true,
    allowRunningInsecureContent: false,
    webviewTag: false,
    spellcheck: false,
  };
}

export function secureWindowOptions(preloadPath: string): BrowserWindowConstructorOptions {
  return {
    width: 1440,
    height: 900,
    minWidth: 1100,
    minHeight: 720,
    show: false,
    backgroundColor: "#edf2f8",
    autoHideMenuBar: true,
    webPreferences: secureWebPreferences(preloadPath),
  };
}

export function buildContentSecurityPolicy(developmentOrigin?: string): string {
  const developmentConnect = developmentOrigin
    ? ` ${developmentOrigin} ${developmentOrigin.replace(/^http/, "ws")}`
    : "";
  return [
    "default-src 'self'",
    "base-uri 'none'",
    "object-src 'none'",
    "frame-src 'none'",
    "form-action 'none'",
    "script-src 'self'",
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob:",
    "font-src 'self'",
    `connect-src 'self'${developmentConnect}`,
    "media-src 'none'",
    "worker-src 'self' blob:",
  ].join("; ");
}

export function assertSafeDevelopmentUrl(value: string): string {
  let url: URL;
  try {
    url = new URL(value);
  } catch (error) {
    throw new AppError("INTERNAL_ERROR", "开发服务器地址无效。", false, { cause: error });
  }
  if (url.protocol !== "http:" || !["127.0.0.1", "localhost"].includes(url.hostname)) {
    throw new AppError("INTERNAL_ERROR", "只允许本机开发服务器。", false);
  }
  return url.origin;
}

export function hardenSession(session: Session, developmentOrigin?: string): void {
  if (hardenedSessions.has(session)) return;
  hardenedSessions.add(session);
  const policy = buildContentSecurityPolicy(developmentOrigin);

  session.setPermissionCheckHandler(() => false);
  session.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false));
  session.on("will-download", (event, item) => {
    event.preventDefault();
    item.cancel();
  });
  session.webRequest.onHeadersReceived((details, callback) => {
    const responseHeaders = { ...(details.responseHeaders ?? {}) };
    for (const header of Object.keys(responseHeaders)) {
      if (header.toLowerCase() === "content-security-policy") delete responseHeaders[header];
    }
    responseHeaders["Content-Security-Policy"] = [policy];
    callback({ cancel: false, responseHeaders });
  });
}

export function hardenWebContents(webContents: WebContents): void {
  webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  webContents.on("will-navigate", (event) => event.preventDefault());
  webContents.on("will-attach-webview", (event) => event.preventDefault());
}
