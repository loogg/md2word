import { BrowserWindow } from "electron";
import path from "node:path";
import { assertSafeDevelopmentUrl, hardenSession, hardenWebContents, secureWindowOptions } from "./security";

export interface CreateMainWindowOptions {
  preloadPath: string;
  rendererHtmlPath: string;
  developmentUrl?: string;
  beforeLoad?: (window: BrowserWindow) => void | Promise<void>;
}

export async function createMainWindow(options: CreateMainWindowOptions): Promise<BrowserWindow> {
  const window = new BrowserWindow(secureWindowOptions(options.preloadPath));
  window.removeMenu();
  hardenWebContents(window.webContents);
  window.once("ready-to-show", () => window.show());
  await options.beforeLoad?.(window);

  if (options.developmentUrl) {
    const developmentOrigin = assertSafeDevelopmentUrl(options.developmentUrl);
    hardenSession(window.webContents.session, developmentOrigin);
    await window.loadURL(developmentOrigin);
  } else {
    if (!path.isAbsolute(options.rendererHtmlPath)) throw new Error("Renderer HTML path must be absolute");
    hardenSession(window.webContents.session);
    await window.loadFile(options.rendererHtmlPath);
  }
  return window;
}
