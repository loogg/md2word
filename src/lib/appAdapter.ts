import type { AppAdapter } from "../types";
import { createBrowserMockAdapter } from "./mockAdapter";

export function createAppAdapter(): AppAdapter {
  if (window.md2word) return window.md2word;
  return createBrowserMockAdapter();
}
