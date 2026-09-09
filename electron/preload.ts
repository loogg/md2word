import { contextBridge, ipcRenderer, webUtils } from "electron";
import type { ConversionEvent, Md2WordApi } from "./contracts";
import { isIpcEnvelope } from "./ipc-envelope";
import { IPC_CHANNELS } from "./ipc-channels";
import {
  assertId,
  parseAddTemplateInput,
  parseStartConversionInput,
  parseUpdateTemplateInput,
  parseValidateTemplateInput,
} from "./validation";

function isConversionEvent(value: unknown): value is ConversionEvent {
  if (!value || typeof value !== "object") return false;
  const event = value as Partial<ConversionEvent>;
  return typeof event.jobId === "string" && typeof event.timestamp === "string" && typeof event.kind === "string";
}

async function invokeMain<T>(channel: string, ...args: unknown[]): Promise<T> {
  const envelope: unknown = await ipcRenderer.invoke(channel, ...args);
  if (!isIpcEnvelope<T>(envelope)) {
    throw {
      code: "IPC_PROTOCOL_INVALID_RESPONSE",
      message: "主进程返回了无效响应，请重新启动应用。",
      retryable: true,
    };
  }
  // Errors crossing contextBridge lose custom Error properties. Reject with the
  // validated plain structured error so code/retryable/stage survive intact.
  if (!envelope.ok) throw envelope.error;
  return envelope.value;
}

const api: Md2WordApi = {
  runtimeCapabilities: Object.freeze({
    backend: "electron",
    fileDialogs: "native",
    templateStorage: "main",
    templateValidation: "worker",
    conversion: "worker",
    environment: "worker",
    shell: "native",
  }),
  templates: {
    list: () => invokeMain(IPC_CHANNELS.templatesList),
    add: (input) => invokeMain(IPC_CHANNELS.templatesAdd, parseAddTemplateInput(input)),
    update: (id, input) =>
      invokeMain(IPC_CHANNELS.templatesUpdate, assertId(id, "模板 ID"), parseUpdateTemplateInput(input)),
    remove: (id) => invokeMain(IPC_CHANNELS.templatesRemove, assertId(id, "模板 ID")),
    setDefault: (id) => invokeMain(IPC_CHANNELS.templatesSetDefault, assertId(id, "模板 ID")),
    validate: (id) => invokeMain(IPC_CHANNELS.templatesValidate, assertId(id, "模板 ID")),
    validateDraft: (input) => invokeMain(IPC_CHANNELS.templatesValidateDraft, parseValidateTemplateInput(input)),
  },
  files: {
    pickMarkdown: () => invokeMain(IPC_CHANNELS.filesPickMarkdown),
    registerMarkdown: (file) => {
      const osBackedPath = webUtils.getPathForFile(file);
      if (!osBackedPath) return Promise.reject(new Error("拖入的文件没有可用本机路径，请改用“选择文件”。"));
      return invokeMain(IPC_CHANNELS.filesRegisterMarkdownPath, osBackedPath);
    },
    pickTemplateDocx: () => invokeMain(IPC_CHANNELS.filesPickTemplateDocx),
    pickCss: () => invokeMain(IPC_CHANNELS.filesPickCss),
    pickOutput: (suggestedName) => invokeMain(IPC_CHANNELS.filesPickOutput, suggestedName),
  },
  conversions: {
    start: (input) => invokeMain(IPC_CHANNELS.conversionsStart, parseStartConversionInput(input)),
    cancel: (jobId) => invokeMain(IPC_CHANNELS.conversionsCancel, assertId(jobId, "任务 ID")),
    onEvent: (listener) => {
      const wrapped = (_event: Electron.IpcRendererEvent, value: unknown) => {
        if (isConversionEvent(value)) listener(value);
      };
      ipcRenderer.on(IPC_CHANNELS.conversionsEvent, wrapped);
      return () => ipcRenderer.removeListener(IPC_CHANNELS.conversionsEvent, wrapped);
    },
  },
  environment: {
    check: () => invokeMain(IPC_CHANNELS.environmentCheck),
  },
  capabilities: {
    describe: () => invokeMain(IPC_CHANNELS.capabilitiesDescribe),
  },
  shell: {
    openOutput: (jobId) => invokeMain(IPC_CHANNELS.shellOpenOutput, assertId(jobId, "任务 ID")),
    revealOutput: (jobId) => invokeMain(IPC_CHANNELS.shellRevealOutput, assertId(jobId, "任务 ID")),
    openTemplateLibrary: () => invokeMain(IPC_CHANNELS.shellOpenTemplateLibrary),
  },
};

contextBridge.exposeInMainWorld("md2word", Object.freeze(api));
