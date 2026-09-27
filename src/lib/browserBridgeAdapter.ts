import { IPC_CHANNELS } from "../../electron/ipc-channels";
import { isIpcEnvelope } from "../../electron/ipc-envelope";
import type { AppAdapter, ConversionEvent, PickedFile } from "../types";

const BASE = "/__md2word_bridge";

function bridgeUnavailable(): { code: string; message: string; retryable: boolean } {
  return {
    code: "BRIDGE_UNAVAILABLE",
    message: "Browser Review Bridge 未连接。请运行 npm run dev:browser-review 并重试。",
    retryable: true,
  };
}

async function invokeBackend<T>(channel: string, ...args: unknown[]): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${BASE}/invoke`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ channel, args }),
    });
  } catch {
    throw bridgeUnavailable();
  }
  if (!response.ok) throw bridgeUnavailable();
  let envelope: unknown;
  try { envelope = await response.json(); }
  catch { throw bridgeUnavailable(); }
  if (!isIpcEnvelope<T>(envelope)) throw bridgeUnavailable();
  if (!envelope.ok) throw envelope.error;
  return envelope.value;
}

function isConversionEvent(value: unknown): value is ConversionEvent {
  if (!value || typeof value !== "object") return false;
  const event = value as Partial<ConversionEvent>;
  return typeof event.jobId === "string" && typeof event.timestamp === "string" && typeof event.kind === "string";
}

/** Browser Review uses the real Main handlers; browser Files never become arbitrary OS paths. */
export function createBrowserReviewAdapter(): AppAdapter {
  return {
    runtimeCapabilities: Object.freeze({
      backend: "browser-bridge",
      fileDialogs: "native",
      templateStorage: "main",
      templateValidation: "worker",
      conversion: "worker",
      environment: "worker",
      shell: "native",
    }),
    templates: {
      list: () => invokeBackend(IPC_CHANNELS.templatesList),
      add: (input) => invokeBackend(IPC_CHANNELS.templatesAdd, input),
      update: (id, input) => invokeBackend(IPC_CHANNELS.templatesUpdate, id, input),
      remove: (id) => invokeBackend(IPC_CHANNELS.templatesRemove, id),
      setDefault: (id) => invokeBackend(IPC_CHANNELS.templatesSetDefault, id),
      validate: (id) => invokeBackend(IPC_CHANNELS.templatesValidate, id),
      validateDraft: (input) => invokeBackend(IPC_CHANNELS.templatesValidateDraft, input),
    },
    files: {
      pickMarkdown: () => invokeBackend(IPC_CHANNELS.filesPickMarkdown),
      registerMarkdown: (_file: File): Promise<PickedFile> => Promise.reject(new Error(
        "浏览器拖放无法保留 Markdown 的本机目录。请点击文件区域，使用由桌面后端打开的原生选择窗口。",
      )),
      pickTemplateDocx: () => invokeBackend(IPC_CHANNELS.filesPickTemplateDocx),
      pickCss: () => invokeBackend(IPC_CHANNELS.filesPickCss),
      pickOutput: (suggestedName) => invokeBackend(IPC_CHANNELS.filesPickOutput, suggestedName),
    },
    conversions: {
      start: (input) => invokeBackend(IPC_CHANNELS.conversionsStart, input),
      cancel: (jobId) => invokeBackend(IPC_CHANNELS.conversionsCancel, jobId),
      onEvent: (listener) => {
        const source = new EventSource(`${BASE}/events`);
        source.onmessage = (message) => {
          try {
            const event: unknown = JSON.parse(message.data);
            if (isConversionEvent(event)) listener(event);
          } catch {
            // Ignore malformed development-only event frames.
          }
        };
        return () => source.close();
      },
    },
    environment: { check: () => invokeBackend(IPC_CHANNELS.environmentCheck) },
    capabilities: { describe: () => invokeBackend(IPC_CHANNELS.capabilitiesDescribe) },
    updates: {
      check: () => invokeBackend(IPC_CHANNELS.updatesCheck),
      openRepository: () => invokeBackend(IPC_CHANNELS.updatesOpenRepository),
      openReleases: () => invokeBackend(IPC_CHANNELS.updatesOpenReleases),
      openLatestRelease: () => invokeBackend(IPC_CHANNELS.updatesOpenLatestRelease),
    },
    shell: {
      openOutput: (jobId) => invokeBackend(IPC_CHANNELS.shellOpenOutput, jobId),
      revealOutput: (jobId) => invokeBackend(IPC_CHANNELS.shellRevealOutput, jobId),
      openTemplateLibrary: () => invokeBackend(IPC_CHANNELS.shellOpenTemplateLibrary),
    },
  };
}
