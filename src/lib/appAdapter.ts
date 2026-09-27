import type { AppAdapter } from "../types";
import { createBrowserReviewAdapter } from "./browserBridgeAdapter";
import { createBrowserMockAdapter } from "./mockAdapter";

export function createAppAdapter(): AppAdapter {
  if (window.md2word) return window.md2word;
  if (import.meta.env.DEV && import.meta.env.VITE_MD2WORD_BROWSER_REVIEW === "1") {
    return createBrowserReviewAdapter();
  }
  return createBrowserMockAdapter();
}
